using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mockifyr.Adapters.OpenApi;
using Mockifyr.Differential.Generator;
using Mockifyr.Differential.Harness;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// The stubs an import with <c>?scenarios=true</c> generates (#449) are served by the reference engine exactly as by
/// Mockifyr. The generator's output is plain mapping JSON (scenario binding, priority, status), so the oracle can
/// judge it: the generated set is loaded raw into both sides, beside one selector stub per state that moves the
/// scenario the way the dialect itself does (<c>newScenarioState</c>), and the same request sequence is diffed step
/// by step. Requires Docker.
/// </summary>
public sealed class G19cOpenApiScenarioDifferentialTests : IAsyncLifetime
{
    private const string Scenario = "payments-v1";

    private readonly DifferentialRunner _runner = new();

    public Task InitializeAsync() => _runner.StartAsync();

    public async Task DisposeAsync() => await _runner.DisposeAsync();

    private static RequestSpec Post(string url) => new() { Method = "POST", Url = url, Body = Encoding.UTF8.GetBytes("{}") };

    private static RequestSpec Get(string url) => new() { Method = "GET", Url = url };

    [Fact]
    public async Task The_generated_scenario_stubs_answer_as_the_reference_engine_answers_in_every_state()
    {
        var import = OpenApiStubGenerator.GenerateWithRelations(
            File.ReadAllText(Path.Combine("Fixtures", "payments-scenarios.yaml")), scenarios: true, scenarioName: Scenario);
        var mappings = new JsonArray([.. import.Mappings.Select(m => JsonNode.Parse(m))]);
        foreach (var state in new[] { "insufficient-funds", "pending", "Started" })
        {
            mappings.Add(JsonNode.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["scenarioName"] = Scenario,
                ["newScenarioState"] = state,
                ["request"] = new { method = "POST", urlPath = $"/__select/{state}" },
                ["response"] = new { status = 204 },
            })));
        }

        await _runner.LoadAsync(new JsonObject { ["mappings"] = mappings }.ToJsonString());

        RequestSpec[] steps =
        [
            Post("/payments"),                      // Started: the default stub, the first example
            Get("/balance"),
            Post("/__select/insufficient-funds"),
            Post("/payments"),                      // the 402 example
            Get("/balance"),                        // no example of that name: the default answer
            Post("/__select/pending"),
            Post("/payments"),                      // the second 201 example
            Post("/__select/Started"),
            Post("/payments"),                      // back to the default
        ];

        var failures = new List<string>();
        var payments = new List<(int Status, string Body)>();
        for (var step = 0; step < steps.Length; step++)
        {
            var outcome = await _runner.ProbeAsync(steps[step]);
            if (!outcome.Diff.IsMatch)
            {
                failures.Add($"step {step} {steps[step].Method} {steps[step].Url}: {outcome.Diff.Report}");
            }

            if (steps[step].Url == "/payments")
            {
                payments.Add((outcome.Oracle.Status, outcome.Oracle.BodyAsText));
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} divergence(s):\n{string.Join("\n", failures)}");
        // And the oracle itself answered what the import promises, so agreement is not agreement on nothing.
        Assert.Equal([201, 402, 201, 201], payments.Select(p => p.Status));
        Assert.Contains("APPROVED", payments[0].Body, StringComparison.Ordinal);
        Assert.Contains("INSUFFICIENT_FUNDS", payments[1].Body, StringComparison.Ordinal);
        Assert.Contains("PENDING", payments[2].Body, StringComparison.Ordinal);
        Assert.Contains("APPROVED", payments[3].Body, StringComparison.Ordinal);
    }
}
