using System.Globalization;
using System.Text;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// The one way a tenant becomes a file-system name, in both directions (#467). A tenant is caller-supplied — the tenant
/// header, a credential's scope, an OIDC claim — so its value is never a path: it is mapped to exactly ONE path segment
/// that cannot climb out of, root itself under, or nest inside the directory it is joined to.
/// </summary>
/// <remarks>
/// Fail-closed by an allow-list: printable ASCII that is a portable file-name character stays as it is, so every name an
/// earlier host wrote that was already a safe segment maps to the same directory. Every other character — a separator,
/// <c>%</c>, a character Windows refuses in a name, a control character, anything non-ASCII — is written as its UTF-8
/// bytes in <c>%XX</c> form, so the mapping is reversible. A name of dots only, and a trailing dot or space (which Windows
/// strips), are escaped too. <see cref="TryDecode"/> accepts only a name <see cref="Encode"/> would have produced, so a
/// directory a person named is never read as a tenant it merely resembles. What this does not change: two tenants that
/// differ only in letter case share one directory on a case-insensitive file system (stated in
/// <c>docs/parity/g16-persistence.md</c>).
/// </remarks>
public static class TenantPathSegment
{
    private const string Kept = "!#$&'()+,-.;=@[]^_`{}~ ";

    /// <summary>The single, reversible path segment for <paramref name="tenant"/>.</summary>
    /// <exception cref="ArgumentException">The tenant's value is empty: it has no segment, and joining one would name the parent.</exception>
    public static string Encode(TenantId tenant)
    {
        var value = tenant.Value;
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("A tenant with an empty name has no directory of its own.", nameof(tenant));
        }

        var builder = new StringBuilder(value.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < value.Length;)
        {
            var c = value[i];
            var last = i == value.Length - 1;
            if (char.IsAsciiLetterOrDigit(c) || (Kept.Contains(c) && !(last && c is '.' or ' ')))
            {
                builder.Append(c);
                i++;
                continue;
            }

            // One scalar at a time (a surrogate pair is one); a lone surrogate has no UTF-8 form and is refused, not guessed.
            if (Rune.DecodeFromUtf16(value.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                throw new ArgumentException("A tenant name that is not well-formed text has no directory of its own.", nameof(tenant));
            }

            foreach (var b in utf8[..rune.EncodeToUtf8(utf8)])
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }

            i += consumed;
        }

        // A name of dots only cannot survive as dots: its last dot is escaped above, so ".." is ".%2E", never a climb.
        return builder.ToString();
    }

    /// <summary>
    /// The tenant a directory <paramref name="segment"/> names — only when <see cref="Encode"/> would have produced exactly
    /// that segment. Anything else (an unescaped character outside the allow-list, a malformed or lower-case escape, bytes
    /// that are not UTF-8, a second spelling of the same name) is <see langword="false"/>.
    /// </summary>
    public static bool TryDecode(string segment, out TenantId tenant)
    {
        tenant = default;
        var bytes = new List<byte>(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] != '%')
            {
                // A character outside ASCII lands as a byte the re-encoding below cannot reproduce, so it is refused there.
                bytes.Add((byte)segment[i]);
                continue;
            }

            if (i + 2 >= segment.Length || !IsUpperHex(segment[i + 1]) || !IsUpperHex(segment[i + 2]))
            {
                return false;
            }

            bytes.Add(byte.Parse(segment.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            i += 2;
        }

        // One rule decides: the segment is a tenant's only when encoding that tenant gives back exactly this segment. Bytes
        // that are not UTF-8 decode to U+FFFD, which encodes to other bytes, so they are refused by the same comparison.
        var value = Encoding.UTF8.GetString([.. bytes]);
        if (value.Length == 0 || !string.Equals(Encode(new TenantId(value)), segment, StringComparison.Ordinal))
        {
            return false;
        }

        tenant = new TenantId(value);
        return true;
    }

    private static bool IsUpperHex(char c) => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F';
}
