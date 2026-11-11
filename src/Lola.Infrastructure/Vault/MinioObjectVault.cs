using Amazon.S3;
using Amazon.S3.Model;
using Lola.Application.Tenancy;
using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Real object vault (MPE-201): MinIO/S3 behind the ADR-0007 seam.
/// The bucket is derived from the ambient TenantContext —
/// `lola-vault-<tid>` — so no code path can name another tenant's
/// objects, the same guarantee as the per-tenant SQLCipher file.
/// Every byte that crosses these methods is LVL1 ciphertext: the
/// caller hands VaultCipher's sealing stream to PutAsync and opens
/// GetAsync streams with the matching DEK — MinIO never sees
/// plaintext (invariant 1).
public sealed class MinioObjectVault(IAmazonS3 s3) : IObjectVault
{
    public string Adapter => "minio";

    /// Bucket for the ambient tenant — required, never optional:
    /// a vault call outside a tenant scope is a bug, not a fallback.
    internal static string BucketFor(TenantScope scope) =>
        $"lola-vault-{scope.TenantId}";

    private string Bucket() => BucketFor(TenantContext.Required);

    public async Task PutAsync(string key, Stream content,
        CancellationToken ct = default)
    {
        // S3 PUT requires a declared content length; the cipher's
        // sealing stream is non-seekable, so its ciphertext is buffered
        // here once — bounded by VAULT_MAX_BYTES + AEAD overhead,
        // ciphertext only, never plaintext.
        Stream upload = content;
        MemoryStream? buffered = null;
        if (!content.CanSeek)
        {
            buffered = new MemoryStream();
            await content.CopyToAsync(buffered, ct);
            buffered.Position = 0;
            upload = buffered;
        }
        try
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = Bucket(),
                Key = key,
                InputStream = upload,
                DisableDefaultChecksumValidation = true
            }, ct);
        }
        finally
        {
            if (buffered is not null)
                await buffered.DisposeAsync();
        }
    }

    public async Task<Stream> GetAsync(string key,
        CancellationToken ct = default)
        => (await s3.GetObjectAsync(Bucket(), key, ct)).ResponseStream;

    public async Task CopyAsync(string sourceKey, string destKey,
        CancellationToken ct = default)
    {
        var bucket = Bucket();
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = sourceKey,
            DestinationBucket = bucket,
            DestinationKey = destKey
        }, ct);
    }

    public async Task DeleteAsync(string key,
        CancellationToken ct = default)
        => await s3.DeleteObjectAsync(Bucket(), key, ct);

    /// Objects → bucket delete, paged. Called on the erasure path
    /// before the key shred — every ciphertext object goes, then the
    /// bucket itself, so no vault residue outlives the tenant.
    public async Task PurgeTenantAsync(CancellationToken ct = default)
        => await PurgeBucketAsync(s3, Bucket(), ct);

    /// Shared purge for the provisioner (names the tenant explicitly
    /// rather than ambient) — list every object, batch-delete, then
    /// drop the bucket. Idempotent: a missing bucket is a no-op.
    internal static async Task PurgeBucketAsync(IAmazonS3 s3,
        string bucket, CancellationToken ct)
    {
        try
        {
            string? continuation = null;
            do
            {
                var page = await s3.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        ContinuationToken = continuation
                    }, ct);
                if (page.S3Objects.Count > 0)
                    await s3.DeleteObjectsAsync(new DeleteObjectsRequest
                    {
                        BucketName = bucket,
                        Objects = page.S3Objects
                            .Select(o => new KeyVersion { Key = o.Key })
                            .ToList()
                    }, ct);
                continuation = page.IsTruncated == true
                    ? page.NextContinuationToken : null;
            } while (continuation is not null);
            await s3.DeleteBucketAsync(bucket, ct);
        }
        catch (AmazonS3Exception ex)
            when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            // Bucket never provisioned — purge stays idempotent.
        }
    }
}
