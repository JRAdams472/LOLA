using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Lola.Application.Security;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace Lola.ControlPlane.OpenIddict;

/// Custom Dapper IOpenIddictAuthorizationStore (ADR-0005). The
/// `properties` column holds `control-plane`-domain ciphertext
/// (MPE-286); encryption happens at the write boundary.
public sealed class DapperOpenIddictAuthorizationStore(ControlPlaneDb db, IFieldCipher cipher)
    : IOpenIddictAuthorizationStore<LolaAuthorization>
{
    private const string Columns =
        "id, application_id AS ApplicationId, creation_date AS CreationDate, " +
        "properties, scopes, status, subject, type, " +
        "concurrency_token AS ConcurrencyToken";

    /// Generic query paths evaluate LINQ predicates in memory — the
    /// entity must carry DECRYPTED fields or predicates compare
    /// ciphertext against plaintext and silently miss (MPE-286).
    private async Task<IQueryable<LolaAuthorization>> AllAsync(CancellationToken ct)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaAuthorization>(
            new CommandDefinition($"SELECT {Columns} FROM openiddict_authorizations",
                cancellationToken: ct));
        var list = rows.ToList();
        foreach (var row in list)
            await DecryptAsync(row, ct);
        return list.AsQueryable();
    }

    private async Task DecryptAsync(LolaAuthorization authorization, CancellationToken ct)
        => authorization.Properties = await CpCrypto.DecryptGlobalAsync(
            cipher, authorization.Properties, ct);

    public ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return new(conn.ExecuteScalar<long>("SELECT count(*) FROM openiddict_authorizations"));
    }

    public async ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<LolaAuthorization>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        => query(await AllAsync(cancellationToken)).LongCount();

    public async ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<LolaAuthorization>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken)
        => query(await AllAsync(cancellationToken), state).FirstOrDefault();

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<LolaAuthorization>, TState, IQueryable<TResult>> query,
        TState state, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in query(await AllAsync(cancellationToken), state))
            yield return item;
    }

    public async IAsyncEnumerable<LolaAuthorization> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaAuthorization>(
            $"SELECT {Columns} FROM openiddict_authorizations ORDER BY id LIMIT @count OFFSET @offset",
            new { count = count ?? int.MaxValue, offset = offset ?? 0 });
        foreach (var row in rows)
        {
            await DecryptAsync(row, cancellationToken);
            yield return row;
        }
    }

    public async ValueTask<LolaAuthorization?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<LolaAuthorization>(
            $"SELECT {Columns} FROM openiddict_authorizations WHERE id = @identifier",
            new { identifier });
        if (row is not null) await DecryptAsync(row, cancellationToken);
        return row;
    }

    public async IAsyncEnumerable<LolaAuthorization> FindBySubjectAsync(
        string subject, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaAuthorization>(
            $"SELECT {Columns} FROM openiddict_authorizations WHERE subject = @subject",
            new { subject });
        foreach (var row in rows)
        {
            await DecryptAsync(row, cancellationToken);
            yield return row;
        }
    }

    public async IAsyncEnumerable<LolaAuthorization> FindByApplicationIdAsync(
        string identifier, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaAuthorization>(
            $"SELECT {Columns} FROM openiddict_authorizations WHERE application_id = @identifier",
            new { identifier });
        foreach (var row in rows)
        {
            await DecryptAsync(row, cancellationToken);
            yield return row;
        }
    }

    /// Composite lookup: every provided filter narrows the query; a
    /// scopes filter requires ALL listed scopes to be present in the
    /// authorization's JSON array.
    public async IAsyncEnumerable<LolaAuthorization> FindAsync(
        string? subject, string? client, string? status, string? type,
        ImmutableArray<string>? scopes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var sql = $"SELECT {Columns} FROM openiddict_authorizations WHERE 1=1";
        if (subject is not null) sql += " AND subject = @subject";
        if (client is not null) sql += " AND application_id = @client";
        if (status is not null) sql += " AND status = @status";
        if (type is not null) sql += " AND type = @type";
        if (scopes is { } s)
            for (var i = 0; i < s.Length; i++)
                sql += $" AND EXISTS (SELECT 1 FROM json_each(scopes) WHERE value = @s{i})";

        var p = new DynamicParameters();
        p.Add("subject", subject); p.Add("client", client);
        p.Add("status", status); p.Add("type", type);
        if (scopes is { } arr)
            for (var i = 0; i < arr.Length; i++) p.Add($"s{i}", arr[i]);

        var rows = await conn.QueryAsync<LolaAuthorization>(sql, p);
        foreach (var row in rows)
        {
            await DecryptAsync(row, cancellationToken);
            yield return row;
        }
    }

    // --- getters ------------------------------------------------------

    public ValueTask<LolaAuthorization> InstantiateAsync(CancellationToken cancellationToken)
        => new(new LolaAuthorization());

    public ValueTask<string?> GetIdAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(authorization.Id);

    public ValueTask<string?> GetApplicationIdAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(authorization.ApplicationId);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(OidcJson.TextToDate(authorization.CreationDate));

    /// DB-materialized entities carry ciphertext; in-flight entities
    /// hold JSON — decrypt only the vault-envelope shape.
    public async ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
        LolaAuthorization authorization, CancellationToken cancellationToken)
    {
        var json = authorization.Properties;
        if (json is not null && json.StartsWith("vault:", StringComparison.Ordinal))
            json = await CpCrypto.DecryptGlobalAsync(cipher, json, cancellationToken);
        return OidcJson.JsonToProperties(json);
    }

    public ValueTask<ImmutableArray<string>> GetScopesAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(OidcJson.JsonToArray(authorization.Scopes));

    public ValueTask<string?> GetStatusAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(authorization.Status);

    public ValueTask<string?> GetSubjectAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(authorization.Subject);

    public ValueTask<string?> GetTypeAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
        => new(authorization.Type);

    // --- setters --------------------------------------------------------

    public ValueTask SetApplicationIdAsync(LolaAuthorization authorization, string? identifier, CancellationToken cancellationToken)
    {
        authorization.ApplicationId = identifier;
        return default;
    }

    public ValueTask SetCreationDateAsync(LolaAuthorization authorization, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        authorization.CreationDate = OidcJson.DateToText(date);
        return default;
    }

    public ValueTask SetPropertiesAsync(LolaAuthorization authorization,
        ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
    {
        authorization.Properties = OidcJson.PropertiesToJson(properties);
        return default;
    }

    public ValueTask SetScopesAsync(LolaAuthorization authorization,
        ImmutableArray<string> scopes, CancellationToken cancellationToken)
    {
        authorization.Scopes = OidcJson.ArrayToJson(scopes);
        return default;
    }

    public ValueTask SetStatusAsync(LolaAuthorization authorization, string? status, CancellationToken cancellationToken)
    {
        authorization.Status = status;
        return default;
    }

    public ValueTask SetSubjectAsync(LolaAuthorization authorization, string? subject, CancellationToken cancellationToken)
    {
        authorization.Subject = subject;
        return default;
    }

    public ValueTask SetTypeAsync(LolaAuthorization authorization, string? type, CancellationToken cancellationToken)
    {
        authorization.Type = type;
        return default;
    }

    // --- writes --------------------------------------------------------

    public async ValueTask CreateAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
    {
        authorization.Id = string.IsNullOrEmpty(authorization.Id)
            ? Guid.NewGuid().ToString("N")
            : authorization.Id;
        authorization.ConcurrencyToken = OidcJson.NewConcurrencyToken();
        var properties = await CpCrypto.EncryptGlobalAsync(
            cipher, authorization.Properties, cancellationToken);
        using var conn = db.Open();
        await conn.ExecuteAsync("""
            INSERT INTO openiddict_authorizations
                (id, application_id, creation_date, properties, scopes,
                 status, subject, type, concurrency_token)
            VALUES (@Id, @ApplicationId, @CreationDate, @Properties, @Scopes,
                 @Status, @Subject, @Type, @ConcurrencyToken)
            """, new
        {
            authorization.Id,
            authorization.ApplicationId,
            authorization.CreationDate,
            Properties = properties,
            authorization.Scopes,
            authorization.Status,
            authorization.Subject,
            authorization.Type,
            authorization.ConcurrencyToken
        });
    }

    public async ValueTask UpdateAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
    {
        var properties = await CpCrypto.EncryptGlobalAsync(
            cipher, authorization.Properties, cancellationToken);
        using var conn = db.Open();
        var token = OidcJson.NewConcurrencyToken();
        var affected = await conn.ExecuteAsync("""
            UPDATE openiddict_authorizations SET
                application_id = @ApplicationId, creation_date = @CreationDate,
                properties = @Properties, scopes = @Scopes, status = @Status,
                subject = @Subject, type = @Type, concurrency_token = @token
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, new
        {
            authorization.ApplicationId,
            authorization.CreationDate,
            Properties = properties,
            authorization.Scopes,
            authorization.Status,
            authorization.Subject,
            authorization.Type,
            authorization.Id,
            authorization.ConcurrencyToken,
            token
        });
        if (affected == 0)
            throw new ConcurrencyException("The authorization was modified concurrently.");
        authorization.ConcurrencyToken = token;
    }

    public async ValueTask DeleteAsync(LolaAuthorization authorization, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var affected = await conn.ExecuteAsync("""
            DELETE FROM openiddict_authorizations
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, authorization);
        if (affected == 0)
            throw new ConcurrencyException("The authorization was modified concurrently.");
    }

    /// Bulk revoke by filters — every provided field narrows the set;
    /// returns the number of authorizations actually revoked.
    public async ValueTask<long> RevokeAsync(
        string? subject, string? client, string? status, string? type,
        CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var sql = "UPDATE openiddict_authorizations SET status = 'revoked' WHERE status = 'valid'";
        if (subject is not null) sql += " AND subject = @subject";
        if (client is not null) sql += " AND application_id = @client";
        if (status is not null) sql += " AND status = @status";
        if (type is not null) sql += " AND type = @type";
        return await conn.ExecuteAsync(sql, new { subject, client, status, type });
    }

    public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync("""
            UPDATE openiddict_authorizations SET status = 'revoked'
            WHERE application_id = @identifier AND status = 'valid'
            """, new { identifier });
    }

    public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync("""
            UPDATE openiddict_authorizations SET status = 'revoked'
            WHERE subject = @subject AND status = 'valid'
            """, new { subject });
    }

    /// Hard-delete authorizations created before the threshold — the
    /// pruning sweep (no pruning job runs yet; the method is correct
    /// for when one lands). Returns the deleted count.
    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync("""
            DELETE FROM openiddict_authorizations
            WHERE creation_date < @threshold
              AND status != 'valid'
            """, new { threshold = OidcJson.DateToText(threshold) });
    }
}
