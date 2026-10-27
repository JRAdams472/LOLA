using System.Data;
using System.Reflection;
using Dapper;
using Lola.Application.Security;
using Lola.Domain.Classification;

namespace Lola.Infrastructure.Mapping;

/// Enforces the PII cipher boundary at the repository edge (ADR-0002/
/// -0004). `[PiiClass]`-annotated string members are ciphertext on the
/// way in and plaintext on the way out; `Searchable` members also emit a
/// `<name>Idx` blind-index parameter for equality search.
///
/// This is a call-site wrapper rather than a registered
/// `SqlMapper.ITypeMap` on purpose: Dapper type maps are metadata-only
/// (generated IL touches PropertyInfo directly) and `TypeHandler` is
/// synchronous — transit crypto is async. Explicit Protect/Unprotect
/// keeps the boundary visible and impossible to forget at the call site.
public static class PiiProtection
{
    /// Row object → Dapper parameters. Non-PII members pass through by
    /// name; `[PiiClass]` string members become `vault:` ciphertext;
    /// `Searchable` ones additionally produce `<Name>Idx` — so SQL uses
    /// `@Ssn` for the ciphertext column and `@SsnIdx` for the index.
    public static async Task<DynamicParameters> ToParametersAsync<T>(
        T row, IFieldCipher cipher, IBlindIndex index, CancellationToken ct = default) where T : notnull
    {
        var parameters = new DynamicParameters();
        var pii = new List<(PropertyInfo Prop, PiiClassAttribute Attr, string Value)>();

        foreach (var prop in typeof(T).GetProperties())
        {
            if (!prop.CanRead) continue;
            var attr = prop.GetCustomAttribute<PiiClassAttribute>();
            var value = prop.GetValue(row);
            if (attr is null)
            {
                parameters.Add(prop.Name, value);
            }
            else if (value is string s && s.Length > 0)
            {
                pii.Add((prop, attr, s));
            }
            else
            {
                // Null/empty PII stays null — no ciphertext, no index.
                parameters.Add(prop.Name, null);
                if (attr.Searchable) parameters.Add(prop.Name + "Idx", null);
            }
        }

        var ciphertexts = pii.Count == 0
            ? []
            : await cipher.EncryptAsync(pii.Select(p => p.Value).ToList(), ct);

        for (var i = 0; i < pii.Count; i++)
        {
            var (prop, attr, value) = pii[i];
            parameters.Add(prop.Name, ciphertexts[i]);
            if (attr.Searchable)
                parameters.Add(prop.Name + "Idx",
                    await index.IndexAsync(attr.Class, value, ct));
        }

        return parameters;
    }

    /// Decrypt `[PiiClass]` string members on materialized rows —
    /// batched across the whole result set (one transit round-trip).
    public static async Task<List<T>> UnprotectAsync<T>(
        IEnumerable<T> rows, IFieldCipher cipher, CancellationToken ct = default)
    {
        var list = rows.ToList();
        var piiProps = typeof(T).GetProperties()
            .Where(p => p.CanWrite && p.PropertyType == typeof(string) &&
                        p.GetCustomAttribute<PiiClassAttribute>() is not null)
            .ToList();
        if (piiProps.Count == 0 || list.Count == 0) return list;

        var pending = list
            .SelectMany(row => piiProps
                .Select(p => (Row: row, Prop: p, Ct: p.GetValue(row) as string))
                .Where(x => !string.IsNullOrEmpty(x.Ct)))
            .ToList();
        if (pending.Count == 0) return list;

        var plaintexts = await cipher.DecryptAsync(
            pending.Select(p => p.Ct!).ToList(), ct);
        for (var i = 0; i < pending.Count; i++)
            pending[i].Prop.SetValue(pending[i].Row, plaintexts[i]);
        return list;
    }

    /// Dapper query + transparent decrypt — the read half of the
    /// boundary. `await conn.QueryPiiAsync<Borrower>(sql, p, cipher)`.
    public static async Task<IEnumerable<T>> QueryPiiAsync<T>(
        this IDbConnection connection, string sql, object? param,
        IFieldCipher cipher, CancellationToken ct = default)
    {
        var rows = await connection.QueryAsync<T>(
            new CommandDefinition(sql, param, cancellationToken: ct));
        return await UnprotectAsync(rows, cipher, ct);
    }
}
