namespace Lola.Application.Compliance;

/// HMDA persistence seam (M24 P2, MPE-313). Rows live in the tenant
/// file — `lar_rows` is the append-only assembled-LAR read surface
/// (never updated; re-assembly inserts), `edit_check_runs` the
/// per-row edit results, `lar_snapshots`(+rows) the reporting-year
/// freezes. REAL columns materialize as `double` per the M22 Dapper
/// lesson — though this surface is JSON-heavy and carries no REALs.
public interface IHmdaRepository
{
    /// Insert one append-only assembled LAR row.
    Task<LarRowRecord> AddLarRowAsync(string appId, int reportingYear,
        string uli, string rowJson, string derivedJson, string actor,
        CancellationToken ct);

    Task<IReadOnlyList<LarRowRecord>> ListLarRowsAsync(string appId,
        CancellationToken ct);

    /// Rows for a reporting year across the whole tenant file
    /// (export + freeze source), newest first per application.
    Task<IReadOnlyList<LarRowRecord>> ListLarRowsByYearAsync(
        int reportingYear, CancellationToken ct);

    /// Insert one edit-check result row per battery edit.
    Task AddEditRunAsync(string larRowId, string tier, string editId,
        string result, string detail, CancellationToken ct);

    Task<IReadOnlyList<EditRunRecord>> ListEditRunsAsync(
        string larRowId, CancellationToken ct);

    /// Append a reporting-year freeze + its row mappings in one
    /// logical write. Re-freeze inserts a NEW snapshot row — prior
    /// freezes never change shape.
    Task<LarSnapshotRecord> AddSnapshotAsync(int reportingYear,
        IReadOnlyList<string> larRowIds, string actor,
        CancellationToken ct);

    Task<IReadOnlyList<LarSnapshotRecord>> ListSnapshotsAsync(
        CancellationToken ct);
}

/// One `lar_rows` row.
public sealed record LarRowRecord(
    string LarRowId, string ApplicationId, long ReportingYear,
    string Uli, string RowJson, string DerivedJson,
    string Actor, string? CreatedUtc);

/// One `edit_check_runs` row.
public sealed record EditRunRecord(
    string RunId, string LarRowId, string Tier, string EditId,
    string Result, string Detail, string? CreatedUtc);

/// One `lar_snapshots` row.
public sealed record LarSnapshotRecord(
    string SnapshotId, long ReportingYear, long RowCount,
    string Actor, string? CreatedUtc);

// ---- handler-facing DTOs ----

public sealed record HmdaEditDto(string EditId, string Tier,
    string Result, string Detail);

public sealed record HmdaLarRowDto(string LarRowId, string Uli,
    long ReportingYear, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<HmdaEditDto> Edits, string Actor, string? CreatedUtc);

public sealed record HmdaReportDto(
    HmdaLarRowDto? Latest,
    IReadOnlyList<HmdaLarRowDto> Lineage,
    HmdaFiguresDto Figures);

/// The officer-legible headline figures extracted from the latest row.
public sealed record HmdaFiguresDto(string? ActionTaken,
    string? ActionTakenDate, string? RateSpread, string? HoepaStatus,
    int FailCount, int WarnCount);

public sealed record HmdaExportResult(long ReportingYear, int RowCount,
    byte[] Content, string FileName);
