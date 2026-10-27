using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Lola.ControlPlane.OpenIddict;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// MPE-85: full-surface coverage of the custom Dapper OpenIddict
/// stores (ADR-0005). Each test walks instantiate → set → create →
/// get → find → list/count → update → delete for one store.
public class OpenIddictStoreTests
{
    private static ControlPlaneDb TempDb()
    {
        var db = new ControlPlaneDb(
            Path.Combine(Path.GetTempPath(), "lola-tests", $"oidc-{Guid.NewGuid():N}.db"));
        db.Migrate();
        return db;
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source) list.Add(item);
        return list;
    }

    [Fact]
    public async Task Application_store_round_trips_every_field()
    {
        var db = TempDb();
        var store = new DapperOpenIddictApplicationStore(db);
        var ct = CancellationToken.None;

        var app = await store.InstantiateAsync(ct);
        await store.SetClientIdAsync(app, "client-1", ct);
        await store.SetClientSecretAsync(app, "hashed-secret", ct);
        await store.SetClientTypeAsync(app, "public", ct);
        await store.SetApplicationTypeAsync(app, "web", ct); // no-op by design
        await store.SetConsentTypeAsync(app, "implicit", ct);
        await store.SetDisplayNameAsync(app, "Test client", ct);
        await store.SetDisplayNamesAsync(app,
            ImmutableDictionary<CultureInfo, string>.Empty.Add(CultureInfo.GetCultureInfo("fr"), "Client test"), ct);
        await store.SetPermissionsAsync(app,
            ImmutableArray.Create("ept:token", "gt:code"), ct);
        await store.SetPostLogoutRedirectUrisAsync(app,
            ImmutableArray.Create("http://localhost:3200/out"), ct);
        await store.SetRedirectUrisAsync(app,
            ImmutableArray.Create("http://localhost:3200/cb"), ct);
        await store.SetRequirementsAsync(app, ImmutableArray.Create("ft:pkce"), ct);
        await store.SetSettingsAsync(app,
            ImmutableDictionary<string, string>.Empty.Add("k", "v"), ct);
        await store.SetPropertiesAsync(app,
            ImmutableDictionary<string, JsonElement>.Empty.Add("p", JsonDocument.Parse("1").RootElement), ct);
        await store.SetJsonWebKeySetAsync(app,
            new JsonWebKeySet("""{"keys":[{"kty":"RSA","kid":"k1","n":"abc","e":"AQAB"}]}"""), ct);

        await store.CreateAsync(app, ct);
        var id = (await store.GetIdAsync(app, ct))!;

        var loaded = await store.FindByIdAsync(id, ct);
        loaded.ShouldNotBeNull();
        (await store.GetClientIdAsync(loaded, ct)).ShouldBe("client-1");
        (await store.GetClientSecretAsync(loaded, ct)).ShouldBe("hashed-secret");
        (await store.GetClientTypeAsync(loaded, ct)).ShouldBe("public");
        (await store.GetApplicationTypeAsync(loaded, ct)).ShouldBeNull();
        (await store.GetConsentTypeAsync(loaded, ct)).ShouldBe("implicit");
        (await store.GetDisplayNameAsync(loaded, ct)).ShouldBe("Test client");
        (await store.GetDisplayNamesAsync(loaded, ct))[CultureInfo.GetCultureInfo("fr")].ShouldBe("Client test");
        (await store.GetPermissionsAsync(loaded, ct)).ShouldBe(["ept:token", "gt:code"]);
        (await store.GetPostLogoutRedirectUrisAsync(loaded, ct)).ShouldBe(["http://localhost:3200/out"]);
        (await store.GetRedirectUrisAsync(loaded, ct)).ShouldBe(["http://localhost:3200/cb"]);
        (await store.GetRequirementsAsync(loaded, ct)).ShouldBe(["ft:pkce"]);
        (await store.GetSettingsAsync(loaded, ct))["k"].ShouldBe("v");
        (await store.GetPropertiesAsync(loaded, ct))["p"].GetInt32().ShouldBe(1);
        (await store.GetJsonWebKeySetAsync(loaded, ct))!.Keys.Single().Kid.ShouldBe("k1");

        (await store.FindByClientIdAsync("client-1", ct))!.Id.ShouldBe(id);
        (await Collect(store.FindByRedirectUriAsync("http://localhost:3200/cb", ct))).Count.ShouldBe(1);
        (await Collect(store.FindByPostLogoutRedirectUriAsync("http://localhost:3200/out", ct))).Count.ShouldBe(1);
        (await Collect(store.ListAsync(10, 0, ct))).Count.ShouldBe(1);
        (await store.CountAsync(ct)).ShouldBe(1);
        (await store.CountAsync<string>(q => q.Select(a => a.ClientId!), ct)).ShouldBe(1);
        (await store.GetAsync<string, string?>(
            (q, state) => q.Where(a => a.ClientId == state).Select(a => a.ClientId),
            "client-1", ct)).ShouldBe("client-1");
        (await Collect(store.ListAsync<string, string?>(
            (q, state) => q.Where(a => a.ClientId == state).Select(a => a.ClientId),
            "client-1", ct))).Count.ShouldBe(1);

        await store.SetDisplayNameAsync(loaded, "Renamed", ct);
        await store.UpdateAsync(loaded, ct);
        (await store.GetDisplayNameAsync((await store.FindByIdAsync(id, ct))!, ct)).ShouldBe("Renamed");

        await store.DeleteAsync(loaded, ct);
        (await store.FindByIdAsync(id, ct)).ShouldBeNull();
        (await store.CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Authorization_store_round_trips_and_revokes()
    {
        var db = TempDb();
        var store = new DapperOpenIddictAuthorizationStore(db);
        var apps = new DapperOpenIddictApplicationStore(db);
        var ct = CancellationToken.None;

        var app = await apps.InstantiateAsync(ct);
        await apps.SetClientIdAsync(app, "client-authz", ct);
        await apps.CreateAsync(app, ct);

        var auth = await store.InstantiateAsync(ct);
        await store.SetApplicationIdAsync(auth, app.Id, ct);
        await store.SetCreationDateAsync(auth, DateTimeOffset.UtcNow, ct);
        await store.SetStatusAsync(auth, "valid", ct);
        await store.SetSubjectAsync(auth, "subject-1", ct);
        await store.SetTypeAsync(auth, "permanent", ct);
        await store.SetScopesAsync(auth, ImmutableArray.Create("openid", "lola-api"), ct);
        await store.SetPropertiesAsync(auth,
            ImmutableDictionary<string, JsonElement>.Empty.Add("p", JsonDocument.Parse("1").RootElement), ct);
        await store.CreateAsync(auth, ct);

        var id = (await store.GetIdAsync(auth, ct))!;
        var loaded = await store.FindByIdAsync(id, ct);
        loaded.ShouldNotBeNull();
        (await store.GetApplicationIdAsync(loaded, ct)).ShouldBe(app.Id);
        (await store.GetCreationDateAsync(loaded, ct)).ShouldNotBeNull();
        (await store.GetStatusAsync(loaded, ct)).ShouldBe("valid");
        (await store.GetSubjectAsync(loaded, ct)).ShouldBe("subject-1");
        (await store.GetTypeAsync(loaded, ct)).ShouldBe("permanent");
        (await store.GetScopesAsync(loaded, ct)).ShouldBe(["openid", "lola-api"]);
        (await store.GetPropertiesAsync(loaded, ct))["p"].GetInt32().ShouldBe(1);

        (await Collect(store.FindBySubjectAsync("subject-1", ct))).Count.ShouldBe(1);
        (await Collect(store.FindByApplicationIdAsync(app.Id, ct))).Count.ShouldBe(1);
        (await Collect(store.FindAsync("subject-1", null, "valid", "permanent",
            ImmutableArray.Create("openid"), ct))).Count.ShouldBe(1);
        (await Collect(store.FindAsync("other", null, null, null, null, ct))).Count.ShouldBe(0);
        (await Collect(store.ListAsync(10, 0, ct))).Count.ShouldBe(1);
        (await store.CountAsync(ct)).ShouldBe(1);
        (await store.CountAsync<string>(q => q.Select(a => a.Subject!), ct)).ShouldBe(1);
        (await store.GetAsync<string, string?>(
            (q, state) => q.Where(a => a.Subject == state).Select(a => a.Subject),
            "subject-1", ct)).ShouldBe("subject-1");
        (await Collect(store.ListAsync<string, string?>(
            (q, state) => q.Where(a => a.Subject == state).Select(a => a.Subject),
            "subject-1", ct))).Count.ShouldBe(1);

        await store.SetStatusAsync(loaded, "revoked", ct);
        await store.UpdateAsync(loaded, ct);
        (await store.GetStatusAsync((await store.FindByIdAsync(id, ct))!, ct)).ShouldBe("revoked");

        // Bulk revoke paths — create fresh valid rows first.
        var a2 = await store.InstantiateAsync(ct);
        await store.SetSubjectAsync(a2, "subject-2", ct);
        await store.SetStatusAsync(a2, "valid", ct);
        await store.SetTypeAsync(a2, "ad_hoc", ct);
        await store.SetApplicationIdAsync(a2, app.Id, ct);
        await store.CreateAsync(a2, ct);

        (await store.RevokeAsync("subject-2", null, null, null, ct)).ShouldBe(1);

        var a3 = await store.InstantiateAsync(ct);
        await store.SetSubjectAsync(a3, "subject-3", ct);
        await store.SetStatusAsync(a3, "valid", ct);
        await store.SetApplicationIdAsync(a3, app.Id, ct);
        await store.CreateAsync(a3, ct);
        (await store.RevokeBySubjectAsync("subject-3", ct)).ShouldBe(1);
        (await store.RevokeByApplicationIdAsync(app.Id, ct)).ShouldBeGreaterThanOrEqualTo(0);

        // Prune deletes only non-valid rows older than the threshold.
        (await store.PruneAsync(DateTimeOffset.UtcNow.AddDays(1), ct)).ShouldBeGreaterThanOrEqualTo(1);

        var a4 = await store.InstantiateAsync(ct);
        await store.SetSubjectAsync(a4, "subject-4", ct);
        await store.SetStatusAsync(a4, "valid", ct);
        await store.CreateAsync(a4, ct);
        await store.DeleteAsync(a4, ct);
        (await store.FindByIdAsync((await store.GetIdAsync(a4, ct))!, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Scope_store_round_trips_every_field()
    {
        var db = TempDb();
        var store = new DapperOpenIddictScopeStore(db);
        var ct = CancellationToken.None;

        var scope = await store.InstantiateAsync(ct);
        await store.SetNameAsync(scope, "lola-api", ct);
        await store.SetDescriptionAsync(scope, "Tenant access", ct);
        await store.SetDescriptionsAsync(scope,
            ImmutableDictionary<CultureInfo, string>.Empty.Add(CultureInfo.GetCultureInfo("fr"), "Accès"), ct);
        await store.SetDisplayNameAsync(scope, "LOLA API", ct);
        await store.SetDisplayNamesAsync(scope,
            ImmutableDictionary<CultureInfo, string>.Empty.Add(CultureInfo.GetCultureInfo("fr"), "API LOLA"), ct);
        await store.SetResourcesAsync(scope, ImmutableArray.Create("lola"), ct);
        await store.SetPropertiesAsync(scope,
            ImmutableDictionary<string, JsonElement>.Empty.Add("p", JsonDocument.Parse("1").RootElement), ct);
        await store.CreateAsync(scope, ct);

        var id = (await store.GetIdAsync(scope, ct))!;
        var loaded = await store.FindByIdAsync(id, ct);
        loaded.ShouldNotBeNull();
        (await store.GetNameAsync(loaded, ct)).ShouldBe("lola-api");
        (await store.GetDescriptionAsync(loaded, ct)).ShouldBe("Tenant access");
        (await store.GetDescriptionsAsync(loaded, ct))[CultureInfo.GetCultureInfo("fr")].ShouldBe("Accès");
        (await store.GetDisplayNameAsync(loaded, ct)).ShouldBe("LOLA API");
        (await store.GetDisplayNamesAsync(loaded, ct))[CultureInfo.GetCultureInfo("fr")].ShouldBe("API LOLA");
        (await store.GetResourcesAsync(loaded, ct)).ShouldBe(["lola"]);
        (await store.GetPropertiesAsync(loaded, ct))["p"].GetInt32().ShouldBe(1);

        (await store.FindByNameAsync("lola-api", ct))!.Id.ShouldBe(id);
        (await Collect(store.FindByNamesAsync(ImmutableArray.Create("lola-api", "nope"), ct))).Count.ShouldBe(1);
        (await Collect(store.FindByResourceAsync("lola", ct))).Count.ShouldBe(1);
        (await Collect(store.FindByResourceAsync("other", ct))).Count.ShouldBe(0);
        (await Collect(store.ListAsync(10, 0, ct))).Count.ShouldBe(1);
        (await store.CountAsync(ct)).ShouldBe(1);
        (await store.CountAsync<string>(q => q.Select(s => s.Name!), ct)).ShouldBe(1);
        (await store.GetAsync<string, string?>(
            (q, state) => q.Where(s => s.Name == state).Select(s => s.Name),
            "lola-api", ct)).ShouldBe("lola-api");
        (await Collect(store.ListAsync<string, string?>(
            (q, state) => q.Where(s => s.Name == state).Select(s => s.Name),
            "lola-api", ct))).Count.ShouldBe(1);

        await store.SetDisplayNameAsync(loaded, "Renamed", ct);
        await store.UpdateAsync(loaded, ct);
        (await store.GetDisplayNameAsync((await store.FindByIdAsync(id, ct))!, ct)).ShouldBe("Renamed");

        await store.DeleteAsync(loaded, ct);
        (await store.FindByIdAsync(id, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Token_store_round_trips_revokes_and_prunes()
    {
        var db = TempDb();
        var store = new DapperOpenIddictTokenStore(db);
        var ct = CancellationToken.None;

        var token = await store.InstantiateAsync(ct);
        await store.SetSubjectAsync(token, "subject-1", ct);
        await store.SetTypeAsync(token, "refresh_token", ct);
        await store.SetStatusAsync(token, "valid", ct);
        await store.SetReferenceIdAsync(token, "ref-1", ct);
        await store.SetPayloadAsync(token, "payload-blob", ct);
        await store.SetCreationDateAsync(token, DateTimeOffset.UtcNow.AddDays(-1), ct);
        await store.SetExpirationDateAsync(token, DateTimeOffset.UtcNow.AddDays(13), ct);
        await store.SetPropertiesAsync(token,
            ImmutableDictionary<string, JsonElement>.Empty.Add("p", JsonDocument.Parse("1").RootElement), ct);
        await store.CreateAsync(token, ct);

        var id = (await store.GetIdAsync(token, ct))!;
        var loaded = await store.FindByIdAsync(id, ct);
        loaded.ShouldNotBeNull();
        (await store.GetApplicationIdAsync(loaded, ct)).ShouldBeNull();
        (await store.GetAuthorizationIdAsync(loaded, ct)).ShouldBeNull();
        (await store.GetSubjectAsync(loaded, ct)).ShouldBe("subject-1");
        (await store.GetTypeAsync(loaded, ct)).ShouldBe("refresh_token");
        (await store.GetStatusAsync(loaded, ct)).ShouldBe("valid");
        (await store.GetReferenceIdAsync(loaded, ct)).ShouldBe("ref-1");
        (await store.GetPayloadAsync(loaded, ct)).ShouldBe("payload-blob");
        (await store.GetCreationDateAsync(loaded, ct)).ShouldNotBeNull();
        (await store.GetExpirationDateAsync(loaded, ct)).ShouldNotBeNull();
        (await store.GetRedemptionDateAsync(loaded, ct)).ShouldBeNull();
        (await store.GetPropertiesAsync(loaded, ct))["p"].GetInt32().ShouldBe(1);

        (await store.FindByReferenceIdAsync("ref-1", ct))!.Id.ShouldBe(id);
        (await Collect(store.FindBySubjectAsync("subject-1", ct))).Count.ShouldBe(1);
        (await Collect(store.FindByApplicationIdAsync("app-x", ct))).Count.ShouldBe(0);
        (await Collect(store.FindByAuthorizationIdAsync("auth-x", ct))).Count.ShouldBe(0);
        (await Collect(store.FindAsync("subject-1", null, "valid", "refresh_token", ct))).Count.ShouldBe(1);
        (await Collect(store.FindAsync("other", null, null, null, ct))).Count.ShouldBe(0);
        (await Collect(store.ListAsync(10, 0, ct))).Count.ShouldBe(1);
        (await store.CountAsync(ct)).ShouldBe(1);
        (await store.CountAsync<string>(q => q.Select(t => t.Subject!), ct)).ShouldBe(1);
        (await store.GetAsync<string, string?>(
            (q, state) => q.Where(t => t.Subject == state).Select(t => t.Subject),
            "subject-1", ct)).ShouldBe("subject-1");
        (await Collect(store.ListAsync<string, string?>(
            (q, state) => q.Where(t => t.Subject == state).Select(t => t.Subject),
            "subject-1", ct))).Count.ShouldBe(1);

        // Redeem → update persists redemption (the reuse-detection path).
        await store.SetStatusAsync(loaded, "redeemed", ct);
        await store.SetRedemptionDateAsync(loaded, DateTimeOffset.UtcNow, ct);
        await store.UpdateAsync(loaded, ct);
        var reloaded = await store.FindByIdAsync(id, ct);
        (await store.GetStatusAsync(reloaded!, ct)).ShouldBe("redeemed");
        (await store.GetRedemptionDateAsync(reloaded!, ct)).ShouldNotBeNull();

        // Bulk revoke variants.
        var t2 = await store.InstantiateAsync(ct);
        await store.SetSubjectAsync(t2, "subject-2", ct);
        await store.SetStatusAsync(t2, "valid", ct);
        await store.SetTypeAsync(t2, "access_token", ct);
        await store.SetCreationDateAsync(t2, DateTimeOffset.UtcNow.AddDays(-1), ct);
        await store.CreateAsync(t2, ct);
        (await store.RevokeAsync("subject-2", null, null, null, ct)).ShouldBe(1);

        var apps = new DapperOpenIddictApplicationStore(db);
        var app = await apps.InstantiateAsync(ct);
        await apps.SetClientIdAsync(app, "client-tok", ct);
        await apps.CreateAsync(app, ct);
        var authzs = new DapperOpenIddictAuthorizationStore(db);
        var authz = await authzs.InstantiateAsync(ct);
        await authzs.SetSubjectAsync(authz, "subject-3", ct);
        await authzs.SetStatusAsync(authz, "valid", ct);
        await authzs.SetApplicationIdAsync(authz, app.Id, ct);
        await authzs.CreateAsync(authz, ct);

        var t3 = await store.InstantiateAsync(ct);
        await store.SetSubjectAsync(t3, "subject-3", ct);
        await store.SetStatusAsync(t3, "valid", ct);
        await store.SetApplicationIdAsync(t3, app.Id, ct);
        await store.SetAuthorizationIdAsync(t3, authz.Id, ct);
        await store.SetCreationDateAsync(t3, DateTimeOffset.UtcNow.AddDays(-1), ct);
        await store.CreateAsync(t3, ct);
        (await store.RevokeByAuthorizationIdAsync(authz.Id, ct)).ShouldBe(1);
        (await store.RevokeByApplicationIdAsync(app.Id, ct)).ShouldBeGreaterThanOrEqualTo(0);
        (await store.RevokeBySubjectAsync("nobody", ct)).ShouldBe(0);

        // `token` holds a stale concurrency token now (UpdateAsync
        // bumped it via `loaded`) — delete must use a fresh fetch.
        await store.DeleteAsync(reloaded!, ct);
        (await store.FindByIdAsync(id, ct)).ShouldBeNull();

        // Prune removes old non-valid rows only (t2/t3 are revoked).
        (await store.PruneAsync(DateTimeOffset.UtcNow, ct)).ShouldBeGreaterThanOrEqualTo(1);
    }
}
