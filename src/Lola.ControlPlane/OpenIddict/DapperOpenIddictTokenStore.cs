using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace Lola.ControlPlane.OpenIddict;

/// Custom Dapper IOpenIddictTokenStore (ADR-0005).
public sealed class DapperOpenIddictTokenStore(ControlPlaneDb db)
    : IOpenIddictTokenStore<LolaToken>
{
    private const string Columns =
        "id, application_id AS ApplicationId, authorization_id AS AuthorizationId, " +
        "creation_date AS CreationDate, expiration_date AS ExpirationDate, " +
        "payload, properties, redemption_date AS RedemptionDate, " +
        "reference_id AS ReferenceId, status, subject, type, " +
        "concurrency_token AS ConcurrencyToken";

    private IQueryable<LolaToken> All()
    {
        using var conn = db.Open();
        return conn.Query<LolaToken>($"SELECT {Columns} FROM openiddict_tokens")
            .AsQueryable();
    }

    public ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return new(conn.ExecuteScalar<long>("SELECT count(*) FROM openiddict_tokens"));
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<LolaToken>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        => new(query(All()).LongCount());

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<LolaToken>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken)
        => new(query(All(), state).FirstOrDefault());

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<LolaToken>, TState, IQueryable<TResult>> query,
        TState state, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in query(All(), state))
            yield return item;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<LolaToken> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaToken>(
            $"SELECT {Columns} FROM openiddict_tokens ORDER BY id LIMIT @count OFFSET @offset",
            new { count = count ?? int.MaxValue, offset = offset ?? 0 });
        foreach (var row in rows) yield return row;
    }

    public async ValueTask<LolaToken?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<LolaToken>(
            $"SELECT {Columns} FROM openiddict_tokens WHERE id = @identifier",
            new { identifier });
    }

    public async ValueTask<LolaToken?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<LolaToken>(
            $"SELECT {Columns} FROM openiddict_tokens WHERE reference_id = @identifier",
            new { identifier });
    }

    public async IAsyncEnumerable<LolaToken> FindBySubjectAsync(
        string subject, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaToken>(
            $"SELECT {Columns} FROM openiddict_tokens WHERE subject = @subject",
            new { subject });
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<LolaToken> FindByApplicationIdAsync(
        string identifier, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaToken>(
            $"SELECT {Columns} FROM openiddict_tokens WHERE application_id = @identifier",
            new { identifier });
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<LolaToken> FindByAuthorizationIdAsync(
        string identifier, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaToken>(
            $"SELECT {Columns} FROM openiddict_tokens WHERE authorization_id = @identifier",
            new { identifier });
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<LolaToken> FindAsync(
        string? subject, string? client, string? status, string? type,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var sql = $"SELECT {Columns} FROM openiddict_tokens WHERE 1=1";
        if (subject is not null) sql += " AND subject = @subject";
        if (client is not null) sql += " AND application_id = @client";
        if (status is not null) sql += " AND status = @status";
        if (type is not null) sql += " AND type = @type";
        var rows = await conn.QueryAsync<LolaToken>(sql, new { subject, client, status, type });
        foreach (var row in rows) yield return row;
    }

    // --- getters ------------------------------------------------------

    public ValueTask<LolaToken> InstantiateAsync(CancellationToken cancellationToken)
        => new(new LolaToken());

    public ValueTask<string?> GetIdAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.Id);

    public ValueTask<string?> GetApplicationIdAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.ApplicationId);

    public ValueTask<string?> GetAuthorizationIdAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.AuthorizationId);

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(LolaToken token, CancellationToken cancellationToken)
        => new(OidcJson.TextToDate(token.CreationDate));

    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(LolaToken token, CancellationToken cancellationToken)
        => new(OidcJson.TextToDate(token.ExpirationDate));

    public ValueTask<string?> GetPayloadAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.Payload);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(LolaToken token, CancellationToken cancellationToken)
        => new(OidcJson.JsonToProperties(token.Properties));

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(LolaToken token, CancellationToken cancellationToken)
        => new(OidcJson.TextToDate(token.RedemptionDate));

    public ValueTask<string?> GetReferenceIdAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.ReferenceId);

    public ValueTask<string?> GetStatusAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.Status);

    public ValueTask<string?> GetSubjectAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.Subject);

    public ValueTask<string?> GetTypeAsync(LolaToken token, CancellationToken cancellationToken)
        => new(token.Type);

    // --- setters --------------------------------------------------------

    public ValueTask SetApplicationIdAsync(LolaToken token, string? identifier, CancellationToken cancellationToken)
    {
        token.ApplicationId = identifier;
        return default;
    }

    public ValueTask SetAuthorizationIdAsync(LolaToken token, string? identifier, CancellationToken cancellationToken)
    {
        token.AuthorizationId = identifier;
        return default;
    }

    public ValueTask SetCreationDateAsync(LolaToken token, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        token.CreationDate = OidcJson.DateToText(date);
        return default;
    }

    public ValueTask SetExpirationDateAsync(LolaToken token, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        token.ExpirationDate = OidcJson.DateToText(date);
        return default;
    }

    public ValueTask SetPayloadAsync(LolaToken token, string? payload, CancellationToken cancellationToken)
    {
        token.Payload = payload;
        return default;
    }

    public ValueTask SetPropertiesAsync(LolaToken token,
        ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
    {
        token.Properties = OidcJson.PropertiesToJson(properties);
        return default;
    }

    public ValueTask SetRedemptionDateAsync(LolaToken token, DateTimeOffset? date, CancellationToken cancellationToken)
    {
        token.RedemptionDate = OidcJson.DateToText(date);
        return default;
    }

    public ValueTask SetReferenceIdAsync(LolaToken token, string? identifier, CancellationToken cancellationToken)
    {
        token.ReferenceId = identifier;
        return default;
    }

    public ValueTask SetStatusAsync(LolaToken token, string? status, CancellationToken cancellationToken)
    {
        token.Status = status;
        return default;
    }

    public ValueTask SetSubjectAsync(LolaToken token, string? subject, CancellationToken cancellationToken)
    {
        token.Subject = subject;
        return default;
    }

    public ValueTask SetTypeAsync(LolaToken token, string? type, CancellationToken cancellationToken)
    {
        token.Type = type;
        return default;
    }

    // --- writes --------------------------------------------------------

    public async ValueTask CreateAsync(LolaToken token, CancellationToken cancellationToken)
    {
        token.Id = string.IsNullOrEmpty(token.Id) ? Guid.NewGuid().ToString("N") : token.Id;
        token.ConcurrencyToken = OidcJson.NewConcurrencyToken();
        using var conn = db.Open();
        await conn.ExecuteAsync("""
            INSERT INTO openiddict_tokens
                (id, application_id, authorization_id, creation_date,
                 expiration_date, payload, properties, redemption_date,
                 reference_id, status, subject, type, concurrency_token)
            VALUES (@Id, @ApplicationId, @AuthorizationId, @CreationDate,
                 @ExpirationDate, @Payload, @Properties, @RedemptionDate,
                 @ReferenceId, @Status, @Subject, @Type, @ConcurrencyToken)
            """, token);
    }

    public async ValueTask UpdateAsync(LolaToken token, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var ct = OidcJson.NewConcurrencyToken();
        var affected = await conn.ExecuteAsync("""
            UPDATE openiddict_tokens SET
                application_id = @ApplicationId, authorization_id = @AuthorizationId,
                creation_date = @CreationDate, expiration_date = @ExpirationDate,
                payload = @Payload, properties = @Properties,
                redemption_date = @RedemptionDate, reference_id = @ReferenceId,
                status = @Status, subject = @Subject, type = @Type,
                concurrency_token = @ct
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, new
        {
            token.ApplicationId,
            token.AuthorizationId,
            token.CreationDate,
            token.ExpirationDate,
            token.Payload,
            token.Properties,
            token.RedemptionDate,
            token.ReferenceId,
            token.Status,
            token.Subject,
            token.Type,
            token.Id,
            token.ConcurrencyToken,
            ct
        });
        if (affected == 0)
            throw new ConcurrencyException("The token was modified concurrently.");
        token.ConcurrencyToken = ct;
    }

    public async ValueTask DeleteAsync(LolaToken token, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var affected = await conn.ExecuteAsync("""
            DELETE FROM openiddict_tokens
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, token);
        if (affected == 0)
            throw new ConcurrencyException("The token was modified concurrently.");
    }

    /// Bulk revoke by filters; returns the number of tokens revoked.
    public async ValueTask<long> RevokeAsync(
        string? subject, string? client, string? status, string? type,
        CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var sql = "UPDATE openiddict_tokens SET status = 'revoked' WHERE status = 'valid'";
        if (subject is not null) sql += " AND subject = @subject";
        if (client is not null) sql += " AND application_id = @client";
        if (status is not null) sql += " AND status = @status";
        if (type is not null) sql += " AND type = @type";
        return await conn.ExecuteAsync(sql, new { subject, client, status, type });
    }

    public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync(
            "UPDATE openiddict_tokens SET status = 'revoked' WHERE application_id = @identifier",
            new { identifier });
    }

    public async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync(
            "UPDATE openiddict_tokens SET status = 'revoked' WHERE authorization_id = @identifier",
            new { identifier });
    }

    public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync(
            "UPDATE openiddict_tokens SET status = 'revoked' WHERE subject = @subject",
            new { subject });
    }

    /// Returns the number of tokens pruned.
    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync("""
            DELETE FROM openiddict_tokens
            WHERE creation_date < @threshold
              AND status != 'valid'
            """, new { threshold = OidcJson.DateToText(threshold) });
    }
}
