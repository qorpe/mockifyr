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
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.Facade.Admin;
using Mockifyr.Facade.Http;
using Mockifyr.Facade.Sandbox;
using Mockifyr.Facade.WebSocket;
using Mockifyr.Stores.InMemory;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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
    /// <summary>
    /// Resolves a cryptographic key source from an inline value or a file (#250).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file wins when both are given, because reaching for a file is the deliberate act: an
    /// inline value is usually a leftover from a laptop run, and silently preferring it over a
    /// mounted Secret would be the wrong surprise.
    /// </para>
    /// <para>
    /// A file source re-reads on change, so rotation needs no restart. An inline value cannot, which
    /// the startup line says out loud rather than leaving an operator to discover it during a
    /// rollover. Anything misconfigured turns the capability OFF with a message — never half on,
    /// which would mean stubs that mysteriously stop matching.
    /// </para>
    /// </remarks>
    private static Crypto.IKeySource? ResolveKeySource(string? inline, string? path, string flag, TimeSpan? reload = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"mockifyr: {flag}-file '{path}' does not exist — that capability is OFF.");
                return null;
            }

            try
            {
                var source = new Crypto.FileKeySource(path, reload);
                if (source.Current.IsEmpty)
                {
                    Console.WriteLine($"mockifyr: {flag}-file '{path}' holds no 256-bit base64 key — that capability is OFF.");
                    return null;
                }

                Console.WriteLine($"mockifyr: {flag}-file '{path}' loaded; it is re-read on change, so keys rotate without a restart.");
                return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"mockifyr: {flag}-file '{path}' could not be read ({ex.GetType().Name}) — that capability is OFF.");
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(inline))
        {
            return null;
        }

        if (Crypto.KeyRing.ReadKey(inline) is not { } material)
        {
            Console.WriteLine($"mockifyr: {flag} is not a 256-bit base64 key — that capability is OFF.");
            return null;
        }

        return new Crypto.StaticKeySource(material);
    }

    /// <summary>
    /// Reads a single-line secret from a file, trimmed. Null (with a message) when it cannot be read,
    /// so a missing file leaves the surface unauthenticated *visibly* rather than silently.
    /// </summary>
    private static string? ReadSecretFile(string? path, string flag)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(path).Trim();
            if (value.Length == 0)
            {
                Console.WriteLine($"mockifyr: {flag} '{path}' is empty — admin authentication stays OFF.");
                return null;
            }

            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"mockifyr: {flag} '{path}' could not be read ({ex.GetType().Name}) — admin authentication stays OFF.");
            return null;
        }
    }

    /// <summary>
    /// The unauthenticated probe paths (#218, #242): health for humans and the dashboard, live and
    /// ready for the orchestrator. Credentials cannot be attached to a kubelet probe, and a 401
    /// health check restarts pods in a loop.
    /// </summary>
    private static bool IsProbePath(PathString path) =>
        path.Equals("/__admin/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/__admin/live", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/__admin/ready", StringComparison.OrdinalIgnoreCase)
        // The scrape endpoint too (#246): a Prometheus scraper cannot carry credentials, and the
        // series it exposes are counts and latencies — never payloads.
        || path.Equals("/__admin/metrics", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tenant header the admin surface reads (mirrors the serving facade).</summary>
    private const string TenantCredentialHeader = "X-Mockifyr-Tenant";

    /// <summary>
    /// The admin routes that make this host act on the network rather than on one tenant's data:
    /// recording (a forward proxy to any target), outbound certificate trust, and Git sync.
    /// </summary>
    private static readonly string[] OutwardRoutes =
        ["/__admin/recordings", "/__admin/outbound-trust", "/__admin/git"];

    /// <summary>
    /// Whether a partner principal's request is refused, and why (#346). Two refusals, because
    /// "may reach the network from this host" is not a property of a route set: the three routes above
    /// are one way to reach outward and a stub definition is the other. Blocking only the routes would
    /// leave an operator holding a control that looks like it holds and does not.
    /// </summary>
    /// <remarks>
    /// Every method is refused on an outward route, not only the mutating ones. A partner has no
    /// business reading which upstream a recording is pointed at either, and a rule stated as "these
    /// routes are not yours" is one an operator can keep in their head.
    /// </remarks>
    private static async Task<(string Error, string Message)?> PartnerRefusal(HttpContext context)
    {
        var path = context.Request.Path;
        foreach (var route in OutwardRoutes)
        {
            if (path.StartsWithSegments(route))
            {
                return ("Admin.PartnerRouteForbidden",
                    $"'{route}' makes this host act on the network, and these credentials are scoped to "
                    + "one tenant's data. An operator credential (--admin-user, or --tenant-credential) "
                    + "can use it.");
            }
        }

        if (!path.StartsWithSegments("/__admin/mappings")
            || !(HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)))
        {
            return null;
        }

        // Buffered and rewound: the handler downstream reads the same body, and a check that consumed
        // it would turn every allowed request into an empty one.
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;

        if (OutboundReach.DeclaredBy(body) is not { Count: > 0 } declared)
        {
            return null;
        }

        // The field is named so a partner who legitimately needs a proxy stub gets something they can
        // act on — and so the operator reading the audit trail knows what was asked for.
        return ("Admin.PartnerOutboundStubForbidden",
            $"This stub declares {string.Join(" and ", declared)}, which makes this host call out to a "
            + "target the stub names. These credentials are scoped to one tenant's data. Ask an "
            + "operator to add the stub, or drop the field.");
    }

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

            // Response bodies held as files, by the same convention as the mapping sets this dialect
            // comes from: <root-dir>/__files. Registered only with a root-dir — without one there is
            // no directory a `bodyFileName` could sensibly mean, and the import warning says so.
            builder.Services.AddSingleton<IResponseBodyFiles>(
                new DirectoryResponseBodyFiles(Path.Combine(rootDir, "__files")));

            // A root-dir also makes stub mutations durable (G16a): they persist to the same mappings
            // directory the loader reads on startup. Registered last so it wins over the no-op default.
            builder.Services.AddSingleton<IStubPersistence>(new FileSystemStubPersistence(mappingsDir));

            // Environments persist alongside the mappings (G17), under <root-dir>/environments/<tenant>/.
            var environmentsDir = Path.Combine(rootDir, "environments");
            builder.Services.AddSingleton<IEnvironmentPersistence>(new FileSystemEnvironmentPersistence(environmentsDir));
            builder.Services.AddSingleton<IEnvironmentsLoader>(new FileSystemEnvironmentsLoader(environmentsDir));

            // Sandbox resources persist alongside too: a partner seeds fixtures into a sandbox and a
            // restart must not take them away. Backup/restore covered the deliberate case; this
            // covers the one nobody plans for.
            var resourcesDir = Path.Combine(rootDir, "resources");
            builder.Services.AddSingleton<IResourcePersistence>(new FileSystemResourcePersistence(resourcesDir));
            builder.Services.AddSingleton<IResourcesLoader>(new FileSystemResourcesLoader(resourcesDir));

            // API keys persist alongside (G19d, ADR 0011 addendum): a credential that vanishes on
            // redeploy is not a credential. Host-level directory — the key selects the tenant.
            var apiKeysDir = Path.Combine(rootDir, "apikeys");
            builder.Services.AddSingleton<IApiKeyPersistence>(new FileSystemApiKeyPersistence(apiKeysDir));

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

        // Bounded request journal (#220): --journal-limit overrides the default per-tenant cap
        // (1000, oldest evicted first); --max-request-journal-entries is the reference engine's
        // name for the same thing and is honored as an alias. A value <= 0 means unbounded.
        // --journal-disabled (alias --no-request-journal) records nothing at all — for load tests
        // where the journal is pure overhead.
        if (builder.Configuration.GetValue<bool>("journal-disabled") ||
            builder.Configuration.GetValue<bool>("no-request-journal"))
        {
            builder.Services.AddSingleton<IRequestJournal, NullRequestJournal>();
        }
        else if (int.TryParse(
            builder.Configuration["journal-limit"] ?? builder.Configuration["max-request-journal-entries"],
            out var journalLimit))
        {
            builder.Services.AddSingleton<IRequestJournal>(
                new InMemoryRequestJournal(journalLimit > 0 ? journalLimit : null));
        }

        // Admin audit trail (#247): opt-in with --audit, bounded per tenant by --audit-limit (default
        // 1000, oldest evicted first — the journal's retention model, so an operator learns one). Off
        // by default because a laptop mock has nothing to audit; on, it is the "who changed what"
        // record a review asks for. The trail lives in memory and is also emitted as a log line, so a
        // SIEM keeps the durable copy — deliberately not persisted through the G16 seam, which would
        // make the audit log a tenant-writable store.
        var auditEnabled = builder.Configuration.GetValue<bool>("audit");
        if (auditEnabled)
        {
            var auditLimit = int.TryParse(builder.Configuration["audit-limit"], out var parsedAuditLimit)
                ? parsedAuditLimit
                : InMemoryAuditLog.DefaultLimit;
            builder.Services.AddSingleton<IAuditLog>(new InMemoryAuditLog(auditLimit));
        }

        // OIDC (#251): a third principal source on the admin surface, alongside the system credential
        // and per-tenant credentials — not a replacement. A host can run OIDC for people and
        // --admin-user for machines, which is what makes adopting it incremental rather than a flag
        // day. Nothing here reaches Core; authentication has always lived at this edge.
        var oidc = OidcOptions.Parse(key => builder.Configuration[key]);
        OidcTokenValidator? oidcValidator = null;
        if (oidc is not null)
        {
            // The validator itself is a local, not a service: the two things that need it — the auth
            // middleware and the audit principal resolver — are both constructed further down in this
            // same method. What IS registered is the public descriptor the dashboard reads to know
            // where to send a user to sign in.
            oidcValidator = new OidcTokenValidator(oidc);
            builder.Services.AddSingleton(new AdminAuthDescriptor("oidc", oidc.Authority, oidc.ClientId));
        }
        else if (!string.IsNullOrEmpty(builder.Configuration["admin-user"])
            && (!string.IsNullOrEmpty(builder.Configuration["admin-pass"])
                || !string.IsNullOrEmpty(builder.Configuration["admin-pass-file"])))
        {
            // Reported so /__admin/health tells the truth about how to sign in. The dashboard reaches
            // the same conclusion from its own 401 either way, but a documented surface should not
            // answer "none" for a host that plainly requires credentials.
            builder.Services.AddSingleton(new AdminAuthDescriptor("basic"));
        }

        if (oidc is not null)
        {
            Console.WriteLine($"mockifyr: OIDC is on — bearer tokens from '{oidc.Authority}' authenticate the admin API"
                + (oidc.TenantClaim is { } claim ? $", scoped by the '{claim}' claim." : ".")
                + (oidc.RequiredRole is { } role ? $" Requires the '{role}' role." : string.Empty));
        }

        // Observability (#246): opt-in, because a mock on a laptop should not open a metrics port or
        // ship spans anywhere. --otel-endpoint enables the OTLP exporter (traces + metrics);
        // --metrics-port… no: the scrape endpoint rides on the existing port at /__admin/metrics so
        // no extra listener, no extra Service port, and it stays outside admin auth because a
        // scraper cannot authenticate — the same reasoning as the probes (#242).
        var otelEndpoint = builder.Configuration["otel-endpoint"];
        var metricsEnabled = builder.Configuration.GetValue<bool>("metrics");
        if (!string.IsNullOrWhiteSpace(otelEndpoint) || metricsEnabled)
        {
            builder.Services.AddSingleton<IServeEventListener, MetricsServeEventListener>();

            var telemetry = builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(
                    serviceName: MockifyrTelemetry.Name,
                    serviceVersion: typeof(MockifyrHost).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
                .WithMetrics(metrics =>
                {
                    metrics.AddMeter(MockifyrTelemetry.Name)
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();
                    if (metricsEnabled)
                    {
                        metrics.AddPrometheusExporter();
                    }
                })
                .WithTracing(tracing => tracing
                    .AddSource(MockifyrTelemetry.Name)
                    .AddAspNetCoreInstrumentation(options =>
                        // Probes and the scrape endpoint would otherwise dominate the trace volume
                        // with spans nobody reads.
                        options.Filter = context => !IsProbePath(context.Request.Path)
                            && !context.Request.Path.Equals("/__admin/metrics", StringComparison.OrdinalIgnoreCase))
                    .AddHttpClientInstrumentation());

            if (!string.IsNullOrWhiteSpace(otelEndpoint))
            {
                telemetry.UseOtlpExporter(OpenTelemetry.Exporter.OtlpExportProtocol.Grpc, new Uri(otelEndpoint));
                Console.WriteLine($"mockifyr: OpenTelemetry enabled, exporting to {otelEndpoint}.");
            }

            if (metricsEnabled)
            {
                Console.WriteLine("mockifyr: Prometheus metrics enabled at /__admin/metrics.");
            }
        }

        // Structured logging (#246): --log-json swaps the console formatter for the JSON one, so a
        // log pipeline gets fields instead of prose. Off by default — a developer reading a terminal
        // wants the readable form.
        if (builder.Configuration.GetValue<bool>("log-json"))
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
        }

        // Payload decryption (G20a, ADR 0012): --decrypt-key <base64 256-bit key> registers the
        // JWE(dir+A256GCM) field decryptor. Key material stops here — Core only ever sees that a
        // scheme was applied. No flag, no decryptor, and stubs declaring `decrypt` simply do not
        // match, which is the honest outcome for a host that was never given the key.
        // How often a key file is re-read (#250). The default suits a Kubernetes Secret update, which
        // takes up to a minute to propagate anyway; a shorter value is for tests and for deployments
        // that rotate on a tighter schedule.
        var keyReload = double.TryParse(builder.Configuration["key-reload-seconds"], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : (TimeSpan?)null;

        var decryptKeys = ResolveKeySource(
            builder.Configuration["decrypt-key"], builder.Configuration["decrypt-key-file"], "--decrypt-key", keyReload);
        if (decryptKeys is not null)
        {
            builder.Services.AddSingleton<IPayloadDecryptor>(new Crypto.JweFieldDecryptor(decryptKeys));
            // The same key protects responses (G20b): a partner that encrypts what it sends also
            // decrypts what it receives, and asking the operator for two keys for one relationship
            // would be ceremony without a security benefit.
            builder.Services.AddSingleton<IPayloadProtector>(new Crypto.JweResponseProtector(decryptKeys));
            builder.Services.AddKeyedSingleton("decrypt", decryptKeys);
            Console.WriteLine($"mockifyr: payload cryptography enabled (scheme {Crypto.JweFieldDecryptor.SchemeName}, "
                + $"{decryptKeys.Current.Keys.Count} active key(s)).");
        }

        // Request/response signing (G20c, ADR 0012): --sign-key <base64 secret> registers the
        // HMAC-SHA256 verifier and signer. A separate key from --decrypt-key on purpose: signing
        // secrets and encryption keys are managed separately in every scheme that uses both.
        var signKeys = ResolveKeySource(
            builder.Configuration["sign-key"], builder.Configuration["sign-key-file"], "--sign-key", keyReload);
        if (signKeys is not null)
        {
            builder.Services.AddSingleton<ISignatureVerifier>(new Crypto.HmacSignatureVerifier(signKeys));
            builder.Services.AddSingleton<IResponseSigner>(new Crypto.HmacResponseSigner(signKeys));
            builder.Services.AddKeyedSingleton("sign", signKeys);
            Console.WriteLine($"mockifyr: request/response signing enabled (scheme {Crypto.HmacSignatureVerifier.SchemeName}, "
                + $"{signKeys.Current.Keys.Count} active key(s)).");
        }

        // What /__admin/health reports about key rotation (#250): counts, read live, never material.
        if (decryptKeys is not null || signKeys is not null)
        {
            builder.Services.AddSingleton(new ActiveKeyReport(
                () => decryptKeys?.Current.Keys.Count ?? 0,
                () => signKeys?.Current.Keys.Count ?? 0));
        }

        // Journal masking (#227): --mask-headers / --mask-body-fields keep named values out of the
        // journal entirely (they are replaced before the event is stored, so they cannot be read
        // back through the admin API or the dashboard). Opt-in on purpose: a masked value is also
        // invisible to verify/near-miss, which read the same stored request. Decorates whatever
        // journal was registered above, so the bound and the disabled switch still apply.
        var masking = JournalMaskingOptions.Parse(
            builder.Configuration["mask-headers"], builder.Configuration["mask-body-fields"]);
        if (!masking.IsEmpty)
        {
            builder.Services.AddSingleton<IRequestJournal>(sp => new MaskingRequestJournal(
                ActivatorUtilities.CreateInstance<InMemoryRequestJournal>(sp), masking));
        }

        // Message behaviors (G18e): a bounded inbox override (--message-limit) and the capture
        // webhook decorating the sink. Registered after AddMockifyr so they win the resolution.
        if (int.TryParse(builder.Configuration["message-limit"], out var messageLimit))
        {
            builder.Services.AddSingleton<IMessageStore>(new InMemoryMessageStore(messageLimit));
        }

        // Sandbox access (G19d, ADR 0011): --sandbox-auth turns on key-based tenant resolution
        // ahead of the host/header chain. Off by default — zero behavior change without the flag.
        if (builder.Configuration.GetValue<bool>("sandbox-auth"))
        {
            builder.Services.AddSingleton(new SandboxAuthOptions(Enabled: true));
        }

        // Sandbox resources (G19a, ADR 0011 addendum): both caps are flag-tunable — the
        // per-collection document bound and the per-document body bytes (413 beyond it).
        if (int.TryParse(builder.Configuration["resource-limit"], out var resourceLimit))
        {
            builder.Services.AddSingleton<IResourceStore>(new InMemoryResourceStore(resourceLimit));
        }

        if (int.TryParse(builder.Configuration["resource-max-body"], out var resourceMaxBody) && resourceMaxBody > 0)
        {
            builder.Services.AddSingleton(new ResourceOptions(resourceMaxBody));
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
            builder.Services.AddSingleton<IResourcePersistence>(sp =>
                new LiteDbResourcePersistence(sp.GetRequiredService<LiteDB.LiteDatabase>()));
            builder.Services.AddSingleton<IResourcesLoader>(sp =>
                new LiteDbResourcesLoader(sp.GetRequiredService<LiteDB.LiteDatabase>()));
            builder.Services.AddSingleton<IApiKeyPersistence>(sp =>
                new LiteDbApiKeyPersistence(sp.GetRequiredService<LiteDB.LiteDatabase>()));
        }

        // PostgreSQL persistence (G16c): stubs persist to a SQL table and reload on startup.
        var postgres = builder.Configuration["postgres"];
        if (!string.IsNullOrWhiteSpace(postgres))
        {
            builder.Services.AddSingleton<IStubPersistence>(sp =>
                new PostgresStubPersistence(postgres, sp.GetRequiredService<ChangeFeedIdentity>()));
            builder.Services.AddSingleton<IMappingsLoader>(sp =>
                new PostgresMappingsLoader(postgres, sp.GetRequiredService<IMatcherRegistry>()));
            builder.Services.AddSingleton<IEnvironmentPersistence>(sp =>
                new PostgresEnvironmentPersistence(postgres, sp.GetRequiredService<ChangeFeedIdentity>()));
            builder.Services.AddSingleton<IEnvironmentsLoader>(new PostgresEnvironmentsLoader(postgres));
            builder.Services.AddSingleton<IResourcePersistence>(sp =>
                new PostgresResourcePersistence(postgres, sp.GetRequiredService<ChangeFeedIdentity>()));
            builder.Services.AddSingleton<IResourcesLoader>(new PostgresResourcesLoader(postgres));
            builder.Services.AddSingleton<IApiKeyPersistence>(new PostgresApiKeyPersistence(postgres));

            // Change-feed reload (G16f): opt-in multi-instance coherence via Postgres LISTEN/NOTIFY —
            // the same seam as Redis (G16e). Each host listens for change announcements and reconciles
            // its in-memory state — stubs, environment keys and sandbox documents (#279) — so a mutation
            // on one instance is reflected by the others live.
            if (builder.Configuration.GetValue<bool>("change-feed"))
            {
                builder.Services.AddSingleton<IHostedService>(sp =>
                    new PostgresChangeFeedReloader(postgres, sp.GetRequiredService<ChangeFeedTargets>()));
            }
        }

        // Broker channel (ADR 0013): opt-in, per transport. Nothing is connected without a flag, so a
        // host that mocks no events pays nothing — no producer, no background threads, no connection
        // attempt at startup.
        var kafka = builder.Configuration["kafka-bootstrap"];
        var amqp = builder.Configuration["amqp-uri"];
        if (!string.IsNullOrWhiteSpace(kafka) || !string.IsNullOrWhiteSpace(amqp))
        {
            // One publisher seam over however many transports were configured. With a single broker
            // the router is a pass-through, so nothing about writing a stub changes; with two, a topic
            // can name where it goes (slice 4).
            builder.Services.AddSingleton<Facade.Broker.IBrokerPublisher>(_ => new Facade.Broker.BrokerRouter(
                string.IsNullOrWhiteSpace(kafka) ? null : new Facade.Broker.KafkaPublisher(kafka),
                string.IsNullOrWhiteSpace(amqp) ? null : new Facade.Broker.AmqpPublisher(amqp)));
            builder.Services.AddSingleton<IServeEventListener>(sp => new Facade.Broker.PublishServeEventListener(
                sp.GetRequiredService<Facade.Broker.IBrokerPublisher>(),
                sp.GetRequiredService<IServeEventTemplateRenderer>()));

            // Serve on consume (ADR 0013, slice 3): the mappings an inbound message is matched against.
            // Registered whenever a broker is configured, so the admin routes exist as soon as they
            // could do anything — a stub you can post but that will never be evaluated is a trap.
            builder.Services.AddSingleton(new Facade.Broker.BrokerMappingStore());
            builder.Services.AddSingleton(sp => new Facade.Broker.BrokerMappingPlanner(
                sp.GetRequiredService<Facade.Broker.BrokerMappingStore>(),
                new Templating.MessageTemplateRenderer(
                    sp.GetService<IEnvironmentResolver>(), sp.GetService<IClockResolver>())));

            if (!string.IsNullOrWhiteSpace(kafka))
            {
                Console.WriteLine($"mockifyr: publishing to Kafka at '{kafka}' for stubs that declare a publish action.");

                // Capture (ADR 0013, slice 2): only with topics to listen to. A host that publishes but
                // subscribes to nothing starts no consumer and joins no group.
                var topics = (builder.Configuration["kafka-subscribe"] ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (topics.Length > 0)
                {
                    var group = builder.Configuration["kafka-group"] is { Length: > 0 } g ? g : "mockifyr";
                    builder.Services.AddSingleton(new Facade.Broker.BrokerCaptureOptions(kafka, topics, group));
                    builder.Services.AddHostedService(sp => new Facade.Broker.KafkaCaptureService(
                        sp.GetRequiredService<Facade.Broker.BrokerCaptureOptions>(),
                        sp.GetRequiredService<IMessageSink>(),
                        clock: null,
                        sp.GetRequiredService<Facade.Broker.BrokerMappingPlanner>(),
                        sp.GetRequiredService<Facade.Broker.IBrokerPublisher>()));
                    Console.WriteLine(
                        $"mockifyr: capturing {string.Join(", ", topics)} into the message inbox as consumer group '{group}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(amqp))
            {
                Console.WriteLine("mockifyr: publishing to AMQP for stubs that declare a publish action.");

                var queues = (builder.Configuration["amqp-subscribe"] ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (queues.Length > 0)
                {
                    builder.Services.AddSingleton(new Facade.Broker.AmqpCaptureOptions(amqp, queues));
                    builder.Services.AddHostedService(sp => new Facade.Broker.AmqpCaptureService(
                        sp.GetRequiredService<Facade.Broker.AmqpCaptureOptions>(),
                        sp.GetRequiredService<IMessageSink>(),
                        clock: null,
                        sp.GetRequiredService<Facade.Broker.BrokerMappingPlanner>(),
                        sp.GetRequiredService<Facade.Broker.IBrokerPublisher>()));
                    Console.WriteLine(
                        $"mockifyr: consuming {string.Join(", ", queues)} from AMQP into the message inbox.");
                }
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
                new RedisStubPersistence(
                    sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
                    sp.GetRequiredService<ChangeFeedIdentity>()));
            builder.Services.AddSingleton<IMappingsLoader>(sp =>
                new RedisMappingsLoader(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(), sp.GetRequiredService<IMatcherRegistry>()));
            builder.Services.AddSingleton<IEnvironmentPersistence>(sp =>
                new RedisEnvironmentPersistence(
                    sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
                    sp.GetRequiredService<ChangeFeedIdentity>()));
            builder.Services.AddSingleton<IEnvironmentsLoader>(sp =>
                new RedisEnvironmentsLoader(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));
            builder.Services.AddSingleton<IResourcePersistence>(sp =>
                new RedisResourcePersistence(
                    sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>(),
                    sp.GetRequiredService<ChangeFeedIdentity>()));
            builder.Services.AddSingleton<IResourcesLoader>(sp =>
                new RedisResourcesLoader(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));
            builder.Services.AddSingleton<IApiKeyPersistence>(sp =>
                new RedisApiKeyPersistence(sp.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()));

            // Change-feed reload (G16e): opt-in multi-instance coherence. Each host subscribes to Redis
            // change announcements and reloads its in-memory state — stubs, environment keys and sandbox
            // documents (#279) — so a mutation on one instance is reflected by the others without a
            // restart.
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

        // Broker mappings (ADR 0013, slice 3): registered only when a broker is configured, so the
        // routes exist exactly when a mapping posted to them could be evaluated.
        if (app.Services.GetService<Facade.Broker.BrokerMappingStore>() is { } brokerMappings)
        {
            Facade.Broker.BrokerMappingEndpoints.UseMockifyrBrokerMappings(app, brokerMappings);
        }

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
        // --admin-pass-file keeps the password out of the process listing and out of shell history
        // (#250): on a shared host, `ps` is readable by anyone, and a mounted Secret is the whole
        // point of running in Kubernetes. The inline flag still works and wins when both are given,
        // so nothing that worked before changes.
        var adminPass = builder.Configuration["admin-pass"]
            ?? ReadSecretFile(builder.Configuration["admin-pass-file"], "--admin-pass-file");
        var adminAuthenticated = !string.IsNullOrEmpty(adminUser) && !string.IsNullOrEmpty(adminPass);

        // An unauthenticated admin surface is a deliberate default (the documented quick start relies
        // on it), but it should never be a silent one (#225): say what is reachable, the same way the
        // outbound-trust flags already announce themselves. OIDC counts as authentication (#251) —
        // warning about an open surface that is not open would teach operators to ignore the warning,
        // which is the only thing that makes it useful.
        if (!adminAuthenticated && oidcValidator is null)
        {
            Console.WriteLine(
                "mockifyr: the admin API (/__admin/*) is UNAUTHENTICATED — anyone who can reach this "
                + "host can read the request journal and captured messages, and can start a recording, "
                + "trust a certificate or configure Git sync. Set --admin-user/--admin-pass for a shared "
                + "host, or --block-outbound-routes to refuse the outbound-affecting routes outright.");
        }

        // Outbound-route blocking (#225): the routes that make this host act on the network — starting
        // a recording (a forward proxy to any target), trusting a certificate, configuring Git — are
        // refused while the admin surface is unauthenticated, so an open host cannot be turned into an
        // SSRF primitive against a cluster. Opt-in and inert once credentials exist, since the auth
        // middleware below already gates the same routes then.
        var blockOutbound = builder.Configuration.GetValue<bool>("block-outbound-routes");
        if (blockOutbound && adminAuthenticated)
        {
            // Said out loud rather than left to be discovered (#346). The flag is scoped to the
            // unauthenticated case by design, so on an authenticated host it is doing nothing — and a
            // flag that silently no-ops is how an operator comes to believe in a control they do not
            // have. The sentence names what actually scopes a credential instead.
            Console.WriteLine("mockifyr: --block-outbound-routes has NO effect here — the admin API is "
                + "authenticated, so those routes are already gated by credentials. To scope a specific "
                + "credential away from them, issue it with --partner-credential.");
        }

        if (blockOutbound && !adminAuthenticated)
        {
            Console.WriteLine("mockifyr: --block-outbound-routes is on — recording, outbound trust and "
                + "Git routes are refused while the admin API is unauthenticated.");
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                var blocked = path.StartsWithSegments("/__admin/recordings")
                    || path.StartsWithSegments("/__admin/outbound-trust")
                    || path.StartsWithSegments("/__admin/git");
                if (blocked && !HttpMethods.IsGet(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Admin.OutboundRoutesBlocked",
                        message = "This route makes outbound calls or changes outbound trust, and the "
                            + "admin API is unauthenticated. Set --admin-user/--admin-pass, or drop "
                            + "--block-outbound-routes to allow it.",
                    });
                    return;
                }

                await next();
            });
        }
        // Per-tenant admin credentials (#224): --tenant-credential <tenant>:<user>:<pass> (repeatable)
        // turns the tenant header from a claim into an authorization decision — a principal
        // authenticated for one tenant cannot address another by renaming the header. The global
        // --admin-user stays the privileged "system" scope ARCHITECTURE §6 anticipates, and a host
        // with no --tenant-credential behaves exactly as before.
        var tenantCredentials = TenantCredentials.Parse(args);

        // The audit trail (#247) sits ahead of the authorization middlewares on purpose: a refused
        // cross-tenant attempt (403) is exactly the event a reviewer wants, and it is recorded with the
        // principal that made it. Unauthenticated attempts are skipped inside the middleware — they
        // have no principal, and auditing them would let anyone evict the bounded trail.
        if (auditEnabled)
        {
            Console.WriteLine("mockifyr: --audit is on — admin changes are recorded at /__admin/audit "
                + "and emitted as admin.audit log lines.");
            var auditLog = app.Services.GetRequiredService<IAuditLog>();
            var auditLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Mockifyr.Audit");
            var principals = new AuditPrincipalResolver(
                adminAuthenticated
                    ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{adminUser}:{adminPass}"))
                    : null,
                tenantCredentials,
                oidcValidator);
            app.Use((context, next) => AdminAuditMiddleware.InvokeAsync(
                context, _ => next(), auditLog, principals, auditLogger, TenantCredentialHeader));
        }

        if (!tenantCredentials.IsEmpty)
        {
            Console.WriteLine($"mockifyr: {tenantCredentials.Count} per-tenant admin credential(s) configured — "
                + "each may only address its own tenant; --admin-user remains the system scope."
                + (tenantCredentials.PartnerCount > 0
                    ? $" {tenantCredentials.PartnerCount} of them are PARTNER credentials: refused on "
                      + "/__admin/recordings, /__admin/outbound-trust and /__admin/git, and on any stub "
                      + "declaring proxyBaseUrl or a post-serve action."
                    : string.Empty));
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/__admin") && !IsProbePath(context.Request.Path))
                {
                    var presented = context.Request.Headers.Authorization.ToString();
                    // A principal we know: it must own the tenant it is addressing. Anything else
                    // (the system credential, or no credential on an open host) falls through to the
                    // existing behavior — this middleware only ever narrows a known tenant principal.
                    if (tenantCredentials.PrincipalFor(presented) is { } principal)
                    {
                        var requested = context.Request.Headers.TryGetValue(TenantCredentialHeader, out var header)
                            && !string.IsNullOrEmpty(header)
                                ? header.ToString()
                                : TenantId.Default.Value;
                        if (!string.Equals(principal.Tenant, requested, StringComparison.Ordinal))
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            await context.Response.WriteAsJsonAsync(new
                            {
                                error = "Admin.TenantForbidden",
                                message = $"These credentials are scoped to tenant '{principal.Tenant}' and cannot address '{requested}'.",
                            });
                            return;
                        }

                        if (principal.IsPartner
                            && await PartnerRefusal(context) is { } refusal)
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            await context.Response.WriteAsJsonAsync(new { error = refusal.Error, message = refusal.Message });
                            return;
                        }
                    }
                }

                await next();
            });
        }

        if (adminAuthenticated || !tenantCredentials.IsEmpty || oidcValidator is not null)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{adminUser}:{adminPass}"));
            app.Use(async (context, next) =>
            {
                // The probe paths stay open (#218, #242): an orchestrator cannot attach credentials,
                // and a 401 on liveness sends the pod into a restart loop — enabling auth must never
                // break the documented deployment target. All three are read-only and expose only
                // name/version/persistence/tenant-count or a status word.
                if (context.Request.Path.StartsWithSegments("/__admin") && !IsProbePath(context.Request.Path))
                {
                    var provided = context.Request.Headers.Authorization.ToString();

                    // A bearer token is checked first, because it is the only shape a Basic comparison
                    // could never accidentally accept. A validated principal scoped to a tenant may
                    // only address that tenant — the same rule --tenant-credential enforces (#224),
                    // applied to a claim instead of a password.
                    if (oidcValidator is not null && provided.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var principal = await oidcValidator.ValidateAsync(provided, context.RequestAborted);
                        if (principal is null)
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return;
                        }

                        if (principal.Tenant is { } owned)
                        {
                            var requested = context.Request.Headers.TryGetValue(TenantCredentialHeader, out var header)
                                && !string.IsNullOrEmpty(header)
                                    ? header.ToString()
                                    : TenantId.Default.Value;
                            if (!string.Equals(owned, requested, StringComparison.Ordinal))
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                await context.Response.WriteAsJsonAsync(new
                                {
                                    error = "Admin.TenantForbidden",
                                    message = $"This identity is scoped to tenant '{owned}' and cannot address '{requested}'.",
                                });
                                return;
                            }
                        }

                        await next();
                        return;
                    }

                    // A per-tenant credential authenticates too; the middleware above has already
                    // confirmed it is addressing its own tenant.
                    if (tenantCredentials.TenantFor(provided) is null &&
                        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
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

        // Kubernetes probes (#242). Deliberately mapped here rather than in the admin facade: whether
        // this host is ready is a composition concern, not an admin API feature. Both stay outside
        // admin auth like /__admin/health (#218) — a probe cannot carry credentials.
        var readiness = app.Services.GetRequiredService<HostReadiness>();
        app.MapGet("/__admin/live", () => Results.Json(new { status = "alive" }));
        app.MapGet("/__admin/ready", () => readiness.IsReady
            ? Results.Json(new { status = readiness.State })
            : Results.Json(new { status = readiness.State }, statusCode: StatusCodes.Status503ServiceUnavailable));

        // Drain before the server stops accepting: readiness flips off first, so a rolling update
        // takes this pod out of rotation while it finishes what it is already serving.
        app.Lifetime.ApplicationStopping.Register(readiness.BeginDraining);

        // The Prometheus scrape endpoint, when enabled: on the existing port, outside admin auth (a
        // scraper cannot authenticate), and excluded from tracing above.
        if (metricsEnabled)
        {
            app.MapPrometheusScrapingEndpoint("/__admin/metrics");
        }

        app.MapAdminEndpoints();

        // The partner self-service surface (#347). A separate namespace on purpose: ADR 0011 binds
        // /__admin/* to ignore sandbox keys entirely, so this stands beside it rather than loosening
        // it. Absent --sandbox-auth there is no way to tell one partner from another, and the whole
        // namespace is absent rather than open.
        app.MapSandboxEndpoints(app.Services.GetRequiredService<SandboxAuthOptions>().Enabled);

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
                    // Hashed assets are immutable by construction; everything else (icons, manifest)
                    // must revalidate so a redeployed dashboard is picked up on the next load.
                    context.Response.Headers.CacheControl = path!.StartsWith("assets/", StringComparison.Ordinal)
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
                    await context.Response.SendFileAsync(file);
                    return;
                }

                // The SPA shell must never be served from a stale browser cache: an old bundle can
                // predate the host's capabilities (e.g. the OIDC login gate) and fail in silently
                // confusing ways. no-cache forces revalidation on every load.
                context.Response.ContentType = "text/html";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.SendFileAsync(provider.GetFileInfo("index.html"));
            });
        }

        app.MapMockServing();

        ApplyStartupMappings(app);
        ApplyStartupEnvironments(app);
        ApplyStartupResources(app);
        ApplyStartupApiKeys(app);

        // Everything the host needs in memory is loaded — only now may traffic be routed here (#242).
        app.Services.GetRequiredService<HostReadiness>().MarkReady();
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

    /// <summary>
    /// Rehydrates persisted sandbox resources, so a partner's seeded fixtures survive a restart.
    /// </summary>
    /// <remarks>
    /// Written through the store's own <c>Put</c> rather than restored wholesale, so a rehydrated
    /// document is indistinguishable from one that was just created — including the per-collection
    /// bound, which a bulk restore could otherwise walk straight past.
    /// </remarks>
    private static void ApplyStartupResources(WebApplication app)
    {
        var store = app.Services.GetRequiredService<IResourceStore>();
        foreach (var loader in app.Services.GetServices<IResourcesLoader>())
        {
            foreach (var (tenant, documents) in loader.LoadAll())
            {
                foreach (var document in documents)
                {
                    store.Put(tenant, document.Collection, document.Id, document.Body);
                }
            }
        }
    }

    /// <summary>Rehydrates persisted API keys (G19d) — issued credentials survive restarts.</summary>
    private static void ApplyStartupApiKeys(WebApplication app)
    {
        var store = app.Services.GetRequiredService<IApiKeyStore>();
        foreach (var key in app.Services.GetRequiredService<IApiKeyPersistence>().LoadAll())
        {
            store.Put(key);
        }
    }

    /// <summary>Loads every registered <see cref="IMappingsLoader"/> into the store for the default tenant.</summary>
    private static void ApplyStartupMappings(WebApplication app)
    {
        var store = app.Services.GetRequiredService<IStubStore>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);

        // Whether a `publish` action is honoured depends on how this host was started, not on the
        // mapping — so the answer has to come from here.
        var broker = app.Services.GetService<Facade.Broker.IBrokerPublisher>() is not null;
        foreach (var loader in app.Services.GetServices<IMappingsLoader>())
        {
            foreach (var stub in loader.Load(TenantId.Default))
            {
                store.Put(stub);
                if (stub.Source is { } source)
                {
                    warnings.UnionWith(Adapters.MappingJson.UnsupportedFieldWarnings.For(source, broker));
                }
            }
        }

        // Mappings loaded from disk never pass through the admin API, so this is the only place an
        // operator would hear about a field the engine accepts but does not act on. Saying nothing is
        // how a `bodyFileName` stub becomes an afternoon of debugging an empty body.
        foreach (var warning in warnings)
        {
            Console.WriteLine($"mockifyr: {warning}");
        }
    }
}
