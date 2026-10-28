using System.Net.Mail;
using Lola.Application.Errors;

namespace Lola.Application.Admin;

/// Shared boundary validation for admin/invite handlers (MPE-90) —
/// keeps malformed identity data out of the control plane. Deliberately
/// simple: format + bounds, not content judgement.
public static class InputValidation
{
    public const int MinPasswordLength = 12;
    public const int MaxDisplayNameLength = 200;

    /// Returns the trimmed address or throws BadRequest.
    public static string RequireEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw DomainException.BadRequest("email is required");
        try
        {
            _ = new MailAddress(email.Trim());
        }
        catch (FormatException)
        {
            throw DomainException.BadRequest("email is not a valid address");
        }
        return email.Trim();
    }

    public static void RequirePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw DomainException.BadRequest("password is required");
        if (password.Length < MinPasswordLength)
            throw DomainException.BadRequest(
                $"password must be at least {MinPasswordLength} characters");
    }

    /// Returns the trimmed name or throws BadRequest.
    public static string RequireDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw DomainException.BadRequest("display name is required");
        var trimmed = name.Trim();
        if (trimmed.Length > MaxDisplayNameLength)
            throw DomainException.BadRequest(
                $"display name must be {MaxDisplayNameLength} characters or fewer");
        return trimmed;
    }
}
