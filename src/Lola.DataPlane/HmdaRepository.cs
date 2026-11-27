using Lola.Application.Compliance;

namespace Lola.DataPlane;

/// HMDA store (M24 P2, MPE-313): append-only `lar_rows` +
/// `edit_check_runs` + `lar_snapshots`(+rows), all in the tenant
/// file. LAR rows are never updated — re-assembly inserts the next
/// row of the lineage; freezes only ever insert.
public sealed class HmdaRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IHmdaRepository
{
    private static string Now() => DateTimeOffset.UtcNow
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public async Task<LarRowRecord> AddLarRowAsync(string appId,
        int reportingYear, string uli, string rowJson,
        string derivedJson, string actor, CancellationToken ct)
    {
        var larRowId = $"lar-{Guid.NewGuid():N}";
        var created = Now();
        await ExecuteAsync(
            """
            INSERT INTO lar_rows
                (lar_row_id, application_id, reporting_year, uli,
                 row_json, derived_json, actor, created_utc)
            VALUES (@larRowId, @appId, @reportingYear, @uli,
                    @rowJson, @derivedJson, @actor, @created)
            """,
            new
            {
                larRowId,
                appId,
                reportingYear,
                uli,
                rowJson,
                derivedJson,
                actor,
                created
            }, ct);
        return new LarRowRecord(larRowId, appId, reportingYear, uli,
            rowJson, derivedJson, actor, created);
    }

    private const string LarRowColumns = """
        lar_row_id AS LarRowId, application_id AS ApplicationId,
        reporting_year AS ReportingYear, uli AS Uli,
        row_json AS RowJson, derived_json AS DerivedJson,
        actor AS Actor, created_utc AS CreatedUtc
        """;

    public async Task<IReadOnlyList<LarRowRecord>> ListLarRowsAsync(
        string appId, CancellationToken ct)
        => await QueryAsync<LarRowRecord>(
            $"""
            SELECT {LarRowColumns} FROM lar_rows
            WHERE application_id=@appId
            ORDER BY created_utc DESC, rowid DESC
            """, new { appId }, ct);

    public async Task<IReadOnlyList<LarRowRecord>>
        ListLarRowsByYearAsync(int reportingYear, CancellationToken ct)
        => await QueryAsync<LarRowRecord>(
            $"""
            SELECT {LarRowColumns} FROM lar_rows
            WHERE reporting_year=@reportingYear
            ORDER BY created_utc DESC, rowid DESC
            """, new { reportingYear }, ct);

    public async Task AddEditRunAsync(string larRowId, string tier,
        string editId, string result, string detail,
        CancellationToken ct)
    {
        await ExecuteAsync(
            """
            INSERT INTO edit_check_runs
                (run_id, lar_row_id, tier, edit_id, result, detail,
                 created_utc)
            VALUES (@runId, @larRowId, @tier, @editId, @result,
                    @detail, @created)
            """,
            new
            {
                runId = $"edit-{Guid.NewGuid():N}",
                larRowId,
                tier,
                editId,
                result,
                detail,
                created = Now()
            }, ct);
        await Task.CompletedTask;
    }

    public async Task<IReadOnlyList<EditRunRecord>> ListEditRunsAsync(
        string larRowId, CancellationToken ct)
        => await QueryAsync<EditRunRecord>(
            """
            SELECT run_id AS RunId, lar_row_id AS LarRowId,
                   tier AS Tier, edit_id AS EditId, result AS Result,
                   detail AS Detail, created_utc AS CreatedUtc
            FROM edit_check_runs
            WHERE lar_row_id=@larRowId
            ORDER BY rowid
            """, new { larRowId }, ct);

    public async Task<LarSnapshotRecord> AddSnapshotAsync(
        int reportingYear, IReadOnlyList<string> larRowIds,
        string actor, CancellationToken ct)
    {
        var snapshotId = $"snap-{Guid.NewGuid():N}";
        var created = Now();
        await ExecuteAsync(
            """
            INSERT INTO lar_snapshots
                (snapshot_id, reporting_year, row_count, actor,
                 created_utc)
            VALUES (@snapshotId, @reportingYear, @rowCount, @actor,
                    @created)
            """,
            new
            {
                snapshotId,
                reportingYear,
                rowCount = larRowIds.Count,
                actor,
                created
            }, ct);
        foreach (var rowId in larRowIds)
        {
            await ExecuteAsync(
                """
                INSERT INTO lar_snapshot_rows (snapshot_id, lar_row_id)
                VALUES (@snapshotId, @rowId)
                """,
                new { snapshotId, rowId }, ct);
        }
        return new LarSnapshotRecord(snapshotId, reportingYear,
            larRowIds.Count, actor, created);
    }

    public async Task<IReadOnlyList<LarSnapshotRecord>>
        ListSnapshotsAsync(CancellationToken ct)
        => await QueryAsync<LarSnapshotRecord>(
            """
            SELECT snapshot_id AS SnapshotId,
                   reporting_year AS ReportingYear,
                   row_count AS RowCount, actor AS Actor,
                   created_utc AS CreatedUtc
            FROM lar_snapshots
            ORDER BY created_utc DESC, rowid DESC
            """, new { }, ct);
}
