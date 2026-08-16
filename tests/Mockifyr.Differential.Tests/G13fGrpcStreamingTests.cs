using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Harness;
using Mockifyr.Grpc.Test;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation of single-message gRPC streaming (G13f) — the extent WireMock's gRPC
/// extension supports (server streaming returns one message; client streaming consumes one; bidi is
/// unsupported). At the wire level a single-message stream is one request frame → one response frame,
/// so the unchanged codec+middleware serve it; the generated C# streaming clients read/write the stream.
/// The oracle (WireMock + gRPC extension) and Mockifyr must return the same reply. Requires Docker.
/// </summary>
public sealed class G13fGrpcStreamingTests : IAsyncLifetime
{
    private const string MappingJson =
        """
        {
          "mappings": [
            {
              "request": { "method": "POST", "urlPath": "/mockifyr.grpc.test.Greeter/ServerStream",
                           "bodyPatterns": [ { "equalToJson": "{ \"name\": \"Tom\" }" } ] },
              "response": { "status": 200, "jsonBody": { "message": "server-stream-reply" } }
            },
            {
              "request": { "method": "POST", "urlPath": "/mockifyr.grpc.test.Greeter/ClientStream",
                           "bodyPatterns": [ { "equalToJson": "{ \"name\": \"Ada\" }" } ] },
              "response": { "status": 200, "jsonBody": { "message": "client-stream-reply" } }
            }
          ]
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
    public async Task SingleMessageStreaming_MatchesTheOracle()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-grpc-f-");
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

            await _oracle.WarmUpAsync(() => ServerStreamAsync(_oracle.GrpcAddress));
            var oracleServer = await ServerStreamAsync(_oracle.GrpcAddress);
            var mockifyrServer = await ServerStreamAsync(mockifyrAddress);
            Assert.Equal(oracleServer, mockifyrServer);
            Assert.Equal(new[] { "server-stream-reply" }, mockifyrServer);

            var oracleClient = await ClientStreamAsync(_oracle.GrpcAddress);
            var mockifyrClient = await ClientStreamAsync(mockifyrAddress);
            Assert.Equal(oracleClient, mockifyrClient);
            Assert.Equal("client-stream-reply", mockifyrClient);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static Greeter.GreeterClient Client(Uri address)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        return new Greeter.GreeterClient(channel);
    }

    private static async Task<List<string>> ServerStreamAsync(Uri address)
    {
        var client = Client(address);
        using var call = client.ServerStream(new HelloRequest { Name = "Tom" });
        var messages = new List<string>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            messages.Add(call.ResponseStream.Current.Message);
        }

        return messages;
    }

    private static async Task<string> ClientStreamAsync(Uri address)
    {
        var client = Client(address);
        using var call = client.ClientStream();
        await call.RequestStream.WriteAsync(new HelloRequest { Name = "Ada" });
        await call.RequestStream.CompleteAsync();
        return (await call.ResponseAsync).Message;
    }
}
