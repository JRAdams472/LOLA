using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Lola.Application.Vendors;
using Lola.Domain.Aus;

namespace Lola.Infrastructure.Vendors;

/// <summary>
/// Shared base for the DU/LPA AUS adapters (M25 P2, ADR-0007). Both
/// GSEs are SYNCHRONOUS underwriting services: the POST returns the
/// full decision in-band. The adapter performs the call in
/// SubmitAsync, caches the normalized result against the receipt id,
/// and the first PollAsync drains it — the outbox processor's
/// opportunistic in-band completion does the rest. Credentials come
/// from OpenBao KV per tenant; a missing binding is a typed auth
/// failure, never a shared-credential fallback. TLS + client-
/// credentials are deployment config on the HttpClient factory.
/// </summary>
public abstract class AusAdapterBase(HttpClient http, string baseUrl)
    : IVendorAdapter
{
    private readonly ConcurrentDictionary<string, VendorResult> _completed = new();

    public abstract string Adapter { get; }
    public IReadOnlySet<VendorKind> Kinds { get; } =
        new HashSet<VendorKind> { VendorKind.Aus };

    /// Parse the vendor's wire response into the normalized report.
    protected abstract AusResult ParseResponse(string xml);

    /// Vendor endpoint path for the submission.
    protected abstract string SubmitPath { get; }

    public async Task<VendorReceipt> SubmitAsync(VendorOrderRequest order,
        VendorCredentials? creds, CancellationToken ct = default)
    {
        if (order is not AusOrderRequest aus)
            throw new VendorAdapterException(VendorErrorKind.Contract,
                $"{Adapter} adapter only accepts aus orders");
        if (creds?.ApiKey is not { Length: > 0 } apiKey)
            throw new VendorAdapterException(VendorErrorKind.Auth,
                $"no {Adapter} credential binding for this tenant");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}{SubmitPath}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", apiKey);
        request.Content = new StringContent(aus.MismoJson, Encoding.UTF8,
            "application/xml");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new VendorAdapterException(VendorErrorKind.Unavailable,
                $"{Adapter} submit transport failure: {ex.Message}");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new VendorAdapterException(
                (int)response.StatusCode >= 500
                    ? VendorErrorKind.Unavailable : VendorErrorKind.Rejected,
                $"{Adapter} submit HTTP {(int)response.StatusCode}");

        var parsed = ParseResponse(body);
        var externalId = parsed.CaseId
            ?? $"{Adapter}-{Guid.NewGuid():N}"[..32];
        var report = NormalizeReport(parsed, externalId, body);
        _completed[externalId] = new VendorResult(VendorKind.Aus,
            "completed", null, report);
        return new VendorReceipt(externalId,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
    }

    public Task<VendorResult?> PollAsync(VendorReceipt receipt,
        VendorCredentials? creds, CancellationToken ct = default)
    {
        // Synchronous vendor: the result arrived with the submit —
        // the first poll drains it, later polls find nothing.
        return Task.FromResult(
            _completed.TryRemove(receipt.ExternalId, out var result)
                ? result : null);
    }

    /// Canonical report shape shared with the stub — downstream
    /// processing never sees vendor XML structure. `responseXml`
    /// carries the raw wire bytes for the tenant-boundary store.
    private JsonObject NormalizeReport(AusResult result,
        string externalId, string rawXml)
    {
        var findings = new JsonArray();
        foreach (var f in result.Findings)
        {
            findings.Add(new JsonObject
            {
                ["code"] = f.VendorCode,
                ["category"] = f.Category.ToString().ToLowerInvariant(),
                ["severity"] = f.Severity.ToString().ToLowerInvariant(),
                ["message"] = f.Message,
            });
        }
        return new JsonObject
        {
            ["ausVendor"] = result.Vendor,
            ["ausEngine"] = Adapter,
            ["externalId"] = externalId,
            ["caseId"] = result.CaseId,
            ["recommendation"] = result.Recommendation switch
            {
                AusRecommendation.ApproveEligible => "approve-eligible",
                AusRecommendation.Accept => "accept",
                AusRecommendation.Refer => "refer",
                AusRecommendation.Caution => "caution",
                AusRecommendation.Ineligible => "ineligible",
                _ => "unable-to-evaluate",
            },
            ["riskClassification"] = result.RiskClass,
            ["findings"] = findings,
            ["responseXml"] = rawXml,
        };
    }
}

/// Desktop Underwriter (Fannie Mae) — CodifiedFindings response.
public sealed class DuAdapter(HttpClient http, string baseUrl)
    : AusAdapterBase(http, baseUrl)
{
    public override string Adapter => "du";
    protected override string SubmitPath => "/du/v1/underwrite";

    protected override AusResult ParseResponse(string xml)
        => DuResponseParser.Parse(xml);
}

/// Loan Product Advisor (Freddie Mac) — feedback certificate response.
public sealed class LpaAdapter(HttpClient http, string baseUrl)
    : AusAdapterBase(http, baseUrl)
{
    public override string Adapter => "lpa";
    protected override string SubmitPath => "/lpa/v1/assess";

    protected override AusResult ParseResponse(string xml)
        => LpaResponseParser.Parse(xml);
}
