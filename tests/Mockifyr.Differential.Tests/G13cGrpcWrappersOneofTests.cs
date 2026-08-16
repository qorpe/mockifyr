using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Harness;
using Mockifyr.Grpc.Test;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation of the codec's <c>oneof</c> + well-known <c>wrapper</c> support (G13c): a
/// <c>Wrapped</c> call carries <c>StringValue</c>/<c>Int32Value</c>/<c>BoolValue</c> wrappers and a
/// <c>oneof</c> on the request, and <c>StringValue</c>/<c>Int64Value</c> wrappers and a <c>oneof</c> on
/// the reply. Wrappers must render as their bare inner scalar (so the request's <c>equalToJson</c> even
/// matches) and the oneof must round-trip the single set member. The oracle (WireMock + gRPC extension)
/// and Mockifyr serve the same stub and their decoded replies must match. Requires Docker.
/// </summary>
public sealed class G13cGrpcWrappersOneofTests : IAsyncLifetime
{
    private const string MappingJson =
        """
        {
          "request": {
            "method": "POST",
            "urlPath": "/mockifyr.grpc.test.Greeter/Wrapped",
            "bodyPatterns": [ { "equalToJson": "{ \"note\": \"hi\", \"count\": 7, \"active\": true, \"text\": \"pick\" }" } ]
          },
          "response": {
            "status": 200,
            "jsonBody": { "label": "done", "total": "123", "ok": "yes" }
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
    public async Task Wrapped_WithWrappersAndOneof_MatchesTheOracle()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-grpc-c-");
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

            await _oracle.WarmUpAsync(() => WrappedAsync(_oracle.GrpcAddress));
            var oracle = await WrappedAsync(_oracle.GrpcAddress);
            var mockifyrReply = await WrappedAsync(mockifyrAddress);

            // The two engines must agree, and on the concrete expected wrapper/oneof reply.
            Assert.Equal(oracle.Label, mockifyrReply.Label);
            Assert.Equal(oracle.Total, mockifyrReply.Total);
            Assert.Equal(oracle.ResultCase, mockifyrReply.ResultCase);
            Assert.Equal(oracle.Ok, mockifyrReply.Ok);

            Assert.Equal("done", mockifyrReply.Label);
            Assert.Equal(123L, mockifyrReply.Total);
            Assert.Equal(WrappedReply.ResultOneofCase.Ok, mockifyrReply.ResultCase);
            Assert.Equal("yes", mockifyrReply.Ok);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<WrappedReply> WrappedAsync(Uri address)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Greeter.GreeterClient(channel);

        // Wrappers (note/count/active) plus the `text` arm of the request oneof.
        var request = new WrappedRequest { Note = "hi", Count = 7, Active = true, Text = "pick" };
        return await client.WrappedAsync(request);
    }
}
