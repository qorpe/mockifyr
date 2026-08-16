using System.Text;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Harness;
using Mockifyr.Grpc.Test;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation that an <em>admin-managed</em> gRPC stub reaches gRPC serving (G13e): a
/// <c>Describe</c> stub is POSTed to <c>/__admin/mappings</c> at runtime (not loaded from a file), then
/// the call is made over gRPC. The management path and the gRPC serving hot path share the same store,
/// so the oracle (WireMock + gRPC extension) and Mockifyr must both serve the admin-added stub with the
/// same reply — the gRPC analogue of the G7a in-process management check. Requires Docker.
/// </summary>
public sealed class G13eGrpcAdminTests : IAsyncLifetime
{
    // The oracle needs a mapping file at startup (its descriptor loads regardless); this one is unused —
    // the Describe stub under test is added later via the admin API.
    private const string SeedMapping =
        """{"request":{"method":"POST","urlPath":"/mockifyr.grpc.test.Greeter/SayHello"},"response":{"status":200,"jsonBody":{"message":"seed"}}}""";

    private const string DescribeMapping =
        """
        {
          "request": { "method": "POST", "urlPath": "/mockifyr.grpc.test.Greeter/Describe" },
          "response": { "status": 200, "jsonBody": { "summary": "added via admin", "codes": [7, 8] } }
        }
        """;

    private static byte[] Descriptor() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Protos", "greeter.dsc"));

    private WireMockGrpcOracle _oracle = null!;

    public Task InitializeAsync()
    {
        _oracle = new WireMockGrpcOracle(Descriptor(), SeedMapping);
        return _oracle.StartAsync();
    }

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    [Fact]
    public async Task AdminAddedStub_IsServedOverGrpc_MatchesTheOracle()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-grpc-e-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "mappings"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "grpc"));
        File.WriteAllText(Path.Combine(root.FullName, "mappings", "seed.json"), SeedMapping);
        File.WriteAllBytes(Path.Combine(root.FullName, "grpc", "greeter.dsc"), Descriptor());

        await using var mockifyr = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--root-dir", root.FullName]);
        await mockifyr.StartAsync();
        try
        {
            var addresses = mockifyr.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var grpcAddress = new Uri(Local(addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal))));
            var adminAddress = new Uri(Local(addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))));

            // Add the Describe stub at runtime through the admin API on each side.
            using (var oracleAdmin = _oracle.CreateAdminClient())
            {
                await PostMappingAsync(oracleAdmin, DescribeMapping);
            }

            using (var mockifyrAdmin = new HttpClient { BaseAddress = adminAddress })
            {
                await PostMappingAsync(mockifyrAdmin, DescribeMapping);
            }

            await _oracle.WarmUpAsync(() => DescribeAsync(_oracle.GrpcAddress));
            var oracle = await DescribeAsync(_oracle.GrpcAddress);
            var mockifyrReply = await DescribeAsync(grpcAddress);

            Assert.Equal(oracle.Summary, mockifyrReply.Summary);
            Assert.Equal(oracle.Codes, mockifyrReply.Codes);
            Assert.Equal("added via admin", mockifyrReply.Summary);
            Assert.Equal(new[] { 7, 8 }, mockifyrReply.Codes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string Local(string address) => address.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");

    private static async Task PostMappingAsync(HttpClient client, string mappingJson)
    {
        using var content = new StringContent(mappingJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/__admin/mappings", content);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<DescribeReply> DescribeAsync(Uri address)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Greeter.GreeterClient(channel);
        return await client.DescribeAsync(new DescribeRequest());
    }
}
