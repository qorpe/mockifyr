using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// The simulated client-credentials grant (#450, ADR 0016) against a REAL host built by
/// <c>MockifyrHost.Build</c>: a sandbox key obtains a token, the token opens that tenant's mocks and no
/// other's, it expires on the tenant's clock, and every refusal is RFC 6749's or RFC 6750's.
/// </summary>
/// <remarks>
/// No oracle: the reference engine has no token endpoint. Validation is a self-test against the RFCs'
/// wire shapes, in the G18 honesty rule's terms (<c>docs/parity/g19-sandbox.md</c>).
/// </remarks>
public sealed class SandboxOAuthWireTests
{
    private const string Frozen = "2027-01-01T00:00:00Z";

    private static readonly string AdminBasic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("op:secret"));

    private static async Task<WebApplication> StartAsync(params string[] extra)
    {
        string[] args = ["--port", "0", "--sandbox-auth", "true", "--admin-user", "op", "--admin-pass", "secret", .. extra];
        var host = MockifyrHost.Build(args);
        await host.StartAsync();
        return host;
    }

    private static Task<WebApplication> StartWithOAuthAsync(params string[] extra) =>
        StartAsync(["--sandbox-oauth", "true", .. extra]);

    private static HttpClient Client(WebApplication host)
    {
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static async Task<HttpResponseMessage> AdminAsync(HttpClient client, HttpMethod method, string path, string tenant, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", AdminBasic);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task<(string Id, string Secret)> IssueKeyAsync(HttpClient client, string tenant, string body = """{"name":"ci"}""")
    {
        using var response = await AdminAsync(client, HttpMethod.Post, "/__admin/apikeys", tenant, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("key").GetString()!);
    }

    private static async Task SeedStubAsync(HttpClient client, string tenant, string method, string url, string body)
    {
        var stub = $$$"""{"request":{"method":"{{{method}}}","urlPath":"{{{url}}}"},"response":{"status":200,"body":"{{{body}}}"}}""";
        using var response = await AdminAsync(client, HttpMethod.Post, "/__admin/mappings", tenant, stub);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string BasicOf(string id, string secret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(id) + ":" + Uri.EscapeDataString(secret)));

    private static async Task<HttpResponseMessage> TokenAsync(
        HttpClient client, string? basic, IEnumerable<KeyValuePair<string, string>> form, string path = "/oauth/token")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        if (basic is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", basic);
        }

        return await client.SendAsync(request);
    }

    private static KeyValuePair<string, string> P(string name, string value) => new(name, value);

    private static async Task<string> ObtainAsync(HttpClient client, string id, string secret)
    {
        using var response = await TokenAsync(client, BasicOf(id, secret), [P("grant_type", "client_credentials")]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> CallAsync(
        HttpClient client, string bearer, string path = "/whoami", HttpMethod? method = null, string? tenantHeader = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenantHeader);
        }

        var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task A_key_obtains_a_token_and_the_token_opens_its_tenants_mocks_and_no_others()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        await SeedStubAsync(client, "globex", "GET", "/whoami", "globex-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme");

        using var response = await TokenAsync(client, BasicOf(id, secret), [P("grant_type", "client_credentials"), P("scope", "payments:read")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await ErrorOf(response);
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(3600, body.GetProperty("expires_in").GetInt64());
        Assert.Equal("payments:read", body.GetProperty("scope").GetString());
        var token = body.GetProperty("access_token").GetString()!;

        Assert.Equal((HttpStatusCode.OK, "acme-answer"), await CallAsync(client, token) is var a ? (a.Status, a.Body) : default);

        // The token names the tenant; a contradicting header does not move it.
        Assert.Equal((HttpStatusCode.OK, "acme-answer"),
            await CallAsync(client, token, tenantHeader: "globex") is var g ? (g.Status, g.Body) : default);
    }

    [Fact]
    public async Task The_credentials_may_travel_in_the_body_instead()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme");

        using var response = await TokenAsync(client, null,
            [P("grant_type", "client_credentials"), P("client_id", id), P("client_secret", secret)]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await ErrorOf(response)).GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, token)).Status);
    }

    [Fact]
    public async Task A_token_expires_on_the_tenants_clock()
    {
        await using var host = await StartWithOAuthAsync("--oauth-token-lifetime", "600");
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme");
        using (var frozen = await AdminAsync(client, HttpMethod.Put, "/__admin/clock", "acme", $$"""{"frozenAt":"{{Frozen}}"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        }

        var token = await ObtainAsync(client, id, secret);
        var payload = JsonDocument.Parse(FromB64(token.Split('.')[1])).RootElement;
        Assert.Equal(DateTimeOffset.Parse(Frozen).ToUnixTimeSeconds(), payload.GetProperty("iat").GetInt64());

        using (var almost = await AdminAsync(client, HttpMethod.Put, "/__admin/clock", "acme", """{"frozenAt":"2027-01-01T00:09:59Z"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, token)).Status);
        }

        using (var past = await AdminAsync(client, HttpMethod.Put, "/__admin/clock", "acme", """{"frozenAt":"2027-01-01T00:10:00Z"}"""))
        {
            var (status, body, response) = await CallAsync(client, token);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Contains("error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("invalid_token", doc.RootElement.GetProperty("error").GetString());
            Assert.Equal("The access token has expired.", doc.RootElement.GetProperty("error_description").GetString());
        }
    }

    [Fact]
    public async Task A_token_this_host_did_not_sign_is_refused_and_told_nothing()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme");

        // Another host's token: a real token in every respect but the key.
        await using var other = await StartWithOAuthAsync();
        using var otherClient = Client(other);
        var (otherId, otherSecret) = await IssueKeyAsync(otherClient, "acme");
        var foreign = await ObtainAsync(otherClient, otherId, otherSecret);

        // And this host's own token with one claim changed.
        var parts = (await ObtainAsync(client, id, secret)).Split('.');
        var tampered = parts[0] + "." + B64(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(FromB64(parts[1])).Replace("\"acme\"", "\"globex\"", StringComparison.Ordinal))) + "." + parts[2];

        foreach (var token in new[] { foreign, tampered, "a.b.c", "x.y" })
        {
            var (status, body, response) = await CallAsync(client, token);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Contains("error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("The access token is not valid.", doc.RootElement.GetProperty("error_description").GetString());
        }
    }

    [Fact]
    public async Task A_revoked_key_ends_its_tokens_and_cannot_obtain_new_ones()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme");
        var token = await ObtainAsync(client, id, secret);

        using (var revoked = await AdminAsync(client, HttpMethod.Delete, $"/__admin/apikeys/{id}", "acme"))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }

        var (status, body, _) = await CallAsync(client, token);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("ApiKey.Revoked", body, StringComparison.Ordinal);

        using var again = await TokenAsync(client, BasicOf(id, secret), [P("grant_type", "client_credentials")]);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        var error = await ErrorOf(again);
        Assert.Equal("invalid_client", error.GetProperty("error").GetString());
        Assert.Equal("This sandbox key has been revoked.", error.GetProperty("error_description").GetString());
    }

    [Fact]
    public async Task A_read_only_keys_token_may_only_read()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        await SeedStubAsync(client, "acme", "POST", "/whoami", "written");
        var (id, secret) = await IssueKeyAsync(client, "acme", """{"name":"monitoring","scope":"read"}""");
        var token = await ObtainAsync(client, id, secret);

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, token)).Status);
        var (status, body, _) = await CallAsync(client, token, method: HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("ApiKey.ReadOnly", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tokens_calls_count_against_its_keys_quota()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme", """{"name":"ci","quotaPerHour":2}""");
        var token = await ObtainAsync(client, id, secret);

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(client, token)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CallAsync(client, token)).Status);
    }

    // The class: every way a client can fail to authenticate. Each is RFC 6749 §5.2's invalid_client,
    // and an unknown id reads exactly as a wrong secret does.
    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("unknown-id")]
    [InlineData("no-credentials")]
    [InlineData("bearer-scheme")]
    [InlineData("not-base64")]
    [InlineData("no-colon")]
    [InlineData("empty-secret")]
    public async Task A_client_that_cannot_authenticate_is_invalid_client(string shape)
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        var basic = shape switch
        {
            "wrong-secret" => BasicOf(id, secret + "x"),
            "unknown-id" => BasicOf("no-such-key", secret),
            "no-credentials" => null,
            "bearer-scheme" => "Bearer " + secret,
            "not-base64" => "Basic !!!",
            "no-colon" => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(id + secret)),
            "empty-secret" => BasicOf(id, string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        using var response = await TokenAsync(client, basic, [P("grant_type", "client_credentials")]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("Basic", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal("invalid_client", (await ErrorOf(response)).GetProperty("error").GetString());
        Assert.DoesNotContain(secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_id_and_a_wrong_secret_are_told_the_same_thing()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        using var wrong = await TokenAsync(client, BasicOf(id, secret + "x"), [P("grant_type", "client_credentials")]);
        using var unknown = await TokenAsync(client, BasicOf("no-such-key", secret), [P("grant_type", "client_credentials")]);

        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    // The class: a request the endpoint cannot read as one client-credentials grant.
    [Theory]
    [InlineData("missing-grant", "invalid_request")]
    [InlineData("password-grant", "unsupported_grant_type")]
    [InlineData("case-grant", "unsupported_grant_type")]
    [InlineData("duplicate-grant", "invalid_request")]
    [InlineData("duplicate-scope", "invalid_request")]
    [InlineData("header-and-body", "invalid_request")]
    [InlineData("bad-scope", "invalid_scope")]
    [InlineData("empty-scope", "invalid_scope")]
    public async Task A_malformed_grant_request_is_refused_with_its_rfc6749_error(string shape, string expected)
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");
        var basic = BasicOf(id, secret);
        var grant = P("grant_type", "client_credentials");

        using var response = shape switch
        {
            "missing-grant" => await TokenAsync(client, basic, [P("scope", "read")]),
            "password-grant" => await TokenAsync(client, basic, [P("grant_type", "password")]),
            "case-grant" => await TokenAsync(client, basic, [P("grant_type", "Client_Credentials")]),
            "duplicate-grant" => await TokenAsync(client, basic, [grant, grant]),
            "duplicate-scope" => await TokenAsync(client, basic, [grant, P("scope", "a"), P("scope", "b")]),
            "header-and-body" => await TokenAsync(client, basic, [grant, P("client_id", id), P("client_secret", secret)]),
            "bad-scope" => await TokenAsync(client, basic, [grant, P("scope", "read\"write")]),
            "empty-scope" => await TokenAsync(client, basic, [grant, P("scope", string.Empty)]),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, (await ErrorOf(response)).GetProperty("error").GetString());
    }

    // The class: every Content-Type that is not exactly the form type. Compared whole after parsing, so
    // a spelling nobody listed here is refused too, and none of them reaches the form reader to throw.
    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/x-www-form-urlencoded-evil")]
    [InlineData("application/x-www-form-urlencodedx; charset=utf-8")]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded;;;===")]
    [InlineData(null)]
    public async Task A_request_that_is_not_form_encoded_is_refused(string? contentType)
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        var content = new ByteArrayContent(Encoding.ASCII.GetBytes("grant_type=client_credentials"));
        if (contentType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", BasicOf(id, secret));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await ErrorOf(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/x-www-form-urlencoded; charset=utf-8")]
    [InlineData("APPLICATION/X-WWW-FORM-URLENCODED")]
    public async Task The_form_type_is_accepted_with_parameters_and_in_any_case(string contentType)
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        var content = new ByteArrayContent(Encoding.ASCII.GetBytes("grant_type=client_credentials"));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", BasicOf(id, secret));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_request_over_the_size_limit_is_refused_whether_or_not_it_declares_its_length(bool declared)
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        var form = "grant_type=client_credentials&pad=" + new string('x', 9000);
        HttpContent content = declared
            ? new StringContent(form, Encoding.ASCII)
            : new StreamContent(new NonSeekableStream(Encoding.ASCII.GetBytes(form)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", BasicOf(id, secret));
        if (!declared)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await ErrorOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_key_set_verifies_an_issued_token()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");
        var token = await ObtainAsync(client, id, secret);

        using var jwks = JsonDocument.Parse(await client.GetStringAsync("/.well-known/jwks.json"));
        var jwk = Assert.Single(jwks.RootElement.GetProperty("keys").EnumerateArray());
        using var verifier = RSA.Create(new RSAParameters
        {
            Modulus = FromB64(jwk.GetProperty("n").GetString()!),
            Exponent = FromB64(jwk.GetProperty("e").GetString()!),
        });
        var parts = token.Split('.');

        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(jwk.GetProperty("kid").GetString(),
            JsonDocument.Parse(FromB64(parts[0])).RootElement.GetProperty("kid").GetString());
    }

    [Fact]
    public async Task Two_hosts_given_one_signing_key_accept_each_others_tokens()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "mockifyr-oauth-" + Guid.NewGuid().ToString("N") + ".pem");
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(keyFile, rsa.ExportPkcs8PrivateKeyPem());
        }

        var rootDir = Path.Combine(Path.GetTempPath(), "mockifyr-oauth-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);
        try
        {
            // One root dir stands in for the shared store two replicas would read their keys from.
            await using var first = await StartWithOAuthAsync("--oauth-signing-key-file", keyFile, "--root-dir", rootDir);
            using var firstClient = Client(first);
            var (id, secret) = await IssueKeyAsync(firstClient, "acme");
            var token = await ObtainAsync(firstClient, id, secret);

            await using var second = await StartWithOAuthAsync("--oauth-signing-key-file", keyFile, "--root-dir", rootDir);
            using var secondClient = Client(second);
            await SeedStubAsync(secondClient, "acme", "GET", "/whoami", "acme-answer");

            Assert.Equal((HttpStatusCode.OK, "acme-answer"),
                await CallAsync(secondClient, token) is var r ? (r.Status, r.Body) : default);
        }
        finally
        {
            File.Delete(keyFile);
            Directory.Delete(rootDir, recursive: true);
        }
    }

    [Fact]
    public async Task A_token_whose_key_this_host_does_not_hold_is_refused_though_the_signature_holds()
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "mockifyr-oauth-" + Guid.NewGuid().ToString("N") + ".pem");
        using (var rsa = RSA.Create(2048))
        {
            await File.WriteAllTextAsync(keyFile, rsa.ExportPkcs8PrivateKeyPem());
        }

        try
        {
            // One signing key, two key stores: the second host can verify the token and must still refuse
            // it, because the key it was issued to is not one of this host's.
            await using var first = await StartWithOAuthAsync("--oauth-signing-key-file", keyFile);
            using var firstClient = Client(first);
            var (id, secret) = await IssueKeyAsync(firstClient, "acme");
            var token = await ObtainAsync(firstClient, id, secret);

            await using var second = await StartWithOAuthAsync("--oauth-signing-key-file", keyFile);
            using var secondClient = Client(second);
            await SeedStubAsync(secondClient, "acme", "GET", "/whoami", "acme-answer");

            var (status, body, _) = await CallAsync(secondClient, token);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Contains("The access token is not valid.", body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public async Task A_token_naming_another_tenant_than_its_key_now_belongs_to_is_refused()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        await SeedStubAsync(client, "acme", "GET", "/whoami", "acme-answer");
        await SeedStubAsync(client, "globex", "GET", "/whoami", "globex-answer");
        var (id, secret) = await IssueKeyAsync(client, "acme");
        var token = await ObtainAsync(client, id, secret);

        // A restore can put a key id under another tenant. The token says acme, the key now says globex,
        // and neither may win: the request is refused rather than served as either.
        var keys = host.Services.GetRequiredService<Mockifyr.Core.IApiKeyStore>();
        keys.Put(keys.Get(id)! with { Tenant = new Mockifyr.Core.TenantId("globex") });

        var (status, body, _) = await CallAsync(client, token);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Contains("The access token is not valid.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_path_is_configurable_and_the_default_then_belongs_to_the_mocks()
    {
        await using var host = await StartWithOAuthAsync("--oauth-token-path", "/connect/token");
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        using var moved = await TokenAsync(client, BasicOf(id, secret), [P("grant_type", "client_credentials")], "/connect/token");
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        // No grant answers at the default path now: the request reaches mock serving, which knows no stub.
        using var old = await TokenAsync(client, BasicOf(id, secret), [P("grant_type", "client_credentials")]);
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task Without_the_flag_nothing_is_mapped_and_a_jwt_is_still_refused_as_an_unknown_key()
    {
        await using var host = await StartAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");

        using var token = await TokenAsync(client, BasicOf(id, secret), [P("grant_type", "client_credentials")]);
        Assert.Equal(HttpStatusCode.NotFound, token.StatusCode);
        using var jwks = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.NotFound, jwks.StatusCode);

        var (status, body, _) = await CallAsync(client, "eyJh.eyJh.c2ln");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task The_admin_surface_ignores_an_access_token()
    {
        await using var host = await StartWithOAuthAsync();
        using var client = Client(host);
        var (id, secret) = await IssueKeyAsync(client, "acme");
        var token = await ObtainAsync(client, id, secret);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The class: a configuration value the host cannot honour. Each stops the host rather than
    // starting it with a grant nobody can reach or a lifetime nobody wrote.
    [Theory]
    [InlineData("--oauth-token-path", "oauth/token")]
    [InlineData("--oauth-token-path", "/__admin/token")]
    [InlineData("--oauth-token-path", "/.well-known/token")]
    [InlineData("--oauth-token-path", "/oauth token")]
    [InlineData("--oauth-token-lifetime", "0")]
    [InlineData("--oauth-token-lifetime", "-1")]
    [InlineData("--oauth-token-lifetime", "86401")]
    [InlineData("--oauth-token-lifetime", "1.5")]
    [InlineData("--oauth-token-lifetime", "1e3")]
    [InlineData("--oauth-token-lifetime", " 60")]
    [InlineData("--oauth-token-lifetime", "abc")]
    [InlineData("--oauth-issuer", "has space")]
    [InlineData("--oauth-signing-key-file", "/no/such/file.pem")]
    public void A_value_the_host_cannot_honour_stops_it(string flag, string value)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => MockifyrHost.Build(
            ["--port", "0", "--sandbox-auth", "true", "--sandbox-oauth", "true", flag, value]));
        Assert.Contains(flag, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grant_without_sandbox_keys_stops_the_host()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => MockifyrHost.Build(["--port", "0", "--sandbox-oauth", "true"]));
        Assert.Contains("--sandbox-auth", refused.Message, StringComparison.Ordinal);
    }

    // The class: a key file the signer cannot sign with. A public key and a short key both import
    // cleanly, so each is a case of its own.
    [Theory]
    [InlineData("public")]
    [InlineData("short")]
    [InlineData("garbage")]
    [InlineData("ec")]
    public void A_key_file_that_cannot_sign_stops_the_host_without_echoing_it(string shape)
    {
        var keyFile = Path.Combine(Path.GetTempPath(), "mockifyr-oauth-" + Guid.NewGuid().ToString("N") + ".pem");
        string content;
        switch (shape)
        {
            case "public":
                using (var rsa = RSA.Create(2048)) content = rsa.ExportSubjectPublicKeyInfoPem();
                break;
            case "short":
                using (var rsa = RSA.Create(1024)) content = rsa.ExportPkcs8PrivateKeyPem();
                break;
            case "ec":
                using (var ec = ECDsa.Create()) content = ec.ExportPkcs8PrivateKeyPem();
                break;
            default:
                content = "not a key at all";
                break;
        }

        File.WriteAllText(keyFile, content);
        try
        {
            var refused = Assert.Throws<InvalidOperationException>(() => MockifyrHost.Build(
                ["--port", "0", "--sandbox-auth", "true", "--sandbox-oauth", "true", "--oauth-signing-key-file", keyFile]));
            Assert.Contains("--oauth-signing-key-file", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE KEY", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }

    /// <summary>A body with no length, so the client must send it chunked.</summary>
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
