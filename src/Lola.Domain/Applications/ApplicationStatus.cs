namespace Lola.Domain.Applications;

/// Intake lifecycle (M5): draft → submitted → in_review → decisioned.
/// Pipeline position (`POS Intake → Validate → PPE → Fee → Doc → Package`)
/// is tracked separately on `applications.pipeline_step` — it moves
/// through the named-step workflow seam (P4), not this enum.
public enum ApplicationStatus
{
    Draft,
    Submitted,
    InReview,
    Decisioned
}
