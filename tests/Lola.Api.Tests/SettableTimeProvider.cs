namespace Lola.Api.Tests;

/// Test clock for the vault signer: starts at real UTC now and only
/// advances when a test says so — the app's own TimeProvider.System is
/// untouched, so minted expiry strings stay real while the test alone
/// decides when the capability has aged out.
public sealed class SettableTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
    /// The provider is a fixture singleton — every test re-bases to
    /// real now or an earlier test's Advance leaks into the next.
    public void Reset() => _now = DateTimeOffset.UtcNow;
}
