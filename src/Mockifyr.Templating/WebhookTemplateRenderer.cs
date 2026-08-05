using HandlebarsDotNet;
using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>
/// Renders webhook fields (URL, header values, body) as Handlebars templates against the triggering
/// request, which is exposed to templates as <c>originalRequest</c> (G3b, verified by the differential
/// suite). Reuses the same engine and helper
/// set as response templating — so <c>{{jsonPath originalRequest.body '$.id'}}</c>,
/// <c>{{originalRequest.headers.X}}</c>, etc. all work. See docs/parity/g3-webhook.md.
/// </summary>
public sealed class WebhookTemplateRenderer(
    IEnvironmentResolver? environments = null, IClockResolver? clock = null) : IServeEventTemplateRenderer
{
    // Compiled once per distinct template (#266) — a webhook body re-renders on every delivery.
    private readonly CompiledTemplateCache _templates = CompiledTemplateCache.Create();

    /// <inheritdoc />
    public string Render(string template, CanonicalRequest originalRequest, TenantId tenant)
    {
        // Same ordering as response rendering: environment keys resolve before Handlebars, against the
        // tenant that owns the stub which fired the webhook (G17, issue #166).
        if (environments is { } resolver && resolver.HasKeys(tenant))
        {
            template = EnvironmentSubstitution.Apply(
                template,
                (string key, out string value) => resolver.TryResolve(tenant, key, out value));
        }

        var model = new Dictionary<string, object?> { ["originalRequest"] = RequestModel.Build(originalRequest) };

        // The same tenant clock the response saw (#290): a callback whose timestamp disagreed with the
        // response that triggered it would be a puzzle for whoever receives both.
        using var scope = RenderClock.Use(clock?.UtcNow(tenant));
        return _templates.Render(template, model);
    }
}
