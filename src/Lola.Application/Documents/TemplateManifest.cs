using System.Text.Json;
using Lola.Application.Errors;

namespace Lola.Application.Documents;

/// The minimization contract (MPE-72, invariant 2): `manifest.json`
/// declares, per template, the ONLY payload keys that may cross to the
/// document vendor. `DisclosurePayloadBuilder` emits exactly these
/// keys — the firewall test proves emitted ⊆ manifest and manifest
/// ⊆ emitted, so no field drifts in or out unreviewed. The embedded
/// `.hbs` sources are what per-tenant provisioning uploads to ISPPr.
public static class TemplateManifest
{
    private const string Prefix = "Lola.Application.Documents.Templates.";
    private static readonly Lazy<JsonElement> Doc = new(Load);

    /// Package kind → ordered template names (`le-package` → the three
    /// disclosure docs). Unknown kind → BadRequest.
    public static IReadOnlyList<string> TemplatesForKind(string kind)
        => Doc.Value.GetProperty("kinds").TryGetProperty(kind, out var t)
            ? t.EnumerateArray().Select(e => e.GetString()!).ToList()
            : throw DomainException.BadRequest($"unknown package kind '{kind}'");

    /// The allowlist of payload keys a template may receive.
    public static IReadOnlySet<string> FieldsFor(string templateId)
        => Doc.Value.GetProperty("templates").TryGetProperty(
                templateId, out var t)
            ? t.GetProperty("fields").EnumerateArray()
                .Select(e => e.GetString()!).ToHashSet()
            : throw DomainException.BadRequest(
                $"unknown template '{templateId}'");

    /// All declared template names — provisioning uploads each.
    public static IReadOnlyList<string> AllTemplates()
        => Doc.Value.GetProperty("templates").EnumerateObject()
            .Select(p => p.Name).ToList();

    /// The embedded Handlebars source for one template — what the
    /// per-tenant ISPPr provisioner uploads.
    public static string SourceFor(string templateId)
    {
        using var s = typeof(TemplateManifest).Assembly
            .GetManifestResourceStream($"{Prefix}{templateId}.hbs")
            ?? throw new InvalidOperationException(
                $"template '{templateId}' is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static JsonElement Load()
    {
        using var s = typeof(TemplateManifest).Assembly
            .GetManifestResourceStream($"{Prefix}manifest.json")
            ?? throw new InvalidOperationException(
                "disclosures manifest.json is not embedded");
        using var d = JsonDocument.Parse(s);
        return d.RootElement.Clone();
    }
}
