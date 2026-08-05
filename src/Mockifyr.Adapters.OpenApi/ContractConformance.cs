using System.Text.Json;
using Json.Schema;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>What kind of disagreement a conformance run found (#287).</summary>
public enum DriftKind
{
    /// <summary>A stub describes an operation the specification does not declare.</summary>
    UndeclaredOperation,

    /// <summary>The specification declares an operation no stub answers.</summary>
    UncoveredOperation,

    /// <summary>A stub answers with a status the specification does not declare for that operation.</summary>
    UndeclaredStatus,

    /// <summary>A stub's response body does not satisfy the schema the specification declares.</summary>
    SchemaViolation,
}

/// <summary>One disagreement between what is stubbed and what the specification says (#287).</summary>
/// <param name="Kind">Which kind of disagreement.</param>
/// <param name="Method">The HTTP method involved.</param>
/// <param name="Path">The specification path, or the stub's URL when the operation is undeclared.</param>
/// <param name="StubId">The stub at fault, or null when the finding is about a missing stub.</param>
/// <param name="Detail">A sentence a human can act on, naming the pointer where one exists.</param>
public sealed record ContractDrift(
    DriftKind Kind, string Method, string Path, Guid? StubId, string Detail);

/// <summary>What one conformance run concluded (#287).</summary>
/// <param name="Findings">Every disagreement, in a stable order.</param>
/// <param name="OperationsInSpec">How many operations the specification declares.</param>
/// <param name="OperationsCovered">How many of them at least one stub answers.</param>
public sealed record ConformanceReport(
    IReadOnlyList<ContractDrift> Findings, int OperationsInSpec, int OperationsCovered)
{
    /// <summary>True when nothing disagreed — the answer a CI step wants.</summary>
    public bool Conforms => Findings.Count == 0;
}

/// <summary>
/// The stub a conformance run examines, reduced to what the check needs (#287).
/// </summary>
/// <remarks>
/// Deliberately not <c>StubMapping</c>: the check is about the <em>declared</em> shape a stub was
/// written with, which lives in its mapping JSON, not about the matcher objects the engine compiled
/// from it. Keeping it a plain record also keeps this adapter free of a dependency on the engine.
/// </remarks>
/// <param name="Id">The stub's id, so a finding can point at it.</param>
/// <param name="MappingJson">The stub's own mapping JSON.</param>
public sealed record StubUnderTest(Guid Id, string MappingJson);

/// <summary>
/// Checks a stub set against an OpenAPI specification (#287).
/// </summary>
/// <remarks>
/// <para>
/// The deepest failure mode of mocking is not a bug in the mock — it is the mock being confidently out
/// of date. The upstream adds a required field, tightens a status, drops an endpoint; the stubs do not
/// move; every test stays green; production breaks. A mock that has silently drifted is worse than no
/// mock, because it manufactures confidence.
/// </para>
/// <para>
/// This reports, it never mutates. A conformance run tells an operator what disagrees; deciding which
/// side is wrong is a judgement about their system, not ours.
/// </para>
/// </remarks>
public static class ContractConformance
{
    /// <summary>Checks every stub against the specification and reports what disagrees.</summary>
    public static ConformanceReport Verify(string specText, IReadOnlyList<StubUnderTest> stubs)
    {
        var document = Parse(specText);
        var operations = Operations(document);
        var findings = new List<ContractDrift>();
        var covered = new HashSet<(string Path, string Method)>();

        foreach (var stub in stubs)
        {
            var declared = StubShape.Read(stub);
            if (declared is null)
            {
                // A stub with no method or URL to speak of is not describing an HTTP operation at all —
                // a gRPC or message stub sharing the tenant. Silence is the honest answer, not a finding.
                continue;
            }

            var match = Find(operations, declared);
            if (match is null)
            {
                findings.Add(new ContractDrift(
                    DriftKind.UndeclaredOperation, declared.Method, declared.PathPattern, stub.Id,
                    "The specification declares no such operation. Either the upstream dropped it or the stub is stale."));
                continue;
            }

            covered.Add((match.Path, match.Method));
            findings.AddRange(CheckResponse(declared, match, stub.Id));
        }

        findings.AddRange(operations
            .Where(operation => !covered.Contains((operation.Path, operation.Method)))
            .Select(operation => new ContractDrift(
                DriftKind.UncoveredOperation, operation.Method, operation.Path, null,
                "The specification declares this operation and no stub answers it.")));

        return new ConformanceReport(
            [.. findings.OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Method, StringComparer.Ordinal)
                .ThenBy(f => f.Kind)],
            operations.Count,
            covered.Count);
    }

    private static OpenApiDocument Parse(string specText)
    {
        // The same guards the importer applies, for the same reasons (G19c): a conformance run takes a
        // document from the same untrusted place an import does.
        OpenApiStubGenerator.GuardSpec(specText);

        var document = new OpenApiStringReader().Read(specText, out var diagnostic);
        return diagnostic?.Errors is { Count: > 0 } errors
            ? throw new OpenApiImportException(OpenApiImportError.Invalid, errors[0].Message)
            : document;
    }

    private sealed record SpecOperation(string Path, string Method, OpenApiOperation Operation);

    private static List<SpecOperation> Operations(OpenApiDocument document) =>
    [
        .. from entry in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal)
           from operation in entry.Value.Operations
           select new SpecOperation(entry.Key, operation.Key.ToString().ToUpperInvariant(), operation.Value),
    ];

    /// <summary>
    /// Matches a stub to the operation it is answering. Path templates are compared structurally —
    /// <c>/orders/{id}</c> and <c>/orders/{orderId}</c> describe the same operation, and a stub written
    /// against a concrete path (<c>/orders/42</c>) still belongs to the templated operation it exercises.
    /// </summary>
    private static SpecOperation? Find(List<SpecOperation> operations, StubShape stub)
    {
        var candidates = operations.Where(operation =>
            string.Equals(operation.Method, stub.Method, StringComparison.OrdinalIgnoreCase)
            && PathsAgree(operation.Path, stub.PathPattern)).ToList();

        // A specification can declare both /orders/new and /orders/{id}, and a stub for /orders/new
        // agrees with each of them. The literal is the one it is answering; leaving the choice to
        // enumeration order would decide it by alphabet, which is the same as deciding it by luck.
        return candidates.Count <= 1
            ? candidates.FirstOrDefault()
            : candidates.OrderBy(Wildcards).ThenBy(o => o.Path, StringComparer.Ordinal).First();
    }

    private static int Wildcards(SpecOperation operation) =>
        operation.Path.Count(character => character == '{');

    private static bool PathsAgree(string specPath, string stubPath)
    {
        var spec = specPath.Trim('/').Split('/');
        var stub = stubPath.Split('?')[0].Trim('/').Split('/');
        if (spec.Length != stub.Length)
        {
            return false;
        }

        for (var i = 0; i < spec.Length; i++)
        {
            var specSegment = spec[i];
            var stubSegment = stub[i];

            // A templated segment on either side stands for anything: the specification's {id} and the
            // stub's own {orderId} or literal 42 are all the same position in the same operation.
            var wildcard = specSegment.StartsWith('{') || stubSegment.StartsWith('{');
            if (!wildcard && !string.Equals(specSegment, stubSegment, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The leaves of an evaluation that actually failed. The list output nests, and the parent nodes
    /// carry no message of their own — reporting them would print the same failure several times with
    /// less information each time.
    /// </summary>
    private static IEnumerable<EvaluationResults> Failures(EvaluationResults result)
    {
        if (result.Errors is { Count: > 0 })
        {
            yield return result;
        }

        foreach (var child in (result.Details ?? []).SelectMany(Failures))
        {
            yield return child;
        }
    }

    private static IEnumerable<ContractDrift> CheckResponse(StubShape stub, SpecOperation match, Guid stubId)
    {
        var status = stub.Status.ToString();
        var response = match.Operation.Responses.TryGetValue(status, out var exact)
            ? exact
            : match.Operation.Responses.TryGetValue("default", out var fallback) ? fallback : null;

        if (response is null)
        {
            yield return new ContractDrift(
                DriftKind.UndeclaredStatus, match.Method, match.Path, stubId,
                $"The stub answers {status}, which the specification does not declare for this operation.");
            yield break;
        }

        if (stub.Body is null
            || !response.Content.TryGetValue("application/json", out var media)
            || media.Schema is null)
        {
            // Nothing to check against: an empty body, or an operation whose JSON response carries no
            // schema. Reporting that as drift would train people to ignore the report.
            yield break;
        }

        // A templated body is not JSON until it is rendered, and rendering needs a request. Validating
        // the template text would report drift on every stub that uses templating, which is the fastest
        // way to make a conformance report worthless.
        if (stub.Body.Contains("{{", StringComparison.Ordinal))
        {
            yield break;
        }

        // Parsing is separated from reporting because a yield cannot live in a catch block, and the
        // failure it reports — "the specification says JSON and this is not" — is a finding, not a crash.
        JsonDocument? parsed = null;
        try
        {
            parsed = JsonDocument.Parse(stub.Body);
        }
        catch (JsonException)
        {
            // handled below
        }

        if (parsed is null)
        {
            yield return new ContractDrift(
                DriftKind.SchemaViolation, match.Method, match.Path, stubId,
                "The specification declares a JSON response and the stub's body is not JSON.");
            yield break;
        }

        using (parsed)
        {
            var schema = JsonSchema.FromText(SchemaJson.Write(media.Schema));
            var result = schema.Evaluate(
                parsed.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

            if (result.IsValid)
            {
                yield break;
            }

            // A handful of pointers is a report somebody reads; forty is a wall somebody closes. The
            // first few name the shape of the problem, which is what a reader needs to go and look.
            var reported = 0;
            foreach (var detail in Failures(result))
            {
                if (reported++ >= 5)
                {
                    yield break;
                }

                var pointer = detail.InstanceLocation.ToString();
                var message = detail.Errors?.Values.FirstOrDefault() ?? "does not satisfy the schema";
                yield return new ContractDrift(
                    DriftKind.SchemaViolation, match.Method, match.Path, stubId,
                    $"{(pointer.Length == 0 ? "the body" : pointer)}: {message}");
            }
        }
    }
}
