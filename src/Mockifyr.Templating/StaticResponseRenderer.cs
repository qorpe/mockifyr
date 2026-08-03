using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>
/// Renders a response definition verbatim, with no templating. This is the G0/G2a baseline;
/// the Handlebars.Net-based templating renderer is layered on from G2b.
/// </summary>
public sealed class StaticResponseRenderer(IResponseBodyFiles? bodyFiles = null) : IResponseRenderer
{
    private readonly IResponseBodyFiles _files = bodyFiles ?? new NoResponseBodyFiles();

    /// <inheritdoc />
    public CanonicalResponse Render(ResponseDefinition definition, RenderContext context)
    {
        _ = context;
        if (ResponseBody.Resolve(definition, _files) is not { } body)
        {
            return ResponseBody.MissingFile(definition);
        }

        return new CanonicalResponse
        {
            Status = definition.Status,
            StatusMessage = definition.StatusMessage,
            Headers = definition.Headers,
            Body = body,
        };
    }
}
