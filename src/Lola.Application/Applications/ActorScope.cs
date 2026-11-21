namespace Lola.Application.Applications;

/// Who is acting relative to an application row — the row-scope
/// discriminant `RequireAppAsync` enforces (M19, ADR-0003):
/// Staff reach every app in the tenant; a Borrower reaches only the
/// app whose `BorrowerUserId` they are; a Partner (Broker/Realtor)
/// reaches only apps with an active `application_partners` link —
/// revoked links don't count. The API layer derives the scope from
/// role claims; handlers never re-derive it.
public enum ActorScope
{
    Staff,
    Borrower,
    Partner
}
