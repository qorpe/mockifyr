using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// The partner principal (#346): a credential that may do everything with its own tenant's data and
/// nothing that makes the host act on the network. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
/// <remarks>
/// The operator principal is asserted alongside the partner in nearly every case on purpose. A test
/// that only showed the partner being refused would pass just as well if the route were broken for
/// everybody, which is a different bug wearing the same 403.
/// </remarks>
public sealed class PartnerPrincipalTests : IAsyncLifetime
{
    private const string Tenant = "acme";

    private WebApplication? _host;
    private HttpClient? _client;

    private static AuthenticationHeaderValue Basic(string user, string pass) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0",
            "--admin-user", "root", "--admin-pass", "rootpass",
            "--tenant-credential", $"{Tenant}:operator:operatorpass",
            "--partner-credential", $"{Tenant}:partner:partnerpass",
            // A bare "--audit" is not a value the configuration binder reads as true.
            "--audit", "true",
        ]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<HttpStatusCode> As(
        AuthenticationHeaderValue credential, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path) { Headers = { Authorization = credential } };
        request.Headers.Add("X-Mockifyr-Tenant", Tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client!.SendAsync(request);
        return response.StatusCode;
    }

    private static AuthenticationHeaderValue Partner => Basic("partner", "partnerpass");

    private static AuthenticationHeaderValue Operator => Basic("operator", "operatorpass");

    [Theory]
    [InlineData("/__admin/recordings/status")]
    [InlineData("/__admin/outbound-trust")]
    [InlineData("/__admin/git/status")]
    public async Task A_partner_cannot_even_read_a_route_that_acts_on_the_network(string path)
    {
        // Reading which upstream a recording points at is not a partner's business either, and "these
        // routes are not yours" is a rule an operator can keep in their head. That the operator gets
        // through on the same path is what proves the route itself still works.
        Assert.Equal(HttpStatusCode.Forbidden, await As(Partner, HttpMethod.Get, path));
        Assert.NotEqual(HttpStatusCode.Forbidden, await As(Operator, HttpMethod.Get, path));
    }

    [Fact]
    public async Task A_partner_cannot_start_a_recording()
    {
        var body = """{"targetBaseUrl":"http://example.invalid"}""";

        Assert.Equal(HttpStatusCode.Forbidden, await As(Partner, HttpMethod.Post, "/__admin/recordings/start", body));
    }

    [Theory]
    [InlineData("""{"request":{"method":"GET","url":"/p"},"response":{"proxyBaseUrl":"http://internal.invalid"}}""")]
    [InlineData("""{"request":{"method":"GET","url":"/w"},"response":{"status":200},"postServeActions":[{"name":"webhook","parameters":{"url":"http://internal.invalid"}}]}""")]
    [InlineData("""{"request":{"method":"GET","url":"/l"},"response":{"status":200},"serveEventListeners":[{"name":"webhook","parameters":{"url":"http://internal.invalid"}}]}""")]
    public async Task A_partner_cannot_add_a_stub_that_makes_the_host_call_out(string mapping)
    {
        // The half the route list misses. Refusing the three routes while the dialect still expresses
        // the same capability would give an operator a control that looks like it holds and does not.
        Assert.Equal(HttpStatusCode.Forbidden, await As(Partner, HttpMethod.Post, "/__admin/mappings", mapping));
        Assert.Equal(HttpStatusCode.Created, await As(Operator, HttpMethod.Post, "/__admin/mappings", mapping));
    }

    [Fact]
    public async Task A_bundle_is_checked_the_same_way_as_a_single_stub()
    {
        // Both shapes arrive on the same routes, so a check that knew only one would be a check with a
        // documented way around it.
        var bundle = """
        {"mappings":[
          {"request":{"method":"GET","url":"/ok"},"response":{"status":200}},
          {"request":{"method":"GET","url":"/out"},"response":{"proxyBaseUrl":"http://internal.invalid"}}
        ]}
        """;

        Assert.Equal(HttpStatusCode.Forbidden, await As(Partner, HttpMethod.Post, "/__admin/mappings/import", bundle));
    }

    [Fact]
    public async Task A_partner_still_does_everything_with_its_own_tenants_data()
    {
        // The refusal has to be narrow, or the class is unusable and nobody will turn it on.
        var stub = """{"request":{"method":"GET","url":"/hello"},"response":{"status":200,"body":"hi"}}""";
        Assert.Equal(HttpStatusCode.Created, await As(Partner, HttpMethod.Post, "/__admin/mappings", stub));

        Assert.Equal(HttpStatusCode.OK, await As(Partner, HttpMethod.Get, "/__admin/mappings"));
        Assert.Equal(HttpStatusCode.OK, await As(Partner, HttpMethod.Put, "/__admin/resources/things/t1", """{"a":1}"""));
        Assert.Equal(HttpStatusCode.OK, await As(Partner, HttpMethod.Get, "/__admin/resources/things"));
        Assert.Equal(HttpStatusCode.OK, await As(Partner, HttpMethod.Get, "/__admin/requests"));
        Assert.Equal(HttpStatusCode.OK, await As(Partner, HttpMethod.Get, "/__admin/messages"));

        // And the stub it added actually serves — the point of having the credential at all. The tenant
        // header goes on the serving request too: the stub lives in the partner's tenant, and without it
        // this asks the default tenant, which has never heard of it.
        using var serve = new HttpRequestMessage(HttpMethod.Get, "/hello");
        serve.Headers.Add("X-Mockifyr-Tenant", Tenant);
        using var served = await _client!.SendAsync(serve);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task A_partner_is_still_scoped_to_its_tenant()
    {
        // The #224 rule is unchanged by the new class, not replaced by it.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings")
        {
            Headers = { Authorization = Partner },
        };
        request.Headers.Add("X-Mockifyr-Tenant", "someone-else");

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Admin.TenantForbidden", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refusal_names_the_field_so_it_can_be_acted_on()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
        {
            Headers = { Authorization = Partner },
            Content = new StringContent(
                """{"request":{"method":"GET","url":"/p"},"response":{"proxyBaseUrl":"http://internal.invalid"}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Mockifyr-Tenant", Tenant);

        using var response = await _client!.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("proxyBaseUrl", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refusal_reaches_the_audit_trail_naming_the_partner_and_the_route()
    {
        // The acceptance criterion that was asserted in prose and nowhere in code. A refusal nobody can
        // read afterwards is a refusal that never happened, as far as a reviewer is concerned.
        var refused = await As(Partner, HttpMethod.Post, "/__admin/recordings/start",
            """{"targetBaseUrl":"http://example.invalid"}""");
        Assert.Equal(HttpStatusCode.Forbidden, refused);

        // The trail is tenant-scoped like every other admin query, so the header goes on this read too —
        // without it the system credential asks the default tenant, which saw none of this.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/audit")
        {
            Headers = { Authorization = Basic("root", "rootpass") },
        };
        request.Headers.Add("X-Mockifyr-Tenant", Tenant);
        using var response = await _client!.SendAsync(request);
        var trail = await response.Content.ReadAsStringAsync();

        // `partner:acme`, not `tenant:acme`: this tenant has both credentials, and a trail that cannot
        // say which of the two acted answers the wrong question.
        Assert.Contains("partner:acme", trail, StringComparison.Ordinal);
        Assert.Contains("/__admin/recordings/start", trail, StringComparison.Ordinal);
        Assert.Contains("403", trail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operator_is_not_recorded_as_a_partner()
    {
        // The other half: if both labels collapsed to the same string this would pass by accident.
        await As(Operator, HttpMethod.Put, "/__admin/resources/audited/x", """{"a":1}""");

        // The trail is tenant-scoped like every other admin query, so the header goes on this read too —
        // without it the system credential asks the default tenant, which saw none of this.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/audit")
        {
            Headers = { Authorization = Basic("root", "rootpass") },
        };
        request.Headers.Add("X-Mockifyr-Tenant", Tenant);
        using var response = await _client!.SendAsync(request);
        var trail = await response.Content.ReadAsStringAsync();

        Assert.Contains("tenant:acme", trail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_system_credential_is_untouched()
    {
        Assert.NotEqual(HttpStatusCode.Forbidden, await As(Basic("root", "rootpass"), HttpMethod.Get, "/__admin/recordings/status"));
    }
}
