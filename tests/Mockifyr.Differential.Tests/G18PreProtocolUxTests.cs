using System.Net;
using System.Text;
using System.Text.Json;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Grpc.Test;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Self-tests for the protocol-aware stub UX (G18-pre, ADR 0010). There is no WireMock oracle for any
/// of this — the <c>protocol</c> field, the descriptor admin endpoints and the message-mapping listing
/// are Mockifyr surface, not WireMock dialect — so the claims verified here are Mockifyr's own:
/// classification is computed (never stored), descriptor upload hot-enables serving, and the
/// message-mapping list/delete round-trips. Host-only; no Docker.
/// </summary>
public sealed class G18PreProtocolUxTests
{
    private static byte[] Descriptor() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Protos", "greeter.dsc"));

    private const string GrpcStub =
        """{"request":{"method":"POST","urlPath":"/mockifyr.grpc.test.Greeter/SayHello","bodyPatterns":[{"equalToJson":"{\"name\":\"Tom\"}"}]},"response":{"status":200,"jsonBody":{"message":"Hello Tom"}}}""";

    private const string GraphqlStub =
        """{"request":{"method":"POST","urlPath":"/graphql","customMatcher":{"name":"graphql-body-matcher","parameters":{"query":"{ hero { id } }"}}},"response":{"status":200,"body":"ok"}}""";

    private const string HttpStub =
        """{"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"plain"}}""";

    private static async Task<(IAsyncDisposable App, HttpClient Client, Uri HttpsAddress, DirectoryInfo Root)> StartHostAsync(
        bool withDescriptor)
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-g18pre-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "mappings"));
        if (withDescriptor)
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "grpc"));
            File.WriteAllBytes(Path.Combine(root.FullName, "grpc", "greeter.dsc"), Descriptor());
        }

        var app = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--root-dir", root.FullName]);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => a.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1")).ToList();
        var http = addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
        var https = new Uri(addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal)));
        return (app, new HttpClient { BaseAddress = new Uri(http) }, https, root);
    }

    private static async Task<Dictionary<string, string>> ProtocolsByUrlAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/__admin/mappings"));
        return document.RootElement.GetProperty("mappings").EnumerateArray().ToDictionary(
            m => m.GetProperty("request").TryGetProperty("urlPath", out var p) ? p.GetString()! : "?",
            m => m.GetProperty("protocol").GetString()!);
    }

    [Fact]
    public async Task MappingsList_ClassifiesGrpcGraphqlAndHttp()
    {
        var (app, client, _, root) = await StartHostAsync(withDescriptor: true);
        await using var _ = app;
        try
        {
            foreach (var stub in new[] { GrpcStub, GraphqlStub, HttpStub })
            {
                var created = await client.PostAsync("/__admin/mappings", new StringContent(stub, Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            var protocols = await ProtocolsByUrlAsync(client);

            Assert.Equal("grpc", protocols["/mockifyr.grpc.test.Greeter/SayHello"]);
            Assert.Equal("graphql", protocols["/graphql"]);
            Assert.Equal("http", protocols["/plain"]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Protocol_IsComputedAtQueryTime_NeverStored()
    {
        var (app, client, _, root) = await StartHostAsync(withDescriptor: true);
        await using var _ = app;
        try
        {
            var created = await client.PostAsync("/__admin/mappings", new StringContent(GrpcStub, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            // Listing computes the field...
            Assert.Equal("grpc", (await ProtocolsByUrlAsync(client))["/mockifyr.grpc.test.Greeter/SayHello"]);

            // ...but the persisted document is exactly the posted mapping plus the id stamp G16 always
            // writes (id/uuid) — in particular, NO protocol key ever reaches storage.
            var persisted = Directory.EnumerateFiles(Path.Combine(root.FullName, "mappings"), "*.json").Single();
            var node = JsonDocument.Parse(await File.ReadAllTextAsync(persisted)).RootElement;
            Assert.False(node.TryGetProperty("protocol", out JsonElement ignored));
            var keys = node.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(new[] { "id", "request", "response", "uuid" }, keys);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(GrpcStub)!["request"],
                System.Text.Json.Nodes.JsonNode.Parse(node.GetProperty("request").GetRawText())));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WithoutItsDescriptor_AGrpcShapedStub_ClassifiesAsHttp()
    {
        var (app, client, _, root) = await StartHostAsync(withDescriptor: false);
        await using var _ = app;
        try
        {
            await client.PostAsync("/__admin/mappings", new StringContent(GrpcStub, Encoding.UTF8, "application/json"));

            // Without the descriptor the path resolves nowhere — and could not serve gRPC anyway.
            Assert.Equal("http", (await ProtocolsByUrlAsync(client))["/mockifyr.grpc.test.Greeter/SayHello"]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DescriptorUpload_HotEnablesGrpcServing_AndDeleteDisables()
    {
        // Starts with NO grpc directory at all: upload must create it and enable serving in-place.
        var (app, client, httpsAddress, root) = await StartHostAsync(withDescriptor: false);
        await using var _ = app;
        try
        {
            var empty = JsonDocument.Parse(await client.GetStringAsync("/__admin/grpc/descriptors"));
            Assert.Empty(empty.RootElement.GetProperty("descriptors").EnumerateArray());

            var upload = await client.PostAsync("/__admin/grpc/descriptors?name=greeter",
                new ByteArrayContent(Descriptor()));
            Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

            var listed = JsonDocument.Parse(await client.GetStringAsync("/__admin/grpc/descriptors"));
            Assert.Equal("greeter.dsc",
                listed.RootElement.GetProperty("descriptors").EnumerateArray().Single().GetProperty("name").GetString());
            Assert.Contains(listed.RootElement.GetProperty("services").EnumerateArray(),
                s => s.GetProperty("service").GetString() == "mockifyr.grpc.test.Greeter");

            // The uploaded descriptor serves a real gRPC call — no restart.
            await client.PostAsync("/__admin/mappings", new StringContent(GrpcStub, Encoding.UTF8, "application/json"));
            Assert.Equal("Hello Tom", await CallSayHelloAsync(httpsAddress));

            // Delete → the index is empty again and the file is gone.
            var deleted = await client.DeleteAsync("/__admin/grpc/descriptors/greeter.dsc");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.False(File.Exists(Path.Combine(root.FullName, "grpc", "greeter.dsc")));
            var after = JsonDocument.Parse(await client.GetStringAsync("/__admin/grpc/descriptors"));
            Assert.Empty(after.RootElement.GetProperty("services").EnumerateArray());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DescriptorUpload_RejectsGarbage_WithoutWedgingTheIndex()
    {
        var (app, client, _, root) = await StartHostAsync(withDescriptor: true);
        await using var _ = app;
        try
        {
            var bad = await client.PostAsync("/__admin/grpc/descriptors?name=broken",
                new ByteArrayContent(Encoding.UTF8.GetBytes("not a descriptor")));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

            // The pre-existing descriptor still resolves — nothing was overwritten or reloaded away.
            var listed = JsonDocument.Parse(await client.GetStringAsync("/__admin/grpc/descriptors"));
            Assert.Contains(listed.RootElement.GetProperty("services").EnumerateArray(),
                s => s.GetProperty("service").GetString() == "mockifyr.grpc.test.Greeter");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MessageMappings_ListAndDelete_TenantScoped()
    {
        var (app, client, _, root) = await StartHostAsync(withDescriptor: false);
        await using var _ = app;
        try
        {
            const string wsMapping =
                """{"trigger":{"message":{"body":{"equalTo":"ping"}}},"actions":[{"type":"send","message":{"body":{"data":"pong"}}}]}""";
            var created = await client.PostAsync("/__admin/message-mappings",
                new StringContent(wsMapping, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            // Listed for the default tenant, with the registration JSON + a stamped id.
            using var list = JsonDocument.Parse(await client.GetStringAsync("/__admin/message-mappings"));
            var entry = list.RootElement.GetProperty("messageMappings").EnumerateArray().Single();
            var id = entry.GetProperty("id").GetString()!;
            Assert.Equal("ping", entry.GetProperty("trigger").GetProperty("message").GetProperty("body")
                .GetProperty("equalTo").GetString());

            // Another tenant sees nothing (tenant isolation, ADR 0003).
            using var other = new HttpRequestMessage(HttpMethod.Get, "/__admin/message-mappings");
            other.Headers.Add("X-Mockifyr-Tenant", "acme");
            using var otherResponse = await client.SendAsync(other);
            using var otherList = JsonDocument.Parse(await otherResponse.Content.ReadAsStringAsync());
            Assert.Empty(otherList.RootElement.GetProperty("messageMappings").EnumerateArray());

            // Delete round-trips; a second delete is a 404.
            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/__admin/message-mappings/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/__admin/message-mappings/{id}")).StatusCode);
            using var emptied = JsonDocument.Parse(await client.GetStringAsync("/__admin/message-mappings"));
            Assert.Empty(emptied.RootElement.GetProperty("messageMappings").EnumerateArray());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<string> CallSayHelloAsync(Uri address)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions { HttpHandler = handler });
        var client = new Greeter.GreeterClient(channel);
        var reply = await client.SayHelloAsync(new HelloRequest { Name = "Tom" });
        return reply.Message;
    }
}
