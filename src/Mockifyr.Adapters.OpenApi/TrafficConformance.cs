using System.Text.Json;
using Json.Schema;
using Microsoft.OpenApi.Models;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>What kind of disagreement a traffic conformance run found (#287).</summary>
public enum TrafficDriftKind
{
    /// <summary>A client called an operation the specification does not declare.</summary>
    UndeclaredOperation,

    /// <summary>A required parameter the specification declares was not sent.</summary>
    MissingParameter,

    /// <summary>The request body does not satisfy the schema the specification declares.</summary>
    RequestSchemaViolation,
}

/// <summary>One way recorded traffic disagreed with the contract (#287).</summary>
/// <param name="Kind">Which kind of disagreement.</param>
/// <param name="Method">The method the client used.</param>
/// <param name="Url">The URL the client called.</param>
/// <param name="Detail">A sentence naming what the contract expected and what arrived.</param>
public sealed record TrafficDrift(TrafficDriftKind Kind, string Method, string Url, string Detail);

/// <summary>What one traffic conformance run concluded (#287).</summary>
/// <param name="Findings">Every disagreement, in a stable order.</param>
/// <param name="RequestsExamined">How many journaled requests were checked.</param>
/// <param name="RequestsConforming">How many of them the contract allows.</param>
public sealed record TrafficReport(
    IReadOnlyList<TrafficDrift> Findings, int RequestsExamined, int RequestsConforming)
{
    /// <summary>True when every examined request was one the contract allows.</summary>
    public bool Conforms => Findings.Count == 0;
}

/// <summary>One journaled request, reduced to what a contract check needs (#287).</summary>
/// <param name="Method">The method.</param>
/// <param name="Url">The URL including its query string.</param>
/// <param name="Body">The request body as text, or null when there was none.</param>
/// <param name="Headers">Header names that were present.</param>
public sealed record RecordedRequest(
    string Method, string Url, string? Body, IReadOnlyCollection<string> Headers);

/// <summary>
/// Checks what clients actually sent against what the contract allows (#287).
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="ContractConformance"/>, pointed the other way. That one asks whether the
/// mock still describes the API; this asks whether the <em>consumer</em> is staying inside it — calling
/// an endpoint the contract never promised, omitting a required parameter, sending a body the schema
/// forbids. Every one of those works perfectly against a mock that is more permissive than the real
/// service, and fails the first time it meets the real one.
/// </para>
/// <para>
/// It reads the journal and reports; it never changes a stub, a request, or the journal itself.
/// </para>
/// </remarks>
public static class TrafficConformance
{
    /// <summary>The maximum number of schema pointers reported per request.</summary>
    public const int MaxSchemaFindings = 3;

    /// <summary>Checks every recorded request against the specification.</summary>
    public static TrafficReport Verify(string specText, IReadOnlyList<RecordedRequest> requests)
    {
        var document = ContractConformance.ParseSpec(specText);
        var operations = ContractConformance.OperationsOf(document);
        var findings = new List<TrafficDrift>();
        var conforming = 0;

        foreach (var request in requests)
        {
            var before = findings.Count;
            var match = ContractConformance.FindOperation(operations, request.Method, Path(request.Url));

            if (match is null)
            {
                findings.Add(new TrafficDrift(
                    TrafficDriftKind.UndeclaredOperation, request.Method, request.Url,
                    "A client called this and the specification does not declare it. Either the contract is behind, or the client is calling something that will not exist in production."));
                continue;
            }

            findings.AddRange(CheckParameters(request, match));
            findings.AddRange(CheckBody(request, match));

            if (findings.Count == before)
            {
                conforming++;
            }
        }

        return new TrafficReport(
            [.. findings.OrderBy(f => f.Url, StringComparer.Ordinal).ThenBy(f => f.Method, StringComparer.Ordinal)
                .ThenBy(f => f.Kind)],
            requests.Count,
            conforming);
    }

    private static string Path(string url) => url.Split('?')[0];

    private static IEnumerable<TrafficDrift> CheckParameters(RecordedRequest request, OpenApiOperation operation)
    {
        var query = QueryNames(request.Url);

        foreach (var parameter in operation.Parameters.Where(p => p.Required))
        {
            // Path parameters are satisfied by the URL matching the template at all, and cookies are
            // carried inside a header this check already sees as `Cookie` — neither can be missing in a
            // way worth reporting separately.
            var present = parameter.In switch
            {
                ParameterLocation.Query => query.Contains(parameter.Name),
                ParameterLocation.Header => request.Headers.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase),
                _ => true,
            };

            if (!present)
            {
                yield return new TrafficDrift(
                    TrafficDriftKind.MissingParameter, request.Method, request.Url,
                    $"The specification requires the {Where(parameter.In)} '{parameter.Name}' and the client did not send it.");
            }
        }
    }

    private static string Where(ParameterLocation? location) => location switch
    {
        ParameterLocation.Query => "query parameter",
        ParameterLocation.Header => "header",
        ParameterLocation.Cookie => "cookie",
        _ => "path parameter",
    };

    private static HashSet<string> QueryNames(string url)
    {
        var index = url.IndexOf('?', StringComparison.Ordinal);
        if (index < 0)
        {
            return [];
        }

        return
        [
            .. url[(index + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => Uri.UnescapeDataString(pair.Split('=')[0])),
        ];
    }

    private static IEnumerable<TrafficDrift> CheckBody(RecordedRequest request, OpenApiOperation operation)
    {
        if (operation.RequestBody is not { } declared
            || !declared.Content.TryGetValue("application/json", out var media)
            || media.Schema is null)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            // An absent body is only a finding when the contract insists on one; a GET with no body is
            // the normal case, not a violation.
            if (declared.Required)
            {
                yield return new TrafficDrift(
                    TrafficDriftKind.RequestSchemaViolation, request.Method, request.Url,
                    "The specification requires a request body and the client sent none.");
            }

            yield break;
        }

        JsonDocument? parsed = null;
        try
        {
            parsed = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            // handled below — a yield cannot live in a catch
        }

        if (parsed is null)
        {
            yield return new TrafficDrift(
                TrafficDriftKind.RequestSchemaViolation, request.Method, request.Url,
                "The specification declares a JSON request body and the client sent something that is not JSON.");
            yield break;
        }

        using (parsed)
        {
            var schema = JsonSchema.FromText(SchemaJson.Write(media.Schema));
            var result = schema.Evaluate(parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid)
            {
                yield break;
            }

            var reported = 0;
            foreach (var detail in ContractConformance.FailureLeaves(result))
            {
                if (reported++ >= MaxSchemaFindings)
                {
                    yield break;
                }

                var pointer = detail.InstanceLocation.ToString();
                var message = detail.Errors?.Values.FirstOrDefault() ?? "does not satisfy the schema";
                yield return new TrafficDrift(
                    TrafficDriftKind.RequestSchemaViolation, request.Method, request.Url,
                    $"{(pointer.Length == 0 ? "the request body" : pointer)}: {message}");
            }
        }
    }
}
