using System.Text.Json;

namespace Mockifyr.Core;

/// <summary>Where in a request a contract parameter lives (#448).</summary>
public enum ContractParameterLocation
{
    /// <summary>A segment of the path template, such as <c>{id}</c> in <c>/users/{id}</c>.</summary>
    Path,

    /// <summary>A query-string parameter.</summary>
    Query,

    /// <summary>A request header, matched case-insensitively.</summary>
    Header,
}

/// <summary>One parameter of an operation's contract (#448).</summary>
/// <param name="Name">The parameter's name as the contract declares it.</param>
/// <param name="In">Where it lives.</param>
/// <param name="Required">Whether the request must carry it.</param>
/// <param name="Schema">The parameter's JSON Schema as JSON text, or null when the contract declares none.</param>
/// <param name="Style">
/// The OpenAPI serialization style (<c>form</c>, <c>simple</c>, <c>deepObject</c>…), or null for the location's
/// default (<c>form</c> for query, <c>simple</c> for path and header). The validator judges a value by its schema
/// only in a serialization it implements; any other is checked for presence only (deferred-edges).
/// </param>
/// <param name="Explode">The OpenAPI <c>explode</c> flag, or null for the style's default (true for <c>form</c>).</param>
public sealed record ContractParameter(
    string Name, ContractParameterLocation In, bool Required, string? Schema, string? Style = null, bool? Explode = null);

/// <summary>An operation's request body contract (#448).</summary>
/// <param name="Required">Whether the request must carry a body.</param>
/// <param name="Schema">The JSON Schema the <c>application/json</c> body must satisfy, as JSON text.</param>
/// <param name="MediaTypes">
/// Every media type the operation declares for its body, JSON or not, or null when the contract does not list
/// them. A body sent as a JSON type is judged by <paramref name="Schema"/>; one sent as another declared type is
/// not judged (presence only); one sent as a type the list does not declare is refused.
/// </param>
public sealed record ContractBody(bool Required, string Schema, IReadOnlyList<string>? MediaTypes = null);

/// <summary>
/// A stub's opt-in declaration that a request it matches must also satisfy the operation's contract
/// (#448, ADR 0018). It is carried by the stub itself, as <c>request.validate</c> in the mapping JSON,
/// so it persists, exports and restores with the stub on every backend. Null on every stub that does not
/// declare it: the default path is untouched.
/// </summary>
/// <param name="PathTemplate">The contract's path template, from which path parameter values are read.</param>
/// <param name="Parameters">The parameters the contract declares.</param>
/// <param name="Body">The body contract, or null when the operation declares no JSON body.</param>
public sealed record RequestValidationDirective(
    string PathTemplate, IReadOnlyList<ContractParameter> Parameters, ContractBody? Body)
{
    /// <summary>
    /// Why the declaration could not be read, when it could not (a hand-written <c>validate</c> with a
    /// missing path, a parameter with no name). Fail-closed: such a stub rejects every request it wins
    /// with this reason, rather than being served unchecked by a contract that was meant to hold.
    /// </summary>
    public string? Defect { get; init; }
}

/// <summary>One way a request disagreed with its operation's contract (#448).</summary>
/// <param name="Location">Where: <c>path</c>, <c>query</c>, <c>header</c> or <c>body</c>.</param>
/// <param name="Pointer">
/// What, inside that location: the parameter's name for a parameter, a JSON Pointer into the body
/// (<c>""</c> for the body as a whole).
/// </param>
/// <param name="Rule">The contract rule that failed, in JSON Schema's vocabulary where one applies
/// (<c>required</c>, <c>type</c>, <c>enum</c>, <c>format</c>, <c>additionalProperties</c>, <c>maximum</c>…).</param>
/// <param name="Message">A sentence a person can act on.</param>
public sealed record RequestViolation(string Location, string Pointer, string Rule, string Message);

/// <summary>
/// Checks a request against an operation's contract (#448). Implemented at the edge
/// (<c>Mockifyr.Adapters.OpenApi</c>), because judging a JSON Schema needs a library and the engine has
/// no dependencies.
/// </summary>
public interface IRequestValidator
{
    /// <summary>Every violation, in a stable order; empty when the request satisfies the contract.</summary>
    IReadOnlyList<RequestViolation> Validate(CanonicalRequest request, RequestValidationDirective contract);
}

/// <summary>
/// Applies a stub's <see cref="RequestValidationDirective"/> and builds the answer a violating request
/// gets (#448). Fail-closed: a stub that demands validation on a host with no validator registered
/// rejects the request rather than serving it unchecked, as the signature gate does.
/// </summary>
public sealed class RequestValidationGate(IEnumerable<IRequestValidator> validators)
{
    /// <summary>The most violations one answer names, so a hostile body cannot grow the response without bound.</summary>
    public const int MaxViolations = 20;

    private readonly IReadOnlyList<IRequestValidator> _validators = [.. validators];

    /// <summary>The violations of <paramref name="request"/>, at most <see cref="MaxViolations"/>; empty when none or nothing is declared.</summary>
    public IReadOnlyList<RequestViolation> Check(CanonicalRequest request, RequestValidationDirective? contract)
    {
        if (contract is null)
        {
            return [];
        }

        if (contract.Defect is { } defect)
        {
            return [new RequestViolation("request", "", "contract", $"This stub's validation contract cannot be read: {defect}")];
        }

        if (_validators.Count == 0)
        {
            return
            [
                new RequestViolation(
                    "request", "", "unvalidated",
                    "This stub requires its request to be validated against its contract, and this host has no validator registered."),
            ];
        }

        var found = new List<RequestViolation>();
        foreach (var validator in _validators)
        {
            found.AddRange(validator.Validate(request, contract));
        }

        return found.Count <= MaxViolations ? found : found[..MaxViolations];
    }

    /// <summary>
    /// The 400 answer for a request that broke its contract: an RFC 9457 problem document naming each
    /// violation by location, pointer and rule.
    /// </summary>
    public static CanonicalResponse Problem(IReadOnlyList<RequestViolation> violations)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank",
            title = "The request does not satisfy the API contract.",
            status = 400,
            violations = violations.Select(v => new { location = v.Location, pointer = v.Pointer, rule = v.Rule, message = v.Message }),
        });

        return new CanonicalResponse
        {
            Status = 400,
            Headers = new[] { new KeyValuePair<string, string>("Content-Type", "application/problem+json") }
                .ToLookup(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase),
            Body = body,
        };
    }
}
