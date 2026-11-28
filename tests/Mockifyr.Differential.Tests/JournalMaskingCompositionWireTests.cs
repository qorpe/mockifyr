using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// #482: journal masking DECORATES the journal the other switches chose, so turning masking on never
/// turns a disabled journal back on or lifts its bound. Every spelling of every journal switch is held
/// here with masking on, because the defect was one decorator that ignored all of them at once.
/// </summary>
/// <remarks>
/// A self-test on a real host: the reference engine has no masking. No Docker.
/// </remarks>
public sealed class JournalMaskingCompositionWireTests
{
    private const string MaskFlag = "--mask-headers";
    private const string Masked = "X-Card-Number";

    [Theory]
    [InlineData("--journal-disabled")]
    [InlineData("--no-request-journal")]
    public async Task With_masking_on_a_disabled_journal_records_nothing(string disabledFlag)
    {
        var (host, client) = await StartAsync(disabledFlag, "true", MaskFlag, Masked);
        try
        {
            await CreateStub(client);
            using (var served = await Send(client, "4111111111111111"))
            {
                Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            }

            Assert.Equal(0, await JournalCount(client));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("--journal-limit")]
    [InlineData("--max-request-journal-entries")]
    public async Task With_masking_on_the_journal_limit_still_bounds_the_journal_and_still_masks(string limitFlag)
    {
        var (host, client) = await StartAsync(limitFlag, "2", MaskFlag, Masked);
        try
        {
            await CreateStub(client);
            for (var i = 0; i < 3; i++)
            {
                using var served = await Send(client, "4111111111111111");
                Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            }

            Assert.Equal(2, await JournalCount(client));
            using var list = await client.GetAsync("/__admin/requests");
            using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var id = document.RootElement.GetProperty("requests").EnumerateArray().First().GetProperty("id").GetString();
            using var detail = await client.GetAsync($"/__admin/requests/{id}");
            var body = await detail.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.DoesNotContain("4111111111111111", body, StringComparison.Ordinal);
            Assert.Contains("***", body, StringComparison.Ordinal);
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task With_masking_on_a_limit_of_zero_is_unbounded_as_it_is_without_masking()
    {
        // The default bound is 1000; one past it proves the zero reached the journal rather than the default.
        var (host, client) = await StartAsync("--journal-limit", "0", MaskFlag, Masked);
        try
        {
            await CreateStub(client);
            for (var i = 0; i < 1001; i++)
            {
                using var served = await Send(client, "4111111111111111");
                Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            }

            Assert.Equal(1001, await JournalCount(client));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disabled_wins_over_a_limit_with_masking_on_as_it_does_without_it()
    {
        var (host, client) = await StartAsync("--journal-disabled", "true", "--journal-limit", "5", MaskFlag, Masked);
        try
        {
            await CreateStub(client);
            using (await Send(client, "4111111111111111")) { }

            Assert.Equal(0, await JournalCount(client));
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    private static async Task CreateStub(HttpClient client)
    {
        using var stub = new StringContent(
            """{"request":{"method":"GET","urlPath":"/cards"},"response":{"status":200}}""",
            Encoding.UTF8, "application/json");
        using var created = await client.PostAsync("/__admin/mappings", stub);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string card)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/cards");
        request.Headers.Add(Masked, card);
        return client.SendAsync(request);
    }

    private static async Task<int> JournalCount(HttpClient client)
    {
        using var list = await client.GetAsync("/__admin/requests");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("requests").GetArrayLength();
    }

    private static async Task<(WebApplication Host, HttpClient Client)> StartAsync(params string[] flags)
    {
        var host = MockifyrHost.Build(["--port", "0", "--https-port", "0", .. flags]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }
}
