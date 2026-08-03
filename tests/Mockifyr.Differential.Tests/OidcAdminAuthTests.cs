using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Mockifyr.Server;
using System.IdentityModel.Tokens.Jwt;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for OIDC on the admin API (#251), against a real issuer rather than a stub: the test
/// runs an in-process provider that publishes a discovery document and a JWKS, and signs RS256 tokens
/// with the matching key. So the discovery fetch, the key lookup and the signature check are all the
/// production path — there is no test-only shortcut that could hide a hole in it.
/// </summary>
public sealed class OidcAdminAuthTests : IAsyncLifetime
{
    private readonly RSA _key = RSA.Create(2048);
    private WebApplication _issuer = null!;
    private string _authority = string.Empty;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        _issuer = builder.Build();
        _issuer.Urls.Add("http://127.0.0.1:0");

        var parameters = _key.ExportParameters(includePrivateParameters: false);
        var jwks = new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = "test-key",
                    n = Base64Url(parameters.Modulus!),
                    e = Base64Url(parameters.Exponent!),
                },
            },
        };

        _issuer.MapGet("/.well-known/openid-configuration", (HttpContext context) => Results.Json(new
        {
            issuer = _authority,
            jwks_uri = $"{_authority}/jwks",
            authorization_endpoint = $"{_authority}/authorize",
            token_endpoint = $"{_authority}/token",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));
        _issuer.MapGet("/jwks", () => Results.Json(jwks));

        await _issuer.StartAsync();
        _authority = _issuer.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1").TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        await _issuer.DisposeAsync();
        _key.Dispose();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string Token(
        string subject = "jane@example.com",
        string? tenant = null,
        string? role = null,
        string? audience = "mockifyr",
        DateTime? expires = null)
    {
        var claims = new List<System.Security.Claims.Claim> { new("preferred_username", subject) };
        if (tenant is not null)
        {
            claims.Add(new System.Security.Claims.Claim("mockifyr_tenant", tenant));
        }

        if (role is not null)
        {
            claims.Add(new System.Security.Claims.Claim("roles", role));
        }

        var credentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: _authority,
            audience: audience,
            claims: claims,
            // notBefore trails the expiry so an already-expired token can be minted at all.
            notBefore: (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-30),
            expires: expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<(WebApplication Host, HttpClient Client)> StartHostAsync(params string[] extra)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0", "--oidc-authority", _authority, "--oidc-audience", "mockifyr" }.Concat(extra)]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static HttpRequestMessage Get(string path, string? token = null, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        return request;
    }

    [Fact]
    public async Task A_valid_token_authenticates_and_an_absent_one_does_not()
    {
        var (host, client) = await StartHostAsync();
        await using (host)
        {
            using var anonymous = await client.GetAsync("/__admin/mappings");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var authenticated = await client.SendAsync(Get("/__admin/mappings", Token()));
            Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_token_this_issuer_did_not_sign_is_refused()
    {
        var (host, client) = await StartHostAsync();
        await using (host)
        {
            using var stranger = RSA.Create(2048);
            var credentials = new SigningCredentials(new RsaSecurityKey(stranger) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);
            var forged = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: _authority, audience: "mockifyr",
                claims: [new System.Security.Claims.Claim("preferred_username", "mallory")],
                expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials));

            // Same issuer, same audience, same kid — only the key differs. If the signature were not
            // checked against the published JWKS, this would sail through.
            using var response = await client.SendAsync(Get("/__admin/mappings", forged));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (host, client) = await StartHostAsync();
        await using (host)
        {
            using var response = await client.SendAsync(
                Get("/__admin/mappings", Token(expires: DateTime.UtcNow.AddMinutes(-10))));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_token_for_another_audience_is_refused()
    {
        var (host, client) = await StartHostAsync();
        await using (host)
        {
            // A token minted for a different application in the same directory must not be a key to
            // this one — the whole point of declaring an audience.
            using var response = await client.SendAsync(Get("/__admin/mappings", Token(audience: "some-other-app")));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_tenant_claim_scopes_the_identity_the_way_a_tenant_credential_does()
    {
        var (host, client) = await StartHostAsync("--oidc-tenant-claim", "mockifyr_tenant");
        await using (host)
        {
            var token = Token(tenant: "acme");

            using var own = await client.SendAsync(Get("/__admin/mappings", token, tenant: "acme"));
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);

            // The same rule --tenant-credential enforces (#224), applied to a claim instead of a
            // password: renaming the header does not change what you may address.
            using var other = await client.SendAsync(Get("/__admin/mappings", token, tenant: "globex"));
            Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
            Assert.Contains("Admin.TenantForbidden", await other.Content.ReadAsStringAsync());

            // Omitting the header addresses the default tenant, which a scoped identity does not own.
            using var absent = await client.SendAsync(Get("/__admin/mappings", token));
            Assert.Equal(HttpStatusCode.Forbidden, absent.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task An_identity_with_no_tenant_claim_keeps_system_scope()
    {
        var (host, client) = await StartHostAsync("--oidc-tenant-claim", "mockifyr_tenant");
        await using (host)
        {
            // No claim means the OIDC equivalent of --admin-user: it reaches every tenant, so an
            // operator's own account still works while individual teams are scoped.
            var token = Token();
            foreach (var tenant in new[] { "acme", "globex" })
            {
                using var response = await client.SendAsync(Get("/__admin/mappings", token, tenant));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_required_role_is_enforced()
    {
        var (host, client) = await StartHostAsync("--oidc-required-role", "mockifyr-admin");
        await using (host)
        {
            using var without = await client.SendAsync(Get("/__admin/mappings", Token()));
            Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);

            using var with = await client.SendAsync(Get("/__admin/mappings", Token(role: "mockifyr-admin")));
            Assert.Equal(HttpStatusCode.OK, with.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_audit_trail_records_the_person_not_the_token()
    {
        var (host, client) = await StartHostAsync("--audit", "true");
        await using (host)
        {
            var token = Token(subject: "jane@example.com");
            using var created = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = new StringContent(
                    """{"request":{"method":"GET","urlPath":"/x"},"response":{"status":200}}""",
                    Encoding.UTF8, "application/json"),
            };
            created.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(created);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            using var audit = await client.SendAsync(Get("/__admin/audit", token));
            var body = await audit.Content.ReadAsStringAsync();
            var entry = JsonDocument.Parse(body).RootElement.GetProperty("entries").EnumerateArray().Single();

            // A name a reviewer can act on — and no part of the token, for the same reason a password
            // never appeared in an entry.
            Assert.Equal("oidc:jane@example.com", entry.GetProperty("principal").GetString());
            Assert.DoesNotContain(token, body);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Probes_and_the_mock_surface_stay_open()
    {
        var (host, client) = await StartHostAsync();
        await using (host)
        {
            // Enabling OIDC must not break the deployment target: a kubelet cannot carry a token.
            foreach (var probe in new[] { "/__admin/health", "/__admin/live", "/__admin/ready" })
            {
                using var response = await client.GetAsync(probe);
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                    $"{probe} answered {response.StatusCode}");
            }

            // And the mock surface is for clients, who have no business holding an admin identity.
            using var stub = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = new StringContent(
                    """{"request":{"method":"GET","urlPath":"/open"},"response":{"status":200,"body":"served"}}""",
                    Encoding.UTF8, "application/json"),
            };
            stub.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
            using var created = await client.SendAsync(stub);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            Assert.Equal("served", await client.GetStringAsync("/open"));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Basic_credentials_keep_working_alongside_OIDC()
    {
        var (host, client) = await StartHostAsync("--admin-user", "op", "--admin-pass", "secret");
        await using (host)
        {
            // Adopting OIDC for people must not lock out the machines: a host can run both, which is
            // what makes the migration incremental rather than a flag day.
            using var basic = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings");
            basic.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("op:secret")));
            using var viaBasic = await client.SendAsync(basic);
            Assert.Equal(HttpStatusCode.OK, viaBasic.StatusCode);

            using var viaToken = await client.SendAsync(Get("/__admin/mappings", Token()));
            Assert.Equal(HttpStatusCode.OK, viaToken.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }
}
