using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Mockifyr.Core;

namespace Mockifyr.Providers.Sms;

/// <summary>
/// The Twilio SMS provider profile (G18d, ADR 0009): an opt-in emulation of
/// <c>POST /2010-04-01/Accounts/{AccountSid}/Messages.json</c> mounted ahead of the mock-serving
/// fallback. A send parses into an SMS <see cref="MessageEnvelope"/> (captured in the tenant's
/// inbox) and answers a Twilio-shaped message resource the official SDK accepts — protocol mock +
/// capture in one, no stub required. <b>A hand-written stub on the same URL still wins</b>: the
/// profile peeks the engine first and steps aside on a match, so enabling it can never change what
/// an existing stub serves (the as-is rule). Tenant = <c>X-Mockifyr-Tenant</c>, as everywhere.
/// </summary>
public sealed partial class TwilioSmsProfileMiddleware(RequestDelegate next, StubEngine engine, IMessageSink sink)
{
    private const string TenantHeader = "X-Mockifyr-Tenant";

    [GeneratedRegex(@"^/2010-04-01/Accounts/(?<sid>[^/]+)/Messages\.json$")]
    private static partial Regex MessagesPath();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            MessagesPath().Match(context.Request.Path.Value ?? string.Empty) is not { Success: true } match)
        {
            await next(context);
            return;
        }

        // Stub peek: buffer the body, ask the engine, and step aside when a stub matches — the
        // downstream serving path re-reads the rewound body and applies its full behavior
        // (templating, delays, faults, journal) untouched.
        context.Request.EnableBuffering();
        var canonical = await BuildCanonicalAsync(context);
        var tenant = TenantOf(context);
        if (engine.Handle(tenant, canonical).Response is not null)
        {
            context.Request.Body.Position = 0;
            await next(context);
            return;
        }

        context.Request.Body.Position = 0;
        var form = await context.Request.ReadFormAsync();
        string? to = form["To"].FirstOrDefault();
        string? from = form["From"].FirstOrDefault();
        string? messagingServiceSid = form["MessagingServiceSid"].FirstOrDefault();
        string? body = form["Body"].FirstOrDefault();

        // Twilio's own validation order and error codes, so SDK error handling behaves realistically.
        if (string.IsNullOrEmpty(to))
        {
            await WriteErrorAsync(context, 21604, "A 'To' phone number is required.");
            return;
        }

        if (string.IsNullOrEmpty(from) && string.IsNullOrEmpty(messagingServiceSid))
        {
            await WriteErrorAsync(context, 21603, "A 'From' phone number or MessagingServiceSid is required.");
            return;
        }

        if (string.IsNullOrEmpty(body))
        {
            await WriteErrorAsync(context, 21602, "Message body is required.");
            return;
        }

        var accountSid = match.Groups["sid"].Value;
        var messageSid = "SM" + Guid.NewGuid().ToString("N");
        var meta = new Dictionary<string, string>
        {
            ["provider"] = "twilio",
            ["sid"] = messageSid,
            ["accountSid"] = accountSid,
            ["status"] = "queued",
        };
        if (!string.IsNullOrEmpty(messagingServiceSid))
        {
            meta["messagingServiceSid"] = messagingServiceSid;
        }

        sink.Accept(tenant, new MessageEnvelope(
            Guid.NewGuid(), MessageChannel.Sms,
            from ?? messagingServiceSid!, [to],
            Subject: null, body, HtmlBody: null,
            meta, [], DateTimeOffset.UtcNow));

        var now = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss zzz");
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsJsonAsync(new
        {
            sid = messageSid,
            date_created = now,
            date_updated = now,
            date_sent = (string?)null,
            account_sid = accountSid,
            to,
            from,
            messaging_service_sid = messagingServiceSid,
            body,
            status = "queued",
            num_segments = NumSegments(body),
            num_media = "0",
            direction = "outbound-api",
            api_version = "2010-04-01",
            price = (string?)null,
            price_unit = "USD",
            error_code = (int?)null,
            error_message = (string?)null,
            uri = $"/2010-04-01/Accounts/{accountSid}/Messages/{messageSid}.json",
        });
    }

    // GSM segmentation, coarsely: one segment up to 160 chars, then 153-char concatenated parts —
    // enough for realistic-looking resources without modeling the full character-set rules.
    private static string NumSegments(string body) =>
        (body.Length <= 160 ? 1 : (body.Length + 152) / 153).ToString();

    private static TenantId TenantOf(HttpContext context) =>
        context.Request.Headers.TryGetValue(TenantHeader, out var value) && !string.IsNullOrEmpty(value)
            ? new TenantId(value!)
            : TenantId.Default;

    private static async Task<CanonicalRequest> BuildCanonicalAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);
        var headers = context.Request.Headers
            .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value ?? string.Empty)))
            .ToList();
        return CanonicalRequestBuilder.Build(
            context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            headers, buffer.ToArray(), context.Request.Scheme);
    }

    private static Task WriteErrorAsync(HttpContext context, int code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(new
        {
            code,
            message,
            more_info = $"https://www.twilio.com/docs/errors/{code}",
            status = 400,
        });
    }
}
