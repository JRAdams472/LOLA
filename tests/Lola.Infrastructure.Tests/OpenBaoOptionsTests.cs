using System.Net;
using Lola.Infrastructure.OpenBao;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// OpenBao options + error-surface proofs: the token file wins over
/// the env token (rotated dev tokens heal without a restart), a
/// missing/empty file falls back to the env token, and
/// FromResponse flattens `errors[]` while passing raw bodies through.
public sealed class OpenBaoOptionsTests
{
    [Fact]
    public void ResolveToken_prefers_the_file()
    {
        var file = Path.Combine(Path.GetTempPath(),
            $"bao-{Guid.NewGuid():N}.tok");
        File.WriteAllText(file, " s.file-token \n");
        try
        {
            var opts = new OpenBaoOptions
            {
                Address = "http://bao:8200",
                Token = "env-token",
                TokenFile = file,
            };
            opts.Configured.ShouldBeTrue();
            opts.ResolveToken().ShouldBe("s.file-token");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ResolveToken_falls_back_to_env_when_no_file()
    {
        new OpenBaoOptions
        {
            Address = "a",
            Token = "env-token",
            TokenFile = "/nonexistent/tok"
        }
            .ResolveToken().ShouldBe("env-token");
        new OpenBaoOptions { Address = "a" }
            .ResolveToken().ShouldBeNull();
        new OpenBaoOptions().Configured.ShouldBeFalse();
    }

    [Fact]
    public void FromResponse_flattens_errors_and_keeps_raw_bodies()
    {
        var ex = OpenBaoException.FromResponse(
            HttpStatusCode.Forbidden, """{"errors":["denied","nope"]}""");
        ex.Status.ShouldBe(HttpStatusCode.Forbidden);
        ex.Message.ShouldContain("denied; nope");

        var raw = OpenBaoException.FromResponse(
            HttpStatusCode.InternalServerError, "not json");
        raw.Message.ShouldContain("not json");
    }
}
