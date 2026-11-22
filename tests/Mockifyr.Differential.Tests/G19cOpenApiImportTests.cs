using System.Net;
using System.Text;
using System.Text.Json;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire-level self-tests for <c>POST /__admin/openapi/import</c> (G19c, ADR 0011). Import claims
/// are proven by SERVING: the spec goes in over HTTP and the generated stubs are then driven with
/// real requests — declared examples come back, synthesized samples render (Faker formats
/// included), and with <c>?stateful=true</c> the imported CRUD set drives the full G19b loop.
/// Refusals are typed and transactional: nothing half-lands.
/// </summary>
public sealed class G19cOpenApiImportTests : IAsyncDisposable
{
    private readonly MockifyrKestrelHost _host = new();
    private readonly HttpClient _client;

    public G19cOpenApiImportTests()
    {
        _client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string path, string? body = null, string tenant = "acme")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_imported_spec_serves_examples_and_synthesized_samples()
    {
        var (importStatus, importBody) = await SendAsync(HttpMethod.Post, "/__admin/openapi/import", Fixture("petstore.json"));
        Assert.Equal(HttpStatusCode.OK, importStatus);
        Assert.Contains("\"imported\":3", importBody.Replace(" ", ""));

        // The declared example serves verbatim.
        var (showStatus, showBody) = await SendAsync(HttpMethod.Get, "/pets/7");
        Assert.Equal(HttpStatusCode.OK, showStatus);
        using (var doc = JsonDocument.Parse(showBody))
        {
            Assert.Equal("Odie", doc.RootElement.GetProperty("name").GetString());
        }

        // The synthesized sample renders live: static primitives plus Faker-backed formats.
        var (listStatus, listBody) = await SendAsync(HttpMethod.Get, "/pets");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        using (var doc = JsonDocument.Parse(listBody))
        {
            var pet = doc.RootElement[0];
            Assert.Equal("string", pet.GetProperty("name").GetString());
            Assert.Contains("@", pet.GetProperty("contact").GetString());
            Assert.True(Guid.TryParse(pet.GetProperty("ref").GetString(), out _));
        }

        var (createStatus, _) = await SendAsync(HttpMethod.Post, "/pets");
        Assert.Equal(HttpStatusCode.Created, createStatus);
    }

    [Fact]
    public async Task A_stateful_import_drives_the_full_crud_loop_from_the_yaml_spec()
    {
        var (importStatus, importBody) = await SendAsync(
            HttpMethod.Post, "/__admin/openapi/import?stateful=true", Fixture("orders-api.yaml"));
        Assert.Equal(HttpStatusCode.OK, importStatus);
        Assert.Contains("\"imported\":7", importBody.Replace(" ", ""));

        // CREATE answers 201 with a Location header and echoes the stored document.
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/orders");
        createRequest.Headers.Add("X-Mockifyr-Tenant", "acme");
        createRequest.Content = new StringContent("""{"status":"created","total":42}""", Encoding.UTF8, "application/json");
        using var createResponse = await _client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var location = Assert.Single(createResponse.Headers.GetValues("Location"));
        var id = location.Split('/')[^1];
        Assert.False(string.IsNullOrWhiteSpace(id));

        // READ what was created; UPDATE it; LIST reflects it; DELETE removes it; then the miss.
        var (readStatus, readBody) = await SendAsync(HttpMethod.Get, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.OK, readStatus);
        Assert.Equal("""{"status":"created","total":42}""", readBody);

        var (putStatus, _) = await SendAsync(HttpMethod.Put, $"/api/orders/{id}", """{"status":"shipped","total":42}""");
        Assert.Equal(HttpStatusCode.OK, putStatus);

        var (_, listBody) = await SendAsync(HttpMethod.Get, "/api/orders");
        using (var doc = JsonDocument.Parse(listBody))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("shipped", doc.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        }

        var (deleteStatus, _) = await SendAsync(HttpMethod.Delete, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteStatus);
        var (missStatus, _) = await SendAsync(HttpMethod.Get, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missStatus);

        // The non-resource operations imported as ordinary example stubs alongside.
        var (healthStatus, healthBody) = await SendAsync(HttpMethod.Get, "/api/health");
        Assert.Equal(HttpStatusCode.OK, healthStatus);
        Assert.Contains("up", healthBody);
    }

    [Fact]
    public async Task Refusals_are_typed_and_transactional()
    {
        var external = """
            {"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/a":{"get":{
              "responses":{"200":{"description":"ok","content":{"application/json":{
                "schema":{"$ref":"https://evil.example.com/steal.json#/S"}}}}}}}}}
            """;
        var (externalStatus, externalBody) = await SendAsync(HttpMethod.Post, "/__admin/openapi/import", external);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, externalStatus);
        Assert.Contains("OpenApi.ExternalRef", externalBody);
        Assert.Contains("evil.example.com", externalBody);

        var (invalidStatus, invalidBody) = await SendAsync(HttpMethod.Post, "/__admin/openapi/import", "not a spec at all");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidStatus);
        Assert.Contains("OpenApi.Invalid", invalidBody);

        // Nothing half-landed from either refusal.
        var (_, mappings) = await SendAsync(HttpMethod.Get, "/__admin/mappings");
        Assert.Contains("\"mappings\":[]", mappings.Replace(" ", ""));
    }

    [Fact]
    public async Task Imported_stubs_are_ordinary_mappings_listable_and_exportable()
    {
        (await SendAsync(HttpMethod.Post, "/__admin/openapi/import", Fixture("petstore.json"))).ToString();

        var (status, body) = await SendAsync(HttpMethod.Get, "/__admin/mappings");
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var mappings = doc.RootElement.GetProperty("mappings").EnumerateArray().ToList();
        Assert.Equal(3, mappings.Count);
        Assert.All(mappings, m => Assert.True(m.TryGetProperty("id", out _)));
    }

    // ---- named examples as scenario states (#449) ----

    private async Task<(int Status, string Body)> PayAsync(string tenant = "acme")
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/payments", "{}", tenant);
        return ((int)status, body);
    }

    private Task<(HttpStatusCode Status, string Body)> SelectAsync(string scenario, string state, string tenant = "acme") =>
        SendAsync(HttpMethod.Put, $"/__admin/scenarios/{Uri.EscapeDataString(scenario)}/state",
            JsonSerializer.Serialize(new { state }), tenant);

    [Fact]
    public async Task With_scenarios_a_selected_state_serves_its_named_example_with_its_own_status()
    {
        var (importStatus, importBody) = await SendAsync(
            HttpMethod.Post, "/__admin/openapi/import?scenarios=true&scenario=payments-v1", Fixture("payments-scenarios.yaml"));
        Assert.Equal(HttpStatusCode.OK, importStatus);
        using (var report = JsonDocument.Parse(importBody))
        {
            // Two operations' default stubs, and one stub per named example of createPayment.
            Assert.Equal(5, report.RootElement.GetProperty("imported").GetInt32());
            var scenarios = report.RootElement.GetProperty("scenarios");
            Assert.Equal("payments-v1", scenarios.GetProperty("scenario").GetString());
            Assert.Equal(
                ["createPayment approved 201", "createPayment pending 201", "createPayment insufficient-funds 402"],
                scenarios.GetProperty("states").EnumerateArray().Select(s =>
                    $"{s.GetProperty("operation").GetString()} {s.GetProperty("state").GetString()} {s.GetProperty("status").GetInt32()}"));
            Assert.Empty(scenarios.GetProperty("ignored").EnumerateArray());
        }

        // Before anything is selected the scenario is Started, and the default answer is today's: the first example.
        var (status, body) = await PayAsync();
        Assert.Equal(201, status);
        Assert.Contains("APPROVED", body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await SelectAsync("payments-v1", "insufficient-funds")).Status);
        (status, body) = await PayAsync();
        Assert.Equal(402, status);
        Assert.Contains("INSUFFICIENT_FUNDS", body, StringComparison.Ordinal);

        // An operation that declares no example of that name keeps its default answer in that state.
        var (balanceStatus, balanceBody) = await SendAsync(HttpMethod.Get, "/balance");
        Assert.Equal((HttpStatusCode.OK, true), (balanceStatus, balanceBody.Contains("100", StringComparison.Ordinal)));

        await SelectAsync("payments-v1", "pending");
        (status, body) = await PayAsync();
        Assert.Equal((201, true), (status, body.Contains("PENDING", StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/__admin/scenarios/reset")).Status);
        (status, body) = await PayAsync();
        Assert.Equal((201, true), (status, body.Contains("APPROVED", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_scenarios_selecting_a_state_changes_nothing_and_the_report_is_unchanged()
    {
        var (_, importBody) = await SendAsync(HttpMethod.Post, "/__admin/openapi/import", Fixture("payments-scenarios.yaml"));
        Assert.Equal("{\"imported\":2}", importBody.Replace(" ", ""));

        await SelectAsync("Payments", "insufficient-funds");
        var (status, body) = await PayAsync();
        Assert.Equal((201, true), (status, body.Contains("APPROVED", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_scenario_defaults_to_the_document_title_and_a_state_selected_in_one_tenant_stays_there()
    {
        foreach (var tenant in new[] { "acme", "globex" })
        {
            var (importStatus, _) = await SendAsync(
                HttpMethod.Post, "/__admin/openapi/import?scenarios=true", Fixture("payments-scenarios.yaml"), tenant);
            Assert.Equal(HttpStatusCode.OK, importStatus);
        }

        await SelectAsync("Payments", "insufficient-funds", "acme");

        Assert.Equal(402, (await PayAsync("acme")).Status);
        Assert.Equal(201, (await PayAsync("globex")).Status);
    }

    [Theory]
    [InlineData("%20%20", "The scenario name is empty")]
    [InlineData("a%01b", "The scenario name carries a control character")]
    public async Task An_unusable_scenario_name_is_refused_and_nothing_lands(string name, string expected)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Post, $"/__admin/openapi/import?scenarios=true&scenario={name}", Fixture("payments-scenarios.yaml"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains(expected, body, StringComparison.Ordinal);

        var (_, mappings) = await SendAsync(HttpMethod.Get, "/__admin/mappings");
        using var doc = JsonDocument.Parse(mappings);
        Assert.Empty(doc.RootElement.GetProperty("mappings").EnumerateArray());
    }

    /// <summary>
    /// The scenario name is a path segment of <c>PUT /__admin/scenarios/{name}/state</c>, so a name is accepted only
    /// if that route selects it: every name a client escapes and the route decodes back is driven end to end here.
    /// </summary>
    [Theory]
    [InlineData("a%b")]
    [InlineData("a%2Fb")]
    [InlineData("pay ments")]
    [InlineData("a+b")]
    [InlineData("a?b#c")]
    [InlineData("a\\b")]
    [InlineData("a.b")]
    [InlineData("...a")]
    [InlineData("çekirdek İ")]
    public async Task An_accepted_scenario_name_is_one_the_state_route_selects(string name)
    {
        var (importStatus, _) = await SendAsync(
            HttpMethod.Post, $"/__admin/openapi/import?scenarios=true&scenario={Uri.EscapeDataString(name)}", Fixture("payments-scenarios.yaml"));
        Assert.Equal(HttpStatusCode.OK, importStatus);

        Assert.Equal(HttpStatusCode.OK, (await SelectAsync(name, "insufficient-funds")).Status);
        Assert.Equal(402, (await PayAsync()).Status);
    }

    /// <summary>
    /// The names the route cannot round-trip are refused at import, where the fix is one parameter away, rather than
    /// accepted and found unselectable: an escaped '/' stays escaped in a route value (the select answers 200 and
    /// sets another name), and '.' / '..' are dot-segments a client removes (the select answers 404).
    /// </summary>
    [Theory]
    [InlineData("payments/v1", "contains '/'")]
    [InlineData("/", "contains '/'")]
    [InlineData(".", "is only dots")]
    [InlineData("..", "is only dots")]
    [InlineData("...", "is only dots")]
    public async Task A_scenario_name_the_state_route_cannot_select_is_refused(string name, string expected)
    {
        var (status, body) = await SendAsync(
            HttpMethod.Post, $"/__admin/openapi/import?scenarios=true&scenario={Uri.EscapeDataString(name)}", Fixture("payments-scenarios.yaml"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains($"The scenario name {expected}", body, StringComparison.Ordinal);
    }
}
