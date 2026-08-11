namespace Mockifyr.Core;

/// <summary>
/// Which hosts this instance may call out to (#349): the second half of "the sandbox cannot be used
/// as a way into the network it runs in", the first being the partner principal (#346).
/// </summary>
/// <remarks>
/// <para>
/// Unrestricted by default. A mock server's whole job includes proxying and calling webhooks, so a
/// deny-by-default policy would break every existing host on upgrade — the restriction is something an
/// operator opts into when they put the host somewhere it can reach things it should not.
/// </para>
/// <para>
/// A <b>host</b> list, not a URL list. Paths and query strings are the caller's business and change
/// constantly; the question this answers is "may we open a connection to that machine at all", which
/// is the question a network boundary actually asks.
/// </para>
/// </remarks>
public sealed class OutboundHostPolicy
{
    private readonly List<Entry> _allowed = [];

    private OutboundHostPolicy()
    {
    }

    /// <summary>Builds a policy from configured entries; an empty set means unrestricted.</summary>
    /// <remarks>
    /// Entries are <c>host</c>, <c>host:port</c>, or <c>*.domain</c> (and <c>*.domain:port</c>).
    /// A portless entry allows any port on that host, because an operator naming a machine means the
    /// machine — requiring them to enumerate ports would produce allowlists that are wrong in the
    /// direction of "blocked something legitimate", which is how a control gets turned off.
    /// </remarks>
    public static OutboundHostPolicy From(IEnumerable<string>? entries)
    {
        var policy = new OutboundHostPolicy();
        foreach (var raw in entries ?? [])
        {
            if (Entry.TryParse(raw) is { } entry)
            {
                policy._allowed.Add(entry);
            }
        }

        return policy;
    }

    /// <summary>An unrestricted policy — the default, and what every host has today.</summary>
    public static OutboundHostPolicy Unrestricted { get; } = new();

    /// <summary>Whether any restriction is in force at all.</summary>
    public bool IsRestricted => _allowed.Count > 0;

    /// <summary>The configured entries, for the startup line and the admin surface.</summary>
    public IReadOnlyList<string> Entries => [.. _allowed.Select(e => e.ToString())];

    /// <summary>
    /// Whether this instance may call <paramref name="url"/>. Unrestricted policies allow everything.
    /// </summary>
    /// <remarks>
    /// A URL that does not parse is <b>refused</b> under a restriction. It cannot be checked, and
    /// "we could not tell, so we allowed it" is the failure mode an allowlist exists to remove.
    /// </remarks>
    public bool Allows(string? url)
    {
        if (!IsRestricted)
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var entry in _allowed)
        {
            if (entry.Matches(uri.Host, uri.Port))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The refusal a caller reports, naming the host so an operator can act on it.</summary>
    public string Refusal(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"outbound host '{uri.Host}' is not in the allowlist ({string.Join(", ", Entries)})"
            : $"outbound target '{url}' is not a usable absolute URL, and an allowlist is in force";

    private sealed record Entry(string Host, int? Port, bool Wildcard)
    {
        public static Entry? TryParse(string raw)
        {
            var text = raw.Trim();
            if (text.Length == 0)
            {
                return null;
            }

            int? port = null;
            var colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
            {
                port = parsed;
                text = text[..colon];
            }

            var wildcard = text.StartsWith("*.", StringComparison.Ordinal);
            if (wildcard)
            {
                text = text[2..];
            }

            return text.Length == 0 ? null : new Entry(text, port, wildcard);
        }

        public bool Matches(string host, int port)
        {
            if (Port is { } required && required != port)
            {
                return false;
            }

            // A wildcard covers subdomains only. The apex is a separate entry on purpose: somebody
            // allowing *.internal.example almost never means to allow internal.example itself, and
            // guessing in the permissive direction is the wrong guess for a control like this.
            return Wildcard
                ? host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase)
                : string.Equals(host, Host, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString() =>
            (Wildcard ? "*." : string.Empty) + Host + (Port is { } p ? ":" + p : string.Empty);
    }
}
