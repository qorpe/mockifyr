using System.Text.Json.Nodes;
using Mediant.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Adapters.OpenApi;
using Mockifyr.Application;
using Mockifyr.Core;
using Mockifyr.Outbound;

namespace Mockifyr.Facade.Admin;

/// <summary>
/// The admin HTTP surface (G7b), whose routes and JSON shapes match the stub-format dialect Mockifyr
/// imports so existing tooling interoperates (verified by the differential suite). Each route is a thin translation of an HTTP
/// request into a Mediant command/query on <see cref="ISender"/>; all logic lives in
/// <c>Mockifyr.Application</c>. Every route is scoped to the tenant named by the <c>X-Mockifyr-Tenant</c>
/// header (the same header the mock-serving facade honours); an absent header resolves to the default
/// tenant, so single-tenant callers are unaffected.
/// </summary>
public static class AdminEndpoints
{
    private const string TenantHeader = "X-Mockifyr-Tenant";

    /// <summary>Resolves the request's tenant from <c>X-Mockifyr-Tenant</c>, defaulting when absent.</summary>
    private static TenantId TenantOf(HttpRequest request) =>
        request.Headers.TryGetValue(TenantHeader, out var value) && !string.IsNullOrEmpty(value)
            ? new TenantId(value!)
            : TenantId.Default;

    /// <summary>Flattens multi-valued headers into name/value pairs for the journal detail view.</summary>
    private static object HeaderPairs(ILookup<string, string> headers) =>
        headers.Select(g => new { name = g.Key, value = string.Join(", ", g) }).ToArray();

    /// <summary>Decodes a body for display; bodies in the journal are already materialised in memory.</summary>
    private static string Utf8(byte[] body) => System.Text.Encoding.UTF8.GetString(body);

    /// <summary>
    /// Projects a serve event's callbacks for the journal detail. Deliveries recorded as sub-events
    /// (WEBHOOK_REQUEST + the paired WEBHOOK_RESPONSE / ERROR, in append order — the listener sends the
    /// stub's webhooks sequentially) win over the configured definitions; a definition beyond the
    /// recorded deliveries (still in flight or delayed) is shown as-configured with <c>delivered: false</c>.
    /// </summary>
    /// <summary>
    /// The broker messages this request published, and any that failed (ADR 0013). Beside the webhooks
    /// rather than mixed into them: a reader debugging "did the event go out?" is asking about a
    /// different system than "did the callback land?", and one list of two kinds would answer neither
    /// question quickly.
    /// </summary>
    /// <summary>Whether this host can actually publish, which decides whether a `publish` action is a gap.</summary>
    /// <remarks>
    /// Asked of the container rather than of configuration: the publisher is what does the work, so its
    /// presence is the fact, and a future broker that is wired differently cannot drift from this answer.
    /// </remarks>
    private static bool BrokerConfigured(IServiceProvider services) =>
        services.GetService<Facade.Broker.IBrokerPublisher>() is not null;

    private static IReadOnlyList<object> JournalPublishes(ServeEvent e) =>
    [
        .. e.SubEvents
            .Where(sub => sub.Type is Facade.Broker.PublishServeEventListener.PublishedType
                or Facade.Broker.PublishServeEventListener.FailedType)
            .Select(sub => sub.Data switch
            {
                Facade.Broker.PublishData published => (object)new
                {
                    topic = published.Topic,
                    key = published.Key,
                    body = published.Body,
                    delivered = true,
                    error = (string?)null,
                },
                Facade.Broker.PublishErrorData failed => new
                {
                    topic = failed.Topic,

                    // What it was carrying, not just that it failed. Null here means rendering itself
                    // failed, so there was never a message — which the error text says.
                    key = failed.Key,
                    body = failed.Body,
                    delivered = false,
                    error = (string?)failed.Error,
                },
                _ => new { topic = "", key = (string?)null, body = (string?)null, delivered = false, error = (string?)null },
            }),
    ];

    private static IReadOnlyList<object> JournalWebhooks(ServeEvent e)
    {
        var definitions = e.MatchedStub?.Webhooks ?? [];
        var items = new List<object>();
        WebhookRequestData? pendingRequest = null;

        void Flush(object? response, string? error)
        {
            if (pendingRequest is not { } req)
            {
                return;
            }

            items.Add(new
            {
                method = req.Method,
                url = req.Url,
                headers = req.Headers.Select(h => new { name = h.Key, value = h.Value }),
                body = req.Body is null ? null : Utf8(req.Body),
                delivered = true,
                response,
                error,
            });
            pendingRequest = null;
        }

        foreach (var sub in e.SubEvents)
        {
            switch (sub.Type, sub.Data)
            {
                case (SubEvent.WebhookRequestType, WebhookRequestData request):
                    Flush(response: null, error: null); // a request with no outcome yet (in flight)
                    pendingRequest = request;
                    break;
                case (SubEvent.WebhookResponseType, WebhookResponseData response):
                    Flush(
                        response: new
                        {
                            status = response.Status,
                            headers = response.Headers.Select(h => new { name = h.Key, value = h.Value }),
                            body = response.Body is null ? null : Utf8(response.Body),
                        },
                        error: null);
                    break;
                case (SubEvent.ErrorType, WebhookErrorData failure):
                    if (pendingRequest is not null)
                    {
                        Flush(response: null, error: failure.Message);
                    }
                    else if (items.Count < definitions.Count)
                    {
                        // The delivery died before a request was even built (e.g. the template failed
                        // to render): show the configured definition carrying the error.
                        var w = definitions[items.Count];
                        items.Add(new
                        {
                            method = w.Method,
                            url = w.Url,
                            headers = w.Headers.Select(h => new { name = h.Key, value = h.Value }),
                            body = w.Body is null ? null : Utf8(w.Body),
                            delivered = false,
                            response = (object?)null,
                            error = failure.Message,
                        });
                    }

                    break;
            }
        }

        Flush(response: null, error: null);

        // Definitions beyond the recorded deliveries have not fired yet — show the configured template.
        foreach (var w in definitions.Skip(items.Count))
        {
            items.Add(new
            {
                method = w.Method,
                url = w.Url,
                headers = w.Headers.Select(h => new { name = h.Key, value = h.Value }),
                body = w.Body is null ? null : Utf8(w.Body),
                delivered = false,
                response = (object?)null,
                error = (string?)null,
            });
        }

        return items;
    }

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/__admin");

        // Host status for the dashboard's Settings/Status screen: the active persistence provider and
        // live tenant/stub counts, gathered from DI. Host-config knobs (TLS, ports) are set by CLI flags
        // at startup and aren't admin-mutable, so they are documented in the UI rather than reported here.
        admin.MapGet("/health", (
            IStubStore store,
            IStubPersistence persistence,
            IEnumerable<IPayloadDecryptor> decryptors,
            IEnumerable<IPayloadProtector> protectors,
            IEnumerable<ISignatureVerifier> verifiers,
            IEnumerable<IResponseSigner> signers,
            IAuditLog auditLog,
            ActiveKeyReport keys,
            AdminAuthDescriptor auth) =>
        {
            var tenants = store.GetTenants();
            return Results.Json(new
            {
                name = "Mockifyr",
                // The assembly's informational version, so an operator reading health knows which
                // build answered. It used to be hard-coded "1.0" — harmless while the product was
                // 0.x and actively misleading the moment it is not.
                version = MockifyrVersion.Current,
                persistence = persistence.ProviderName,
                tenants = tenants.Count,
                totalStubs = tenants.Sum(t => store.GetStubs(t).Count),
                // Cryptography capabilities (G20e): a stub can declare decrypt/protect/sign, but
                // whether the host can honor it depends on the keys it was started with. Reporting
                // it here is what lets the dashboard say so instead of leaving an operator to
                // discover it from a stub that mysteriously never matches.
                cryptography = new
                {
                    payloadDecryption = decryptors.Any(),
                    responseProtection = protectors.Any(),
                    signatureVerification = verifiers.Any(),
                    responseSigning = signers.Any(),
                    // How many keys are currently active per capability (#250) — never the keys
                    // themselves. During a rollover this is how an operator confirms the new key
                    // was picked up without restarting anything, and it is what turns "rotate and
                    // hope" into "rotate and check".
                    decryptKeys = keys.ActiveDecryptKeys,
                    signKeys = keys.ActiveSignKeys,
                },
                // Whether this host records administrative changes (#247). An empty trail is
                // ambiguous on its own — "nothing has changed" and "nobody is recording" look
                // identical — so the dashboard needs to be told which it is.
                audit = auditLog is not NullAuditLog,
                // How to sign in (#251). Necessarily unauthenticated: a login screen cannot
                // authenticate before it knows where to send the user. Only the public parameters of
                // a public OIDC client — the authority and the client id — never a secret.
                auth = new { mode = auth.Mode, authority = auth.Authority, clientId = auth.ClientId },
            });
        });

        // The tenants that currently exist server-side (a tenant materializes once it has stubs), so the
        // dashboard's switcher can surface tenants created via the API alongside the operator's own list.
        admin.MapGet("/tenants", (IStubStore store) =>
            Results.Json(new { tenants = store.GetTenants().Select(t => t.Value).OrderBy(v => v) }));

        admin.MapGet("/mappings", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetStubsQuery(TenantOf(request)));
            // The protocol is computed per response, never stored (ADR 0010) — like id/uuid, it is
            // presentation the exporter tolerates, and the stub's Source stays byte-identical.
            var probe = request.HttpContext.RequestServices.GetService(typeof(IStubProtocolProbe)) as IStubProtocolProbe;
            var mappings = result.Value.Select(stub =>
            {
                var node = FullMapping(stub);
                node["protocol"] = StubProtocols.Classify((node as JsonObject)!, probe);
                return node;
            }).ToList();
            return Results.Json(new { mappings });
        });

        admin.MapPost("/mappings", async (HttpRequest request, ISender sender, IServiceProvider services) =>
        {
            var body = await ReadBody(request);
            var result = await sender.Send(new CreateStubCommand(body, TenantOf(request)));
            if (!result.IsSuccess)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }

            // Fields this engine accepts but does not act on are reported rather than ignored. The
            // stub is still created — refusing it would break importing a mapping set written for the
            // reference engine, which is the point of accepting the dialect. The goal is to be loud,
            // not strict: a `bodyFileName` stub used to match and return an empty body, which reads
            // as a matching bug and is not one.
            var warnings = UnsupportedFieldWarnings.For(body, BrokerConfigured(services));
            return warnings.Count == 0
                ? Results.Json(new { id = result.Value, uuid = result.Value }, statusCode: StatusCodes.Status201Created)
                : Results.Json(
                    new { id = result.Value, uuid = result.Value, warnings },
                    statusCode: StatusCodes.Status201Created);
        });

        admin.MapGet("/mappings/{id:guid}", async (Guid id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetStubQuery(id, TenantOf(request)));
            return result.IsSuccess ? Results.Json(new { id = result.Value.Id }) : Results.NotFound();
        });

        admin.MapPut("/mappings/{id:guid}", async (Guid id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new UpdateStubCommand(id, await ReadBody(request), TenantOf(request)));
            return result.IsSuccess
                ? Results.Json(new { id, uuid = id })
                : Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
        });

        admin.MapDelete("/mappings/{id:guid}", async (Guid id, HttpRequest request, ISender sender) =>
        {
            await sender.Send(new DeleteStubCommand(id, TenantOf(request)));
            return Results.Ok();
        });

        admin.MapPost("/mappings/import", async (HttpRequest request, ISender sender, IServiceProvider services) =>
        {
            var body = await ReadBody(request);
            var result = await sender.Send(new ImportMappingsCommand(body, TenantOf(request)));
            if (!result.IsSuccess)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }

            // A bundle is where deferred fields hide best — nobody reads 200 stubs. Warnings are
            // de-duplicated, so the same gap across the whole bundle is reported as one fact.
            var warnings = UnsupportedFieldWarnings.For(body, BrokerConfigured(services));
            return warnings.Count == 0 ? Results.Ok() : Results.Json(new { warnings });
        });

        admin.MapPost("/mappings/reset", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetMappingsCommand(TenantOf(request)));
            return Results.Ok();
        });

        // The consumer side of conformance (#287): what clients actually sent, against what the contract
        // allows. Symmetric with /__admin/recordings/verify, which asks about the mock rather than the
        // client.
        admin.MapPost("/requests/verify", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new VerifyTrafficQuery(await ReadBody(request), TenantOf(request)));
            if (!result.IsSuccess)
            {
                return Results.Json(
                    new { error = result.Error.Code, message = result.Error.Description },
                    statusCode: result.Error.Code == "OpenApi.TooLarge"
                        ? StatusCodes.Status413PayloadTooLarge
                        : StatusCodes.Status422UnprocessableEntity);
            }

            var report = result.Value;
            return Results.Json(new
            {
                conforms = report.Conforms,
                requestsExamined = report.RequestsExamined,
                requestsConforming = report.RequestsConforming,
                findings = report.Findings.Select(f => new
                {
                    kind = TrafficDriftKindName(f.Kind),
                    method = f.Method,
                    url = f.Url,
                    detail = f.Detail,
                }),
            });
        });

        // Near-miss diagnostics (#288). Deliberately an admin query rather than a 404 body: the served
        // response stays byte-identical to what the differential suite proves, and computing a
        // diagnostic never touches the serve path.
        admin.MapGet("/requests/{id}/near-misses", async (string id, HttpRequest request, ISender sender) =>
        {
            if (!Guid.TryParse(id, out var eventId))
            {
                return Results.NotFound();
            }

            var tenant = TenantOf(request);
            var found = await sender.Send(new GetServeEventQuery(eventId, tenant));
            if (found.Value is not { } serveEvent)
            {
                return Results.NotFound();
            }

            var misses = await sender.Send(new FindNearMissesQuery(serveEvent.Request, tenant));
            return Results.Json(new
            {
                wasMatched = serveEvent.MatchedStub is not null,
                nearMisses = misses.Value.Select(NearMissJson),
            });
        });

        admin.MapPost("/near-misses/request", async (HttpRequest request, ISender sender) =>
        {
            CanonicalRequest candidate;
            try
            {
                candidate = ReadCandidateRequest(await ReadBody(request));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                return Results.Json(
                    new { errors = new[] { new { code = "NearMiss.InvalidBody", message = "Expected {method, url, headers?, body?}." } } },
                    statusCode: 422);
            }

            var result = await sender.Send(new FindNearMissesQuery(candidate, TenantOf(request)));
            return Results.Json(new { nearMisses = result.Value.Select(NearMissJson) });
        });

        // Contract conformance (#287): does this stub set still tell the truth about the specification?
        // A report, never a mutation — which side is wrong is a judgement about the caller's system.
        admin.MapPost("/openapi/verify", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new VerifyContractQuery(await ReadBody(request), TenantOf(request)));
            if (!result.IsSuccess)
            {
                // The same typed refusals the importer gives, for the same document problems: a spec
                // that cannot be imported cannot be verified against either, and saying so the same way
                // twice is one thing to learn rather than two.
                return Results.Json(
                    new { error = result.Error.Code, message = result.Error.Description },
                    statusCode: result.Error.Code == "OpenApi.TooLarge"
                        ? StatusCodes.Status413PayloadTooLarge
                        : StatusCodes.Status422UnprocessableEntity);
            }

            var report = result.Value;
            return Results.Json(new
            {
                conforms = report.Conforms,
                operationsInSpec = report.OperationsInSpec,
                operationsCovered = report.OperationsCovered,
                findings = report.Findings.Select(f => new
                {
                    kind = DriftKindName(f.Kind),
                    method = f.Method,
                    path = f.Path,
                    stubId = f.StubId,
                    detail = f.Detail,
                }),
            });
        });

        // Degradation profiles (#289): what the whole dependency is doing, rather than what one stub
        // declares. Scoped to the tenant, because degrading a shared host for everybody is the failure
        // this exists to avoid.
        admin.MapGet("/degradation", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetDegradationQuery(TenantOf(request)));
            return Results.Json(DegradationJson(result.Value));
        });

        admin.MapPut("/degradation", async (HttpRequest request, ISender sender) =>
        {
            DegradationProfile parsed;
            try
            {
                parsed = ReadDegradation(await ReadBody(request));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException)
            {
                return DegradationFailure("Degradation.InvalidBody", "The degradation profile JSON is malformed.");
            }
            catch (InvalidOperationException ex)
            {
                return DegradationFailure("Degradation.OutOfRange", ex.Message);
            }

            var result = await sender.Send(new SetDegradationCommand(parsed, TenantOf(request)));
            return result.IsSuccess ? Results.Json(DegradationJson(parsed)) : DegradationFailure(
                result.Error.Code, result.Error.Description);
        });

        admin.MapDelete("/degradation", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ClearDegradationCommand(TenantOf(request)));
            return Results.Ok();
        });

        // Tenant clock control (#290). Deliberately not a mapping-JSON concept: a stub says what it
        // renders, the tenant says what time it is.
        admin.MapGet("/clock", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetClockQuery(TenantOf(request)));
            return Results.Json(ClockJson(result.Value));
        });

        admin.MapPut("/clock", async (HttpRequest request, ISender sender) =>
        {
            ClockOverride parsed;
            try
            {
                parsed = ReadClock(await ReadBody(request));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
            {
                return ClockFailure(Mediant.Results.Error.Validation(
                    "Clock.InvalidBody", "Expected {\"frozenAt\": \"<ISO-8601>\"} or {\"offsetSeconds\": <number>}."));
            }
            catch (InvalidOperationException ex)
            {
                return ClockFailure(Mediant.Results.Error.Validation("Clock.Ambiguous", ex.Message));
            }

            var result = await sender.Send(new SetClockCommand(parsed, TenantOf(request)));
            return result.IsSuccess ? Results.Json(ClockJson(parsed)) : ClockFailure(result.Error);
        });

        admin.MapDelete("/clock", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ClearClockCommand(TenantOf(request)));
            return Results.Ok();
        });

        // The reference engine spells journal reset as DELETE on the collection (its
        // /__admin/requests/reset answers 404), so the dialect is matched rather than invented.
        admin.MapDelete("/requests", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetRequestsCommand(TenantOf(request)));
            return Results.Ok();
        });

        admin.MapPost("/requests/count", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new CountRequestsQuery(await ReadBody(request), TenantOf(request)));
            return Results.Json(new { count = result.Value });
        });

        admin.MapGet("/requests", async (HttpRequest request, ISender sender) =>
        {
            var unmatchedOnly = request.Query.TryGetValue("unmatched", out var u) && u == "true";
            var result = await sender.Send(new GetServeEventsQuery(TenantOf(request), unmatchedOnly));
            // The journal classifies like the stub list (ADR 0010): computed per response, never
            // stored — so gRPC calls, GraphQL posts and SMS-profile sends read as what they are.
            var probe = request.HttpContext.RequestServices.GetService(typeof(IStubProtocolProbe)) as IStubProtocolProbe;
            return Results.Json(new
            {
                requests = result.Value.Select(e => new
                {
                    id = e.Id,
                    method = e.Request.Method,
                    url = e.Request.Url,
                    protocol = JournalProtocol(e, probe),
                    status = e.Response?.Status,
                    wasMatched = e.MatchedStub is not null,
                    stubId = e.MatchedStub?.Id,
                    loggedDate = e.Timestamp,
                }),
            });
        });

        // Full detail for one journal entry (backs the dashboard's Request/Response/Callback tabs). The
        // list stays lean; headers + bodies are fetched on demand here. Webhooks show the actual
        // deliveries recorded as sub-events (rendered request + the target's response or the delivery
        // error); a callback not yet fired (in flight, delayed) falls back to the stub's configured
        // template, flagged `delivered: false`.
        admin.MapGet("/requests/{id}", async (string id, HttpRequest request, ISender sender) =>
        {
            if (!Guid.TryParse(id, out var eventId))
            {
                return Results.NotFound();
            }

            // Indexed lookup (#220): the journal resolves the id directly instead of materializing
            // the whole log and scanning — the detail route stays O(1) as traffic grows.
            var result = await sender.Send(new GetServeEventQuery(eventId, TenantOf(request)));
            var e = result.Value;
            if (e is null)
            {
                return Results.NotFound();
            }

            return Results.Json(new
            {
                id = e.Id,
                loggedDate = e.Timestamp,
                wasMatched = e.MatchedStub is not null,
                stubId = e.MatchedStub?.Id,
                request = new
                {
                    method = e.Request.Method,
                    url = e.Request.Url,
                    headers = HeaderPairs(e.Request.Headers),
                    body = Utf8(e.Request.Body),
                },
                response = e.Response is null ? null : new
                {
                    status = e.Response.Status,
                    statusMessage = e.Response.StatusMessage,
                    headers = HeaderPairs(e.Response.Headers),
                    body = Utf8(e.Response.Body),
                },
                webhooks = JournalWebhooks(e),
                publishes = JournalPublishes(e),
            });
        });

        admin.MapGet("/scenarios", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetScenariosQuery(TenantOf(request)));
            return Results.Json(new
            {
                scenarios = result.Value.Select(s => new { id = s.Name, name = s.Name, state = s.State, possibleStates = s.PossibleStates }),
            });
        });

        admin.MapPost("/scenarios/reset", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetScenariosCommand(TenantOf(request)));
            return Results.Ok();
        });

        admin.MapPut("/scenarios/{name}/state", async (string name, HttpRequest request, ISender sender) =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
            var state = doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() ?? "Started" : "Started";
            await sender.Send(new SetScenarioStateCommand(name, state, TenantOf(request)));
            return Results.Ok();
        });

        // Environments (G17, issues #165/#166): tenant-scoped key/value config resolved into stubs at
        // serve time. Every route derives its tenant from TenantOf(request) and passes it to the
        // handler, so isolation is enforced here at the API — not merely by filtering in the dashboard.
        admin.MapGet("/environments", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetEnvironmentsQuery(TenantOf(request)));
            return Results.Json(new { environments = result.Value.Select(EnvironmentJson) });
        });

        admin.MapPut("/environments/{key}", async (string key, HttpRequest request, ISender sender) =>
        {
            EnvironmentKey parsed;
            try
            {
                parsed = ReadEnvironmentKey(key, await ReadBody(request));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                return EnvironmentFailure(Mediant.Results.Error.Validation(
                    "Environment.InvalidBody", "The environment key JSON is malformed."));
            }

            var result = await sender.Send(new PutEnvironmentKeyCommand(parsed, TenantOf(request)));
            return result.IsSuccess ? Results.Json(EnvironmentJson(parsed)) : EnvironmentFailure(result.Error);
        });

        admin.MapPut("/environments/{key}/active", async (string key, HttpRequest request, ISender sender) =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
            var active = doc.RootElement.TryGetProperty("activeValue", out var a) ? a.GetString() : null;
            if (active is null)
            {
                return EnvironmentFailure(Mediant.Results.Error.Validation(
                    "Environment.InvalidBody", "Expected an 'activeValue' field."));
            }

            var result = await sender.Send(new SetEnvironmentActiveValueCommand(key, active, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : EnvironmentFailure(result.Error);
        });

        admin.MapDelete("/environments/{key}", async (string key, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new DeleteEnvironmentKeyCommand(key, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : EnvironmentFailure(result.Error);
        });

        // Sandbox access (G19d, ADR 0011): operator-issued API keys. The token appears in the
        // issue response ONCE; every later view carries only the display prefix. These endpoints
        // never accept a sandbox key as authentication — admin auth stays --admin-user/--admin-pass.
        // Backup and restore (#252). One archive of everything the tenant's operator authored, and a
        // restore that replaces rather than merges — a restored host is the host that was backed up,
        // not a union with whatever happened to be there.
        admin.MapGet("/backup", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new CreateBackupQuery(TenantOf(request)));
            // Sent as a downloadable file: an archive is something an operator keeps, and the tenant
            // plus timestamp in the name are what makes a directory of them navigable a year later.
            var name = $"mockifyr-backup-{TenantOf(request).Value}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
            request.HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            return Results.Text(BackupJson.Write(result.Value), "application/json");
        });

        admin.MapPost("/restore", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new RestoreBackupCommand(await ReadBody(request), TenantOf(request)));
            return result.IsSuccess
                ? Results.Json(new
                {
                    restored = new
                    {
                        mappings = result.Value.Mappings,
                        environments = result.Value.Environments,
                        resources = result.Value.Resources,
                        apiKeys = result.Value.ApiKeys,
                        scenarios = result.Value.Scenarios,
                    },
                })
                : Results.Json(new { error = result.Error.Code, message = result.Error.Description },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
        });

        // The audit trail (#247) is read-only here by design: entries are appended by the host as a
        // side effect of the change they describe, so nothing on the admin API can rewrite history.
        admin.MapGet("/audit", async (HttpRequest request, ISender sender) =>
        {
            var limit = int.TryParse(request.Query["limit"], out var parsed) ? parsed : (int?)null;
            var result = await sender.Send(new GetAuditEntriesQuery(TenantOf(request), limit));
            return Results.Json(new
            {
                entries = result.Value.Select(entry => new
                {
                    id = entry.Id,
                    timestamp = entry.Timestamp,
                    principal = entry.Principal,
                    tenant = entry.Tenant.Value,
                    action = entry.Action,
                    target = entry.Target,
                    outcome = entry.Outcome,
                }),
            });
        });

        admin.MapGet("/apikeys", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetApiKeysQuery(TenantOf(request)));
            return Results.Json(new { keys = result.Value.Select(entry => ApiKeyJson(entry.Key, entry.Used)) });
        });

        admin.MapPost("/apikeys", async (HttpRequest request, ISender sender) =>
        {
            string? name = null;
            int? quota = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
                name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                quota = doc.RootElement.TryGetProperty("quotaPerHour", out var q) && q.TryGetInt32(out var parsed)
                    ? parsed
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
            }

            var result = await sender.Send(new IssueApiKeyCommand(name ?? string.Empty, quota, TenantOf(request)));
            return result.IsSuccess
                ? Results.Json(new
                {
                    id = result.Value.Key.Id,
                    key = result.Value.Token,
                    prefix = result.Value.Key.Prefix,
                    name = result.Value.Key.Name,
                    quotaPerHour = result.Value.Key.QuotaPerHour,
                    createdAt = result.Value.Key.CreatedAt,
                }, statusCode: StatusCodes.Status201Created)
                : ApiKeyFailure(result.Error);
        });

        admin.MapDelete("/apikeys/{id}", async (string id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new RevokeApiKeyCommand(id, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : ApiKeyFailure(result.Error);
        });

        // OpenAPI import (G19c, ADR 0011): spec in, working sandbox out. The body is the raw
        // OpenAPI 3.x document (JSON or YAML); ?stateful=true wires resource-shaped path pairs to
        // the G19b state directive. Refusals are typed 422s (413 for the size guard).
        admin.MapPost("/openapi/import", async (HttpRequest request, ISender sender) =>
        {
            var stateful = request.Query.TryGetValue("stateful", out var flag) && flag.FirstOrDefault() == "true";
            var result = await sender.Send(new ImportOpenApiCommand(await ReadBody(request), stateful, TenantOf(request)));
            return result.IsSuccess
                ? Results.Json(new { imported = result.Value })
                : Results.Json(new { error = result.Error.Code, message = result.Error.Description },
                    statusCode: result.Error.Code == "OpenApi.TooLarge"
                        ? StatusCodes.Status413PayloadTooLarge
                        : StatusCodes.Status422UnprocessableEntity);
        });

        // Sandbox resources (G19a, ADR 0011): tenant- and collection-scoped JSON documents. Thin
        // HTTP -> CQRS dispatch; every rule (names, ids, body cap, well-formedness) lives in the
        // Application handlers so the Library facade shares it verbatim.
        admin.MapGet("/resources", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetResourceCollectionsQuery(TenantOf(request)));
            return Results.Json(new { collections = result.Value.Select(c => new { name = c.Name, count = c.Count }) });
        });

        admin.MapGet("/resources/{collection}", async (string collection, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new ListResourcesQuery(
                collection,
                int.TryParse(request.Query["limit"].FirstOrDefault(), out var limit) ? limit : null,
                int.TryParse(request.Query["offset"].FirstOrDefault(), out var offset) ? offset : null,
                TenantOf(request),
                // Everything that is not a paging control is a filter (#353).
                ResourceQuery.Parse(request.Query.Select(
                    p => new KeyValuePair<string, string?>(p.Key, p.Value.FirstOrDefault())))));
            return result.IsSuccess
                ? Results.Json(new { documents = result.Value.Documents.Select(ResourceJson), total = result.Value.Total })
                : ResourceFailure(result.Error);
        });

        admin.MapGet("/resources/{collection}/{id}", async (string collection, string id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetResourceQuery(collection, id, TenantOf(request)));
            return result.IsSuccess ? Results.Json(ResourceJson(result.Value)) : ResourceFailure(result.Error);
        });

        admin.MapPut("/resources/{collection}/{id}", async (string collection, string id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new PutResourceCommand(collection, id, await ReadBody(request), TenantOf(request)));
            return result.IsSuccess ? Results.Json(ResourceJson(result.Value)) : ResourceFailure(result.Error);
        });

        admin.MapDelete("/resources/{collection}/{id}", async (string collection, string id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new DeleteResourceCommand(collection, id, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : ResourceFailure(result.Error);
        });

        // Sandbox relations (ADR 0015). Deliberately NOT under /resources/schemas: that path would be
        // shadowed by /resources/{collection} for anyone whose sandbox holds a collection called
        // "schemas", and a route that works until someone names a collection unluckily is a trap.
        admin.MapGet("/relations", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetRelationsQuery(TenantOf(request)));
            return Results.Json(new { relations = result.Value.Select(RelationJson) });
        });

        admin.MapPut("/relations/{collection}", async (string collection, HttpRequest request, ISender sender) =>
        {
            IReadOnlyList<ResourceRelation> belongsTo;
            try
            {
                belongsTo = ReadRelations(await ReadBody(request));
            }
            catch (System.Text.Json.JsonException exception)
            {
                // The reader's own message, not a generic one: it already names which part is wrong
                // ("onDelete must be restrict, cascade or orphan"), and replacing that with a summary
                // of the whole shape makes the caller re-derive what we already knew.
                return RelationFailure(Mediant.Results.Error.Validation("Relation.InvalidBody", exception.Message));
            }

            var result = await sender.Send(new PutRelationCommand(collection, belongsTo, TenantOf(request)));
            return result.IsSuccess ? Results.Json(RelationJson(result.Value)) : RelationFailure(result.Error);
        });

        admin.MapDelete("/relations/{collection}", async (string collection, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new DeleteRelationCommand(collection, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : RelationFailure(result.Error);
        });

        admin.MapPost("/resources/reset", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetResourcesCommand(Collection: null, TenantOf(request)));
            return Results.Ok();
        });

        admin.MapPost("/resources/{collection}/reset", async (string collection, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new ResetResourcesCommand(collection, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : ResourceFailure(result.Error);
        });

        admin.MapPost("/resources/{collection}/seed", async (string collection, HttpRequest request, ISender sender) =>
        {
            IReadOnlyList<SeedResourceItem> items;
            try
            {
                items = ReadSeedItems(await ReadBody(request));
            }
            catch (System.Text.Json.JsonException)
            {
                return ResourceFailure(Mediant.Results.Error.Validation(
                    "Resource.InvalidBody", "The seed payload must be a JSON array of documents."));
            }

            var result = await sender.Send(new SeedResourcesCommand(collection, items, TenantOf(request)));
            return result.IsSuccess ? Results.Json(new { seeded = result.Value }) : ResourceFailure(result.Error);
        });

        admin.MapPost("/environments/reset", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetEnvironmentsCommand(TenantOf(request)));
            return Results.Ok();
        });

        // Captured messages (G18a, ADR 0009): the tenant-scoped inbox facades write into. Filters are
        // query parameters so a test can assert "the OTP SMS reached +90…" in one GET.
        admin.MapGet("/messages", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetMessagesQuery(
                TenantOf(request),
                ChannelOf(request),
                request.Query["recipient"].FirstOrDefault(),
                request.Query["contains"].FirstOrDefault(),
                request.Query["matches"].FirstOrDefault(),
                int.TryParse(request.Query["limit"].FirstOrDefault(), out var limit) ? limit : null));
            return Results.Json(new { messages = result.Value.Select(MessageJson) });
        });

        admin.MapGet("/messages/count", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new CountMessagesQuery(
                TenantOf(request),
                ChannelOf(request),
                request.Query["recipient"].FirstOrDefault(),
                request.Query["contains"].FirstOrDefault(),
                request.Query["matches"].FirstOrDefault()));
            return Results.Json(new { count = result.Value });
        });

        // OTP extraction (G18f): the e2e "wait for the code and read it" as one GET. The
        // recipient/channel form reads the newest matching message; /{id}/otp reads one message.
        admin.MapGet("/messages/otp", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new ExtractOtpQuery(
                TenantOf(request),
                Id: null,
                request.Query["recipient"].FirstOrDefault(),
                ChannelOf(request),
                request.Query["pattern"].FirstOrDefault()));
            return OtpResult(result);
        });

        admin.MapGet("/messages/{id:guid}/otp", async (Guid id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new ExtractOtpQuery(
                TenantOf(request), id, Pattern: request.Query["pattern"].FirstOrDefault()));
            return OtpResult(result);
        });

        admin.MapGet("/messages/{id:guid}", async (Guid id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetMessageQuery(id, TenantOf(request)));
            // The detail carries the raw wire payload (Mailpit-style, #194); the list stays lean.
            return result.IsSuccess
                ? Results.Json(new { message = MessageJson(result.Value), raw = result.Value.Raw })
                : Results.NotFound();
        });

        // Behavior directives (G18e): SMTP fault/delay, simulated SMS provider errors, and the
        // capture webhook — per tenant, applied by the facades like HTTP delay/fault directives.
        admin.MapGet("/messages/behaviors", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetMessageBehaviorsQuery(TenantOf(request)));
            return Results.Json(BehaviorsJson(result.Value));
        });

        admin.MapPut("/messages/behaviors", async (HttpRequest request, ISender sender) =>
        {
            MessageBehaviors behaviors;
            try
            {
                behaviors = ReadBehaviors(await ReadBody(request));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The behaviors JSON is malformed.");
            }

            var result = await sender.Send(new SetMessageBehaviorsCommand(behaviors, TenantOf(request)));
            return result.IsSuccess
                ? Results.Json(BehaviorsJson(behaviors))
                : Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: result.Error.Description);
        });

        admin.MapDelete("/messages/behaviors", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetMessageBehaviorsCommand(TenantOf(request)));
            return Results.Ok();
        });

        // Attachment content is served on demand (it may be megabytes; the list carries only
        // name/type/size). The index is the position in the message's attachments list.
        admin.MapGet("/messages/{id:guid}/attachments/{index:int}", async (Guid id, int index, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GetMessageQuery(id, TenantOf(request)));
            if (!result.IsSuccess || index < 0 || index >= result.Value.Attachments.Count)
            {
                return Results.NotFound();
            }

            var attachment = result.Value.Attachments[index];
            return Results.File(attachment.Content, attachment.ContentType, attachment.Name);
        });

        admin.MapDelete("/messages/{id:guid}", async (Guid id, HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new DeleteMessageCommand(id, TenantOf(request)));
            return result.IsSuccess ? Results.Ok() : Results.NotFound();
        });

        admin.MapPost("/messages/reset", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetMessagesCommand(TenantOf(request)));
            return Results.Ok();
        });

        // Outbound certificate trust (#174). Host-level, not tenant-scoped: the outbound HttpClient is
        // shared, so trust cannot belong to one tenant. Writes are refused (409) on a flag-pinned host,
        // mirroring Git sync's two-mode design.
        admin.MapGet("/outbound-trust", async (ISender sender) =>
            Results.Json(OutboundTrustJson((await sender.Send(new OutboundTrustQuery())).Value)));

        admin.MapPost("/outbound-trust/hosts", async (HttpRequest request, ISender sender) =>
        {
            string? host;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
                host = doc.RootElement.TryGetProperty("host", out var h) ? h.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                host = null;
            }

            var result = await sender.Send(new TrustHostCommand(host ?? string.Empty));
            return result.IsSuccess ? Results.Json(OutboundTrustJson(result.Value)) : TrustFailure(result.Error);
        });

        admin.MapDelete("/outbound-trust/hosts/{host}", async (string host, ISender sender) =>
        {
            var result = await sender.Send(new DistrustHostCommand(host));
            return result.IsSuccess ? Results.Json(OutboundTrustJson(result.Value)) : TrustFailure(result.Error);
        });

        // Record mode (G12d): the record-through-proxy admin API (verified by the differential suite). While
        // a session is live, the mock-serving fallback proxies every request to the target and captures a
        // generated stub.
        admin.MapPost("/recordings/start", async (HttpRequest request, RecordingSession session) =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
            var target = doc.RootElement.TryGetProperty("targetBaseUrl", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(target))
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }

            session.Start(TenantOf(request), target);
            return Results.Ok();
        });

        // Every recording route is tenant-scoped: one team's session must be invisible to another,
        // and stopping a recording must not stop someone else's.
        admin.MapGet("/recordings/status", (HttpRequest request, RecordingSession session) =>
            Results.Json(new { status = session.TargetBaseUrl(TenantOf(request)) is null ? "Stopped" : "Recording" }));

        admin.MapPost("/recordings/snapshot", (HttpRequest request, RecordingSession session) =>
            Mappings(session.Snapshot(TenantOf(request))));

        admin.MapPost("/recordings/stop", (HttpRequest request, RecordingSession session) =>
            Mappings(session.Stop(TenantOf(request))));

        // Drift against reality (#287). Verifying against a specification asks whether the stubs match
        // the document; this asks whether they match the upstream that is running right now — the
        // version teams actually trust, because a document can be stale too.
        admin.MapPost("/recordings/verify", (HttpRequest request, RecordingSession session, StubEngine engine) =>
        {
            var tenant = TenantOf(request);
            var captured = session.Captured(tenant);
            var findings = new List<ResponseDrift>();

            foreach (var (recorded, upstream) in captured)
            {
                // The very same selection the host serves by (priority, scenarios, signatures), and
                // deliberately without serving: nothing is journaled and no scenario advances, so asking
                // the question does not change the answer to the next one.
                var stub = engine.FindMatch(tenant, recorded);

                // The DECLARED body, not a rendered one. Rendering would run the `state` directive and
                // quietly create or delete sandbox documents — a diagnostic must not have side effects,
                // so a templated stub is skipped instead (ResponseDriftCheck says so).
                findings.AddRange(ResponseDriftCheck.Compare(
                    recorded.Method,
                    recorded.Url,
                    stub?.Response.Status,
                    stub?.Response.Body is { } declared ? System.Text.Encoding.UTF8.GetString(declared) : null,
                    upstream.Status,
                    upstream.Body.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(upstream.Body)));
            }

            return Results.Json(new
            {
                recording = session.TargetBaseUrl(tenant) is not null,
                exchanges = captured.Count,
                agrees = findings.Count == 0,
                findings = findings.Select(f => new
                {
                    kind = DriftKindName(f.Kind),
                    method = f.Method,
                    url = f.Url,
                    pointer = f.Pointer,
                    detail = f.Detail,
                }),
            });
        });

        // Git sync (ADR 0007) — host-level, not tenant-scoped: the host has one root-dir working
        // copy. Status always answers (configured=false when the flag is absent); push/pull refuse
        // with a typed error the dashboard can surface (conflict / validation / auth / not set up).
        admin.MapGet("/git/status", async (ISender sender) =>
        {
            var result = await sender.Send(new GitStatusQuery());
            return result.IsSuccess ? GitStatusJson(result.Value) : GitFailure(result.Error);
        });

        // Dashboard connect (#151): {"remoteUrl": "...", "branch": "main"?} — the working copy is
        // resolved host-side (never typed by the operator); flag-pinned hosts refuse.
        admin.MapPost("/git/configure", async (HttpRequest request, ISender sender) =>
        {
            string? remoteUrl = null;
            string? branch = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
                remoteUrl = doc.RootElement.TryGetProperty("remoteUrl", out var r) ? r.GetString() : null;
                branch = doc.RootElement.TryGetProperty("branch", out var b) ? b.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // fall through to the empty-remote validation below
            }

            if (string.IsNullOrWhiteSpace(remoteUrl))
            {
                return Results.Json(new { error = "Git.InvalidRemote", message = "remoteUrl is required." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var result = await sender.Send(new GitConfigureCommand(remoteUrl!, branch));
            return result.IsSuccess ? GitStatusJson(result.Value) : GitFailure(result.Error);
        });

        // Dashboard credentials (#153): {"token": "...", "username": "..."?} — held in host process
        // memory only (never persisted, never echoed back); an empty token clears them. The status
        // response reports only the source (none/environment/dashboard), never the value.
        admin.MapPost("/git/credentials", async (HttpRequest request, ISender sender) =>
        {
            string? username = null;
            string? token = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await ReadBody(request));
                username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
                token = doc.RootElement.TryGetProperty("token", out var k) ? k.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // an empty/invalid body clears the credentials
            }

            var result = await sender.Send(new GitSetCredentialsCommand(username, token));
            return result.IsSuccess ? GitStatusJson(result.Value) : GitFailure(result.Error);
        });

        admin.MapPost("/git/push", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new GitPushCommand(await ReadGitMessage(request)));
            return result.IsSuccess
                ? Results.Json(new { pushed = result.Value.Pushed, commit = result.Value.Commit, reason = result.Value.Reason })
                : GitFailure(result.Error);
        });

        admin.MapPost("/git/pull", async (ISender sender) =>
        {
            var result = await sender.Send(new GitPullCommand());
            return result.IsSuccess
                ? Results.Json(new { updated = result.Value.Updated, commit = result.Value.Commit, stubsLoaded = result.Value.StubsLoaded, reason = result.Value.Reason })
                : GitFailure(result.Error);
        });

        // Custom admin API extensions (G12e): any request under /__admin/ext/<prefix>/… is dispatched
        // to the extension whose RoutePrefix is that first segment. The extension owns everything below
        // it and never sees an HttpContext — the request is lowered to a transport-agnostic shape.
        admin.Map("/ext/{**rest}", async (string? rest, HttpContext http, IEnumerable<IAdminApiExtension> extensions) =>
        {
            var path = rest ?? string.Empty;
            var slash = path.IndexOf('/');
            var prefix = slash < 0 ? path : path[..slash];
            var subpath = slash < 0 ? string.Empty : path[slash..];

            var extension = extensions.FirstOrDefault(e =>
                string.Equals(e.RoutePrefix, prefix, StringComparison.Ordinal));
            if (extension is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            byte[] body;
            using (var buffer = new MemoryStream())
            {
                await http.Request.Body.CopyToAsync(buffer);
                body = buffer.ToArray();
            }

            var apiRequest = new AdminApiRequest(http.Request.Method, subpath, http.Request.QueryString.Value ?? string.Empty, body);
            var response = await extension.HandleAsync(apiRequest, http.RequestAborted);

            http.Response.StatusCode = response.Status;
            http.Response.ContentType = response.ContentType;
            await http.Response.Body.WriteAsync(response.Body);
        });

        return endpoints;
    }

    // Recording responses return a {"mappings":[…]} envelope of the generated stub JSON. The
    // captured stubs are already JSON, so they are spliced in raw rather than re-serialized.
    private static IResult Mappings(IReadOnlyList<string> stubs) =>
        Results.Content("{\"mappings\":[" + string.Join(",", stubs) + "]}", "application/json");

    private static async Task<string> ReadBody(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Reads the optional <c>{"message": "…"}</c> commit message from a push body (empty body is fine).</summary>
    private static async Task<string?> ReadGitMessage(HttpRequest request)
    {
        var body = await ReadBody(request);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static IResult GitStatusJson(GitSyncStatus status) => Results.Json(new
    {
        configured = status.Configured,
        remote = status.Remote,
        branch = status.Branch,
        dirty = status.Dirty,
        ahead = status.Ahead,
        behind = status.Behind,
        fetchError = status.FetchError,
        configuredBy = status.ConfiguredBy,
        workingCopy = status.WorkingCopy,
        credentialsSource = status.CredentialsSource,
    });

    // Typed Git errors → HTTP: setup problems are 404, refusals (pull-first/diverged/dirty/branch/
    // pinned/persistence) are 409, invalid input or a rejected remote tree is 422, remote-auth
    // failures are 502 — deliberately NOT 401, which the dashboard reserves for the host's own
    // admin auth (it would pop the login gate).
    // Environment key JSON: { "activeValue": "dev", "values": [ { "name": "dev", "value": "…" }, … ] }.
    // The key itself comes from the route, so the body cannot disagree with the URL about which key
    // is being written.
    private static EnvironmentKey ReadEnvironmentKey(string key, string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var values = new List<EnvironmentValue>();
        if (root.TryGetProperty("values", out var array) && array.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = item.TryGetProperty("value", out var v) ? v.GetString() : null;
                var secret = item.TryGetProperty("secret", out var sec)
                    && sec.ValueKind == System.Text.Json.JsonValueKind.True;

                // A secret with no literal is what a redacted read hands straight back — it means
                // "unchanged", and EnvironmentSecrets.Merge resolves it against what is stored.
                if (name is not null && (value is not null || secret))
                {
                    values.Add(new EnvironmentValue(name, value ?? string.Empty, secret));
                }
            }
        }

        var active = root.TryGetProperty("activeValue", out var a) ? a.GetString() : null;
        return new EnvironmentKey(key, active ?? values.FirstOrDefault()?.Name ?? string.Empty, values);
    }

    /// <summary>
    /// The read projection, with every secret literal withheld (#348). Two places leak, not one: the
    /// value in the list and the <c>resolved</c> literal computed from the active one — reporting the
    /// second while hiding the first would have been redaction in name only.
    /// </summary>
    private static object EnvironmentJson(EnvironmentKey key) => new
    {
        key = key.Key,
        activeValue = key.ActiveValue,
        resolved = key.ResolvesToSecret() ? null : key.Resolve(),
        secret = key.ResolvesToSecret(),
        values = key.Values.Select(v => v.Secret
            ? (object)new { name = v.Name, secret = true }
            : new { name = v.Name, value = v.Value, secret = false }),
    };

    private static object OutboundTrustJson(OutboundTrustStatus status) => new
    {
        hosts = status.Hosts,
        trustAll = status.TrustAll,
        pinned = status.Pinned,
        persistent = status.Persistent,
    };

    private static IResult TrustFailure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            // Pinned is a conflict, not a bad request: the caller asked for something coherent that
            // this host's startup configuration forbids — the same shape Git sync uses.
            "Trust.FlagPinned" => StatusCodes.Status409Conflict,
            "Trust.UnknownHost" => StatusCodes.Status404NotFound,
            "Trust.Unavailable" => StatusCodes.Status501NotImplemented,
            _ => StatusCodes.Status400BadRequest,
        });

    /// <summary>
    /// A seed payload is a JSON array; each object element may carry a string <c>id</c> (absent ids
    /// are generated) and is stored as its own raw JSON text — the body round-trips verbatim.
    /// </summary>
    private static IReadOnlyList<SeedResourceItem> ReadSeedItems(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new System.Text.Json.JsonException("The seed payload must be a JSON array.");
        }

        var items = new List<SeedResourceItem>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var id = element.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     element.TryGetProperty("id", out var idProperty) &&
                     idProperty.ValueKind == System.Text.Json.JsonValueKind.String
                ? idProperty.GetString()
                : null;
            items.Add(new SeedResourceItem(id, element.GetRawText()));
        }

        return items;
    }

    // The body is stored as opaque text but was validated as well-formed JSON, so it re-embeds
    // as a real JSON value here — clients read a document, not a double-encoded string.
    /// <summary>
    /// Reads a clock override. The two modes are exclusive by design (see <see cref="ClockOverride"/>),
    /// so a body carrying both is refused rather than silently resolved in favour of one.
    /// </summary>
    private static ClockOverride ReadClock(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var hasFrozen = root.TryGetProperty("frozenAt", out var frozen)
            && frozen.ValueKind is System.Text.Json.JsonValueKind.String;
        var hasOffset = root.TryGetProperty("offsetSeconds", out var offset)
            && offset.ValueKind is System.Text.Json.JsonValueKind.Number;

        if (hasFrozen && hasOffset)
        {
            throw new InvalidOperationException(
                "Set either 'frozenAt' or 'offsetSeconds', not both — a frozen clock does not also drift.");
        }

        if (hasFrozen)
        {
            return new ClockOverride(
                DateTimeOffset.Parse(frozen.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                TimeSpan.Zero);
        }

        return hasOffset
            ? new ClockOverride(null, TimeSpan.FromSeconds(offset.GetDouble()))
            : ClockOverride.RealTime;
    }

    /// <summary>
    /// One near miss as the wire sees it. The stub's own request block rides along as <c>expected</c>:
    /// the attribute names are the mapping JSON's own vocabulary, so a reader can find the line that
    /// disagreed without every matcher having to describe itself.
    /// </summary>
    private static object NearMissJson(NearMiss near) => new
    {
        stubId = near.Stub.Id,
        distance = near.Distance,
        expected = (FullMapping(near.Stub) as JsonObject)?["request"],
        attributes = near.Attributes.Select(a => new { attribute = a.Attribute, matched = a.Matched, actual = a.Actual }),
    };

    /// <summary>
    /// Reads the hypothetical request a caller wants explained. Only the parts a stub can match on —
    /// asking for a near miss is asking "what would happen if I sent this".
    /// </summary>
    private static CanonicalRequest ReadCandidateRequest(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("method and url are required.");
        }

        var headers = new List<KeyValuePair<string, string>>();
        if (root.TryGetProperty("headers", out var headerObject)
            && headerObject.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in headerObject.EnumerateObject())
            {
                headers.Add(new KeyValuePair<string, string>(property.Name, property.Value.GetString() ?? string.Empty));
            }
        }

        var payload = root.TryGetProperty("body", out var b) ? b.GetString() : null;
        return CanonicalRequestBuilder.Build(
            method!, url!, headers, payload is null ? null : System.Text.Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// Reads a degradation profile, refusing anything that cannot mean what it says: a ratio outside
    /// 0..1, a negative delay, a status outside the HTTP range, an unknown fault name. Nothing
    /// half-lands — a rejected profile leaves the tenant exactly as healthy (or as degraded) as it was.
    /// </summary>
    private static DegradationProfile ReadDegradation(string body)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        var latency = Section(root, "latency");
        var error = Section(root, "errorRate");
        var fault = Section(root, "faultRate");

        var fixedMs = Int(latency, "fixedMs", 0);
        var jitterMs = Int(latency, "jitterMs", 0);
        var errorRatio = Ratio(error, "ratio");
        var errorStatus = Int(error, "status", 503);
        var faultRatio = Ratio(fault, "ratio");
        var faultKind = FaultName(fault);

        if (fixedMs < 0 || jitterMs < 0)
        {
            throw new InvalidOperationException("Delays cannot be negative.");
        }

        if (errorStatus is < 100 or > 599)
        {
            throw new InvalidOperationException("status must be a valid HTTP status code.");
        }

        // A seed is always stored: one supplied by the caller when they want to replay a known
        // sequence, otherwise one we pick and report back, so a run that turns up something interesting
        // can be reproduced rather than described.
        var seed = root.TryGetProperty("seed", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Number
            ? s.GetInt32()
            : Random.Shared.Next();

        return new DegradationProfile(fixedMs, jitterMs, errorRatio, errorStatus, faultRatio, faultKind, seed);
    }

    private static System.Text.Json.JsonElement? Section(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var section) && section.ValueKind == System.Text.Json.JsonValueKind.Object
            ? section
            : null;

    private static int Int(System.Text.Json.JsonElement? section, string name, int fallback) =>
        section is { } s && s.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static double Ratio(System.Text.Json.JsonElement? section, string name)
    {
        if (section is not { } s || !s.TryGetProperty(name, out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            return 0d;
        }

        var ratio = value.GetDouble();
        return ratio is >= 0d and <= 1d
            ? ratio
            : throw new InvalidOperationException($"{name} must be between 0 and 1.");
    }

    private static FaultKind FaultName(System.Text.Json.JsonElement? section)
    {
        if (section is not { } s || !s.TryGetProperty("fault", out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return FaultKind.ConnectionResetByPeer;
        }

        // The same four names the mapping dialect uses for a stub's own `fault`, so an operator does not
        // have to learn a second vocabulary for the same four behaviours.
        return value.GetString() switch
        {
            "EMPTY_RESPONSE" => FaultKind.EmptyResponse,
            "MALFORMED_RESPONSE_CHUNK" => FaultKind.MalformedResponseChunk,
            "RANDOM_DATA_THEN_CLOSE" => FaultKind.RandomDataThenClose,
            "CONNECTION_RESET_BY_PEER" => FaultKind.ConnectionResetByPeer,
            var unknown => throw new InvalidOperationException($"'{unknown}' is not a known fault."),
        };
    }

    private static string TrafficDriftKindName(TrafficDriftKind kind) => kind switch
    {
        TrafficDriftKind.UndeclaredOperation => "undeclaredOperation",
        TrafficDriftKind.MissingParameter => "missingParameter",
        _ => "requestSchemaViolation",
    };

    private static string DriftKindName(ResponseDriftKind kind) => kind switch
    {
        ResponseDriftKind.NoStub => "noStub",
        ResponseDriftKind.StatusDiffers => "statusDiffers",
        ResponseDriftKind.FieldMissing => "fieldMissing",
        ResponseDriftKind.FieldUnexpected => "fieldUnexpected",
        _ => "typeDiffers",
    };

    private static string DriftKindName(DriftKind kind) => kind switch
    {
        DriftKind.UndeclaredOperation => "undeclaredOperation",
        DriftKind.UncoveredOperation => "uncoveredOperation",
        DriftKind.UndeclaredStatus => "undeclaredStatus",
        _ => "schemaViolation",
    };

    private static object DegradationJson(DegradationProfile profile) => new
    {
        degraded = !profile.IsHealthy,
        latency = new { fixedMs = profile.FixedDelayMs, jitterMs = profile.JitterMs },
        errorRate = new { ratio = profile.ErrorRatio, status = profile.ErrorStatus },
        faultRate = new { ratio = profile.FaultRatio, fault = FaultDialectName(profile.Fault) },
        seed = profile.Seed,
    };

    private static string FaultDialectName(FaultKind kind) => kind switch
    {
        FaultKind.EmptyResponse => "EMPTY_RESPONSE",
        FaultKind.MalformedResponseChunk => "MALFORMED_RESPONSE_CHUNK",
        FaultKind.RandomDataThenClose => "RANDOM_DATA_THEN_CLOSE",
        _ => "CONNECTION_RESET_BY_PEER",
    };

    private static IResult DegradationFailure(string code, string message) =>
        Results.Json(new { errors = new[] { new { code, message } } }, statusCode: 422);

    private static object ClockJson(ClockOverride clock) => new
    {
        mode = clock.FrozenAt is not null ? "frozen" : clock.Offset == TimeSpan.Zero ? "real" : "offset",
        frozenAt = clock.FrozenAt,
        offsetSeconds = (long)clock.Offset.TotalSeconds,
    };

    private static IResult ClockFailure(Mediant.Results.Error error) =>
        Results.Json(new { errors = new[] { new { code = error.Code, message = error.Description } } }, statusCode: 422);

    private static object ResourceJson(ResourceDocument document) => new
    {
        id = document.Id,
        collection = document.Collection,
        body = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(document.Body),
        createdAt = document.CreatedAt,
        updatedAt = document.UpdatedAt,
        version = document.Version,
    };

    private static object ApiKeyJson(ApiKey key, int used) => new
    {
        id = key.Id,
        name = key.Name,
        prefix = key.Prefix,
        createdAt = key.CreatedAt,
        quotaPerHour = key.QuotaPerHour,
        usedThisHour = used,
    };

    private static IResult ApiKeyFailure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            "ApiKey.NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status422UnprocessableEntity,
        });

    /// <summary>
    /// Reads <c>{"belongsTo":[{"collection":…,"via":…,"onDelete":…}]}</c>. An unrecognised
    /// <c>onDelete</c> is refused rather than defaulted: silently reading "casade" as `restrict`
    /// would give the operator the opposite of what they asked for, in the one place that deletes data.
    /// </summary>
    private static IReadOnlyList<ResourceRelation> ReadRelations(string body)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
            || !document.RootElement.TryGetProperty("belongsTo", out var declared)
            || declared.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new System.Text.Json.JsonException("belongsTo must be an array.");
        }

        var relations = new List<ResourceRelation>();
        foreach (var entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != System.Text.Json.JsonValueKind.Object
                || !entry.TryGetProperty("collection", out var collection)
                || collection.ValueKind != System.Text.Json.JsonValueKind.String
                || !entry.TryGetProperty("via", out var via)
                || via.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                throw new System.Text.Json.JsonException("Each relation needs a collection and a via.");
            }

            var rule = entry.TryGetProperty("onDelete", out var onDelete)
                && onDelete.ValueKind == System.Text.Json.JsonValueKind.String
                    ? onDelete.GetString()!.ToLowerInvariant() switch
                    {
                        "restrict" => RelationDeleteRule.Restrict,
                        "cascade" => RelationDeleteRule.Cascade,
                        "orphan" => RelationDeleteRule.Orphan,
                        _ => throw new System.Text.Json.JsonException(
                            "onDelete must be restrict, cascade or orphan."),
                    }
                    : RelationDeleteRule.Restrict;

            relations.Add(new ResourceRelation(collection.GetString()!, via.GetString()!, rule));
        }

        return relations;
    }

    private static object RelationJson(ResourceSchema schema) => new
    {
        collection = schema.Collection,
        belongsTo = schema.BelongsTo.Select(relation => new
        {
            collection = relation.Collection,
            via = relation.Via,
            onDelete = relation.OnDelete.ToString().ToLowerInvariant(),
        }),
    };

    private static IResult RelationFailure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            "Relation.NotFound" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status422UnprocessableEntity,
        });

    private static IResult ResourceFailure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            "Resource.NotFound" => StatusCodes.Status404NotFound,
            "Resource.BodyTooLarge" => StatusCodes.Status413PayloadTooLarge,
            _ => StatusCodes.Status422UnprocessableEntity,
        });

    private static IResult EnvironmentFailure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            "Environment.UnknownKey" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status400BadRequest,
        });

    private static IResult GitFailure(Mediant.Results.Error error) =>
        Results.Json(new { error = error.Code, message = error.Description }, statusCode: error.Code switch
        {
            "Git.NotConfigured" or "Git.NotSupported" or "Git.RemoteBranchMissing" => StatusCodes.Status404NotFound,
            "Git.InvalidMappings" or "Git.InvalidRemote" or "Git.InvalidBranch" => StatusCodes.Status422UnprocessableEntity,
            "Git.RemoteAhead" or "Git.Diverged" or "Git.DirtyWorkingTree" or "Git.LocalOverlap"
                or "Git.WrongBranch" or "Git.FlagPinned" or "Git.PersistenceConflict" => StatusCodes.Status409Conflict,
            "Git.Auth" => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        });

    // The full mapping for GET /mappings: the stub's own source JSON with its id/uuid stamped
    // in, so the dashboard can display and faithfully round-trip an edit (not just see an id).
    // ---- Captured messages (G18a) --------------------------------------------------------------

    /// <summary>
    /// The <c>?channel=</c> filter. Null means no filter — which is why every channel that exists must
    /// be listed here: an unrecognised name falls through to "show everything", so a channel that was
    /// added to the model and forgotten here filters nothing while looking like it filtered.
    /// </summary>
    /// <remarks>
    /// That is exactly what happened to <c>broker</c> between 1.10.0 and 1.12.0. The wire test that
    /// should have caught it counted messages in an inbox holding only broker messages, where "filtered
    /// correctly" and "did not filter at all" give the same answer. A filter test needs a mixed inbox.
    /// </remarks>
    private static MessageChannel? ChannelOf(HttpRequest request) =>
        request.Query["channel"].FirstOrDefault()?.ToLowerInvariant() switch
        {
            "email" => MessageChannel.Email,
            "sms" => MessageChannel.Sms,
            "broker" => MessageChannel.Broker,
            _ => null,
        };

    // Twilio's send-message path (G18d) — an SMS-profile request even when a stub answered it.
    private static readonly System.Text.RegularExpressions.Regex TwilioMessagesPath =
        new(@"^/2010-04-01/Accounts/[^/]+/Messages\.json$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Classifies a journal entry (G18 follow-up, ADR 0010): <c>sms</c> by the provider path,
    /// <c>grpc</c> by descriptor lookup, <c>graphql</c> by the matched stub's custom matcher —
    /// same decision table as the stub list, computed at query time.
    /// </summary>
    private static string JournalProtocol(ServeEvent e, IStubProtocolProbe? probe)
    {
        var url = e.Request.Url;
        var query = url.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? url : url[..query];

        if (TwilioMessagesPath.IsMatch(path))
        {
            return "sms";
        }

        if (probe is not null && probe.IsGrpcPath(path))
        {
            return "grpc";
        }

        if (e.MatchedStub?.Source is { } source)
        {
            try
            {
                var node = JsonNode.Parse(source) as JsonObject;
                if (node is not null && StubProtocols.Classify(node, probe: null) == "graphql")
                {
                    return "graphql";
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // An unparseable source stays http — classification is presentation, never a failure.
            }
        }

        return "http";
    }

    private static IResult OtpResult(Mediant.Results.Result<OtpExtraction> result) =>
        result.IsSuccess
            ? Results.Json(new { otp = result.Value.Otp, messageId = result.Value.MessageId, receivedAt = result.Value.ReceivedAt })
            : result.Error.Code == "Otp.InvalidPattern"
                ? Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: result.Error.Description)
                : Results.NotFound(new { code = result.Error.Code, message = result.Error.Description });

    private static object BehaviorsJson(MessageBehaviors behaviors) => new
    {
        smtpFault = behaviors.SmtpFault switch
        {
            SmtpFaultMode.Reject => "reject",
            SmtpFaultMode.Drop => "drop",
            _ => "none",
        },
        smtpDelayMs = behaviors.SmtpDelayMs,
        smsErrorCode = behaviors.SmsErrorCode,
        webhookUrl = behaviors.WebhookUrl,
    };

    private static MessageBehaviors ReadBehaviors(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var fault = root.TryGetProperty("smtpFault", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String
            ? f.GetString()!.ToLowerInvariant() switch
            {
                "reject" => SmtpFaultMode.Reject,
                "drop" => SmtpFaultMode.Drop,
                "none" => SmtpFaultMode.None,
                _ => throw new InvalidOperationException("Unknown smtpFault."),
            }
            : SmtpFaultMode.None;
        return new MessageBehaviors(
            fault,
            root.TryGetProperty("smtpDelayMs", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Number ? d.GetInt32() : 0,
            root.TryGetProperty("smsErrorCode", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.Number ? e.GetInt32() : null,
            root.TryGetProperty("webhookUrl", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.String ? w.GetString() : null);
    }

    // Attachment content is deliberately not inlined in the JSON (it may be megabytes); the list
    // carries name/type/size and a per-attachment download lands with the inbox UI (G18c).
    private static object MessageJson(MessageEnvelope message) => new
    {
        id = message.Id,
        // A switch rather than a ternary: the two-channel form silently reported a broker message as
        // "sms" the moment a third channel existed (ADR 0013).
        channel = message.Channel switch
        {
            MessageChannel.Email => "email",
            MessageChannel.Sms => "sms",
            _ => "broker",
        },
        from = message.From,
        to = message.To,
        subject = message.Subject,
        body = message.Body,
        htmlBody = message.HtmlBody,
        meta = message.Meta,
        attachments = message.Attachments.Select(a => new { name = a.Name, contentType = a.ContentType, size = a.Size }),
        receivedAt = message.ReceivedAt,
    };

    private static JsonNode FullMapping(StubMapping stub)
    {
        var node = (stub.Source is not null ? JsonNode.Parse(stub.Source) : null) as JsonObject ?? new JsonObject();
        node["id"] = stub.Id.ToString();
        node["uuid"] = stub.Id.ToString();
        return node;
    }
}
