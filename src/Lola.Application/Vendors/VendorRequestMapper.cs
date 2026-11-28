using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Vendors;

/// Maps the application document to the per-kind vendor request —
/// this is the egress-minimization enforcement point: staff order a
/// *kind*, the mapper reads only the allowlisted fields from the doc,
/// and the firewall proves no record can carry anything else.
/// Missing required fields reject 422-style with the doc paths.
public static class VendorRequestMapper
{
    public static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    /// doc paths the request consumed — drives the audit field list.
    public static IReadOnlyList<string> RequiredPaths(VendorKind kind)
        => kind switch
        {
            VendorKind.Credit =>
            [
                "party[0].first_name", "party[0].last_name",
                "party[0].ssn", "party[0].dob",
                "party[0].address[0].street", "party[0].address[0].city",
                "party[0].address[0].state", "party[0].address[0].zip"
            ],
            VendorKind.Voe =>
            [
                "party[0].first_name", "party[0].last_name",
                "party[0].ssn", "party[0].employment[0].employer_name",
                "party[0].phone"
            ],
            VendorKind.Voi =>
            [
                "party[0].first_name", "party[0].last_name",
                "party[0].ssn", "party[0].employment[0].employer_name"
            ],
            VendorKind.Flood =>
            [
                "subject_property.street", "subject_property.city",
                "subject_property.state", "subject_property.zip"
            ],
            VendorKind.Fraud =>
            [
                "party[0].first_name", "party[0].last_name",
                "party[0].ssn", "party[0].dob"
            ],
            VendorKind.Aus =>
            [
                "party[0].first_name", "party[0].last_name",
                "party[0].ssn", "party[0].dob", "loan_amount",
                "loan_purpose", "subject_property.street",
                "subject_property.city", "subject_property.state",
                "subject_property.zip"
            ],
            _ => throw DomainException.BadRequest(
                $"unknown vendor kind '{kind}'")
        };

    /// Build the typed request; every required path must read a
    /// non-empty value — a vendor order on an incomplete application
    /// would burn vendor quota and return a useless result.
    public static VendorOrderRequest Map(VendorKind kind,
        ApplicationRecord app)
    {
        var doc = app.Doc;
        string Field(string path) => doc.Get(path) is JsonValue v
            && v.TryGetValue<string>(out var s) && s.Length > 0
                ? s : throw Missing(kind, path);

        var request = kind switch
        {
            VendorKind.Credit => (VendorOrderRequest)new CreditOrderRequest(
                Field("party[0].first_name"), Field("party[0].last_name"),
                Field("party[0].ssn"), Field("party[0].dob"),
                Field("party[0].address[0].street"),
                Field("party[0].address[0].city"),
                Field("party[0].address[0].state"),
                Field("party[0].address[0].zip")),
            VendorKind.Voe => new VoeOrderRequest(
                Field("party[0].first_name"), Field("party[0].last_name"),
                Field("party[0].ssn"),
                Field("party[0].employment[0].employer_name"),
                Field("party[0].phone")),
            VendorKind.Voi => new VoiOrderRequest(
                Field("party[0].first_name"), Field("party[0].last_name"),
                Field("party[0].ssn"),
                Field("party[0].employment[0].employer_name")),
            VendorKind.Flood => new FloodOrderRequest(
                Field("subject_property.street"),
                Field("subject_property.city"),
                Field("subject_property.state"),
                Field("subject_property.zip")),
            VendorKind.Fraud => new FraudOrderRequest(
                Field("party[0].first_name"), Field("party[0].last_name"),
                Field("party[0].ssn"), Field("party[0].dob")),
            VendorKind.Aus => Aus(app, Field),
            _ => throw DomainException.BadRequest(
                $"unknown vendor kind '{kind}'")
        };
        return request;

        static AusOrderRequest Aus(ApplicationRecord app,
            Func<string, string> field)
        {
            field("party[0].first_name"); field("party[0].last_name");
            field("party[0].ssn"); field("party[0].dob");
            field("loan_amount"); field("loan_purpose");
            field("subject_property.street"); field("subject_property.city");
            field("subject_property.state"); field("subject_property.zip");
            // The wire payload is the XSD-validated MISMO serialization
            // (M25) — never the raw doc json. Missing optional inputs
            // serialize as absent elements (never invented).
            var serializerInput = new Dictionary<string, string?>
            {
                ["borrower.first_name"] = field("party[0].first_name"),
                ["borrower.last_name"] = field("party[0].last_name"),
                ["borrower.date_of_birth"] = field("party[0].dob"),
                ["borrower.ssn"] = field("party[0].ssn"),
                ["loan.amount"] = field("loan_amount"),
                ["loan.purpose"] = field("loan_purpose"),
                ["subject_property.street"] = field("subject_property.street"),
                ["subject_property.city"] = field("subject_property.city"),
                ["subject_property.state"] = field("subject_property.state"),
                ["subject_property.zip"] = field("subject_property.zip"),
            };
            return new AusOrderRequest(app.Id, app.CurrentVersion,
                Lola.Domain.Aus.MismoAusSerializer.Serialize(serializerInput));
        }
    }

    public static string RequestJson(VendorOrderRequest request)
        => JsonSerializer.Serialize((object)request, request.GetType(),
            Json);

    /// Dedupe key — same app + kind + serialized payload is the same
    /// order; a doc change yields a new hash and a new order.
    public static string RequestHash(string tenantId, string appId,
        VendorKind kind, string requestJson)
        => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{tenantId}|{appId}|{VendorGuards.Token(kind)}|{requestJson}")))
            .ToLowerInvariant();

    /// Outbound result envelope for `result_json` — outcome + opaque
    /// per-kind report in one document so reads need one decrypt.
    public static string ResultJson(VendorResult result)
        => JsonSerializer.Serialize(new JsonObject
        {
            ["outcome"] = result.Outcome,
            ["failure_reason"] = result.FailureReason,
            ["report"] = result.Report.DeepClone()
        }, Json);

    private static DomainException Missing(VendorKind kind, string path)
        => DomainException.BadRequest(
            $"application is missing '{path}' required for a " +
            $"{VendorGuards.Token(kind)} order");
}
