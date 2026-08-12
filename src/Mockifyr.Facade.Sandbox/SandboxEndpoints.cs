using Mediant.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Application;
using Mockifyr.Core;

namespace Mockifyr.Facade.Sandbox;

/// <summary>
/// The self-service surface a sandbox key may hold (#347): <c>/__sandbox/*</c>, answering only for the
/// tenant its key belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A separate namespace, not an extension of <c>/__admin</c>. ADR 0011 makes it a binding criterion
/// that <b>a sandbox key never grants admin access</b> — <c>/__admin/*</c> ignores <c>X-Api-Key</c> and
/// bearer tokens entirely, and a wire test asserts it. Teaching that surface to accept a sandbox key
/// for "just a few safe routes" would weaken an invariant somebody may have relied on, and would leave
/// the property true only by inspection of a route list. Standing a second surface beside it keeps the
/// rule literally true, keeps its test green, and makes the boundary something you can see rather than
/// something you have to audit.
/// </para>
/// <para>
/// The tenant comes from the key and <b>only</b> from the key. There is no <c>X-Mockifyr-Tenant</c>
/// header here — not "a header that gets refused", but no header at all, so cross-tenant access is not
/// a check that could be wrong. Reads are the partner's own journal, inbox, resources and environment
/// keys; writes are the between-runs gesture (reset my resources, my inbox, my journal) and nothing
/// that touches another tenant or the host.
/// </para>
/// <para>
/// Without <c>--sandbox-auth</c> there is no way to tell one partner from another, so the whole
/// namespace is absent rather than open.
/// </para>
/// </remarks>
public static class SandboxEndpoints
{
    /// <summary>The prefix this surface owns.</summary>
    public const string Prefix = "/__sandbox";

    /// <summary>Maps the surface. A no-op unless sandbox authentication is configured.</summary>
    public static IEndpointRouteBuilder MapSandboxEndpoints(this IEndpointRouteBuilder endpoints, bool enabled)
    {
        if (!enabled)
        {
            return endpoints;
        }

        var sandbox = endpoints.MapGroup(Prefix);

        sandbox.MapGet("/", (HttpContext context) => Tenant(context) is { } tenant
            ? Results.Json(new
            {
                tenant = tenant.Value,
                // Named so a partner can see what they hold without guessing at routes.
                surfaces = new[] { "requests", "messages", "resources", "environments" },
            })
            : Unauthorized());

        // ---- reads ---------------------------------------------------------------------------

        sandbox.MapGet("/requests", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var unmatchedOnly = context.Request.Query.TryGetValue("unmatched", out var u) && u == "true";
            var result = await sender.Send(new GetServeEventsQuery(tenant, unmatchedOnly));
            return Results.Json(new
            {
                requests = result.Value.Select(e => new
                {
                    id = e.Id,
                    method = e.Request.Method,
                    url = e.Request.Url,
                    status = e.Response?.Status,
                    wasMatched = e.MatchedStub is not null,
                    loggedAt = e.Timestamp,
                }),
            });
        });

        sandbox.MapGet("/messages", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var query = context.Request.Query;
            var result = await sender.Send(new GetMessagesQuery(
                tenant,
                ChannelOf(query["channel"].FirstOrDefault()),
                query["recipient"].FirstOrDefault(),
                query["contains"].FirstOrDefault(),
                query["matches"].FirstOrDefault(),
                int.TryParse(query["limit"].FirstOrDefault(), out var limit) ? limit : null));

            return Results.Json(new { messages = result.Value.Select(MessageJson) });
        });

        // The whole point of a partner reading their inbox: "the code you just sent me". One GET
        // rather than list-then-parse, which is what every integrator would otherwise write.
        sandbox.MapGet("/messages/otp", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var query = context.Request.Query;
            var result = await sender.Send(new ExtractOtpQuery(
                tenant,
                Guid.TryParse(query["id"].FirstOrDefault(), out var id) ? id : null,
                query["recipient"].FirstOrDefault(),
                ChannelOf(query["channel"].FirstOrDefault()),
                query["pattern"].FirstOrDefault()));

            return result.IsSuccess
                ? Results.Json(new { otp = result.Value.Otp, messageId = result.Value.MessageId, receivedAt = result.Value.ReceivedAt })
                : Results.Json(new { error = result.Error.Code, message = result.Error.Description }, statusCode: StatusCodes.Status404NotFound);
        });

        // A partner's own usage (#356). Same numbers the operator sees for this key's tenant, reached
        // with the key the partner already holds — "your calls are failing" is answerable by the person
        // making the calls, without anybody being given an admin credential.
        sandbox.MapGet("/usage", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var hours = context.Request.Query.TryGetValue("hours", out var raw)
                && int.TryParse(raw.FirstOrDefault(), out var parsed)
                    ? parsed
                    : 24;
            var result = await sender.Send(new GetUsageQuery(tenant, hours));
            return Results.Json(new
            {
                keys = result.Value.Select(entry => new
                {
                    name = entry.Name,
                    prefix = entry.Prefix,
                    total = entry.Usage.Total,
                    matched = entry.Usage.Matched,
                    unmatched = entry.Usage.Unmatched,
                    unauthorized = entry.Usage.Unauthorized,
                    rateLimited = entry.Usage.RateLimited,
                    forbidden = entry.Usage.Forbidden,
                    topUnmatchedPaths = entry.Usage.TopUnmatchedPaths.Select(p => new { path = p.Path, count = p.Count }),
                }),
            });
        });

        sandbox.MapGet("/resources", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var result = await sender.Send(new GetResourceCollectionsQuery(tenant));
            return Results.Json(new { collections = result.Value.Select(c => new { name = c.Name, count = c.Count }) });
        });

        sandbox.MapGet("/resources/{collection}", async (string collection, HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var query = context.Request.Query;
            var result = await sender.Send(new ListResourcesQuery(
                collection,
                int.TryParse(query["limit"].FirstOrDefault(), out var limit) ? limit : null,
                int.TryParse(query["offset"].FirstOrDefault(), out var offset) ? offset : null,
                tenant));

            return result.IsSuccess
                ? Results.Json(new { documents = result.Value.Documents.Select(ResourceJson), total = result.Value.Total })
                : Failure(result.Error);
        });

        sandbox.MapGet("/resources/{collection}/{id}", async (string collection, string id, HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var result = await sender.Send(new GetResourceQuery(collection, id, tenant));
            return result.IsSuccess ? Results.Json(ResourceJson(result.Value)) : Failure(result.Error);
        });

        // Values only, and never a secret literal (#348). A partner needs to know which base URL their
        // sandbox currently points at; they have no business with the signing key it holds.
        sandbox.MapGet("/environments", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var result = await sender.Send(new GetEnvironmentsQuery(tenant));
            return Results.Json(new
            {
                environments = result.Value.Select(key => new
                {
                    key = key.Key,
                    activeValue = key.ActiveValue,
                    resolved = key.ResolvesToSecret() ? null : key.Resolve(),
                    secret = key.ResolvesToSecret(),
                }),
            });
        });

        // ---- the between-runs gesture --------------------------------------------------------

        sandbox.MapPost("/resources/reset", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            await sender.Send(new ResetResourcesCommand(Collection: null, tenant));
            return Results.Ok();
        });

        sandbox.MapPost("/resources/{collection}/reset", async (string collection, HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            var result = await sender.Send(new ResetResourcesCommand(collection, tenant));
            return result.IsSuccess ? Results.Ok() : Failure(result.Error);
        });

        sandbox.MapPost("/messages/reset", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            await sender.Send(new ResetMessagesCommand(tenant));
            return Results.Ok();
        });

        sandbox.MapPost("/requests/reset", async (HttpContext context, ISender sender) =>
        {
            if (Tenant(context) is not { } tenant) return Refused(context);
            await sender.Send(new ResetRequestsCommand(tenant));
            return Results.Ok();
        });

        return endpoints;
    }

    /// <summary>
    /// The tenant the presented key belongs to, or null when no valid key was presented.
    /// </summary>
    /// <remarks>
    /// Resolved per request from the key store rather than from anything the caller can state. There is
    /// deliberately no tenant header on this surface: a value a client cannot send is a value a client
    /// cannot forge, which is a stronger property than refusing a forged one correctly.
    /// </remarks>
    private static TenantId? Tenant(HttpContext context)
    {
        if (PresentedKey(context) is not { } presented)
        {
            return null;
        }

        var keys = context.RequestServices.GetRequiredService<IApiKeyStore>();
        var key = keys.GetAll().FirstOrDefault(k => ApiKeyMaterial.Verify(presented, k.Salt, k.Hash));
        if (key is null)
        {
            return null;
        }

        // The lifecycle refusals (#355) name themselves. A partner whose key expired and a partner
        // holding a typo both get 401 today, and they need to do completely different things about it —
        // the first has proved possession of a real credential, so telling them why costs nothing.
        var status = key.StatusAt(DateTimeOffset.UtcNow);
        if (status is not ApiKeyStatus.Active)
        {
            context.Items[RefusalKey] = status == ApiKeyStatus.Expired
                ? Results.Json(
                    new { error = "Sandbox.KeyExpired", message = "This sandbox key has expired — ask for a new one." },
                    statusCode: StatusCodes.Status401Unauthorized)
                : Results.Json(
                    new { error = "Sandbox.KeyRevoked", message = "This sandbox key has been revoked." },
                    statusCode: StatusCodes.Status401Unauthorized);
            return null;
        }

        // Read-only is a 403, not a 401: the credential is fine, the operation is not, and answering
        // 401 would send an integrator to re-check a key that has nothing wrong with it.
        if (key.Scope == ApiKeyScope.ReadOnly && !IsSafe(context.Request.Method))
        {
            context.Items[RefusalKey] = Results.Json(
                new { error = "Sandbox.ReadOnly", message = "This sandbox key may only read." },
                statusCode: StatusCodes.Status403Forbidden);
            return null;
        }

        return key.Tenant;
    }

    /// <summary>Safe methods, in the HTTP sense: they are the ones a read-only key may use.</summary>
    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>The presented credential: <c>X-Api-Key</c> (any casing) or a Bearer token.</summary>
    private static string? PresentedKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var header)
            && header.FirstOrDefault() is { Length: > 0 } apiKey)
        {
            return apiKey;
        }

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>Where a named refusal waits between resolution and the route that reports it.</summary>
    private const string RefusalKey = "mockifyr.sandbox.refusal";

    /// <summary>
    /// The refusal for this request: the specific one resolution recorded, or the generic "present a
    /// key" when there was nothing to be specific about.
    /// </summary>
    private static IResult Refused(HttpContext context) =>
        context.Items.TryGetValue(RefusalKey, out var refusal) && refusal is IResult named
            ? named
            : Unauthorized();

    private static IResult Unauthorized() => Results.Json(
        new
        {
            error = "Sandbox.Unauthorized",
            message = "Present your sandbox key as X-Api-Key or Authorization: Bearer.",
        },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Failure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            "Resource.NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status422UnprocessableEntity,
        });

    private static MessageChannel? ChannelOf(string? channel) => channel?.ToLowerInvariant() switch
    {
        "email" => MessageChannel.Email,
        "sms" => MessageChannel.Sms,
        "broker" => MessageChannel.Broker,
        _ => null,
    };

    private static object MessageJson(MessageEnvelope message) => new
    {
        id = message.Id,
        channel = message.Channel.ToString().ToLowerInvariant(),
        from = message.From,
        to = message.To,
        subject = message.Subject,
        body = message.Body,
        receivedAt = message.ReceivedAt,
    };

    private static object ResourceJson(ResourceDocument document) => new
    {
        id = document.Id,
        collection = document.Collection,
        body = document.Body,
        createdAt = document.CreatedAt,
        updatedAt = document.UpdatedAt,
        version = document.Version,
    };
}
