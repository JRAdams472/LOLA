namespace Lola.Infrastructure.OpenBao;

/// Bound from env: OPENBAO_ADDR, OPENBAO_TOKEN, OPENBAO_TOKEN_FILE.
/// Prefer the file — tokens stay out of env dumps. The file is re-read
/// on resolve because openbao-init rotates the dev token on each stack
/// bootstrap.
public sealed class OpenBaoOptions
{
    public string? Address { get; set; }
    public string? Token { get; set; }
    public string? TokenFile { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(Address);

    public string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(TokenFile) && File.Exists(TokenFile))
        {
            var token = File.ReadAllText(TokenFile).Trim();
            if (token.Length > 0) return token;
        }
        return string.IsNullOrWhiteSpace(Token) ? null : Token;
    }
}
