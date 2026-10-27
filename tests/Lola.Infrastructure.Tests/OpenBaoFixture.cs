using Lola.Infrastructure.OpenBao;
using Microsoft.Extensions.Options;

namespace Lola.Infrastructure.Tests;

/// Live OpenBao fixture — env-gated, hard-fail (never silent-skip: the
/// M2 acceptance bullets ARE this suite, skipping hides regressions).
///
///   OPENBAO_ADDR   http://localhost:8300 (compose) or :8200 (CI)
///   OPENBAO_TOKEN  root-capable token — needs sys/mounts + key admin:
///                  `data/openbao/root-token` locally, `devroot` in CI.
///
/// Tenant keys use unique run-suffixed names and are destroyed after
/// each test — leaving keys behind would make shred-state leak between
/// runs.
public sealed class OpenBaoFixture : IAsyncLifetime
{
    public OpenBaoTransitClient Transit { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var address = Environment.GetEnvironmentVariable("OPENBAO_ADDR");
        var token = Environment.GetEnvironmentVariable("OPENBAO_TOKEN");
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "OPENBAO_ADDR and OPENBAO_TOKEN are required — these tests prove the " +
                "encryption acceptance criteria against a real OpenBao and never skip.");

        Transit = new OpenBaoTransitClient(new HttpClient(),
            Options.Create(new OpenBaoOptions { Address = address, Token = token }));
        await Transit.EnsureMountAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// Unique tenant id per test — isolated keys, safe cleanup.
    public string NewTenant() => $"test-{Guid.NewGuid():N}";
}

[CollectionDefinition("openbao")]
public sealed class OpenBaoCollection : ICollectionFixture<OpenBaoFixture>;
