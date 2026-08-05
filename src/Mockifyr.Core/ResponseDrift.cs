using System.Text.Json;

namespace Mockifyr.Core;

/// <summary>How a stub's answer differs from what the real upstream returned (#287).</summary>
public enum ResponseDriftKind
{
    /// <summary>The upstream answered and no stub would have.</summary>
    NoStub,

    /// <summary>The stub answers with a different status.</summary>
    StatusDiffers,

    /// <summary>A field the upstream returns is missing from the stub's body.</summary>
    FieldMissing,

    /// <summary>The stub's body carries a field the upstream did not return.</summary>
    FieldUnexpected,

    /// <summary>Both carry the field, with different JSON types.</summary>
    TypeDiffers,
}

/// <summary>
/// One way a stub disagrees with reality (#287).
/// </summary>
/// <param name="Kind">Which kind of disagreement.</param>
/// <param name="Method">The method of the recorded exchange.</param>
/// <param name="Url">The URL of the recorded exchange.</param>
/// <param name="Pointer">Where in the body, or null when the finding is about the whole response.</param>
/// <param name="Detail">A sentence naming both sides, so the reader does not have to guess which is which.</param>
public sealed record ResponseDrift(
    ResponseDriftKind Kind, string Method, string Url, string? Pointer, string Detail);

/// <summary>
/// Compares what a stub would answer against what the real upstream actually answered (#287).
/// </summary>
/// <remarks>
/// <para>
/// Verifying a stub set against a specification asks whether it matches the document. This asks the
/// harder question: whether it matches <em>reality</em>. A document can be out of date too; a recording
/// taken against the live upstream cannot be.
/// </para>
/// <para>
/// The comparison is structural, not literal. Values differ between environments and between minutes —
/// an id, a timestamp, a total — and reporting those would bury the findings that matter under noise
/// nobody can act on. What is compared is the <em>shape</em>: which fields exist and what type each
/// one is.
/// </para>
/// </remarks>
public static class ResponseDriftCheck
{
    /// <summary>The maximum number of body findings reported per exchange.</summary>
    /// <remarks>
    /// A handful of pointers is a report somebody reads; forty is a wall somebody closes. When a stub
    /// and reality have wholly diverged, the first few say so as clearly as all of them would.
    /// </remarks>
    public const int MaxBodyFindings = 5;

    /// <summary>
    /// Compares one exchange. <paramref name="stubStatus"/> and <paramref name="stubBody"/> are what
    /// the stub would have answered; null status means no stub matched at all.
    /// </summary>
    public static IReadOnlyList<ResponseDrift> Compare(
        string method,
        string url,
        int? stubStatus,
        string? stubBody,
        int upstreamStatus,
        string? upstreamBody)
    {
        if (stubStatus is null)
        {
            return
            [
                new ResponseDrift(
                    ResponseDriftKind.NoStub, method, url, null,
                    $"The upstream answered {upstreamStatus} and no stub matches this request."),
            ];
        }

        var findings = new List<ResponseDrift>();
        if (stubStatus != upstreamStatus)
        {
            findings.Add(new ResponseDrift(
                ResponseDriftKind.StatusDiffers, method, url, null,
                $"The stub answers {stubStatus}; the upstream answered {upstreamStatus}."));
        }

        // A templated body is not JSON until a request renders it, and a body neither side sends as
        // JSON has no shape to compare. Both are silences rather than findings — a report that fires on
        // every templated stub is one nobody reads.
        if (stubBody is null || upstreamBody is null || stubBody.Contains("{{", StringComparison.Ordinal))
        {
            return findings;
        }

        if (Parse(stubBody) is not { } stub || Parse(upstreamBody) is not { } upstream)
        {
            return findings;
        }

        using (stub)
        using (upstream)
        {
            var body = new List<ResponseDrift>();
            CompareShape(stub.RootElement, upstream.RootElement, string.Empty, method, url, body);
            findings.AddRange(body.Take(MaxBodyFindings));
        }

        return findings;
    }

    private static JsonDocument? Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CompareShape(
        JsonElement stub,
        JsonElement upstream,
        string pointer,
        string method,
        string url,
        List<ResponseDrift> findings)
    {
        if (findings.Count >= MaxBodyFindings)
        {
            return;
        }

        if (Kind(stub) != Kind(upstream))
        {
            findings.Add(new ResponseDrift(
                ResponseDriftKind.TypeDiffers, method, url, Show(pointer),
                $"{Show(pointer)}: the stub returns {Kind(stub)}, the upstream returns {Kind(upstream)}."));
            return;
        }

        switch (upstream.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in upstream.EnumerateObject())
                {
                    if (stub.TryGetProperty(property.Name, out var stubProperty))
                    {
                        CompareShape(stubProperty, property.Value, $"{pointer}/{property.Name}", method, url, findings);
                    }
                    else
                    {
                        findings.Add(new ResponseDrift(
                            ResponseDriftKind.FieldMissing, method, url, $"{pointer}/{property.Name}",
                            $"{pointer}/{property.Name}: the upstream returns this field and the stub does not."));
                    }
                }

                foreach (var property in stub.EnumerateObject())
                {
                    if (!upstream.TryGetProperty(property.Name, out _))
                    {
                        findings.Add(new ResponseDrift(
                            ResponseDriftKind.FieldUnexpected, method, url, $"{pointer}/{property.Name}",
                            $"{pointer}/{property.Name}: the stub returns this field and the upstream does not."));
                    }
                }

                break;

            case JsonValueKind.Array:
                // The first element of each stands for the array's shape. Comparing every element would
                // report the same difference once per row, and an empty array on either side says
                // nothing about shape at all.
                var stubFirst = stub.EnumerateArray().FirstOrDefault();
                var upstreamFirst = upstream.EnumerateArray().FirstOrDefault();
                if (stubFirst.ValueKind != JsonValueKind.Undefined && upstreamFirst.ValueKind != JsonValueKind.Undefined)
                {
                    CompareShape(stubFirst, upstreamFirst, $"{pointer}/0", method, url, findings);
                }

                break;
        }
    }

    /// <summary>
    /// The type as a reader would name it. <c>true</c> and <c>false</c> are one type here, and null is
    /// its own: a field that arrives null in one environment and populated in another is a difference
    /// worth seeing, but "the stub says true and reality says false" is a value, not a shape.
    /// </summary>
    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    private static string Show(string pointer) => pointer.Length == 0 ? "the body" : pointer;
}
