using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-203 vault marquee — the M10 arc on the real stack: the ops
/// client provisions a tenant for real (OpenBao transit keys, keyed
/// SQLCipher file + migrations, OIDC svc client, MinIO bucket), then
/// an officer uploads a paystub through the real endpoint. The scan
/// drain runs real MinIO + clamd zINSTREAM, the download mint is a
/// real transit-HMAC capability URL, and the fetched bytes are
/// asserted SHA-256-identical to the upload — encrypt→quarantine→
/// scan→promote→decrypt is byte-exact end to end. A second test
/// stages EICAR at worker level (the endpoint allowlist would 415 a
/// text file) and proves it never reaches clean/.
public sealed class VaultMarqueeTests(VaultMarqueeFactory factory)
    : IClassFixture<VaultMarqueeFactory>
{
    private const string Eicar =
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$" +
        "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private static byte[] PdfBytes(string marker)
        => System.Text.Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n% {marker}\n1 0 obj<<>>endobj\n" +
            "trailer<<>>\n%%EOF\n");

    [Fact]
    public async Task Full_vault_arc_against_live_minio_clamav()
    {
        var tenant = $"vq{Guid.NewGuid():N}"[..12];

        // 1. Platform-operator provisioning — transit keys, keyed
        //    tenant file + migrations, OIDC svc client, and the
        //    `vault-bucket` step against real MinIO.
        var ops = factory.CreateClient();
        var tok = await PkceLogin.PostWithRetryAsync(ops, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-ops",
                ["client_secret"] = "ops-secret-value"
            });
        ops.DefaultRequestHeaders.Authorization = new("Bearer",
            (await tok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!);
        var prov = await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "VltMq" });
        prov.StatusCode.ShouldBe(HttpStatusCode.Created,
            await prov.Content.ReadAsStringAsync());

        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>()
            .FindByIdAsync(tenant);
        rec.ShouldNotBeNull();
        rec.Status.ShouldBe("active");
        rec.DbKeyWrapped.ShouldNotBeNullOrEmpty();

        // The bucket materialized in real MinIO — provisioning ran the
        // vault step for real, not just the registry row.
        var s3 = factory.Services.GetRequiredService<IAmazonS3>();
        (await AmazonS3Util.DoesS3BucketExistV2Async(s3,
            $"lola-vault-{tenant}")).ShouldBeTrue();

        // 2. Officer draft → paystub upload → 202 quarantined.
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var plaintext = PdfBytes($"mq-marker-{Guid.NewGuid():N}");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(plaintext);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", "march-paystub.pdf");
        var post = await officer.PostAsync(
            $"{base_}/{appId}/uploads?classification=paystub", form);
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var docId = (await post.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("vaultDocId").GetString()!;

        // 3. Drain the scan worker — real ClamAV verdict → CopyObject
        //    promotion to clean/, quarantine destroyed.
        var processor = factory.Services.GetRequiredService<
            Lola.Api.Vault.VaultScanProcessor>();
        await processor.DrainTenantAsync(rec);

        var detail = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads/{docId}");
        detail.GetProperty("status").GetString().ShouldBe("clean");

        // The object really sits under clean/ — registry row points at
        // it, not at the quarantine key (which must be gone).
        var keys = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = $"lola-vault-{tenant}"
        });
        keys.S3Objects.Select(o => o.Key)
            .ShouldBe([$"clean/{docId}"]);

        // 4. Registry list — newest first, decrypted filename.
        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads");
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("vaultDocId").GetString().ShouldBe(docId);
        list[0].GetProperty("classification").GetString()
            .ShouldBe("paystub");
        list[0].GetProperty("filename").GetString()
            .ShouldBe("march-paystub.pdf");

        // 5. Download mint → 302 to the capability URL → auth-free
        //    fetch streams the DECRYPTED bytes — SHA-256 must be
        //    byte-identical to what was uploaded.
        var noRedirect = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization =
            officer.DefaultRequestHeaders.Authorization;
        var dl = await noRedirect.GetAsync(
            $"{base_}/{appId}/uploads/{docId}/download");
        dl.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = dl.Headers.Location!.ToString();
        location.ShouldContain($"/api/vault-content/{docId}");
        location.ShouldContain($"tid={tenant}");
        location.ShouldContain("exp=");
        location.ShouldContain("sig=");

        // The content route is auth-free — the signature IS the
        // credential. No bearer header on this client.
        var fetch = factory.CreateClient();
        var get = await fetch.GetAsync(location);
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        get.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/pdf");
        get.Content.Headers.ContentDisposition!.FileNameStar
            .ShouldBe("march-paystub.pdf");
        get.Headers.CacheControl!.ToString().ShouldContain("no-store");
        get.Headers.GetValues("X-Content-Type-Options")
            .ShouldContain("nosniff");
        var downloaded = await get.Content.ReadAsByteArrayAsync();
        Convert.ToHexString(SHA256.HashData(downloaded))
            .ShouldBe(Convert.ToHexString(SHA256.HashData(plaintext)),
                StringCompareShould.IgnoreCase);
        downloaded.ShouldBe(plaintext);

        // 6. Audit spine — upload, scan verdict, and mint all
        //    attributed on the application's spine, read back from
        //    the real tenant file.
        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        var actions = audit.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("vault_upload");
        actions.ShouldContain("vault_scan_clean");
        actions.ShouldContain("vault_download");
    }

    [Fact]
    public async Task Eicar_staged_through_real_path_never_reaches_clean()
    {
        var tenant = $"ve{Guid.NewGuid():N}"[..12];
        var ops = factory.CreateClient();
        var tok = await PkceLogin.PostWithRetryAsync(ops, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-ops",
                ["client_secret"] = "ops-secret-value"
            });
        ops.DefaultRequestHeaders.Authorization = new("Bearer",
            (await tok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!);
        (await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "VltEic" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>()
            .FindByIdAsync(tenant);

        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        // Stage exactly what UploadVaultDoc leaves behind — sealed
        // ciphertext in quarantine/ + registry row + scan outbox row —
        // with EICAR as the payload. The endpoint allowlist would 415
        // a text file, so the staging is direct (worker level).
        var docId = $"vlt-{Guid.NewGuid():N}";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            using var _t = TenantContext.Use(TenantScope.From(rec!));
            using var _c = CipherContext.Use(rec!.Id);
            var cipher = sp.GetRequiredService<VaultCipher>();
            var store = sp.GetRequiredService<IObjectVault>();
            var repo = sp.GetRequiredService<IVaultDocRepository>();
            var bytes = System.Text.Encoding.ASCII.GetBytes(Eicar);
            await using var sealing = cipher.Seal(
                new MemoryStream(bytes), 25L << 20);
            await store.PutAsync($"quarantine/{docId}", sealing);
            var record = new VaultDocRecord(docId, appId, "paystub",
                "eicar.txt", "text/plain", sealing.PlaintextLength,
                sealing.Sha256Hex, sealing.WrappedDek, "minio",
                $"quarantine/{docId}", "quarantined", null,
                "officer-1", "", null,
                $"up:{sealing.Sha256Hex}:{appId}:{Guid.NewGuid():N}");
            (await repo.EnqueueAsync(record, $"vso-{Guid.NewGuid():N}"))
                .Deduplicated.ShouldBeFalse();
        }

        // Real clamd verdict → infected, quarantine destroyed, nothing
        // under clean/, vault_scan_infected audited.
        await factory.Services.GetRequiredService<
            Lola.Api.Vault.VaultScanProcessor>().DrainTenantAsync(rec!);

        var detail = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads/{docId}");
        detail.GetProperty("status").GetString().ShouldBe("infected");

        var s3 = factory.Services.GetRequiredService<IAmazonS3>();
        var keys = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = $"lola-vault-{tenant}"
        });
        // SDK v4 returns null, not an empty list, for an empty bucket.
        (keys.S3Objects ?? []).ShouldBeEmpty();

        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        var actions = audit.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("vault_scan_infected");
    }
}
