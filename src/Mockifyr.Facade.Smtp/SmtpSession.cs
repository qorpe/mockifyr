using System.Text;
using Mockifyr.Core;

namespace Mockifyr.Facade.Smtp;

/// <summary>What the transport loop must do after a command's replies are written.</summary>
public enum SmtpAction
{
    Continue,
    ReadData,
    Quit,
}

/// <summary>
/// The per-connection ESMTP state machine (G18b), kept free of sockets so the protocol logic is
/// unit- and mutation-testable in isolation. Command grammar is deliberately lenient (a mock, not
/// an MTA): AUTH is accepted without checking credentials — <b>the username names the tenant</b> —
/// and sequencing errors answer 503 without dropping the connection.
/// </summary>
public sealed class SmtpSession(IMessageSink sink)
{
    private string? _from;
    private readonly List<string> _recipients = [];
    private TenantId _tenant = TenantId.Default;
    private string? _pendingAuth; // "PLAIN" (334 sent, awaiting payload) or "LOGIN-USER"/"LOGIN-PASS"

    /// <summary>The tenant the session resolved (AUTH username, else default). Exposed for tests.</summary>
    public TenantId Tenant => _tenant;

    /// <summary>Handles one command line: the replies to write, and what to do next.</summary>
    public (IReadOnlyList<string> Replies, SmtpAction Action) Handle(string line)
    {
        // A dangling AUTH exchange consumes the next line(s) as payload, not as a command.
        if (_pendingAuth is not null)
        {
            return (AuthPayload(line), SmtpAction.Continue);
        }

        var space = line.IndexOf(' ', StringComparison.Ordinal);
        var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
        var argument = space < 0 ? string.Empty : line[(space + 1)..].Trim();

        switch (verb)
        {
            case "EHLO":
                return (["250-mockifyr", "250-AUTH PLAIN LOGIN", "250 OK"], SmtpAction.Continue);
            case "HELO":
                return (["250 mockifyr"], SmtpAction.Continue);
            case "AUTH":
                return (Auth(argument), SmtpAction.Continue);
            case "MAIL":
                _from = Address(argument);
                return (["250 OK"], SmtpAction.Continue);
            case "RCPT":
                if (Address(argument) is { Length: > 0 } recipient)
                {
                    _recipients.Add(recipient);
                    return (["250 OK"], SmtpAction.Continue);
                }

                return (["501 Syntax: RCPT TO:<address>"], SmtpAction.Continue);
            case "DATA":
                return _recipients.Count == 0
                    ? (["503 RCPT TO first"], SmtpAction.Continue)
                    : (["354 End data with <CR><LF>.<CR><LF>"], SmtpAction.ReadData);
            case "RSET":
                _from = null;
                _recipients.Clear();
                return (["250 OK"], SmtpAction.Continue);
            case "NOOP":
                return (["250 OK"], SmtpAction.Continue);
            case "QUIT":
                return (["221 Bye"], SmtpAction.Quit);
            default:
                return (["502 Command not implemented"], SmtpAction.Continue);
        }
    }

    /// <summary>Accepts a completed DATA payload: parse, capture, reset the envelope for the next message.</summary>
    public string AcceptData(string data)
    {
        var envelope = SmtpEnvelopeFactory.FromMime(data, _from, _recipients);
        sink.Accept(_tenant, envelope);
        _from = null;
        _recipients.Clear();
        return "250 OK: message captured";
    }

    // AUTH PLAIN [payload] / AUTH LOGIN — everything is accepted; only the username matters (tenant).
    private IReadOnlyList<string> Auth(string argument)
    {
        var space = argument.IndexOf(' ', StringComparison.Ordinal);
        var mechanism = (space < 0 ? argument : argument[..space]).ToUpperInvariant();
        var initial = space < 0 ? null : argument[(space + 1)..].Trim();

        switch (mechanism)
        {
            case "PLAIN" when initial is { Length: > 0 }:
                _tenant = TenantFromPlain(initial);
                return ["235 Authentication successful"];
            case "PLAIN":
                _pendingAuth = "PLAIN";
                return ["334 "];
            case "LOGIN":
                _pendingAuth = "LOGIN-USER";
                return ["334 VXNlcm5hbWU6"]; // "Username:"
            default:
                return ["504 Mechanism not supported"];
        }
    }

    private IReadOnlyList<string> AuthPayload(string line)
    {
        switch (_pendingAuth)
        {
            case "PLAIN":
                _pendingAuth = null;
                _tenant = TenantFromPlain(line.Trim());
                return ["235 Authentication successful"];
            case "LOGIN-USER":
                _pendingAuth = "LOGIN-PASS";
                _tenant = TenantFromBase64(line.Trim());
                return ["334 UGFzc3dvcmQ6"]; // "Password:"
            default: // LOGIN-PASS — the password is read and ignored.
                _pendingAuth = null;
                return ["235 Authentication successful"];
        }
    }

    // AUTH PLAIN payload: base64("authzid\0authcid\0password") — the authcid names the tenant. Some
    // clients omit the authzid *field* entirely ("authcid\0password"): the two-part form reads the
    // first field as the user.
    private static TenantId TenantFromPlain(string base64)
    {
        var decoded = Decode(base64);
        var parts = decoded.Split('\0');
        var user = parts.Length switch
        {
            >= 3 => parts[1],
            2 => parts[0],
            _ => string.Empty,
        };
        return user.Length > 0 ? new TenantId(user) : TenantId.Default;
    }

    private static TenantId TenantFromBase64(string base64)
    {
        var user = Decode(base64);
        return user.Length > 0 ? new TenantId(user) : TenantId.Default;
    }

    private static string Decode(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    // MAIL FROM:<a@b> / RCPT TO:<a@b> — take what's inside the angle brackets, tolerate their absence.
    private static string? Address(string argument)
    {
        var colon = argument.IndexOf(':', StringComparison.Ordinal);
        var value = (colon < 0 ? argument : argument[(colon + 1)..]).Trim();
        var open = value.IndexOf('<', StringComparison.Ordinal);
        var close = value.LastIndexOf('>');
        return open >= 0 && close > open ? value[(open + 1)..close] : (value.Length > 0 ? value : null);
    }
}
