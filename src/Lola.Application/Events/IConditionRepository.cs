namespace Lola.Application.Events;

/// Conditions store inside the tenant file (M15). Auto-writers
/// (M16–M18) and the staff CRUD surface share this port.
public interface IConditionRepository
{
    Task<Condition> OpenAsync(ConditionDraft draft,
        CancellationToken ct = default);
    Task<Condition?> GetAsync(string conditionId,
        CancellationToken ct = default);
    Task<IReadOnlyList<Condition>> ListByApplicationAsync(
        string applicationId, string? state = null,
        CancellationToken ct = default);
    /// Guarded transition: open→closed|waived, closed→open. The repo
    /// enforces legal moves; the handler owns reason+audit requirements.
    Task<Condition> TransitionAsync(string conditionId, string toState,
        string? reason, CancellationToken ct = default);
}
