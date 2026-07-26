using System.Text;
using Mockifyr.Core;
using Mockifyr.Facade.Smtp;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Unit coverage for the ESMTP state machine (G18b) — the socket-free protocol core. The
/// over-the-wire behavior (real MailKit client end to end) is covered by
/// <c>G18bSmtpCaptureTests</c> in the differential suite.
/// </summary>
public sealed class G18bSmtpSessionTests
{
    private sealed class CapturingSink : IMessageSink
    {
        public readonly List<(TenantId Tenant, MessageEnvelope Message)> Accepted = [];

        public void Accept(TenantId tenant, MessageEnvelope message) => Accepted.Add((tenant, message));
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static void AssertReply(SmtpSession session, string line, string expected, SmtpAction action = SmtpAction.Continue)
    {
        var (replies, actual) = session.Handle(line);
        Assert.Equal(expected, Assert.Single(replies));
        Assert.Equal(action, actual);
    }

    private const string MinimalMime = "Subject: test\r\nFrom: a@b.test\r\n\r\nhello body";

    [Fact]
    public void Ehlo_AdvertisesAuth_AndHeloStaysMinimal()
    {
        var session = new SmtpSession(new CapturingSink());

        var (ehlo, action) = session.Handle("EHLO client.local");
        Assert.Equal(SmtpAction.Continue, action);
        Assert.Equal("250 OK", ehlo[^1]);
        Assert.Contains(ehlo, r => r.Contains("AUTH PLAIN LOGIN"));
        Assert.All(ehlo.Take(ehlo.Count - 1), r => Assert.StartsWith("250-", r));

        var (helo, _) = session.Handle("helo client.local");
        Assert.Equal("250 mockifyr", Assert.Single(helo));
    }

    [Fact]
    public void HappyPath_MailRcptData_CapturesAndResetsTheEnvelope()
    {
        var sink = new CapturingSink();
        var session = new SmtpSession(sink);

        AssertReply(session, "MAIL FROM:<sender@app.test>", "250 OK");
        AssertReply(session, "RCPT TO:<one@x.test>", "250 OK");
        AssertReply(session, "RCPT TO:<two@x.test>", "250 OK");

        var (replies, action) = session.Handle("DATA");
        Assert.Equal(SmtpAction.ReadData, action);
        Assert.StartsWith("354", Assert.Single(replies));

        Assert.StartsWith("250", session.AcceptData(MinimalMime));
        var (tenant, message) = Assert.Single(sink.Accepted);
        Assert.Equal(TenantId.Default, tenant);
        Assert.Equal(["one@x.test", "two@x.test"], message.To);
        Assert.Equal("sender@app.test", message.Meta["envelopeFrom"]);

        // The envelope reset: a second DATA without new RCPTs is refused.
        AssertReply(session, "DATA", "503 RCPT TO first");
    }

    [Fact]
    public void Data_WithoutRecipients_Is503_NotADrop()
    {
        var session = new SmtpSession(new CapturingSink());
        var (replies, action) = session.Handle("DATA");
        Assert.Equal(SmtpAction.Continue, action);
        Assert.StartsWith("503", Assert.Single(replies));
    }

    [Fact]
    public void Rset_ClearsTheEnvelope()
    {
        var session = new SmtpSession(new CapturingSink());
        session.Handle("MAIL FROM:<a@b.test>");
        session.Handle("RCPT TO:<c@d.test>");
        AssertReply(session, "RSET", "250 OK");

        AssertReply(session, "DATA", "503 RCPT TO first");
    }

    [Fact]
    public void UnknownCommand_502_And_Quit_EndsTheSession()
    {
        var session = new SmtpSession(new CapturingSink());
        AssertReply(session, "VRFY someone", "502 Command not implemented");
        AssertReply(session, "NOOP", "250 OK");
        AssertReply(session, "QUIT", "221 Bye", SmtpAction.Quit);
    }

    [Fact]
    public void AuthPlain_Inline_TenantIsTheAuthcid()
    {
        var session = new SmtpSession(new CapturingSink());
        var (replies, _) = session.Handle($"AUTH PLAIN {B64("authz\0acme\0secret")}");
        Assert.StartsWith("235", Assert.Single(replies));
        Assert.Equal(new TenantId("acme"), session.Tenant);
    }

    [Fact]
    public void AuthPlain_TwoStep_ReadsThePayloadLine()
    {
        var session = new SmtpSession(new CapturingSink());
        var (challenge, _) = session.Handle("AUTH PLAIN");
        Assert.StartsWith("334", Assert.Single(challenge));

        var (done, _) = session.Handle(B64("\0globex\0pw"));
        Assert.StartsWith("235", Assert.Single(done));
        Assert.Equal(new TenantId("globex"), session.Tenant);
    }

    [Fact]
    public void AuthLogin_TwoStep_UsernameNamesTheTenant_PasswordIgnored()
    {
        var session = new SmtpSession(new CapturingSink());
        AssertReply(session, "AUTH LOGIN", "334 VXNlcm5hbWU6");
        AssertReply(session, B64("acme"), "334 UGFzc3dvcmQ6");
        var (done, _) = session.Handle(B64("any-password"));
        Assert.StartsWith("235", Assert.Single(done));
        Assert.Equal(new TenantId("acme"), session.Tenant);
    }

    [Fact]
    public void AuthGarbage_FallsBackToTheDefaultTenant()
    {
        var session = new SmtpSession(new CapturingSink());
        session.Handle("AUTH PLAIN");
        var (done, _) = session.Handle("!!!not-base64!!!");
        Assert.StartsWith("235", Assert.Single(done));
        Assert.Equal(TenantId.Default, session.Tenant);
    }

    [Fact]
    public void AuthPlain_TwoPartPayload_ReadsTheFirstFieldAsTheUser()
    {
        // Some clients omit the authzid field entirely: "authcid\0password".
        var session = new SmtpSession(new CapturingSink());
        session.Handle("AUTH PLAIN");
        var (done, _) = session.Handle(B64("acme\0pw"));
        Assert.StartsWith("235", Assert.Single(done));
        Assert.Equal(new TenantId("acme"), session.Tenant);
    }

    [Fact]
    public void AuthLogin_EmptyUsername_StaysOnTheDefaultTenant()
    {
        var session = new SmtpSession(new CapturingSink());
        session.Handle("AUTH LOGIN");
        session.Handle(B64(string.Empty));
        session.Handle(B64("pw"));
        Assert.Equal(TenantId.Default, session.Tenant);
    }

    [Fact]
    public void AuthLogin_GarbageUsername_StaysOnTheDefaultTenant()
    {
        var session = new SmtpSession(new CapturingSink());
        session.Handle("AUTH LOGIN");
        session.Handle("!!!not-base64!!!");
        session.Handle(B64("pw"));
        Assert.Equal(TenantId.Default, session.Tenant);
    }

    [Fact]
    public void UnknownAuthMechanism_Is504()
    {
        var session = new SmtpSession(new CapturingSink());
        var (replies, _) = session.Handle("AUTH CRAM-MD5");
        Assert.StartsWith("504", Assert.Single(replies));
        Assert.Equal(TenantId.Default, session.Tenant);
    }

    [Theory]
    [InlineData("RCPT TO:<user@x.test>", "user@x.test")]
    [InlineData("RCPT TO: user@x.test", "user@x.test")] // bracketless tolerated
    [InlineData("rcpt to:<User@X.test>", "User@X.test")] // verb case-insensitive, address preserved
    [InlineData("RCPT TO:<unclosed", "<unclosed")] // a broken bracket falls back to the raw value
    public void RcptAddress_Forms(string line, string expected)
    {
        var sink = new CapturingSink();
        var session = new SmtpSession(sink);
        session.Handle("MAIL FROM:<a@b.test>");
        var (replies, _) = session.Handle(line);
        Assert.StartsWith("250", Assert.Single(replies));

        session.Handle("DATA");
        session.AcceptData(MinimalMime);
        Assert.Equal(expected, Assert.Single(sink.Accepted).Message.To.Single());
    }

    [Fact]
    public void BareMail_LeavesNoEnvelopeFrom()
    {
        // "MAIL" with no argument must not invent a sender: the capture carries no envelopeFrom.
        var sink = new CapturingSink();
        var session = new SmtpSession(sink);
        session.Handle("MAIL");
        session.Handle("RCPT TO:<a@x.test>");
        session.Handle("DATA");
        session.AcceptData(MinimalMime);

        Assert.False(Assert.Single(sink.Accepted).Message.Meta.ContainsKey("envelopeFrom"));
    }

    [Fact]
    public void EmptyRcpt_Is501()
    {
        var session = new SmtpSession(new CapturingSink());
        session.Handle("MAIL FROM:<a@b.test>");
        var (replies, _) = session.Handle("RCPT TO:");
        Assert.StartsWith("501", Assert.Single(replies));
    }

    // ---- Envelope factory ---------------------------------------------------------------------

    [Fact]
    public void EnvelopeFactory_ParsesMime_AndPrefersHeaderFrom()
    {
        var envelope = SmtpEnvelopeFactory.FromMime(
            "Subject: Hi\r\nFrom: Display Name <header@x.test>\r\nTo: shown@x.test\r\n\r\nbody text",
            envelopeFrom: "envelope@x.test", recipients: ["real@x.test"]);

        Assert.Equal(MessageChannel.Email, envelope.Channel);
        Assert.Equal("header@x.test", envelope.From);
        Assert.Equal(["real@x.test"], envelope.To); // envelope recipients, not the To: header
        Assert.Equal("Hi", envelope.Subject);
        Assert.Equal("body text", envelope.Body);
        Assert.Equal("envelope@x.test", envelope.Meta["envelopeFrom"]);
        Assert.Equal("shown@x.test", envelope.Meta["headerTo"]);
    }

    [Fact]
    public void EnvelopeFactory_UnparseableMime_StillCapturesRaw()
    {
        var envelope = SmtpEnvelopeFactory.FromMime("\0\0garbage", "from@x.test", ["to@x.test"]);

        Assert.Equal("from@x.test", envelope.From);
        Assert.Equal(["to@x.test"], envelope.To);
        Assert.NotEmpty(envelope.Body);
    }
}
