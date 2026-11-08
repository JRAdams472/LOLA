using Lola.Application.Errors;

namespace Lola.Application.Vault;

/// Upload allowlist (MPE-200): classification enum + magic-byte
/// sniffing — Content-Type headers and extensions are caller claims,
/// the bytes decide. 25 MB cap is enforced mid-stream by the cipher
/// (VaultTooLargeException → 413); this gate only answers "may these
/// bytes enter the quarantine pipeline at all".
public static class UploadAllowlist
{
    public static readonly HashSet<string> Classifications = new(
        StringComparer.OrdinalIgnoreCase)
        { "paystub", "w2", "bank-statement", "id", "other" };

    /// Head bytes needed for every signature below (docx needs the
    /// first ZIP entry name, ~64B in practice — we read 512).
    public const int SniffBytes = 512;

    /// Throws 400/415 on anything outside the allowlist; returns the
    /// canonical stored content type on success. `head` is the first
    /// SniffBytes (or fewer for tiny files) of the plaintext stream.
    public static string Validate(string classification,
        string? declaredFileName, string? declaredContentType,
        ReadOnlySpan<byte> head)
    {
        if (!Classifications.Contains(classification))
            throw DomainException.BadRequest(
                $"classification must be one of: " +
                string.Join(", ", Classifications.Order()));
        return Sniff(head, declaredFileName);
    }

    private static string Sniff(ReadOnlySpan<byte> head,
        string? fileName)
    {
        if (head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8))
            return "application/pdf";
        if (head.Length >= 8 && head[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47,
                             0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF
            && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";
        if (head.Length >= 4 &&
            (head[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
             head[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })))
            return "image/tiff";
        // docx/xlsx are ZIPs — PK alone can't separate them without
        // unzipping (which the vault never does), but a real OOXML
        // package's first local entry is always `[Content_Types].xml`;
        // require that name inside the first local file header AND a
        // .docx filename so a renamed xlsx can't slip in disguised.
        if (head.Length >= 4 && head[..4].SequenceEqual(
                new byte[] { (byte)'P', (byte)'K', 0x03, 0x04 }))
        {
            var window = head.Length > 512 ? head[..512] : head;
            if (window.IndexOf("[Content_Types].xml"u8) >= 0
                && fileName?.EndsWith(".docx",
                    StringComparison.OrdinalIgnoreCase) == true)
                return "application/vnd.openxmlformats-officedocument" +
                       ".wordprocessingml.document";
        }
        throw new DomainException(415,
            "file type is not allowed — pdf, png, jpg, tiff, or docx");
    }
}
