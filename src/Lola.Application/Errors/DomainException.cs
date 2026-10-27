namespace Lola.Application.Errors;

/// Domain-level rejection carrying an HTTP status — the single error
/// model maps it to RFC 9457 problem details. Keeps handlers free of
/// transport concerns while letting the edge return honest statuses.
public sealed class DomainException(int status, string title) : Exception(title)
{
    public int Status { get; } = status;

    public static DomainException Forbidden(string detail = "tenant scope does not permit this operation")
        => new(403, detail);

    public static DomainException NotFound(string detail = "resource not found")
        => new(404, detail);

    public static DomainException BadRequest(string detail)
        => new(400, detail);
}
