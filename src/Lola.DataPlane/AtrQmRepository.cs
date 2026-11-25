using Lola.Application.Compliance;

namespace Lola.DataPlane;

/// ATR/QM store (M22 P2, MPE-307): `atr_results` append-only rows +
/// the `apor_rates`/`pf_classes`/`state_high_cost_rules` registries,
/// all in the tenant file. Result rows are never updated — a
/// re-evaluation inserts the next row of the lineage.
public sealed class AtrQmRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IAtrQmRepository
{
    public async Task<AtrResultRecord> AddResultAsync(string appId,
        string? evalId, decimal? dti, decimal? apr, decimal? apor,
        string verdictsJson, string actor, CancellationToken ct)
    {
        var resultId = $"atr-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await ExecuteAsync(
            """
            INSERT INTO atr_results
                (result_id, application_id, eval_id, dti, apr, apor,
                 verdicts_json, actor, created_utc)
            VALUES (@resultId, @appId, @evalId, @dti, @apr, @apor,
                    @verdicts, @actor, @created)
            """,
            new
            {
                resultId,
                appId,
                evalId,
                dti,
                apr,
                apor,
                verdicts = verdictsJson,
                actor,
                created
            }, ct);
        return new AtrResultRecord(resultId, appId, evalId, dti, apr,
            apor, verdictsJson, actor, created);
    }

    private const string ResultColumns = """
        result_id AS ResultId, application_id AS ApplicationId,
        eval_id AS EvalId, dti AS Dti, apr AS Apr, apor AS Apor,
        verdicts_json AS VerdictsJson, actor AS Actor,
        created_utc AS CreatedUtc
        """;

    public async Task<IReadOnlyList<AtrResultRecord>> ListResultsAsync(
        string appId, CancellationToken ct)
        => await QueryAsync<AtrResultRecord>(
            $"""
            SELECT {ResultColumns} FROM atr_results
            WHERE application_id=@appId
            ORDER BY created_utc DESC, rowid DESC
            """, new { appId }, ct);

    public async Task<AporRateRecord?> GetAporAsync(CancellationToken ct)
        => await QuerySingleOrDefaultAsync<AporRateRecord>(
            """
            SELECT effective_date AS EffectiveDate, apor AS Apor
            FROM apor_rates
            WHERE effective_date <= date('now')
            ORDER BY effective_date DESC LIMIT 1
            """, null, ct);

    public async Task<IReadOnlyDictionary<string, PfClassRecord>>
        GetPfClassesAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<PfClassRecord>(
            """
            SELECT fee_code AS FeeCode, category AS Category,
                   notes AS Notes
            FROM pf_classes
            """, null, ct);
        return rows.ToDictionary(r => r.FeeCode,
            StringComparer.Ordinal);
    }

    public async Task<StateHighCostRecord?> GetStateRuleAsync(
        string stateCode, CancellationToken ct)
        => await QuerySingleOrDefaultAsync<StateHighCostRecord>(
            """
            SELECT state_code AS StateCode, rate_spread AS RateSpread,
                   pf_pct AS PfPct, pf_floor AS PfFloor, notes AS Notes
            FROM state_high_cost_rules
            WHERE state_code=@stateCode COLLATE NOCASE
            """, new { stateCode }, ct);
}
