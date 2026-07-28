using System.Diagnostics;
using Mediant.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Self-tests for the validated Git sync (ADR 0007). WireMock has no Git surface, so there is no
/// oracle to diff — like the other oracle-less features these are structural/behavioral tests
/// against a local bare repository: push→pull round-trip between two composed hosts, wholesale
/// rejection of an invalid remote tree, non-fast-forward and dirty-tree refusals, and credential
/// scrubbing. Requires the git binary (present on dev machines and CI).
/// </summary>
public sealed class GitSyncTests : IDisposable
{
    private const string StubJson =
        """{"request":{"method":"GET","url":"/synced"},"response":{"status":200,"body":"from-git"}}""";

    private readonly DirectoryInfo _base = Directory.CreateTempSubdirectory("mockifyr-git-");

    public void Dispose()
    {
        // Git marks loose objects under .git/objects read-only, and Directory.Delete throws
        // UnauthorizedAccessException on read-only files on Windows (#219) — clear the attribute
        // first so a Windows contributor doesn't see eight teardown-only failures.
        foreach (var file in _base.GetFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        _base.Delete(recursive: true);
    }

    private sealed record Host(GitSyncService Git, IStubStore Store, IMatcherRegistry Matchers, FileSystemStubPersistence Persistence);

    /// <summary>Mirrors the standalone host's --root-dir + --git-remote (pinned) wiring, without Kestrel.</summary>
    private static Host Compose(string root, string remote) =>
        Compose(new GitSyncEnvironment(root, remote, "main"));

    private static Host Compose(GitSyncEnvironment env)
    {
        var provider = new ServiceCollection().AddMockifyr().BuildServiceProvider();
        var store = provider.GetRequiredService<IStubStore>();
        var matchers = provider.GetRequiredService<IMatcherRegistry>();
        var mappingsDir = Path.Combine(env.WorkDir, "mappings");
        var loaders = new IMappingsLoader[] { new DirectoryMappingsLoader(mappingsDir, matchers) };
        return new Host(
            new GitSyncService(env, store, loaders, matchers),
            store, matchers, new FileSystemStubPersistence(mappingsDir));
    }

    /// <summary>Creates a stub the way the admin handlers do: parse, put in the store, persist to disk.</summary>
    private static StubMapping AddStub(Host host, string json)
    {
        var (stub, source) = MappingJsonReader.ReadWithSource(json, TenantId.Default, host.Matchers)[0];
        host.Store.Put(stub);
        host.Persistence.Save(stub, source);
        return stub;
    }

    private string NewDir(string name)
    {
        var path = Path.Combine(_base.FullName, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateBareRemote()
    {
        var bare = NewDir("remote.git");
        RunGit(bare, "init", "--bare", "-b", "main");
        return bare;
    }

    /// <summary>Runs git for fixture setup (seeding/advancing the remote out-of-band).</summary>
    private static string RunGit(string workDir, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("user.name=fixture");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("user.email=fixture@localhost");
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    /// <summary>Commits files into the remote's main branch via a scratch clone (an "external" collaborator).</summary>
    private void SeedRemote(string bare, params (string Path, string Content)[] files)
    {
        var clone = NewDir("seed-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(clone, "clone", bare, ".");
        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(clone, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        RunGit(clone, "add", "-A");
        RunGit(clone, "commit", "-m", "external change");
        RunGit(clone, "push", "origin", "HEAD:main");
    }

    [Fact]
    public async Task PushThenPull_RoundTripsAStub_BetweenTwoHosts()
    {
        var bare = CreateBareRemote();
        var hostA = Compose(NewDir("host-a"), bare);
        AddStub(hostA, StubJson);

        var push = await hostA.Git.PushAsync("publish the stub", CancellationToken.None);
        Assert.True(push.IsSuccess, push.IsSuccess ? null : push.Error.Description);
        Assert.True(push.Value.Pushed);
        Assert.Equal("pushed", push.Value.Reason);

        var hostB = Compose(NewDir("host-b"), bare);
        var pull = await hostB.Git.PullAsync(CancellationToken.None);
        Assert.True(pull.IsSuccess, pull.IsSuccess ? null : pull.Error.Description);
        Assert.True(pull.Value.Updated);
        Assert.Equal("fast-forwarded", pull.Value.Reason);
        Assert.Equal(1, pull.Value.StubsLoaded);

        // The pulled stub is served state on host B — same id, same definition.
        var served = Assert.Single(hostB.Store.GetStubs(TenantId.Default));
        Assert.Equal(Assert.Single(hostA.Store.GetStubs(TenantId.Default)).Id, served.Id);

        // A second pull is a no-op, and a push with nothing new says so instead of committing noise.
        var again = await hostB.Git.PullAsync(CancellationToken.None);
        Assert.True(again.IsSuccess);
        Assert.Equal("up-to-date", again.Value.Reason);

        var noop = await hostA.Git.PushAsync(null, CancellationToken.None);
        Assert.True(noop.IsSuccess);
        Assert.False(noop.Value.Pushed);
        Assert.Equal("nothing-to-push", noop.Value.Reason);
    }

    [Fact]
    public async Task Pull_WithAnInvalidMappingInTheRemoteTree_RejectsWholesale()
    {
        var bare = CreateBareRemote();
        SeedRemote(bare,
            ("mappings/good.json", StubJson),
            ("mappings/bad.json", """{"request":{"method": """)); // truncated JSON

        var host = Compose(NewDir("host-v"), bare);
        var pull = await host.Git.PullAsync(CancellationToken.None);

        Assert.False(pull.IsSuccess);
        Assert.Equal("Git.InvalidMappings", pull.Error.Code);
        Assert.Contains("bad.json", pull.Error.Description);

        // All-or-nothing: nothing was applied — no served stubs, no working-tree mappings.
        Assert.Empty(host.Store.GetStubs(TenantId.Default));
        Assert.False(Directory.Exists(Path.Combine(_base.FullName, "host-v", "mappings")));
    }

    [Fact]
    public async Task Push_WhenTheRemoteIsAhead_RefusesWithPullFirst()
    {
        var bare = CreateBareRemote();
        var host = Compose(NewDir("host-p"), bare);
        AddStub(host, StubJson);
        Assert.True((await host.Git.PushAsync(null, CancellationToken.None)).IsSuccess);

        // Someone else advances the remote.
        SeedRemote(bare, ("mappings/other.json",
            """{"request":{"method":"GET","url":"/other"},"response":{"status":200}}"""));

        AddStub(host, """{"request":{"method":"GET","url":"/second"},"response":{"status":200}}""");
        var push = await host.Git.PushAsync(null, CancellationToken.None);

        Assert.False(push.IsSuccess);
        Assert.Equal("Git.RemoteAhead", push.Error.Code);

        // The refusal happened BEFORE anything was committed, so the operator can still pull:
        // the local edit is untouched (dirty) and survives the non-overlapping fast-forward.
        var status = await host.Git.StatusAsync(CancellationToken.None);
        Assert.True(status.Value.Dirty);
        var pull = await host.Git.PullAsync(CancellationToken.None);
        Assert.True(pull.IsSuccess, pull.IsSuccess ? null : pull.Error.Description);
        Assert.Equal(3, pull.Value.StubsLoaded); // pushed + remote's + the local unpushed edit
    }

    [Fact]
    public async Task Pull_KeepsNonOverlappingLocalEdits()
    {
        var bare = CreateBareRemote();
        var host = Compose(NewDir("host-d"), bare);
        AddStub(host, StubJson);
        Assert.True((await host.Git.PushAsync(null, CancellationToken.None)).IsSuccess);

        SeedRemote(bare, ("mappings/other.json",
            """{"request":{"method":"GET","url":"/other"},"response":{"status":200}}"""));

        // A local unpushed stub (its own file) does not overlap the incoming update: the pull
        // fast-forwards around it and both the remote's stub and the local edit end up served.
        AddStub(host, """{"request":{"method":"GET","url":"/unpushed"},"response":{"status":200}}""");
        var pull = await host.Git.PullAsync(CancellationToken.None);

        Assert.True(pull.IsSuccess, pull.IsSuccess ? null : pull.Error.Description);
        Assert.Equal(3, pull.Value.StubsLoaded);
        Assert.True((await host.Git.StatusAsync(CancellationToken.None)).Value.Dirty); // the local edit is still unpushed
    }

    [Fact]
    public async Task Pull_WithOverlappingLocalEdits_RefusesWithoutApplyingAnything()
    {
        var bare = CreateBareRemote();
        var host = Compose(NewDir("host-o"), bare);
        var stub = AddStub(host, StubJson);
        Assert.True((await host.Git.PushAsync(null, CancellationToken.None)).IsSuccess);

        // The remote AND the local working copy both modify the same stub file.
        SeedRemote(bare, ($"mappings/{stub.Id}.json",
            """{"request":{"method":"GET","url":"/synced"},"response":{"status":200,"body":"remote-edit"}}"""));
        var localEdit = MappingJsonReader.ReadWithSource(
            """{"request":{"method":"GET","url":"/synced"},"response":{"status":200,"body":"local-edit"}}""",
            TenantId.Default, host.Matchers)[0];
        host.Persistence.Save(localEdit.Stub with { Id = stub.Id }, localEdit.Source);

        var pull = await host.Git.PullAsync(CancellationToken.None);

        Assert.False(pull.IsSuccess);
        Assert.Equal("Git.LocalOverlap", pull.Error.Code);
        // Nothing was applied — the local edit is exactly as it was.
        Assert.Contains("local-edit", File.ReadAllText(Path.Combine(_base.FullName, "host-o", "mappings", $"{stub.Id}.json")));
    }

    [Fact]
    public async Task Status_ReportsDirtyAndSettlesAfterPush()
    {
        var bare = CreateBareRemote();
        var host = Compose(NewDir("host-s"), bare);
        AddStub(host, StubJson);

        var before = await host.Git.StatusAsync(CancellationToken.None);
        Assert.True(before.IsSuccess);
        Assert.True(before.Value.Configured);
        Assert.Equal("main", before.Value.Branch);
        Assert.True(before.Value.Dirty);

        Assert.True((await host.Git.PushAsync(null, CancellationToken.None)).IsSuccess);

        var after = await host.Git.StatusAsync(CancellationToken.None);
        Assert.True(after.IsSuccess);
        Assert.False(after.Value.Dirty);
        Assert.Equal(0, after.Value.Ahead);
        Assert.Equal(0, after.Value.Behind);
    }

    [Fact]
    public async Task Errors_NeverContainTheTokenOrUrlCredentials()
    {
        const string token = "sekret-token-123";
        Environment.SetEnvironmentVariable("MOCKIFYR_GIT_TOKEN", token);
        try
        {
            // Port 9 (discard) refuses instantly; the URL carries userinfo that must be scrubbed too.
            var host = Compose(NewDir("host-t"), $"https://user:{token}@127.0.0.1:9/repo.git");
            AddStub(host, StubJson);
            var push = await host.Git.PushAsync(null, CancellationToken.None);

            Assert.False(push.IsSuccess);
            Assert.DoesNotContain(token, push.Error.Description, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOCKIFYR_GIT_TOKEN", null);
        }
    }

    [Fact]
    public async Task Configure_OnInMemoryHost_SnapshotsAndSyncsEndToEnd()
    {
        var bare = CreateBareRemote();

        // A pure in-memory host (#151): switchable persistence, no pinned remote.
        var switchable = new SwitchableStubPersistence();
        var root = NewDir("host-cfg");
        var host = Compose(new GitSyncEnvironment(root, Activatable: switchable));

        // A stub that existed BEFORE the connect — it must be snapshotted, not lost.
        var (stub, source) = MappingJsonReader.ReadWithSource(StubJson, TenantId.Default, host.Matchers)[0];
        host.Store.Put(stub);
        switchable.Save(stub, source); // no-op until activation, like a live in-memory host

        var status = await host.Git.StatusAsync(CancellationToken.None);
        Assert.True(status.IsSuccess);
        Assert.False(status.Value.Configured);
        Assert.Equal(root, status.Value.WorkingCopy);

        var configured = await host.Git.ConfigureAsync(bare, null, CancellationToken.None);
        Assert.True(configured.IsSuccess, configured.IsSuccess ? null : configured.Error.Description);
        Assert.True(configured.Value.Configured);
        Assert.Equal("repository", configured.Value.ConfiguredBy);
        Assert.Equal("main", configured.Value.Branch);

        // The snapshot landed in the working copy and persistence is now file-backed.
        Assert.True(File.Exists(Path.Combine(root, "mappings", $"{stub.Id}.json")));
        Assert.True(switchable.IsActive);

        // Push publishes it; a second (pinned) host pulls and serves the same stub.
        var push = await host.Git.PushAsync("connected from the dashboard", CancellationToken.None);
        Assert.True(push.IsSuccess, push.IsSuccess ? null : push.Error.Description);
        Assert.True(push.Value.Pushed);

        var peer = Compose(NewDir("host-cfg-peer"), bare);
        var pull = await peer.Git.PullAsync(CancellationToken.None);
        Assert.True(pull.IsSuccess);
        Assert.Equal(stub.Id, Assert.Single(peer.Store.GetStubs(TenantId.Default)).Id);
    }

    [Fact]
    public async Task Configure_SurvivesARestart_ViaTheWorkingCopyItself()
    {
        var bare = CreateBareRemote();
        var root = NewDir("host-restart");
        var first = Compose(new GitSyncEnvironment(root, Activatable: new SwitchableStubPersistence()));
        Assert.True((await first.Git.ConfigureAsync(bare, "main", CancellationToken.None)).IsSuccess);

        // A "restarted" service over the same working copy resolves the remote from .git/config.
        var second = Compose(new GitSyncEnvironment(root, Activatable: new SwitchableStubPersistence()));
        var status = await second.Git.StatusAsync(CancellationToken.None);
        Assert.True(status.IsSuccess);
        Assert.True(status.Value.Configured);
        Assert.Equal("repository", status.Value.ConfiguredBy);
        Assert.Equal(bare, status.Value.Remote);
    }

    [Fact]
    public async Task Configure_RefusesOnFlagPinnedAndDbPersistenceHosts()
    {
        var bare = CreateBareRemote();

        var pinned = Compose(NewDir("host-pin"), bare);
        var onPinned = await pinned.Git.ConfigureAsync(bare, null, CancellationToken.None);
        Assert.False(onPinned.IsSuccess);
        Assert.Equal("Git.FlagPinned", onPinned.Error.Code);

        var db = Compose(new GitSyncEnvironment(NewDir("host-db"), PersistenceConflict: true));
        var onDb = await db.Git.ConfigureAsync(bare, null, CancellationToken.None);
        Assert.False(onDb.IsSuccess);
        Assert.Equal("Git.PersistenceConflict", onDb.Error.Code);

        var invalid = Compose(new GitSyncEnvironment(NewDir("host-inv"), Activatable: new SwitchableStubPersistence()));
        Assert.Equal("Git.InvalidRemote", (await invalid.Git.ConfigureAsync("--evil", null, CancellationToken.None)).Error.Code);
        Assert.Equal("Git.InvalidBranch", (await invalid.Git.ConfigureAsync(bare, "-bad branch", CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task UnconfiguredUnpinnedHost_RefusesPushAndPullWithGuidance()
    {
        var host = Compose(new GitSyncEnvironment(NewDir("host-unc"), Activatable: new SwitchableStubPersistence()));
        Assert.Equal("Git.NotConfigured", (await host.Git.PushAsync(null, CancellationToken.None)).Error.Code);
        Assert.Equal("Git.NotConfigured", (await host.Git.PullAsync(CancellationToken.None)).Error.Code);
    }

    [Fact]
    public async Task WithoutTheFlag_StatusSaysUnconfigured_AndMutationsRefuse()
    {
        // The DI default (NotConfiguredGitSync) through the real Mediant handlers.
        var provider = new ServiceCollection().AddMockifyr().BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var status = await sender.Send(new GitStatusQuery());
        Assert.True(status.IsSuccess);
        Assert.False(status.Value.Configured);

        var push = await sender.Send(new GitPushCommand(null));
        Assert.False(push.IsSuccess);
        Assert.Equal("Git.NotConfigured", push.Error.Code);

        var pull = await sender.Send(new GitPullCommand());
        Assert.False(pull.IsSuccess);
        Assert.Equal("Git.NotConfigured", pull.Error.Code);
    }

    [Fact]
    public async Task SetCredentials_ReportsDashboardSource_AndClearReverts()
    {
        var remote = CreateBareRemote();
        var host = Compose(NewDir("creds-host"), remote);

        var initial = await host.Git.StatusAsync(CancellationToken.None);
        Assert.True(initial.IsSuccess);
        Assert.NotEqual("dashboard", initial.Value.CredentialsSource);

        var set = await host.Git.SetCredentialsAsync("bot", "s3cret-token", CancellationToken.None);
        Assert.True(set.IsSuccess);
        Assert.Equal("dashboard", set.Value.CredentialsSource);

        // A local-path remote needs no auth, so operations still succeed with credentials present.
        AddStub(host, StubJson);
        var push = await host.Git.PushAsync("with credentials", CancellationToken.None);
        Assert.True(push.IsSuccess, push.IsSuccess ? null : push.Error.Description);

        var cleared = await host.Git.SetCredentialsAsync(null, "", CancellationToken.None);
        Assert.True(cleared.IsSuccess);
        Assert.NotEqual("dashboard", cleared.Value.CredentialsSource);
    }

    [Fact]
    public async Task AuthFailureMessage_NeverContainsTheDashboardToken()
    {
        // An https remote to a closed port fails fast; the surfaced error must not leak the token.
        var host = Compose(NewDir("scrub-host"), "https://localhost:1/private/repo.git");
        const string token = "super-secret-token-value";
        var set = await host.Git.SetCredentialsAsync(null, token, CancellationToken.None);
        Assert.True(set.IsSuccess);

        AddStub(host, StubJson);
        var push = await host.Git.PushAsync(null, CancellationToken.None);
        Assert.False(push.IsSuccess);
        Assert.DoesNotContain(token, push.Error.Description, StringComparison.Ordinal);
    }
}
