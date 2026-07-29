using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire tests for #225: <c>--block-outbound-routes</c> refuses the admin routes that make this host
/// act on the network while the admin API is unauthenticated, without touching serving or the
/// read-only admin surface — and goes inert as soon as credentials exist.
/// </summary>
public sealed class OutboundRouteBlockingTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync(params string[] args)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0" }.Concat(args)]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    [Fact]
    public async Task Blocked_routes_are_refused_while_the_rest_of_the_host_keeps_working()
    {
        var (host, client) = await StartAsync("--block-outbound-routes", "true");
        await using (host)
        {
            using var recording = await client.PostAsync("/__admin/recordings/start",
                new StringContent("""{"targetBaseUrl":"http://169.254.169.254"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, recording.StatusCode);
            Assert.Contains("Admin.OutboundRoutesBlocked", await recording.Content.ReadAsStringAsync());

            using var trust = await client.PostAsync("/__admin/outbound-trust/hosts",
                new StringContent("""{"host":"internal.example"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, trust.StatusCode);

            using var git = await client.PostAsync("/__admin/git/configure",
                new StringContent("""{"remote":"https://example.invalid/x.git"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, git.StatusCode);

            // Reads on the same prefixes stay open — the block is about acting, not looking.
            using var trustState = await client.GetAsync("/__admin/outbound-trust");
            Assert.Equal(HttpStatusCode.OK, trustState.StatusCode);

            // And nothing else moved: stubs still import and serve.
            using var stub = await client.PostAsync("/__admin/mappings", new StringContent(
                """{"request":{"method":"GET","urlPath":"/ok"},"response":{"status":200,"body":"served"}}""",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
            Assert.Equal("served", await client.GetStringAsync("/ok"));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_flag_nothing_changes_and_credentials_make_it_inert()
    {
        // Default host: the route is reachable exactly as before (it fails on the unreachable target,
        // not on a policy refusal) — proving the flag is the only thing that changes behavior.
        var (open_, openClient) = await StartAsync();
        await using (open_)
        {
            using var recording = await openClient.PostAsync("/__admin/recordings/start",
                new StringContent("""{"targetBaseUrl":"http://127.0.0.1:9"}""", Encoding.UTF8, "application/json"));
            Assert.NotEqual(HttpStatusCode.Forbidden, recording.StatusCode);
            await open_.StopAsync();
            openClient.Dispose();
        }

        // With credentials the flag is inert: the ordinary 401 answers, not the 403.
        var (secured, securedClient) = await StartAsync("--block-outbound-routes", "true", "--admin-user", "op", "--admin-pass", "s3cret");
        await using (secured)
        {
            using var unauthorized = await securedClient.PostAsync("/__admin/recordings/start",
                new StringContent("""{"targetBaseUrl":"http://127.0.0.1:9"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/recordings/start")
            {
                Content = new StringContent("""{"targetBaseUrl":"http://127.0.0.1:9"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation(
                "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("op:s3cret")));
            using var authorized = await securedClient.SendAsync(request);
            Assert.NotEqual(HttpStatusCode.Forbidden, authorized.StatusCode);

            await secured.StopAsync();
            securedClient.Dispose();
        }
    }
}
