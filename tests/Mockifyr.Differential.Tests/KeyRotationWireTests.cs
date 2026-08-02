using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for key sources and rotation (#250) on a real host: keys arrive from files instead
/// of the command line, a rotation is picked up without a restart, and the acceptance criterion that
/// matters most — no key material reaches the process arguments, the logs, the journal or health.
/// </summary>
public sealed class KeyRotationWireTests : IDisposable
{
    private readonly List<string> _files = [];

    private static string KeyText(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    private static byte[] KeyBytes(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private string WriteFile(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mockifyr-wire-keys-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, contents);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync(
        params string[] args)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0" }.Concat(args)]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>JWE compact (dir + A256GCM), written here independently of the implementation.</summary>
    private static string Encrypt(byte[] key, string plaintext, string? kid = null)
    {
        var headerJson = kid is null
            ? """{"alg":"dir","enc":"A256GCM"}"""
            : $$"""{"alg":"dir","enc":"A256GCM","kid":"{{kid}}"}""";
        var header = Base64Url(Encoding.UTF8.GetBytes(headerJson));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var body = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[body.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, body, ciphertext, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}..{Base64Url(nonce)}.{Base64Url(ciphertext)}.{Base64Url(tag)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Digest(byte[] body) => "SHA-256=" + Convert.ToBase64String(SHA256.HashData(body));

    private static string Sign(byte[] key, string digest) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(digest)));

    [Fact]
    public async Task A_key_file_arms_decryption_exactly_as_an_inline_key_does()
    {
        var keyFile = WriteFile($"partner-2026: {KeyText(1)}");
        var (host, client) = await StartAsync("--decrypt-key-file", keyFile);
        await using (host)
        {
            using var stub = await client.PostAsync("/__admin/mappings", Json(
                """
                {"request":{"method":"POST","urlPath":"/pay",
                            "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
                            "bodyPatterns":[{"matchesJsonPath":{"expression":"$.encData.currency","equalTo":"SAR"}}]},
                 "response":{"status":200,"body":"decrypted"}}
                """));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            var token = Encrypt(KeyBytes(1), """{"currency":"SAR","amount":100}""");
            using var served = await client.PostAsync("/pay", Json($$"""{"encData":"{{token}}"}"""));

            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("decrypted", await served.Content.ReadAsStringAsync());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_rotation_is_picked_up_without_restarting_the_host()
    {
        var keyFile = WriteFile($"old: {KeyText(1)}");
        // A short poll interval so the test observes the behaviour rather than the default cadence.
        var (host, client) = await StartAsync("--decrypt-key-file", keyFile, "--key-reload-seconds", "1");
        await using (host)
        {
            using var stub = await client.PostAsync("/__admin/mappings", Json(
                """
                {"request":{"method":"POST","urlPath":"/pay",
                            "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
                            "bodyPatterns":[{"matchesJsonPath":{"expression":"$.encData.currency","equalTo":"SAR"}}]},
                 "response":{"status":200,"body":"decrypted"}}
                """));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            using var beforeRotation = await client.PostAsync("/pay",
                Json($$"""{"encData":"{{Encrypt(KeyBytes(1), """{"currency":"SAR"}""")}}"}"""));
            Assert.Equal(HttpStatusCode.OK, beforeRotation.StatusCode);

            // The rollover: the new key goes in first, the old one stays until traffic has drained.
            File.WriteAllText(keyFile, $"new: {KeyText(2)}\nold: {KeyText(1)}");
            File.SetLastWriteTimeUtc(keyFile, DateTime.UtcNow.AddSeconds(2));
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            using var withNewKey = await client.PostAsync("/pay",
                Json($$"""{"encData":"{{Encrypt(KeyBytes(2), """{"currency":"SAR"}""")}}"}"""));
            using var withOldKey = await client.PostAsync("/pay",
                Json($$"""{"encData":"{{Encrypt(KeyBytes(1), """{"currency":"SAR"}""")}}"}"""));

            // Both halves matter. Without the first, rotating means a restart; without the second,
            // rotating means dropping every partner that has not switched yet.
            Assert.Equal(HttpStatusCode.OK, withNewKey.StatusCode);
            Assert.Equal(HttpStatusCode.OK, withOldKey.StatusCode);

            using var health = await client.GetAsync("/__admin/health");
            var crypto = JsonDocument.Parse(await health.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cryptography");
            // The operator's confirmation that the rollover landed, without restarting anything.
            Assert.Equal(2, crypto.GetProperty("decryptKeys").GetInt32());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_retired_key_stops_working_once_it_leaves_the_file()
    {
        var keyFile = WriteFile($"old: {KeyText(1)}");
        var (host, client) = await StartAsync("--decrypt-key-file", keyFile, "--key-reload-seconds", "1");
        await using (host)
        {
            using var stub = await client.PostAsync("/__admin/mappings", Json(
                """
                {"request":{"method":"POST","urlPath":"/pay",
                            "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
                            "bodyPatterns":[{"matchesJsonPath":{"expression":"$.encData.currency","equalTo":"SAR"}}]},
                 "response":{"status":200,"body":"decrypted"}}
                """));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            File.WriteAllText(keyFile, $"new: {KeyText(2)}");
            File.SetLastWriteTimeUtc(keyFile, DateTime.UtcNow.AddSeconds(2));
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            using var withRetiredKey = await client.PostAsync("/pay",
                Json($$"""{"encData":"{{Encrypt(KeyBytes(1), """{"currency":"SAR"}""")}}"}"""));

            // Removing the line is what actually retires a key — otherwise a ring would only ever
            // add trust and never withdraw it. An undecryptable field is a non-match, not a 500.
            Assert.Equal(HttpStatusCode.NotFound, withRetiredKey.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_signing_key_file_verifies_requests_signed_with_any_active_key()
    {
        var keyFile = WriteFile($"new: {KeyText(3)}\nold: {KeyText(4)}");
        var (host, client) = await StartAsync("--sign-key-file", keyFile);
        await using (host)
        {
            using var stub = await client.PostAsync("/__admin/mappings", Json(
                """
                {"request":{"method":"POST","urlPath":"/signed",
                            "signature":{"scheme":"hmac-sha256","header":"Signature","digestHeader":"Digest"}},
                 "response":{"status":200,"body":"verified"}}
                """));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            foreach (var key in new[] { KeyBytes(3), KeyBytes(4) })
            {
                var body = """{"amount":10}""";
                var digest = Digest(Encoding.UTF8.GetBytes(body));
                using var request = new HttpRequestMessage(HttpMethod.Post, "/signed") { Content = Json(body) };
                request.Headers.Add("Digest", digest);
                request.Headers.Add("Signature", Sign(key, digest));

                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // A key that is in no ring is still refused — accepting any signature would make the
            // whole gate decorative.
            var strangerBody = """{"amount":10}""";
            var strangerDigest = Digest(Encoding.UTF8.GetBytes(strangerBody));
            using var stranger = new HttpRequestMessage(HttpMethod.Post, "/signed") { Content = Json(strangerBody) };
            stranger.Headers.Add("Digest", strangerDigest);
            stranger.Headers.Add("Signature", Sign(KeyBytes(9), strangerDigest));

            using var refused = await client.SendAsync(stranger);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Key_material_never_appears_in_health_or_the_journal()
    {
        var secret = KeyText(5);
        var keyFile = WriteFile($"secret-key: {secret}");
        var (host, client) = await StartAsync("--decrypt-key-file", keyFile);
        await using (host)
        {
            using var stub = await client.PostAsync("/__admin/mappings", Json(
                """
                {"request":{"method":"POST","urlPath":"/pay",
                            "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]}},
                 "response":{"status":200,"body":"ok"}}
                """));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            await client.PostAsync("/pay", Json($$"""{"encData":"{{Encrypt(KeyBytes(5), """{"pan":"4111"}""")}}"}"""));

            var health = await client.GetStringAsync("/__admin/health");
            var journal = await client.GetStringAsync("/__admin/requests");

            // The acceptance criterion for #250, asserted rather than assumed. Health reports how
            // many keys are active; it must never report what they are.
            Assert.DoesNotContain(secret, health);
            Assert.DoesNotContain(secret, journal);
            Assert.Contains("\"decryptKeys\":1", health.Replace(" ", ""));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task An_admin_password_can_come_from_a_file_instead_of_the_command_line()
    {
        var passwordFile = WriteFile("s3cr3t-from-a-file\n");
        var (host, client) = await StartAsync("--admin-user", "op", "--admin-pass-file", passwordFile);
        await using (host)
        {
            using var anonymous = await client.GetAsync("/__admin/mappings");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings");
            authenticated.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("op:s3cr3t-from-a-file")));

            // Trailing newline trimmed: `echo secret > file` is how these files get written, and a
            // password that only works without the newline would be a miserable afternoon.
            using var response = await client.SendAsync(authenticated);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_missing_or_unusable_key_file_leaves_the_capability_off_rather_than_half_on()
    {
        var (host, client) = await StartAsync("--decrypt-key-file", Path.Combine(Path.GetTempPath(), "does-not-exist.keys"));
        await using (host)
        {
            using var health = await client.GetAsync("/__admin/health");
            var crypto = JsonDocument.Parse(await health.Content.ReadAsStringAsync())
                .RootElement.GetProperty("cryptography");

            // Off, and visibly so. Half-armed cryptography means stubs that mysteriously never match.
            Assert.False(crypto.GetProperty("payloadDecryption").GetBoolean());
            Assert.Equal(0, crypto.GetProperty("decryptKeys").GetInt32());

            // Serving is unaffected: a misconfigured optional capability must not take the host down.
            using var stub = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"served"}}"""));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
            Assert.Equal("served", await client.GetStringAsync("/plain"));

            await host.StopAsync();
            client.Dispose();
        }
    }
}
