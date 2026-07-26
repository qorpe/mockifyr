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
/// Self-test for journal protocol classification (G18 follow-up, ADR 0010): the request journal
/// must read non-HTTP traffic as what it is. Real traffic is driven over every channel — a gRPC
/// call with a real client, a GraphQL post against the matcher stub, a Twilio-profile SMS send,
/// and a plain HTTP request — and <c>/__admin/requests</c> must classify each entry. Host-only.
/// </summary>
public sealed class G18JournalProtocolTests
{
    private static byte[] Descriptor() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Protos", "greeter.dsc"));

    private const string GrpcStub =
        """{"request":{"method":"POST","urlPath":"/mockifyr.grpc.test.Greeter/SayHello","bodyPatterns":[{"equalToJson":"{\"name\":\"Tom\"}"}]},"response":{"status":200,"jsonBody":{"message":"Hello Tom"}}}""";

    private const string GraphqlStub =
        """{"request":{"method":"POST","urlPath":"/graphql","customMatcher":{"name":"graphql-body-matcher","parameters":{"query":"{ hero { id } }"}}},"response":{"status":200,"body":"ok"}}""";

    private const string HttpStub =
        """{"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"plain"}}""";

    [Fact]
    public async Task Journal_ClassifiesEveryChannel()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-journal-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "grpc"));
        File.WriteAllBytes(Path.Combine(root.FullName, "grpc", "greeter.dsc"), Descriptor());

        await using var app = MockifyrHost.Build(
            ["--port", "0", "--https-port", "0", "--sms-profile", "twilio", "--root-dir", root.FullName]);
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Select(a => a.Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1")).ToList();
            using var client = new HttpClient { BaseAddress = new Uri(addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))) };
            var httpsAddress = new Uri(addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal)));

            foreach (var stub in new[] { GrpcStub, GraphqlStub, HttpStub })
            {
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsync("/__admin/mappings", new StringContent(stub, Encoding.UTF8, "application/json"))).StatusCode);
            }

            // Drive one real request per channel.
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using (var channel = GrpcChannel.ForAddress(httpsAddress, new GrpcChannelOptions { HttpHandler = handler }))
            {
                var reply = await new Greeter.GreeterClient(channel).SayHelloAsync(new HelloRequest { Name = "Tom" });
                Assert.Equal("Hello Tom", reply.Message);
            }

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/graphql",
                new StringContent("""{"query":"{ hero { id } }"}""", Encoding.UTF8, "application/json"))).StatusCode);

            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json",
                new StringContent("To=%2B1&From=%2B2&Body=hi", Encoding.UTF8, "application/x-www-form-urlencoded"))).StatusCode);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/plain")).StatusCode);

            // The journal reads each entry as what it is.
            using var journal = JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
            var byUrl = journal.RootElement.GetProperty("requests").EnumerateArray()
                .ToDictionary(r => r.GetProperty("url").GetString()!, r => r.GetProperty("protocol").GetString());

            Assert.Equal("grpc", byUrl["/mockifyr.grpc.test.Greeter/SayHello"]);
            Assert.Equal("graphql", byUrl["/graphql"]);
            Assert.Equal("sms", byUrl["/2010-04-01/Accounts/ACtest/Messages.json"]);
            Assert.Equal("http", byUrl["/plain"]);

            // An unmatched GraphQL-shaped post (no matched stub to inspect) honestly reads http.
            await client.PostAsync("/graphql",
                new StringContent("""{"query":"{ villain { id } }"}""", Encoding.UTF8, "application/json"));
            using var second = JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
            var unmatchedGraphql = second.RootElement.GetProperty("requests").EnumerateArray()
                .First(r => r.GetProperty("url").GetString() == "/graphql" && !r.GetProperty("wasMatched").GetBoolean());
            Assert.Equal("http", unmatchedGraphql.GetProperty("protocol").GetString());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
