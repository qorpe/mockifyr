using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mockifyr.Core;

namespace Mockifyr.Facade.Broker;

/// <summary>
/// The admin surface for broker mappings (ADR 0013, slice 3): <c>/__admin/broker-mappings</c>.
/// </summary>
/// <remarks>
/// Registered by the facade that owns the concept, exactly as the WebSocket facade owns
/// <c>/__admin/message-mappings</c>. That keeps the broker client dependency out of the admin project
/// and means a host started without a broker exposes no routes that could not do anything.
/// </remarks>
public static class BrokerMappingEndpoints
{
    private const string TenantHeader = "X-Mockifyr-Tenant";

    /// <summary>Adds the broker-mapping routes to the app.</summary>
    public static WebApplication UseMockifyrBrokerMappings(this WebApplication app, BrokerMappingStore store)
    {
        app.MapPost("/__admin/broker-mappings", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var mapping = BrokerMappingReader.Read(json, TenantOf(request));
                store.Add(mapping);
                return Results.Json(new { id = mapping.Id }, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Malformed JSON, or well-formed JSON of the wrong shape. Both are the caller's error,
                // and both are worth a status rather than a 500 that reads as our fault.
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
        });

        app.MapGet("/__admin/broker-mappings", (HttpRequest request) =>
        {
            var mappings = store.For(TenantOf(request));
            return Results.Json(new
            {
                // The registration JSON verbatim, the same as-is rule stub mappings follow: a reader
                // must be able to search their own file for the string we printed.
                mappings = mappings.Select(mapping => new
                {
                    id = mapping.Id,
                    source = mapping.Source,
                    publishes = mapping.Publishes.Count,
                }),
                meta = new { total = mappings.Count },
            });
        });

        app.MapDelete("/__admin/broker-mappings/{id:guid}", (Guid id, HttpRequest request) =>
            store.Remove(TenantOf(request), id) ? Results.Ok() : Results.NotFound());

        app.MapPost("/__admin/broker-mappings/reset", (HttpRequest request) =>
        {
            store.Reset(TenantOf(request));
            return Results.Ok();
        });

        return app;
    }

    private static TenantId TenantOf(HttpRequest request) =>
        request.Headers.TryGetValue(TenantHeader, out var value) && !string.IsNullOrWhiteSpace(value)
            ? new TenantId(value!)
            : TenantId.Default;
}
