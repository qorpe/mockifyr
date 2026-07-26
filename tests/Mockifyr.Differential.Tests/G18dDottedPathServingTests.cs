using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Mockifyr.Differential.Harness;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential regression for file-looking stub URLs (found by G18d): a stub on
/// <c>/report.json</c> (or any dotted last segment, like the Twilio profile's
/// <c>/Messages.json</c>) must serve. Mockifyr's fallback route used ASP.NET's default
/// <c>{*path:nonfile}</c> pattern, whose <c>:nonfile</c> constraint silently 404'd such paths
/// before the engine ever saw them — WireMock serves them, proven here against the oracle.
/// Requires Docker.
/// </summary>
public sealed class G18dDottedPathServingTests : IAsyncLifetime
{
    private readonly WireMockOracle _oracle = new();
    private readonly WebApplicationFactory<Program> _mockifyr = new();

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync()
    {
        await _mockifyr.DisposeAsync();
        await _oracle.DisposeAsync();
    }

    [Theory]
    [InlineData("/report.json")]
    [InlineData("/api/v2/export.csv")]
    [InlineData("/2010-04-01/Accounts/ACtest/Messages.json")]
    public async Task DottedPaths_Serve_MatchingTheOracle(string path)
    {
        var stub = """{"request":{"method":"GET","urlPath":" """.TrimEnd() + path +
                   """ "},"response":{"status":200,"body":"dotted ok"}}""".TrimStart();

        using var oracleClient = _oracle.CreateAdminClient();
        using var mockifyrClient = _mockifyr.CreateClient();

        foreach (var client in new[] { oracleClient, mockifyrClient })
        {
            var created = await client.PostAsync("/__admin/mappings", new StringContent(stub, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var oracleResponse = await oracleClient.GetAsync(path);
        using var mockifyrResponse = await mockifyrClient.GetAsync(path);

        Assert.Equal(oracleResponse.StatusCode, mockifyrResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mockifyrResponse.StatusCode);
        Assert.Equal(
            await oracleResponse.Content.ReadAsStringAsync(),
            await mockifyrResponse.Content.ReadAsStringAsync());
    }
}
