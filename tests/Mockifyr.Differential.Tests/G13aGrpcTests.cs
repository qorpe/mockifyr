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
/// Differential validation of gRPC unary serving (G13a): the same gRPC stub is loaded into both the
/// oracle (WireMock + the official gRPC extension) and a Mockifyr host, and the same unary call is
/// driven against each with a real gRPC client. The replies must match — proving Mockifyr's
/// descriptor-driven protobuf↔JSON codec + engine produce the same result the reference extension does.
/// A gRPC stub is an ordinary POST-to-<c>/service/method</c> stub (<c>equalToJson</c> request →
/// <c>jsonBody</c> response); the facade converts the messages. Requires Docker.
/// </summary>
public sealed class G13aGrpcTests : IAsyncLifetime
{
    private const string MappingJson =
        """
        {
          "request": {
            "method": "POST",
            "urlPath": "/mockifyr.grpc.test.Greeter/SayHello",
            "bodyPatterns": [ { "equalToJson": "{ \"name\": \"Tom\" }" } ]
          },
          "response": {
            "status": 200,
            "jsonBody": { "message": "Hello Tom" }
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
    public async Task UnaryCall_MatchesTheOracle()
    {
        // Mockifyr: a host whose --root-dir has the stub (mappings/) and the descriptor (grpc/).
        var root = Directory.CreateTempSubdirectory("mockifyr-grpc-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "mappings"));
        Directory.CreateDirectory(Path.Combine(root.FullName, "grpc"));
        File.WriteAllText(Path.Combine(root.FullName, "mappings", "stub.json"), MappingJson);
        File.WriteAllBytes(Path.Combine(root.FullName, "grpc", "greeter.dsc"), Descriptor());

        // gRPC is driven over TLS (deterministic ALPN h2), so Mockifyr binds an HTTPS port too.
        await using var mockifyr = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--root-dir", root.FullName]);
        await mockifyr.StartAsync();
        try
        {
            var mockifyrAddress = new Uri(mockifyr.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses
                .First(a => a.StartsWith("https://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1"));

            await _oracle.WarmUpAsync(() => CallAsync(_oracle.GrpcAddress, "Tom"));
            var oracleReply = await CallAsync(_oracle.GrpcAddress, "Tom");
            var mockifyrReply = await CallAsync(mockifyrAddress, "Tom");

            Assert.Equal(oracleReply, mockifyrReply);
            Assert.Equal("Hello Tom", mockifyrReply);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<string> CallAsync(Uri address, string name)
    {
        // Accept the self-signed certs on both sides — parity is about the gRPC reply, not the cert.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Greeter.GreeterClient(channel);
        var reply = await client.SayHelloAsync(new HelloRequest { Name = name });
        return reply.Message;
    }
}
