namespace Mockifyr.Templating;

/// <summary>
/// Renders a WebSocket message-action template (G15d). The same Handlebars engine and helper set as
/// response templating, but the model exposes the inbound message as <c>{{message.body}}</c> (so
/// <c>Echo: {{message.body}}</c> and any helper over it — <c>{{jsonPath message.body '$.x'}}</c> — work).
/// </summary>
public sealed class MessageTemplateRenderer
{
    // Compiled once per distinct template (#266) — a message action re-renders per message.
    private readonly CompiledTemplateCache _templates = CompiledTemplateCache.Create();

    /// <summary>Renders <paramref name="template"/> with the inbound message body in scope.</summary>
    public string Render(string template, string messageBody)
    {
        var model = new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?> { ["body"] = messageBody },
        };

        return _templates.Render(template, model);
    }
}
