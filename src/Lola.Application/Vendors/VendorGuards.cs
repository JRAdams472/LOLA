using Lola.Application.Errors;

namespace Lola.Application.Vendors;

/// Vendor-kind token guards — the wire/DB vocabulary is lowercase
/// (`credit|voe|voi|flood|fraud|aus`); anything else 400s before an
/// order row exists.
public static class VendorGuards
{
    public static string Token(VendorKind kind) => kind switch
    {
        VendorKind.Credit => "credit",
        VendorKind.Voe => "voe",
        VendorKind.Voi => "voi",
        VendorKind.Flood => "flood",
        VendorKind.Fraud => "fraud",
        VendorKind.Aus => "aus",
        _ => throw DomainException.BadRequest(
            $"unknown vendor kind '{kind}'")
    };

    public static VendorKind Parse(string token) =>
        token.ToLowerInvariant() switch
        {
            "credit" => VendorKind.Credit,
            "voe" => VendorKind.Voe,
            "voi" => VendorKind.Voi,
            "flood" => VendorKind.Flood,
            "fraud" => VendorKind.Fraud,
            "aus" => VendorKind.Aus,
            _ => throw DomainException.BadRequest(
                $"unknown vendor kind '{token}'")
        };
}
