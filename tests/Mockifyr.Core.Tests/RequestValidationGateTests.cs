using System.Text.Json;
using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>
/// The engine-side half of serve-time contract validation (#448, ADR 0018): what the gate does with a
/// declaration, a validator's answer, and the absence of either. Every refusal here is fail-closed.
/// </summary>
public sealed class RequestValidationGateTests
{
    private static readonly CanonicalRequest Request = CanonicalRequestBuilder.Build("POST", "/x");

    private static readonly RequestValidationDirective Contract = new("/x", [], new ContractBody(true, "{}"));

    [Fact]
    public void A_stub_that_declares_nothing_is_never_checked()
        => Assert.Empty(new RequestValidationGate([new Fixed(Many(3))]).Check(Request, null));

    [Fact]
    public void A_host_with_no_validator_refuses_rather_than_serving_unchecked()
    {
        var violation = Assert.Single(new RequestValidationGate([]).Check(Request, Contract));

        Assert.Equal(("request", "", "unvalidated"), (violation.Location, violation.Pointer, violation.Rule));
        Assert.Contains("no validator registered", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declaration_that_could_not_be_read_refuses_with_its_reason_and_asks_no_validator()
    {
        var validator = new Fixed([]);
        var broken = new RequestValidationDirective("", [], null) { Defect = "no path" };

        var violation = Assert.Single(new RequestValidationGate([validator]).Check(Request, broken));

        Assert.Equal(("request", "", "contract"), (violation.Location, violation.Pointer, violation.Rule));
        Assert.Equal("This stub's validation contract cannot be read: no path", violation.Message);
        Assert.Equal(0, validator.Calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(RequestValidationGate.MaxViolations, RequestValidationGate.MaxViolations)]
    [InlineData(RequestValidationGate.MaxViolations + 1, RequestValidationGate.MaxViolations)]
    [InlineData(500, RequestValidationGate.MaxViolations)]
    public void The_answer_names_at_most_the_cap(int found, int named)
        => Assert.Equal(named, new RequestValidationGate([new Fixed(Many(found))]).Check(Request, Contract).Count);

    [Fact]
    public void Every_registered_validator_is_asked_and_their_findings_are_kept_in_order()
    {
        var violations = new RequestValidationGate([new Fixed([V("a")]), new Fixed([V("b")])]).Check(Request, Contract);

        Assert.Equal(["a", "b"], violations.Select(v => v.Pointer));
    }

    [Fact]
    public void The_problem_document_is_a_400_naming_location_pointer_and_rule()
    {
        var response = RequestValidationGate.Problem([new RequestViolation("body", "/amount", "type", "The body at /amount: wrong type")]);
        using var problem = JsonDocument.Parse(response.Body);
        var violation = problem.RootElement.GetProperty("violations")[0];

        Assert.Equal(400, response.Status);
        Assert.Equal("application/problem+json", response.Headers["content-type"].Single());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("about:blank", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("The request does not satisfy the API contract.", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal("The body at /amount: wrong type", violation.GetProperty("message").GetString());
        Assert.Equal("body", violation.GetProperty("location").GetString());
        Assert.Equal("/amount", violation.GetProperty("pointer").GetString());
        Assert.Equal("type", violation.GetProperty("rule").GetString());
    }

    private static RequestViolation V(string pointer) => new("body", pointer, "type", "m");

    private static List<RequestViolation> Many(int count) => [.. Enumerable.Range(0, count).Select(i => V($"/p{i}"))];

    private sealed class Fixed(IReadOnlyList<RequestViolation> answer) : IRequestValidator
    {
        public int Calls { get; private set; }

        public IReadOnlyList<RequestViolation> Validate(CanonicalRequest request, RequestValidationDirective contract)
        {
            Calls++;
            return answer;
        }
    }
}
