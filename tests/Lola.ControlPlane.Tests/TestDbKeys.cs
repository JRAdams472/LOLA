namespace Lola.ControlPlane.Tests;

/// Deterministic file key for keyed control-plane fixtures — a
/// well-known test constant, never a secret (MPE-276 / Phase 7b).
internal static class TestDbKeys
{
    public const string ControlFileKeyHex =
        "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";
}
