using MimeKit;
using Mockifyr.Core;

namespace Mockifyr.Facade.Smtp;

/// <summary>
/// Translates an accepted DATA payload into the transport-neutral <see cref="MessageEnvelope"/>
/// (G18b). The <b>envelope</b> recipients (RCPT TO) are the truth about who received the mail — not
/// the To: header, which may be a display list — so <c>To</c> comes from the transaction and the
/// header goes to <c>Meta</c>. Malformed MIME still captures: a mock must never lose a message a
/// real client managed to send.
/// </summary>
public static class SmtpEnvelopeFactory
{
    /// <summary>Builds the envelope from the MIME text plus the SMTP transaction's from/recipients.</summary>
    public static MessageEnvelope FromMime(string mimeText, string? envelopeFrom, IReadOnlyList<string> recipients)
    {
        MimeMessage? mime = null;
        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(mimeText));
            mime = MimeMessage.Load(stream);
        }
        catch (Exception)
        {
            // Unparseable MIME: fall through to a raw-body capture below.
        }

        var meta = new Dictionary<string, string>();
        if (envelopeFrom is { Length: > 0 })
        {
            meta["envelopeFrom"] = envelopeFrom;
        }

        if (mime is null)
        {
            return new MessageEnvelope(
                Guid.NewGuid(), MessageChannel.Email,
                envelopeFrom ?? string.Empty, [.. recipients],
                Subject: null, Body: mimeText, HtmlBody: null,
                meta, [], DateTimeOffset.UtcNow, Raw: mimeText);
        }

        AddIfPresent(meta, "messageId", mime.MessageId);
        AddIfPresent(meta, "headerTo", mime.To.ToString());
        AddIfPresent(meta, "cc", mime.Cc.ToString());
        AddIfPresent(meta, "replyTo", mime.ReplyTo.ToString());

        var attachments = mime.Attachments.Select(entity =>
        {
            using var content = new MemoryStream();
            if (entity is MimePart { Content: { } body })
            {
                body.DecodeTo(content);
            }
            else
            {
                entity.WriteTo(content);
            }

            return new MessageAttachment(
                entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? "attachment",
                entity.ContentType.MimeType,
                content.ToArray());
        }).ToList();

        return new MessageEnvelope(
            Guid.NewGuid(), MessageChannel.Email,
            mime.From.Mailboxes.FirstOrDefault()?.Address ?? envelopeFrom ?? string.Empty,
            [.. recipients],
            mime.Subject is { Length: > 0 } subject ? subject : null,
            TrimTransportNewline(mime.TextBody) ?? string.Empty,
            TrimTransportNewline(mime.HtmlBody),
            meta, attachments, DateTimeOffset.UtcNow, Raw: mimeText);
    }

    // SMTP terminates the DATA body with CRLF, which MIME decoders surface as one trailing newline
    // the sender never wrote. Trim exactly that — inner newlines are content and stay.
    private static string? TrimTransportNewline(string? body) =>
        body is null ? null
        : body.EndsWith("\r\n", StringComparison.Ordinal) ? body[..^2]
        : body.EndsWith('\n') ? body[..^1]
        : body;

    private static void AddIfPresent(Dictionary<string, string> meta, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            meta[key] = value;
        }
    }
}
