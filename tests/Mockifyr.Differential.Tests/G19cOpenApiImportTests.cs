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
}
