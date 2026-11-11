using Amazon.S3;
using Amazon.S3.Util;
using Lola.Application.Vault;
using Lola.Domain.Identity;

namespace Lola.Infrastructure.Vault;

/// Per-tenant vault provisioning step (MPE-201): ensures the
/// `lola-vault-<tid>` bucket exists. Re-entrant like every
/// provisioning step — DoesS3BucketExistV2 is the idempotency check.
/// PurgeAsync (wired on the erasure path in MPE-202) deletes every
/// object then the bucket — ciphertext residue never outlives the
/// tenant, and it runs BEFORE the transit-key shred so a failure
/// aborts retryable rather than orphaning the bucket.
public sealed class TenantVaultProvisioner(IAmazonS3 s3)
    : ITenantVaultProvisioner
{
    public async Task EnsureAsync(TenantRecord tenant,
        CancellationToken ct = default)
    {
        var bucket = $"lola-vault-{tenant.Id}";
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, bucket))
            await s3.PutBucketAsync(bucket, ct);
    }

    public async Task PurgeAsync(TenantRecord tenant,
        CancellationToken ct = default)
        => await MinioObjectVault.PurgeBucketAsync(
            s3, $"lola-vault-{tenant.Id}", ct);
}
