using System.Text.Json.Nodes;
using Mediant.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            ActiveKeyReport keys) =>
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

        admin.MapPost("/mappings", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new CreateStubCommand(await ReadBody(request), TenantOf(request)));
            return result.IsSuccess
                ? Results.Json(new { id = result.Value, uuid = result.Value }, statusCode: StatusCodes.Status201Created)
                : Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
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

        admin.MapPost("/mappings/import", async (HttpRequest request, ISender sender) =>
        {
            var result = await sender.Send(new ImportMappingsCommand(await ReadBody(request), TenantOf(request)));
            return result.IsSuccess
                ? Results.Ok()
                : Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
        });

        admin.MapPost("/mappings/reset", async (HttpRequest request, ISender sender) =>
        {
            await sender.Send(new ResetMappingsCommand(TenantOf(request)));
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
                TenantOf(request)));
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

            session.Start(target);
            return Results.Ok();
        });

        admin.MapGet("/recordings/status", (RecordingSession session) =>
            Results.Json(new { status = session.TargetBaseUrl is null ? "Stopped" : "Recording" }));

        admin.MapPost("/recordings/snapshot", (RecordingSession session) => Mappings(session.Snapshot()));

        admin.MapPost("/recordings/stop", (RecordingSession session) => Mappings(session.Stop()));

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
                if (name is not null && value is not null)
                {
                    values.Add(new EnvironmentValue(name, value));
                }
            }
        }

        var active = root.TryGetProperty("activeValue", out var a) ? a.GetString() : null;
        return new EnvironmentKey(key, active ?? values.FirstOrDefault()?.Name ?? string.Empty, values);
    }

    private static object EnvironmentJson(EnvironmentKey key) => new
    {
        key = key.Key,
        activeValue = key.ActiveValue,
        resolved = key.Resolve(),
        values = key.Values.Select(v => new { name = v.Name, value = v.Value }),
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

    private static MessageChannel? ChannelOf(HttpRequest request) =>
        request.Query["channel"].FirstOrDefault()?.ToLowerInvariant() switch
        {
            "email" => MessageChannel.Email,
            "sms" => MessageChannel.Sms,
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
        channel = message.Channel == MessageChannel.Email ? "email" : "sms",
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
