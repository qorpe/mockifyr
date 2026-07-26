using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mockifyr.Facade.Admin;
using Mockifyr.Facade.Http;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// A real Kestrel-hosted Mockifyr on an ephemeral loopback port, so over-the-wire tests that need a
/// genuine socket (fault emission / connection reset, G12b) see real transport behavior — unlike the
/// in-memory <c>WebApplicationFactory</c> test server used for plain response diffs (G12a).
/// </summary>
public sealed class MockifyrKestrelHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public MockifyrKestrelHost()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMockifyr();

        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        _app.MapAdminEndpoints();
        _app.MapMockServing();
        _app.Start();

        BaseAddress = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    /// <summary>The bound base address, e.g. <c>http://127.0.0.1:53211</c>.</summary>
    public string BaseAddress { get; }

    /// <summary>The host's service provider, for seeding state below the HTTP surface in self-tests.</summary>
    public IServiceProvider Services => _app.Services;

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
