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
/// Differential validation of gRPC error/status responses (G13d): the WireMock gRPC extension returns
/// an error by naming the status in a <c>grpc-status-name</c> response header (plus an optional
/// <c>grpc-status-reason</c> message); the successful message body is not delivered. The oracle
/// (WireMock + gRPC extension) and Mockifyr must fail the call with the <em>same</em> gRPC status code
/// and detail. Requires Docker.
/// </summary>
public sealed class G13dGrpcStatusTests : IAsyncLifetime
{
    private const string MappingJson =
        """
        {
          "request": { "method": "POST", "urlPath": "/mockifyr.grpc.test.Greeter/SayHello" },
          "response": {
            "status": 200,
            "headers": { "grpc-status-name": "NOT_FOUND", "grpc-status-reason": "no such hero" }
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
    public async Task ErrorStatus_MatchesTheOracle()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-grpc-d-");
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

            var oracle = await CallOracleWhenReadyAsync();
            var mockifyrStatus = await CallAsync(mockifyrAddress);

            // Both sides fail the call with the same status code and detail.
            Assert.Equal(oracle.Code, mockifyrStatus.Code);
            Assert.Equal(oracle.Detail, mockifyrStatus.Detail);
            Assert.Equal(StatusCode.NotFound, mockifyrStatus.Code);
            Assert.Equal("no such hero", mockifyrStatus.Detail);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Calls the oracle, waiting out its gRPC warm-up. The container's readiness probe is an HTTP
    /// request against /__admin/mappings, which says nothing about the gRPC extension having loaded
    /// its descriptor and mappings — a call made in that window comes back UNIMPLEMENTED (or a
    /// transport error), and comparing THAT to Mockifyr's answer fails a test that has found no
    /// real divergence. Retries only while the status is one of those startup shapes, so a genuine
    /// mismatch still fails immediately rather than being waited away.
    /// </summary>
    private async Task<(StatusCode Code, string Detail)> CallOracleWhenReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var result = await CallAsync(_oracle.GrpcAddress);
            var warmingUp = result.Code is StatusCode.Unimplemented or StatusCode.Unavailable or StatusCode.Internal;
            if (!warmingUp || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(250);
        }
    }

    private static async Task<(StatusCode Code, string Detail)> CallAsync(Uri address)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Greeter.GreeterClient(channel);
        try
        {
            await client.SayHelloAsync(new HelloRequest { Name = "Tom" });
            return (StatusCode.OK, string.Empty);
        }
        catch (RpcException ex)
        {
            return (ex.StatusCode, ex.Status.Detail);
        }
    }
}
