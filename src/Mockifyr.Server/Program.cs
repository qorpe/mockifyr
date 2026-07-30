// Mockifyr standalone host entry point. The composition lives in MockifyrHost.Build (G12f): it wires
// the shared engine + stores + Mediant management path (AddMockifyr), maps the admin surface that
// speaks Mockifyr's importable JSON stub dialect plus the mock-serving fallback, binds the port
// (--port, default 8080), and loads any --root-dir/mappings/*.json at startup. Kept thin so the same
// wiring is exercised by tests (verified by the differential suite).
using Mockifyr.Server;

// Container health check (#241): `--healthcheck` runs a one-shot probe against this host's own
// readiness endpoint and exits 0/1, so the image needs no curl/wget in its runtime layer. The port
// mirrors --port so a non-default binding still checks itself.
if (args.Contains("--healthcheck", StringComparer.OrdinalIgnoreCase))
{
    var portIndex = Array.FindIndex(args, a => string.Equals(a, "--port", StringComparison.OrdinalIgnoreCase));
    var port = portIndex >= 0 && portIndex + 1 < args.Length && int.TryParse(args[portIndex + 1], out var parsed)
        ? parsed
        : MockifyrHost.DefaultPort;

    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await probe.GetAsync($"http://127.0.0.1:{port}/__admin/ready");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

MockifyrHost.Build(args).Run();
return 0;

// Exposed so the differential test host (WebApplicationFactory) can boot the same composition.
public partial class Program;
