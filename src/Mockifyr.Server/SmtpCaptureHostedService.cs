using Microsoft.Extensions.Hosting;
using Mockifyr.Facade.Smtp;

namespace Mockifyr.Server;

/// <summary>
/// Runs the opt-in SMTP capture listener (G18b) with the host's lifetime: started with the app,
/// disposed (draining connections) on shutdown. Registered only when <c>--smtp-port</c> is set —
/// no flag, no listener, no behavior change.
/// </summary>
internal sealed class SmtpCaptureHostedService(SmtpCaptureServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        server.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await server.DisposeAsync();
}
