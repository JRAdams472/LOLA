using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Default virus scanner (MPE-200): always-clean verdict so the
/// quarantine → scan → promote flow is exercisable without ClamAV.
/// The ClamAV `zINSTREAM` adapter lands behind the same seam in
/// MPE-201. An `AlwaysUnavailableScanner`-style variant for retry
/// tests lives in the test assemblies.
public sealed class StubScanner : IVirusScanner
{
    public string Adapter => "stub";

    public Task<ScanVerdict> ScanAsync(Stream content,
        CancellationToken ct = default)
        => Task.FromResult(new ScanVerdict(ScanStatus.Clean, null));
}
