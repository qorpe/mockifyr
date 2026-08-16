using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Harness;
using Mockifyr.Grpc.Test;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation of the expanded protobuf codec (G13b): a richer <c>Describe</c> call
/// exercises a repeated string field, an <c>enum</c> (by name), and a <c>map</c> on the request, and a
/// packed repeated <c>int32</c> on the reply. The same stub is served by the oracle (WireMock + gRPC
/// extension) and Mockifyr, and the decoded replies must match — proving Mockifyr's codec produces the
/// same proto3-JSON the reference extension does across these field kinds. Requires Docker.
/// </summary>
public sealed class G13bGrpcCodecTests : IAsyncLifetime
{
    private const string MappingJson =
        """
        {
          "request": {
            "method": "POST",
            "urlPath": "/mockifyr.grpc.test.Greeter/Describe",
            "bodyPatterns": [ { "equalToJson": "{ \"tags\": [\"a\", \"b\"], \"color\": \"GREEN\", \"counts\": { \"x\": 1, \"y\": 2 } }" } ]
          },
          "response": {
            "status": 200,
            "jsonBody": { "summary": "2 tags", "codes": [10, 20, 30] }
          }
        }
        """;

    private static byte[] Descriptor() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Protos", "greeter.dsc"));

    private WireMockGrpcOracle _oracle = null!;

    public Task InitializeAsync()
    {
        _oracle = new WireMockGrpcOracle(Descriptor(), MappingJson);
        return _oracle.StartAsync();
    }

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    [Fact]
    public async Task Describe_WithEnumMapRepeated_MatchesTheOracle()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-grpc-b-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "mappings"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "grpc"));
        File.WriteAllText(Path.Combine(root.FullName, "mappings", "stub.json"), MappingJson);
        File.WriteAllBytes(Path.Combine(root.FullName, "grpc", "greeter.dsc"), Descriptor());

        await using var mockifyr = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--root-dir", root.FullName]);
        await mockifyr.StartAsync();
        try
        {
            var mockifyrAddress = new Uri(mockifyr.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses
                .First(a => a.StartsWith("https://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));

            await _oracle.WarmUpAsync(() => DescribeAsync(_oracle.GrpcAddress));
            var oracle = await DescribeAsync(_oracle.GrpcAddress);
            var mockifyrReply = await DescribeAsync(mockifyrAddress);

            Assert.Equal(oracle.Summary, mockifyrReply.Summary);
            Assert.Equal(oracle.Codes, mockifyrReply.Codes);
            Assert.Equal("2 tags", mockifyrReply.Summary);
            Assert.Equal(new[] { 10, 20, 30 }, mockifyrReply.Codes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<DescribeReply> DescribeAsync(Uri address)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Greeter.GreeterClient(channel);

        var request = new DescribeRequest { Color = Color.Green };
        request.Tags.Add(new[] { "a", "b" });
        request.Counts.Add("x", 1);
        request.Counts.Add("y", 2);
        return await client.DescribeAsync(request);
    }
}
