using System.Text;
using HandlebarsDotNet;
using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>
/// Renders a response definition, applying Handlebars templating when the stub declares the
/// <c>response-template</c> transformer (Mockifyr's response templating). The template model exposes
/// the request as <c>{{request.method}}</c>, <c>url</c>, <c>path</c>, <c>pathSegments.[n]</c>,
/// <c>query.name</c>, <c>headers.Name</c>, and <c>body</c>. Escaping is disabled so templates emit
/// raw <c>{{ }}</c> output. The built-in data helpers (<c>jsonPath</c>, <c>xPath</c>,
/// <c>regexExtract</c>, <c>formData</c>, <c>parseJson</c>) are registered from G2c, the date helpers
/// (<c>parseDate</c>, <c>date</c>) from G2d, the random helpers (<c>randomValue</c>,
/// <c>pickRandom</c>, <c>randomInt</c>, <c>randomDecimal</c>) from G2e, and the JSON-manipulation
/// helpers (<c>jsonArrayAdd</c>, <c>jsonMerge</c>, <c>jsonRemove</c>, <c>toJson</c>) from G2f, and the
/// format/math/array/string helpers (<c>math</c>, <c>numberFormat</c>, <c>size</c>, <c>join</c>,
/// <c>substring</c>, <c>replace</c>, <c>upper</c>, <c>lower</c>, <c>capitalize</c>, <c>trim</c>) from
/// G2g, and the system helpers (<c>systemValue</c>, <c>hostname</c>) from G2h. This
/// response-templating behavior is verified by the differential suite. See
/// docs/parity/g2-response.md.
/// </summary>
public sealed class TemplatingResponseRenderer : IResponseRenderer
{
    private const string ResponseTemplateTransformer = "response-template";

    // TextEncoder = null disables HTML escaping so output is emitted raw (non-escaping).
    private readonly IHandlebars _handlebars;
    private readonly bool _globalTemplating;
    private readonly IEnvironmentResolver? _environments;

    /// <summary>
    /// <paramref name="globalTemplating"/> mirrors the reference host's
    /// <c>--global-response-templating</c>: every response renders through the engine regardless of
    /// the per-stub <c>transformers</c> list (#148, verified by the differential suite). Off, the
    /// per-stub opt-in behavior is unchanged.
    /// <paramref name="environments"/> resolves <c>{{key}}</c> environment references (G17); null
    /// disables the pass entirely, which is what a facade with no environment store configured wants.
    /// </summary>
    private readonly IResourceStore? _resources;
    private readonly IResourceIdGenerator? _resourceIds;
    private readonly ResourceOptions _resourceOptions;

    public TemplatingResponseRenderer(
        IEnumerable<TemplateHelperExtension>? extraHelpers = null,
        bool globalTemplating = false,
        IEnvironmentResolver? environments = null,
        IResourceStore? resources = null,
        IResourceIdGenerator? resourceIds = null,
        ResourceOptions? resourceOptions = null)
    {
        _handlebars = HandlebarsFactory.Create(extraHelpers);
        _globalTemplating = globalTemplating;
        _environments = environments;
        _resources = resources;
        _resourceIds = resourceIds;
        _resourceOptions = resourceOptions ?? new ResourceOptions();
    }

    /// <inheritdoc />
    public CanonicalResponse Render(ResponseDefinition definition, RenderContext context)
    {
        // Environment substitution runs FIRST, and deliberately ahead of the transformer guard below:
        // a stub that never opted into `response-template` still gets its {{key}} references resolved.
        // Putting this after the guard would silently skip the majority of stubs (G17, issue #165).
        definition = ApplyEnvironment(definition, context.Tenant);

        // The state directive (G19b) runs the sandbox CRUD operation BEFORE body/header rendering so
        // {{state.*}} is a real model value. Declaring the directive IS the templating opt-in — no
        // separate transformer needed — and a miss/refusal short-circuits to a bare status, like a
        // real API answering 404 rather than rendering a template over nothing.
        Dictionary<string, object?>? stateModel = null;
        if (definition.State is { } state && _resources is { } resources && _resourceIds is { } resourceIds)
        {
            var requestModel = BuildModel(context);
            var outcome = StateDirectiveApplier.Apply(
                state,
                context.Tenant,
                state.Id is { } idTemplate ? RenderTemplate(idTemplate, requestModel) : null,
                state.Document is { } documentTemplate ? RenderTemplate(documentTemplate, requestModel) : null,
                context.Request.Body,
                resources,
                resourceIds,
                _resourceOptions);

            if (outcome.ShortCircuitStatus is { } shortCircuit)
            {
                return new CanonicalResponse
                {
                    Status = shortCircuit,
                    Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(p => p.Key, p => p.Value),
                    Body = [],
                };
            }

            stateModel = new Dictionary<string, object?>(outcome.Model!);
        }

        if (stateModel is null && !_globalTemplating && !definition.Transformers.Contains(ResponseTemplateTransformer))
        {
            return new CanonicalResponse
            {
                Status = definition.Status,
                StatusMessage = definition.StatusMessage,
                Headers = definition.Headers,
                Body = definition.Body ?? [],
                Delay = definition.Delay,
                DelayDistribution = definition.DelayDistribution,
                Fault = definition.Fault,
                Proxy = definition.Proxy,
            };
        }

        var model = BuildModel(context);
        if (stateModel is not null)
        {
            model["state"] = stateModel;
        }

        var body = definition.Body is { } raw
            ? Encoding.UTF8.GetBytes(RenderTemplate(Encoding.UTF8.GetString(raw), model))
            : [];

        var headers = definition.Headers
            .SelectMany(group => group.Select(value =>
                new KeyValuePair<string, string>(group.Key, RenderTemplate(value, model))))
            .ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return new CanonicalResponse
        {
            Status = definition.Status,
            StatusMessage = definition.StatusMessage,
            Headers = headers,
            Body = body,
            Delay = definition.Delay,
            DelayDistribution = definition.DelayDistribution,
            Fault = definition.Fault,
            Proxy = definition.Proxy,
        };
    }

    /// <summary>
    /// Rewrites the definition's body, headers and proxy target with the tenant's active environment
    /// values. Returns the definition unchanged when the tenant has no keys, so the ordinary path pays
    /// only a dictionary probe.
    /// <para>
    /// The proxy target is included because it is the field environments exist for — and it is
    /// otherwise never templated: both branches of <see cref="Render"/> copy <c>Proxy</c> verbatim, so
    /// without this a <c>{{baseUrl}}</c> proxy target would reach the outbound client as literal text.
    /// </para>
    /// </summary>
    private ResponseDefinition ApplyEnvironment(ResponseDefinition definition, TenantId tenant)
    {
        if (_environments is not { } environments || !environments.HasKeys(tenant))
        {
            return definition;
        }

        bool Lookup(string key, out string value) => environments.TryResolve(tenant, key, out value);

        var body = definition.Body;
        if (body is { Length: > 0 })
        {
            var raw = Encoding.UTF8.GetString(body);
            var substituted = EnvironmentSubstitution.Apply(raw, Lookup);
            if (!ReferenceEquals(raw, substituted))
            {
                body = Encoding.UTF8.GetBytes(substituted);
            }
        }

        var headers = definition.Headers
            .SelectMany(group => group.Select(value =>
                new KeyValuePair<string, string>(group.Key, EnvironmentSubstitution.Apply(value, Lookup))))
            .ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var proxy = definition.Proxy is { } directive
            ? directive with { BaseUrl = EnvironmentSubstitution.Apply(directive.BaseUrl, Lookup) }
            : null;

        return definition with { Body = body, Headers = headers, Proxy = proxy };
    }

    private string RenderTemplate(string template, object model) => _handlebars.Compile(template)(model);

    private static Dictionary<string, object?> BuildModel(RenderContext context) =>
        new() { ["request"] = RequestModel.Build(context.Request, context.UrlPathTemplate) };
}
