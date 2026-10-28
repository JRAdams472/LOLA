using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Applications;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-109 acceptance: PII fields are `vault:` ciphertext at rest in
/// payload AND index columns (never plaintext), and blind-index search
/// by SSN/phone/email resolves the application — real OpenBao + real
/// SQLCipher.
[Collection("openbao")]
public sealed class ApplicationRepositoryTests(OpenBaoFixture fixture)
{
    static ApplicationRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (ApplicationRepository apps, ApplicationPartyRepository parties,
             TenantProvisioningService svc, TenantRegistry registry,
             TransitFieldCipher cipher, TransitBlindIndex blind) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var blind = new TransitBlindIndex(fixture.Transit);
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            runner, fixture.DataDir);
        var parties = new ApplicationPartyRepository(
            new TenantConnectionFactory(cipher), cipher, blind);
        var apps = new ApplicationRepository(
            new TenantConnectionFactory(cipher), cipher, parties);
        return (apps, parties, svc, registry, cipher, blind);
    }

    private static ApplicationDocument SampleDoc()
    {
        var doc = ApplicationDocument.Empty();
        doc.Set("loan_amount", JsonValue(425000m));
        doc.Set("loan_purpose", "purchase");
        doc.Set("party[0].role", "borrower");
        doc.Set("party[0].first_name", "Ada");
        doc.Set("party[0].last_name", "Lovelace");
        doc.Set("party[0].ssn", "123456789");
        doc.Set("party[0].email", "ada@example.com");
        doc.Set("party[0].phone", "+1-555-0100");
        doc.Set("party[0].dob", "1815-12-10");
        doc.Set("party[0].address[0].street", "12 Analytical Way");
        doc.Set("party[0].address[0].city", "London");
        doc.Set("party[0].address[0].state", "LN");
        doc.Set("party[0].address[0].zip", "12345");
        doc.Set("party[0].employment[0].monthly_income", JsonValue(14200m));
        doc.Set("subject_property.street", "1 Prime Plaza");
        doc.Set("subject_property.est_value", JsonValue(560000m));
        return doc;
    }

    private static System.Text.Json.Nodes.JsonNode JsonValue(object v)
        => System.Text.Json.Nodes.JsonValue.Create(v)!;

    private static async Task<string> UnwrapAsync(
        TenantRecord tenant, TransitFieldCipher cipher)
    {
        using (CipherContext.Use(tenant.Id))
        {
            var b64 = await cipher.DecryptAsync(tenant.DbKeyWrapped!);
            return Convert.ToHexString(Convert.FromBase64String(b64));
        }
    }

    [Fact]
    public async Task Pii_is_ciphertext_at_rest_and_blind_search_works()
    {
        var (apps, parties, svc, registry, cipher, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var rec = (await registry.FindByIdAsync(id))!;
            var keyHex = await UnwrapAsync(rec, cipher);

            using (CipherContext.Use(id))
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                var appId = $"app-{Guid.NewGuid():N}"[..16];
                await apps.InsertAsync(appId, SampleDoc(), "draft", null, "test");

                // RAW read — payload carries ciphertext, never plaintext.
                string payload; string? ssnCt; string idxCount;
                using (var conn = KeyedSqliteConnectionManager.OpenKeyed(rec.DbPath, keyHex))
                {
                    payload = await conn.QuerySingleAsync<string>(
                        "SELECT payload FROM application_versions WHERE application_id=@id",
                        new { id = appId });
                    ssnCt = await conn.QuerySingleAsync<string>(
                        "SELECT ssn_ct FROM application_parties WHERE application_id=@id",
                        new { id = appId });
                    idxCount = (await conn.QueryAsync<string>(
                        "SELECT idx FROM application_blind_idx WHERE application_id=@id",
                        new { id = appId })).First();
                }
                payload.ShouldContain("vault:");
                payload.ShouldNotContain("123456789");
                payload.ShouldNotContain("ada@example.com");
                ssnCt.ShouldStartWith("vault:");
                idxCount.ShouldStartWith("idx:");

                // Decrypted read round-trips plaintext for the app layer.
                var got = (await apps.GetAsync(appId))!;
                got.Status.ShouldBe("draft");
                got.Doc.Get("party[0].ssn")!.GetValue<string>().ShouldBe("123456789");
                got.Doc.Get("party[0].first_name")!.GetValue<string>().ShouldBe("Ada");

                // Blind-index search — SSN, phone, email all resolve.
                (await parties.SearchAsync("ssn", "123456789")).ShouldContain(appId);
                (await parties.SearchAsync("phone", "+1-555-0100")).ShouldContain(appId);
                (await parties.SearchAsync("email", "ada@example.com")).ShouldContain(appId);
                (await parties.SearchAsync("ssn", "000000000")).ShouldNotContain(appId);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Versions_are_append_only_and_new_version_wins()
    {
        var (apps, _, svc, registry, cipher, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var rec = (await registry.FindByIdAsync(id))!;
            using (CipherContext.Use(id))
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                var appId = $"app-{Guid.NewGuid():N}"[..16];
                await apps.InsertAsync(appId, SampleDoc(), "draft", null, "test");
                var doc2 = SampleDoc();
                doc2.Set("loan_amount", JsonValue(450000m));
                var v = await apps.WriteVersionAsync(
                    appId, doc2, "submitted", "test", submitted: true);
                v.ShouldBe(2);
                var got = (await apps.GetAsync(appId))!;
                got.CurrentVersion.ShouldBe(2);
                got.SubmittedUtc.ShouldNotBeNull();
                got.Doc.Get("loan_amount")!.GetValue<decimal>().ShouldBe(450000m);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
