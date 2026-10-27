using Lola.Infrastructure.OpenBao;
using Microsoft.Extensions.Options;

namespace Lola.DataPlane.Tests;

/// Live OpenBao fixture — same env-gated, hard-fail convention as the
/// Infrastructure suite: these tests prove the provisioning acceptance
/// against a real transit backend and never silently skip.
///
///   OPENBAO_ADDR   http://localhost:8300 (compose) or :8200 (CI)
///   OPENBAO_TOKEN  root-capable token (mount + key admin).
public sealed class OpenBaoFixture : IAsyncLifetime
{
    public OpenBaoTransitClient Transit { get; private set; } = null!;
    public string DataDir { get; } =
        Path.Combine(Path.GetTempPath(), "lola-dp-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        var address = Environment.GetEnvironmentVariable("OPENBAO_ADDR");
        var token = Environment.GetEnvironmentVariable("OPENBAO_TOKEN");
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "OPENBAO_ADDR and OPENBAO_TOKEN are required — these tests prove the " +
                "provisioning acceptance criteria against a real OpenBao and never skip.");

        Transit = new OpenBaoTransitClient(new HttpClient(),
            Options.Create(new OpenBaoOptions { Address = address, Token = token }));
        await Transit.EnsureMountAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// Unique tenant id per test — isolated keys, safe cleanup.
    public string NewTenant() => $"dp{Guid.NewGuid():N}"[..12];
}

[CollectionDefinition("openbao")]
public sealed class OpenBaoCollection : ICollectionFixture<OpenBaoFixture>;
