using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>
/// Renders a message-action template (G15d WebSocket, ADR 0013 slice 3 broker). The same Handlebars
/// engine and helper set as response templating, but the model exposes the inbound message as
/// <c>{{message.body}}</c> (so <c>Echo: {{message.body}}</c> and any helper over it —
/// <c>{{jsonPath message.body '$.x'}}</c> — work).
/// </summary>
/// <remarks>
/// The channel decides what else sits beside <c>body</c>: a broker message also carries a topic, a key
/// and headers. Those are passed in as a model rather than named here, so this class never learns what
/// a broker is and a fourth channel needs no change to it.
/// </remarks>
/// <param name="environments">
/// Tenant configuration (G17). Optional: a caller with no tenant to speak of — the WebSocket facade —
/// passes none, and <c>{{key}}</c> substitution is then simply not attempted, exactly as before.
/// </param>
/// <param name="clock">The tenant clock (#290), so a templated timestamp agrees with the rest of the host.</param>
public sealed class MessageTemplateRenderer(
    IEnvironmentResolver? environments = null, IClockResolver? clock = null)
{
    // Compiled once per distinct template (#266) — a message action re-renders per message.
    private readonly CompiledTemplateCache _templates = CompiledTemplateCache.Create();

    /// <summary>Renders <paramref name="template"/> with the inbound message body in scope.</summary>
    public string Render(string template, string messageBody) =>
        Render(template, new Dictionary<string, object?> { ["body"] = messageBody }, TenantId.Default);

    /// <summary>
    /// Renders <paramref name="template"/> with <paramref name="message"/> in scope as
    /// <c>{{message.*}}</c>, resolving the tenant's environment keys and clock first.
    /// </summary>
    public string Render(string template, IReadOnlyDictionary<string, object?> message, TenantId tenant)
    {
        // Same ordering as response and webhook rendering: environment keys resolve before Handlebars
        // sees the template, against the tenant that owns the mapping (G17).
        if (environments is { } resolver && resolver.HasKeys(tenant))
        {
            template = EnvironmentSubstitution.Apply(
                template,
                (string key, out string value) => resolver.TryResolve(tenant, key, out value));
        }

        var model = new Dictionary<string, object?> { ["message"] = message };

        using var scope = RenderClock.Use(clock?.UtcNow(tenant));
        return _templates.Render(template, model);
    }
}
