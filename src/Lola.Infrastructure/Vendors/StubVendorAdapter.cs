using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Lola.Application.Vendors;

namespace Lola.Infrastructure.Vendors;

/// Default verification provider (MPE-226): deterministic, realistic
/// normalized responses for all five kinds so the order seam, outbox,
/// webhook/poll, and registry flow are exercisable without a real
/// bureau. `VENDOR_ADAPTER=<vendor>` swaps a live adapter in behind
/// the same interface when vendor selection lands.
///
/// Determinism: the external id and report content derive from the
/// order payload's SHA-256 — a replay returns the same receipt,
/// matching real-vendor idempotency. PII never appears in the
/// receipt id (hash-only) and the reports are synthetic fixtures,
/// never echoing request fields (invariant 3).
public sealed class StubVendorAdapter : IVendorAdapter
{
    private readonly string? _webhookUrl;
    private static readonly HttpClient Http = new()
    { Timeout = TimeSpan.FromSeconds(10) };

    /// `webhookUrl` non-null puts the stub in push mode — after each
    /// submit it POSTs a signed completion to our own webhook
    /// ingress (`VENDOR_STUB_WEBHOOK_URL`, e.g.
    /// `http://localhost:8200/api/vendor-webhooks`) so e2e exercises
    /// the push channel without a real vendor.
    public StubVendorAdapter(string? webhookUrl = null)
        => _webhookUrl = webhookUrl?.TrimEnd('/');

    public string Adapter => "stub";

    public bool SupportsPush => _webhookUrl is not null;

    public IReadOnlySet<VendorKind> Kinds { get; } = new HashSet<VendorKind>
    {
        VendorKind.Credit, VendorKind.Voe, VendorKind.Voi,
        VendorKind.Flood, VendorKind.Fraud, VendorKind.Aus
    };

    public Task<VendorReceipt> SubmitAsync(VendorOrderRequest order,
        VendorCredentials? creds, CancellationToken ct = default)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(order))))
            [..16].ToLowerInvariant();
        var receipt = new VendorReceipt(
            $"stub-{Token(order.Kind)}-{hash}",
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        if (_webhookUrl is not null)
            _ = FireWebhookAsync(order.Kind, receipt, creds);
        return Task.FromResult(receipt);
    }

    /// Simulated vendor push — posts the same normalized payload a
    /// real vendor would, signed with the tenant's webhook secret.
    /// Fire-and-forget by design: a dead ingress never breaks submit
    /// (identical to the real-vendor contract — delivery is the
    /// vendor's problem).
    private async Task FireWebhookAsync(VendorKind kind,
        VendorReceipt receipt, VendorCredentials? creds)
    {
        try
        {
            var payload = new JsonObject
            {
                ["tenant"] = Lola.Application.Tenancy.TenantContext
                    .Current?.TenantId ?? "",
                ["external_id"] = receipt.ExternalId,
                ["outcome"] = "completed",
                ["report"] = Report(kind, receipt.ExternalId)
            }.ToJsonString();
            var signature = Convert.ToHexString(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        creds?.WebhookSecret ?? ""),
                    Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant();
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_webhookUrl}/{Token(kind)}")
            {
                Content = new StringContent(payload,
                    Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation(
                "X-Vendor-Signature", $"sha256={signature}");
            using var resp = await Http.SendAsync(req);
        }
        catch { /* dead ingress in dev — the poll path is absent in push mode by contract */ }
    }

    public Task<VendorResult?> PollAsync(VendorReceipt receipt,
        VendorCredentials? creds, CancellationToken ct = default)
    {
        var kind = KindFromReceipt(receipt.ExternalId);
        return Task.FromResult<VendorResult?>(new VendorResult(
            kind, "completed", null, Report(kind, receipt.ExternalId)));
    }

    /// Build the normalized per-kind report for a completed order —
    /// also used by the P3 webhook simulator so both channels emit
    /// identical shapes.
    public static JsonObject Report(VendorKind kind, string externalId)
        => kind switch
        {
            VendorKind.Credit => new JsonObject
            {
                ["reportType"] = "tri-merge",
                ["externalId"] = externalId,
                ["bureaus"] = new JsonArray(
                    Bureau("Experian", 742),
                    Bureau("Equifax", 738),
                    Bureau("TransUnion", 751)),
                ["decisionScore"] = 744,
                ["tradelineCount"] = 7,
                ["inquiryCount90d"] = 1,
                ["publicRecordCount"] = 0
            },
            VendorKind.Voe => new JsonObject
            {
                ["verificationType"] = "employment",
                ["externalId"] = externalId,
                ["employerVerified"] = true,
                ["employmentStatus"] = "active",
                ["startDate"] = "2021-03-15",
                ["tenureMonths"] = 56,
                ["positionTitle"] = "Senior Analyst",
                ["verificationMethod"] = "instant-database"
            },
            VendorKind.Voi => new JsonObject
            {
                ["verificationType"] = "income",
                ["externalId"] = externalId,
                ["incomeVerified"] = true,
                ["annualizedBaseIncome"] = 118500m,
                ["ytdGrossIncome"] = 98750m,
                ["incomeType"] = "base-salary",
                ["verificationMethod"] = "payroll-database"
            },
            VendorKind.Flood => new JsonObject
            {
                ["certificationType"] = "life-of-loan",
                ["externalId"] = externalId,
                ["floodZone"] = "X",
                ["sfha"] = false,
                ["insuranceRequired"] = false,
                ["communityParticipates"] = true,
                ["mapNumber"] = "STUB-48001C",
                ["determinationDate"] = "2026-11-17"
            },
            VendorKind.Fraud => new JsonObject
            {
                ["screeningType"] = "ofac-identity",
                ["externalId"] = externalId,
                ["ofacMatch"] = false,
                ["identityScore"] = 92,
                ["alerts"] = new JsonArray(),
                ["ssnValid"] = true,
                ["addressHistoryMonths"] = 48
            },
            VendorKind.Aus => new JsonObject
            {
                ["ausEngine"] = "stub-du-compatible",
                ["externalId"] = externalId,
                ["recommendation"] = "approve-eligible",
                ["riskClassification"] = "accept",
                ["findings"] = new JsonArray(
                    Finding("credit", "satisfactory"),
                    Finding("ltv", "within-limits"),
                    Finding("reserves", "verified")),
                ["documentationLevel"] = "standard"
            },
            _ => new JsonObject { ["externalId"] = externalId }
        };

    private static JsonObject Bureau(string name, int score)
        => new()
        {
            ["bureau"] = name,
            ["score"] = score,
            ["scoreModel"] = "fico-10t",
            ["tradelines"] = new JsonArray(
                Tradeline("mortgage", "current", 0),
                Tradeline("revolving", "current", 412),
                Tradeline("installment", "current", 221))
        };

    private static JsonObject Tradeline(
        string type, string status, decimal balance)
        => new()
        {
            ["type"] = type,
            ["status"] = status,
            ["balance"] = balance,
            ["delinquent30"] = false
        };

    private static JsonObject Finding(string area, string result)
        => new() { ["area"] = area, ["result"] = result };

    private static string Token(VendorKind kind) => kind switch
    {
        VendorKind.Credit => "credit",
        VendorKind.Voe => "voe",
        VendorKind.Voi => "voi",
        VendorKind.Flood => "flood",
        VendorKind.Fraud => "fraud",
        VendorKind.Aus => "aus",
        _ => "unknown"
    };

    private static VendorKind KindFromReceipt(string externalId)
        => externalId.Split('-') is { Length: >= 2 } parts
            ? parts[1] switch
            {
                "credit" => VendorKind.Credit,
                "voe" => VendorKind.Voe,
                "voi" => VendorKind.Voi,
                "flood" => VendorKind.Flood,
                "fraud" => VendorKind.Fraud,
                "aus" => VendorKind.Aus,
                _ => VendorKind.Credit
            }
            : VendorKind.Credit;
}
