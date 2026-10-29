using System.Reflection;
using Lola.Application.Security;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-118: the committed docs/pii-data-map.json must byte-match the
/// generated map — a stale artifact fails here. Regenerate with
/// REGEN_PII_MAP=1 dotnet test --filter PiiDataMap.
public sealed class PiiDataMapTests
{
    private static readonly Assembly[] MemberAssemblies =
    [
        typeof(PiiClassAttribute).Assembly,       // Lola.Domain
        typeof(Admin.Users.AdminUserDto).Assembly // Lola.Application
    ];

    [Fact]
    public void Data_map_enumerates_every_catalog_pii_field_and_member()
    {
        var map = PiiDataMap.Generate(MemberAssemblies);
        var fields = map["fields"]!.AsArray();
        var expected = Lola.Domain.Applications.FieldCatalog.Current.PiiFields
            .Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        fields.Select(f => f!["path"]!.GetValue<string>())
            .ShouldBe(expected);
        // The control-plane DTO's three attributed members are present.
        var members = map["members"]!.AsArray();
        var memberNames = members.Select(m =>
            $"{m!["type"]!.GetValue<string>()}.{m["member"]!.GetValue<string>()}")
            .ToList();
        memberNames.ShouldContain("Lola.Application.Admin.Users.AdminUserDto.Email");
        memberNames.ShouldContain("Lola.Application.Admin.Users.AdminUserDto.DisplayName");
        memberNames.ShouldContain("Lola.Application.Admin.Users.AdminUserDto.MfaSecretRef");
        // Storage derivation sanity: ssn is payload + index-column + blind.
        var ssn = fields.First(f => f!["path"]!.GetValue<string>() == "party[0].ssn")!;
        var storage = ssn["storage"]!.AsArray()
            .Select(s => s!.GetValue<string>()).ToList();
        storage.ShouldBe(["payload", "index-column", "blind-index"]);
        // Output is deterministic — two generations are identical.
        PiiDataMap.GenerateJson(MemberAssemblies)
            .ShouldBe(PiiDataMap.GenerateJson(MemberAssemblies));
    }

    [Fact]
    public void Committed_artifact_is_not_stale()
    {
        var path = ArtifactPath();
        var generated = PiiDataMap.GenerateJson(MemberAssemblies);
        if (Environment.GetEnvironmentVariable("REGEN_PII_MAP") == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }
        File.Exists(path).ShouldBeTrue($"missing {path} — run REGEN_PII_MAP=1");
        File.ReadAllText(path).ShouldBe(generated,
            "pii-data-map.json is stale — regenerate with " +
            "REGEN_PII_MAP=1 dotnet test --filter PiiDataMap");
    }

    private static string ArtifactPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lola.slnx")))
            dir = dir.Parent;
        dir.ShouldNotBeNull("could not locate repo root (Lola.slnx)");
        return Path.Combine(dir.FullName, "docs", "pii-data-map.json");
    }
}
