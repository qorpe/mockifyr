using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Mockifyr.Facade.Grpc;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Facade.Admin;
using Mockifyr.Facade.Http;
using Mockifyr.Facade.WebSocket;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Server;

/// <summary>
/// The standalone-host composition (G12f + G11a). Turns command-line/config into a runnable Mockifyr
/// host: binds the mock-serving port (<c>--port</c>, defaulting to <c>8080</c>), optionally an
/// HTTPS port (<c>--https-port</c>) with a self-signed certificate, and, when a <c>--root-dir</c> is
/// given, loads its <c>mappings/*.json</c> into the default tenant at startup via the
/// <see cref="IMappingsLoader"/> seam. Kept separate from <c>Program</c> so the same wiring is
/// exercised by tests (which drive it on ephemeral ports).
/// </summary>
public static class MockifyrHost
{
    /// <summary>The default mock-serving port (<c>8080</c>).</summary>
    public const int DefaultPort = 8080;

    /// <summary>
    /// Builds the standalone host from <paramref name="args"/> (config keys <c>port</c>,
    /// <c>https-port</c>, <c>root-dir</c>, supplied as <c>--port</c>/<c>--https-port</c>/
    /// <c>--root-dir</c>). The returned app is built but not started; startup mappings have already been
    /// applied to the store.
    /// </summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddMockifyr();

        // A root-dir registers a directory loader for <root-dir>/mappings, resolved after the matcher
        // registry exists (customMatcher references in files resolve against it).
        var rootDir = builder.Configuration["root-dir"];

        // Git working copy resolution (#151): the flagged root-dir when given, else a default the
        // operator never has to type (overridable via --git-work-dir as an escape hatch). A flag-less
        // host that finds a Git working copy at the default location ADOPTS it as its root-dir — a
        // dashboard-connected setup therefore survives restarts with no flags at all. The directory
        // only exists if the operator connected before, so untouched setups see no behavior change.
        var gitWorkDir = builder.Configuration["git-work-dir"] is { Length: > 0 } w
            ? w
            : Path.Combine(Environment.CurrentDirectory, "mockifyr-data");
        if (string.IsNullOrWhiteSpace(rootDir) && Directory.Exists(Path.Combine(gitWorkDir, ".git")))
        {
            rootDir = gitWorkDir;
        }

        // Outbound certificate trust (#172), mirroring WireMock's --trust-proxy-target /
        // --trust-all-proxy-targets. Registered before anything resolves an outbound client, and only
        // when something is actually trusted, so the default path keeps the stock handler.
        var outboundTls = OutboundTlsPolicy.From(builder.Configuration, args);
        if (!outboundTls.IsDefault)
        {
            Console.WriteLine($"mockifyr: {outboundTls.Describe()}");
        }

        // The container-localhost fallback (#170, #176) is on by default; this turns it off for a host
        // that must reach exactly the address as written, on both outbound paths — callbacks and
        // proxying. --webhook-host-fallback is kept as an alias for the name it shipped under in
        // v0.8.1. Registered as options rather than by re-registering the listener, which would add a
        // SECOND listener and double every delivery.
        var hostFallback = builder.Configuration.GetValue<bool?>("outbound-host-fallback")
            ?? builder.Configuration.GetValue("webhook-host-fallback", true);
        if (!hostFallback)
        {
            builder.Services.AddSingleton(new Outbound.OutboundOptions(HostFallback: false));
        }

        // The runtime trust store (#174). Registered after rootDir is resolved because that is where
        // dashboard-added hosts persist; without one they are runtime-only, which the status reports
        // rather than hides. A flag-pinned host keeps the flag as the whole configuration.
        var trustStore = new OutboundTrustStore(outboundTls, string.IsNullOrWhiteSpace(rootDir) ? null : rootDir);
        builder.Services.AddSingleton(trustStore);
        builder.Services.AddSingleton<Application.IOutboundTrust>(trustStore);

        var grpcEnabled = false;
        if (!string.IsNullOrWhiteSpace(rootDir))
        {
            var mappingsDir = Path.Combine(rootDir, "mappings");
            builder.Services.AddSingleton<IMappingsLoader>(sp =>
                new DirectoryMappingsLoader(mappingsDir, sp.GetRequiredService<IMatcherRegistry>()));

            // A root-dir also makes stub mutations durable (G16a): they persist to the same mappings
            // directory the loader reads on startup. Registered last so it wins over the no-op default.
            builder.Services.AddSingleton<IStubPersistence>(new FileSystemStubPersistence(mappingsDir));

            // Environments persist alongside the mappings (G17), under <root-dir>/environments/<tenant>/.
            var environmentsDir = Path.Combine(rootDir, "environments");
            builder.Services.AddSingleton<IEnvironmentPersistence>(new FileSystemEnvironmentPersistence(environmentsDir));
            builder.Services.AddSingleton<IEnvironmentsLoader>(new FileSystemEnvironmentsLoader(environmentsDir));

            // gRPC serving (G13, verified by the differential suite): compiled proto descriptors live in
            // the conventional <root-dir>/grpc/*.dsc location. The index is registered even when the
            // directory is empty (G18-pre): the admin descriptor endpoints can then hot-load a first
            // descriptor without a restart, and the middleware only engages for application/grpc
            // requests that resolve against it.
            var grpcDir = Path.Combine(rootDir, "grpc");
            builder.Services.AddMockifyrGrpc(GrpcAdminEndpoints.ReadAll(grpcDir));
            grpcEnabled = true;

            // The protocol probe (G18-pre, ADR 0010): the admin facade classifies a stub as gRPC when
            // its path resolves against the loaded descriptors. Adapter here — facades never
            // reference each other.
            builder.Services.AddSingleton<Facade.Admin.IStubProtocolProbe>(sp =>
                new DescriptorProtocolProbe(sp.GetRequiredService<ProtoDescriptors>()));
        }

        // SMTP capture (G18b, ADR 0009): opt-in via --smtp-port; no flag, no listener. The AUTH
        // username names the tenant (the SMTP analog of X-Mockifyr-Tenant); mail lands in the
        // message inbox behind /__admin/messages.
        if (int.TryParse(builder.Configuration["smtp-port"], out var smtpPort))
        {
            builder.Services.AddSingleton(sp => new Facade.Smtp.SmtpCaptureServer(
                sp.GetRequiredService<IMessageSink>(), smtpPort, sp.GetRequiredService<IMessageBehaviorStore>()));
            builder.Services.AddHostedService<SmtpCaptureHostedService>();
        }

        // Message behaviors (G18e): a bounded inbox override (--message-limit) and the capture
        // webhook decorating the sink. Registered after AddMockifyr so they win the resolution.
        if (int.TryParse(builder.Configuration["message-limit"], out var messageLimit))
        {
            builder.Services.AddSingleton<IMessageStore>(new InMemoryMessageStore(messageLimit));
        }

        builder.Services.AddSingleton<IMessageSink>(sp => new NotifyingMessageSink(
            new StoreMessageSink(sp.GetRequiredService<IMessageStore>()),
            sp.GetRequiredService<IMessageBehaviorStore>(),
            new HttpClient()));

        // Git sync (ADR 0007 + #151). Two modes, registered last so they win over the default:
        //  - Pinned: --git-remote (+ --git-branch) fixes the configuration at startup; the dashboard
        //    shows it read-only. Requires --root-dir (the working copy).
        //  - Dashboard-configurable: without the flag, POST /__admin/git/configure connects a remote
        //    from Settings. A root-dir (given or adopted) host syncs its existing working copy; a pure
        //    in-memory host gets a switchable persistence that connect activates (snapshotting the
        //    current stubs); a DB-persistence host refuses configure with guidance.
        var gitRemote = builder.Configuration["git-remote"];
        var liteDb = builder.Configuration["litedb"];
        var postgresConn = builder.Configuration["postgres"];
        var redisConn = builder.Configuration["redis"];
        var dbPersistence = !string.IsNullOrWhiteSpace(liteDb) || !string.IsNullOrWhiteSpace(postgresConn) || !string.IsNullOrWhiteSpace(redisConn);
        if (!string.IsNullOrWhiteSpace(gitRemote))
        {
            if (string.IsNullOrWhiteSpace(rootDir))
            {
                throw new InvalidOperationException("--git-remote requires --root-dir (the Git working copy).");
            }

            var gitBranch = builder.Configuration["git-branch"] is { Length: > 0 } b ? b : "main";
            GitSyncService.ValidateConfiguration(gitRemote, gitBranch);
            builder.Services.AddSingleton<Application.IGitSync>(sp => new GitSyncService(
                new GitSyncEnvironment(rootDir, gitRemote, gitBranch),
                sp.GetRequiredService<IStubStore>(),
                sp.GetServices<IMappingsLoader>(),
                sp.GetRequiredService<IMatcherRegistry>()));
        }
        else
        {
            var hasFilePersistence = !string.IsNullOrWhiteSpace(rootDir);
            var workDir = hasFilePersistence ? rootDir! : gitWorkDir;
            if (!hasFilePersistence && !dbPersistence)
            {
                // Pure in-memory host: connecting from the dashboard flips this to file persistence.
                builder.Services.AddSingleton<SwitchableStubPersistence>();
                builder.Services.AddSingleton<IStubPersistence>(sp => sp.GetRequiredService<SwitchableStubPersistence>());
            }

            builder.Services.AddSingleton<Application.IGitSync>(sp => new GitSyncService(
                new GitSyncEnvironment(
                    workDir,
                    Activatable: hasFilePersistence || dbPersistence ? null : sp.GetRequiredService<SwitchableStubPersistence>(),
                    PersistenceConflict: dbPersistence && !hasFilePersistence),
                sp.GetRequiredService<IStubStore>(),
                hasFilePersistence
                    ? sp.GetServices<IMappingsLoader>()
                    : [new DirectoryMappingsLoader(Path.Combine(workDir, "mappings"), sp.GetRequiredService<IMatcherRegistry>())],
                sp.GetRequiredService<IMatcherRegistry>()));
        }

        // Global response templating (#148): mirrors the reference host's flag — every response
        // renders through the templating engine regardless of the per-stub transformers list, so
        // exports from hosts that ran with global templating serve their {{…}} bodies correctly.
        // Registered last so it wins over AddMockifyr's opt-in default.
        if (builder.Configuration.GetValue<bool>("global-response-templating"))
        {
            // Resolved from the container, not constructed standalone: this registration REPLACES the
            // one AddMockifyr made, so building it without the environment resolver would silently
            // turn {{key}} substitution (G17) off for anyone running with global templating.
            builder.Services.AddSingleton<Mockifyr.Core.IResponseRenderer>(sp =>
                new Mockifyr.Templating.TemplatingResponseRenderer(
                    extraHelpers: null,
                    globalTemplating: true,
                    environments: sp.GetRequiredService<Mockifyr.Core.IEnvironmentResolver>()));
        }

        // LiteDB persistence (G16b): stubs persist to an embedded single-file database and reload on
        // startup. The LiteDatabase is a DI-created singleton so the container disposes it on shutdown
        // (flushing the file before the next process opens it).
        var liteDbPath = builder.Configuration["litedb"];
        if (!string.IsNullOrWhiteSpace(liteDbPath))
        {
            builder.Services.AddSingleton(_ => new LiteDB.LiteDatabase(liteDbPath));
            builder.Services.AddSingleton<IStubPersistence>(sp =>
                new LiteDbStubPersistence(sp.GetRequiredService<LiteDB.LiteDatabase>()));
            builder.Services.AddSingleton<IMappingsLoader>(sp =>
                new LiteDbMappingsLoader(sp.GetRequiredService<LiteDB.LiteDatabase>(), sp.GetRequiredService<IMatcherRegistry>()));
            builder.Services.AddSingleton<IEnvironmentPersistence>(sp =>
                new LiteDbEnvironmentPersistence(sp.GetRequiredService<LiteDB.LiteDatabase>()));
            builder.Services.AddSingleton<IEnvironmentsLoader>(sp =>
                new LiteDbEnvironmentsLoader(sp.GetRequiredService<LiteDB.LiteDatabase>()));
        }

        // PostgreSQL persistence (G16c): stubs persist to a SQL table and reload on startup.
        var postgres = builder.Configuration["postgres"];
        if (!string.IsNullOrWhiteSpace(postgres))
        {
            builder.Services.AddSingleton<IStubPersistence>(new PostgresStubPersistence(postgres));
            builder.Services.AddSingleton<IMappingsLoader>(sp =>
                new PostgresMappingsLoader(postgres, sp.GetRequiredService<IMatcherRegistry>()));
            builder.Services.AddSingleton<IEnvironmentPersistence>(new PostgresEnvironmentPersistence(postgres));
            builder.Services.AddSingleton<IEnvironmentsLoader>(new PostgresEnvironmentsLoader(postgres));

            // Change-feed reload (G16f): opt-in multi-instance coherence via Postgres LISTEN/NOTIFY —
            // the same seam as Redis (G16e). Each host listens for change announcements and reconciles
            // its in-memory store, so a mutation on one instance is reflected by the others live.
            if (builder.Configuration.GetValue<bool>("change-feed"))
            {
                builder.Services.AddSingleton<IHostedService>(sp =>
                    new PostgresChangeFeedReloader(
                        postgres, sp.GetRequiredService<IStubStore>(), sp.GetServices<IMappingsLoader>()));
            }
        }

        // Redis persistence (G16d): stubs persist to a Redis hash and reload on startup. The
        // multiplexer is a DI-created singleton so the container disposes it on shutdown.
        var redis = builder.Configuration["redis"];
        if (!string.IsNullOrWhiteSpace(redis))
        {
            builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
                _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redis));
            builder.Services.AddSingleton<IStubPersistence>(sp =>
                new RedisStubPersistence(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));
            builder.Services.AddSingleton<IMappingsLoader>(sp =>
                new RedisMappingsLoader(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(), sp.GetRequiredService<IMatcherRegistry>()));
            builder.Services.AddSingleton<IEnvironmentPersistence>(sp =>
                new RedisEnvironmentPersistence(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));
            builder.Services.AddSingleton<IEnvironmentsLoader>(sp =>
                new RedisEnvironmentsLoader(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));

            // Change-feed reload (G16e): opt-in multi-instance coherence. Each host subscribes to Redis
            // change announcements and reloads its in-memory store, so a mutation on one instance is
            // reflected by the others without a restart.
            if (builder.Configuration.GetValue<bool>("change-feed"))
            {
                builder.Services.AddHostedService<RedisChangeFeedReloader>();
            }
        }

        // Port 0 asks Kestrel for an ephemeral port (used by tests).
        var port = builder.Configuration.GetValue("port", DefaultPort);
        var httpsPort = builder.Configuration.GetValue<int?>("https-port");

        // When HTTPS is enabled both listeners are configured on Kestrel directly (a self-signed cert
        // by default); otherwise the HTTP port alone is bound via app.Urls.
        if (httpsPort is { } securePort)
        {
            var certificate = SelfSignedCertificate.Create();
            // TLS options (G11c): a configured keystore replaces the self-signed cert, and mutual TLS
            // (require + validate a client certificate) is enabled on demand. See TlsConfiguration.
            var configureTls = TlsConfiguration.Build(builder.Configuration, certificate);
            builder.WebHost.ConfigureKestrel(options =>
            {
                // HTTP/2 (G11b, verified by the differential suite): both listeners speak HTTP/1.1 and
                // HTTP/2 — ALPN-negotiated h2 on TLS, and prior-knowledge h2c on plaintext — on both
                // ports by default. See docs/parity/g11-tls-http2.md.
                options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
                options.ListenAnyIP(securePort, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(configureTls);
                });
            });
        }

        var app = builder.Build();

        if (httpsPort is null)
        {
            app.Urls.Add($"http://0.0.0.0:{port}");
        }

        // WebSocket message serving (G15d): accepts WS upgrades at the front of the pipeline (before the
        // mock-serving fallback) and registers POST /__admin/message-mappings.
        // WebSocket `filePath` message bodies (G15g) resolve from the conventional <root-dir>/__files directory.
        var filesDirectory = string.IsNullOrWhiteSpace(rootDir) ? null : Path.Combine(rootDir, "__files");
        app.UseMockifyrWebSockets(filesDirectory);

        // SMS provider profile (G18d, ADR 0009): opt-in via --sms-profile twilio. Mounted ahead of the
        // mock-serving fallback, but a hand-written stub on the same URL still wins (the middleware
        // peeks the engine and steps aside on a match), so enabling it never changes existing serving.
        if (string.Equals(builder.Configuration["sms-profile"], "twilio", StringComparison.OrdinalIgnoreCase))
        {
            app.UseMiddleware<Providers.Sms.TwilioSmsProfileMiddleware>();
        }

        // gRPC serving (G13) runs ahead of the endpoints: application/grpc requests are handled by the
        // codec+engine, everything else falls through to the admin/mock-serving endpoints. The admin
        // descriptor endpoints (G18-pre) manage <root-dir>/grpc/*.dsc and hot-reload the same index.
        if (grpcEnabled)
        {
            app.UseMockifyrGrpc();
            app.MapGrpcAdminEndpoints(
                app.Services.GetRequiredService<ProtoDescriptors>(),
                Path.Combine(rootDir!, "grpc"));
        }

        // Optional admin auth: when --admin-user + --admin-pass are set, require HTTP Basic on the admin
        // surface (/__admin/*). The mock-serving surface and the dashboard static files stay open — the
        // dashboard loads and shows its own login screen, then sends the credentials on each admin call.
        var adminUser = builder.Configuration["admin-user"];
        var adminPass = builder.Configuration["admin-pass"];
        if (!string.IsNullOrEmpty(adminUser) && !string.IsNullOrEmpty(adminPass))
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{adminUser}:{adminPass}"));
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/__admin"))
                {
                    var provided = context.Request.Headers.Authorization.ToString();
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        // Deliberately NO WWW-Authenticate: Basic header. That header makes the browser pop
                        // its native Basic-auth dialog on the dashboard's fetch() calls, which blocks the
                        // page. The dashboard has its own login screen and sends the credentials itself;
                        // CLI clients (curl -u and similar) send Basic proactively and don't need the challenge.
                        return;
                    }
                }

                await next();
            });
        }

        app.MapAdminEndpoints();

        // Dashboard (optional): when --dashboard <dir> points at the built UI (ui/dist), serve it under
        // the reserved /__mockifyr prefix — static assets plus an SPA fallback to index.html for client
        // routes. Mapped before the mock-serving catch-all and scoped to /__mockifyr, so mocked APIs on
        // every other path are untouched. The built UI uses base '/__mockifyr/', so its asset + router
        // paths line up. Absent the flag, nothing changes.
        var dashboardDir = builder.Configuration["dashboard"];
        if (!string.IsNullOrWhiteSpace(dashboardDir) && Directory.Exists(dashboardDir))
        {
            var provider = new PhysicalFileProvider(Path.GetFullPath(dashboardDir));
            var contentTypes = new FileExtensionContentTypeProvider();
            // Serve a real asset when the path maps to a file (with its proper content type), otherwise
            // fall back to index.html for the SPA's client routes. Doing both in one endpoint avoids the
            // static-file-middleware-vs-catch-all ordering trap that made asset requests (…/assets/*.js)
            // return index.html as text/html — which breaks the module scripts and blanks the page.
            app.MapGet("/__mockifyr/{**path}", async (HttpContext context, string? path) =>
            {
                var file = string.IsNullOrEmpty(path) ? null : provider.GetFileInfo(path);
                if (file is { Exists: true, IsDirectory: false })
                {
                    context.Response.ContentType = contentTypes.TryGetContentType(path!, out var ct) ? ct : "application/octet-stream";
                    await context.Response.SendFileAsync(file);
                    return;
                }

                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(provider.GetFileInfo("index.html"));
            });
        }

        app.MapMockServing();

        ApplyStartupMappings(app);
        ApplyStartupEnvironments(app);
        return app;
    }

    /// <summary>
    /// Restores persisted environment keys (G17). Unlike mappings — which load only the default tenant
    /// at startup — this restores <b>every</b> tenant, because a key that failed to come back would not
    /// fail loudly: the stub referencing it would serve the literal <c>{{key}}</c> instead.
    /// </summary>
    private static void ApplyStartupEnvironments(WebApplication app)
    {
        var store = app.Services.GetRequiredService<IEnvironmentStore>();
        foreach (var loader in app.Services.GetServices<IEnvironmentsLoader>())
        {
            foreach (var (tenant, keys) in loader.LoadAll())
            {
                foreach (var key in keys)
                {
                    store.Put(tenant, key);
                }
            }
        }
    }

    /// <summary>Loads every registered <see cref="IMappingsLoader"/> into the store for the default tenant.</summary>
    private static void ApplyStartupMappings(WebApplication app)
    {
        var store = app.Services.GetRequiredService<IStubStore>();
        foreach (var loader in app.Services.GetServices<IMappingsLoader>())
        {
            foreach (var stub in loader.Load(TenantId.Default))
            {
                store.Put(stub);
            }
        }
    }
}
