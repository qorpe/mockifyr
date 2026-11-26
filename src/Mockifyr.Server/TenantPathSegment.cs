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

    /// <summary>
    /// The tenant an earlier release's writer meant by a directory <paramref name="segment"/>: the raw tenant value, which
    /// the stub and environment providers joined to the path before #467. Only a value that writer could have produced as
    /// ONE directory is accepted — not empty, not <c>.</c> or <c>..</c>, no separator, not rooted. It is consulted only for
    /// a directory without <see cref="MarkerFileName"/>, so it never competes with this release's reading: a name such as
    /// <c>%C4%B0</c> is this release's <c>İ</c> when marked and an earlier tenant literally named <c>%C4%B0</c> when not.
    /// </summary>
    public static bool TryDecodeEarlierRawName(string segment, out TenantId tenant)
    {
        tenant = default;
        if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(['/', '\\']) >= 0
            || Path.IsPathRooted(segment))
        {
            return false;
        }

        tenant = new TenantId(segment);
        return true;
    }

    /// <summary>
    /// The tenant an earlier release's resources provider meant by a directory <paramref name="segment"/>, consulted only
    /// for a directory without <see cref="MarkerFileName"/>. That writer escaped every UTF-16 code unit outside
    /// <c>[A-Za-z0-9._-]</c> as <c>%</c> and its hexadecimal value with AT LEAST two digits, so a code unit above
    /// <c>0xFF</c> took three or four (<c>İ</c>, U+0130, became <c>%130</c>; <c>Σ</c>, U+03A3, <c>%3A3</c>) and its
    /// output is ambiguous wherever an escape is followed by a digit or <c>A</c>–<c>F</c>, which that writer otherwise
    /// kept as they were. Fail-closed: a segment is accepted only when no escape other than a space (<c>%20</c>) is
    /// followed by one of those, it reads as printable ASCII, and writing that value again gives back exactly
    /// <paramref name="segment"/>; anything else is refused rather than guessed. The one accepted ambiguity is the space:
    /// <c>Acme%20Corp</c> is <c>Acme Corp</c>, not U+020C, because refusing every space before a capital would lose
    /// the names this reading exists for; it costs U+0200–U+020F and U+2000–U+20FF, read as a space and hex digits.
    /// </summary>
    public static bool TryDecodeEarlierResourceName(string segment, out TenantId tenant)
    {
        tenant = default;
        if (segment.Length == 0)
        {
            return false;
        }

        var builder = new StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] != '%')
            {
                builder.Append(segment[i]);
                continue;
            }

            if (i + 2 >= segment.Length || !IsUpperHex(segment[i + 1]) || !IsUpperHex(segment[i + 2]))
            {
                return false;
            }

            // A third hexadecimal digit means the earlier writer may have escaped a code unit above 0xFF here: ambiguous,
            // so refused — except after a space, whose names ("Acme Corp") are the common case (stated in g16-persistence.md).
            if (i + 3 < segment.Length && IsUpperHex(segment[i + 3]) && segment.AsSpan(i + 1, 2) is not "20")
            {
                return false;
            }

            builder.Append((char)byte.Parse(segment.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            i += 2;
        }

        var value = builder.ToString();
        foreach (var c in value)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        if (!string.Equals(EarlierResourceName(value), segment, StringComparison.Ordinal))
        {
            return false;
        }

        tenant = new TenantId(value);
        return true;
    }

    /// <summary>
    /// The file a provider writes into every tenant directory it creates (#467): its presence is what says "this release
    /// named this directory by <see cref="Encode"/>". Without it a name cannot be told apart from an earlier release's,
    /// because both writers produced names the other could also have produced for a different tenant.
    /// </summary>
    public const string MarkerFileName = ".mockifyr-tenant";

    private const string MarkerText =
        "This directory's name is its tenant, written by Mockifyr's TenantPathSegment (#467). Keep this file with it.\n";

    /// <summary>
    /// Creates a tenant directory the way every file provider must (#467): the directory, and the
    /// <see cref="MarkerFileName"/> that makes the loader read its name by this release's rule.
    /// </summary>
    /// <param name="directory">The tenant directory, <c>parent/Encode(tenant)</c>.</param>
    public static void CreateTenantDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, MarkerFileName);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, MarkerText);
        }
    }

    /// <summary>
    /// The tenant a directory names, without touching it: by this release's rule when it carries
    /// <see cref="MarkerFileName"/>, and otherwise by <paramref name="earlier"/>, the rule of the release that wrote it.
    /// The two are never both consulted, so a name that both writers could have produced is never decided by which rule
    /// happened to run first.
    /// </summary>
    /// <param name="directory">The tenant directory found under the provider's root.</param>
    /// <param name="earlier">How an earlier release named a tenant's directory.</param>
    /// <param name="tenant">The tenant, when the name reads as one.</param>
    /// <param name="marked">Whether the directory carries this release's marker.</param>
    public static bool TryReadDirectoryName(string directory, TryDecodeEarlier earlier, out TenantId tenant, out bool marked)
    {
        var name = Path.GetFileName(directory);
        marked = File.Exists(Path.Combine(directory, MarkerFileName));
        if (marked ? TryDecode(name, out tenant) : earlier(name, out tenant))
        {
            return true;
        }

        tenant = default;
        return false;
    }

    /// <summary>
    /// Resolves one tenant directory of a file provider (#467): the tenant it names, and the directory to read it from.
    /// A directory carrying <see cref="MarkerFileName"/> is this release's and is read where it is. One without it was
    /// written by an earlier release (as <paramref name="earlier"/> reads it): it is renamed to the name this release
    /// writes, once, and marked, so the next write, delete or reset for that tenant lands on the same directory the
    /// reload read — reading it in place would let a deleted stub or a reset environment come back on the next restart.
    /// Anything else, including an earlier name that could mean more than one tenant, is skipped.
    /// <paramref name="notice"/> carries the line the caller prints: what was renamed, or which directory was skipped,
    /// why, and how to recover it.
    /// </summary>
    /// <param name="directory">The tenant directory found under the provider's root.</param>
    /// <param name="area">The provider's directory, as the operator knows it (<c>mappings</c>, <c>environments</c>, …).</param>
    /// <param name="earlier">How an earlier release named a tenant's directory.</param>
    /// <param name="notice">Empty when the directory is read where it is; otherwise the line describing the rename or the skip.</param>
    /// <param name="tenant">The tenant, when one is resolved.</param>
    /// <param name="path">The directory to read, when one is resolved.</param>
    public static bool TryResolveDirectory(
        string directory,
        string area,
        TryDecodeEarlier earlier,
        out string notice,
        out TenantId tenant,
        out string path)
    {
        var name = Path.GetFileName(directory);
        path = directory;
        notice = "";
        if (!TryReadDirectoryName(directory, earlier, out tenant, out var marked))
        {
            notice = marked
                ? $"{area}/{name}: it carries {MarkerFileName}, but its name is not one this provider writes for a tenant — skipped, not read as a tenant."
                : $"{area}/{name}: it has no {MarkerFileName}, so an earlier release wrote it, and its name does not name exactly one tenant by "
                    + $"that release's rule — skipped, not guessed; to keep it, rename it to its tenant's name as this release writes it "
                    + $"(docs/parity/g16-persistence.md) and put an empty {MarkerFileName} in it.";
            return false;
        }

        if (marked)
        {
            return true;
        }

        var segment = Encode(tenant);
        if (string.Equals(segment, name, StringComparison.Ordinal))
        {
            // The same name in both releases: read in place, marked so the next load reads it by this release's rule. A
            // root that cannot be written (a read-only mount) is still read; it is resolved the same way next time.
            TryMark(directory);
            return true;
        }

        var target = Path.Combine(Path.GetDirectoryName(directory)!, segment);
        if (Directory.Exists(target))
        {
            notice = $"{area}/{name}: written by an earlier release for tenant '{tenant.Value}', which also has {area}/{segment} — "
                + "skipped, not merged; move what it holds into the second directory to keep it.";
            tenant = default;
            return false;
        }

        try
        {
            Directory.Move(directory, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            notice = $"{area}/{name}: written by an earlier release for tenant '{tenant.Value}', and renaming it to {area}/{segment} failed "
                + $"({exception.GetType().Name}) — skipped; rename it by hand to keep it.";
            tenant = default;
            return false;
        }

        TryMark(target);
        notice = $"{area}/{name}: written by an earlier release for tenant '{tenant.Value}' — renamed to {area}/{segment}.";
        path = target;
        return true;
    }

    /// <summary>How an earlier release named a tenant's directory: the tenant <paramref name="segment"/> meant, if any.</summary>
    public delegate bool TryDecodeEarlier(string segment, out TenantId tenant);

    // The resources provider's writer before #467 (ResourceJson.SafeName as it was), kept only to recognise its output.
    private static string EarlierResourceName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        var safe = builder.ToString();
        return safe is "." or ".." ? safe.Replace(".", "%2E", StringComparison.Ordinal) : safe;
    }

    private static void TryMark(string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, MarkerFileName), MarkerText);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unmarked, the directory is resolved by the earlier rule again on the next load, to the same tenant.
        }
    }

    private static bool IsUpperHex(char c) => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F';
}
