using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for the cryptography capability report (G20e): the dashboard must be able to say
/// what this host can honor, because a stub declaring encryption or signing on a keyless host simply
/// never matches — a symptom that is otherwise indistinguishable from a bad matcher.
/// </summary>
public sealed class CryptoCapabilityTests
{
    private static async Task<JsonElement> HealthAsync(params string[] args)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0" }.Concat(args)]);
        await host.StartAsync();
        await using (host)
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .First(a => a.StartsWith("http://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var body = await client.GetStringAsync("/__admin/health");
            await host.StopAsync();
            return JsonDocument.Parse(body).RootElement.Clone().GetProperty("cryptography");
        }
    }

    [Fact]
    public async Task A_keyless_host_reports_every_capability_off()
    {
        var crypto = await HealthAsync();

        Assert.False(crypto.GetProperty("payloadDecryption").GetBoolean());
        Assert.False(crypto.GetProperty("responseProtection").GetBoolean());
        Assert.False(crypto.GetProperty("signatureVerification").GetBoolean());
        Assert.False(crypto.GetProperty("responseSigning").GetBoolean());
    }

    [Fact]
    public async Task Each_key_switches_on_exactly_the_pair_it_enables()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        // --decrypt-key covers both payload directions; signing stays off.
        var encryption = await HealthAsync("--decrypt-key", key);
        Assert.True(encryption.GetProperty("payloadDecryption").GetBoolean());
        Assert.True(encryption.GetProperty("responseProtection").GetBoolean());
        Assert.False(encryption.GetProperty("signatureVerification").GetBoolean());

        // --sign-key covers verification and signing; payload cryptography stays off.
        var signing = await HealthAsync("--sign-key", key);
        Assert.True(signing.GetProperty("signatureVerification").GetBoolean());
        Assert.True(signing.GetProperty("responseSigning").GetBoolean());
        Assert.False(signing.GetProperty("payloadDecryption").GetBoolean());

        // Both keys → everything on.
        var both = await HealthAsync("--decrypt-key", key, "--sign-key", key);
        foreach (var name in new[] { "payloadDecryption", "responseProtection", "signatureVerification", "responseSigning" })
        {
            Assert.True(both.GetProperty(name).GetBoolean(), name);
        }
    }
}
