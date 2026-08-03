using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mediant.Results;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Application;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// How the host composed Git sync (ADR 0007, amended for #151):
/// <list type="bullet">
/// <item><see cref="PinnedRemote"/>/<see cref="PinnedBranch"/> carry <c>--git-remote</c>/<c>--git-branch</c>
/// — when set, the configuration is host-pinned and the dashboard's configure endpoint refuses.</item>
/// <item>Unpinned hosts resolve the remote/branch from the working copy's own <c>.git/config</c>,
/// which is exactly where <see cref="GitSyncService.ConfigureAsync"/> writes it — so a dashboard
/// connection needs no extra config store and survives restarts.</item>
/// <item><see cref="Activatable"/> is the pure-in-memory host's persistence switch: connecting
/// snapshots the current stubs into the working copy and flips subsequent mutations to file
/// persistence, making the host behave exactly like a <c>--root-dir</c> host from then on.</item>
/// <item><see cref="PersistenceConflict"/> marks a DB-persistence host without <c>--root-dir</c>:
/// configure refuses with guidance instead of inventing dual-persistence semantics.</item>
/// </list>
/// </summary>
public sealed record GitSyncEnvironment(
    string WorkDir,
    string? PinnedRemote = null,
    string PinnedBranch = "main",
    SwitchableStubPersistence? Activatable = null,
    bool PersistenceConflict = false);

/// <summary>
/// Git sync over the host's working copy (ADR 0007, issues #143/#151). Shells out to the plain
/// <c>git</c> binary so every provider (GitHub, GitLab, Bitbucket, self-hosted) works identically
/// over HTTPS or SSH. Safety properties, in order of importance:
/// <list type="bullet">
/// <item><b>Pull validates before it applies.</b> Every mapping file in the fetched tree is parsed
/// (straight from the Git objects) with the same strict reader the admin API uses; one bad file
/// fails the whole pull and nothing changes — not the working tree, not the served stubs.</item>
/// <item><b>Fast-forward only.</b> Push checks the remote before committing; pull keeps
/// non-overlapping local edits (git's no-clobber guarantee) and refuses overlaps. Divergence is
/// refused with guidance, never auto-resolved.</item>
/// <item><b>Credentials never touch disk, argv, or output.</b> HTTPS tokens are read by an inline
/// credential helper from the host's environment (<c>MOCKIFYR_GIT_TOKEN</c> / optional
/// <c>MOCKIFYR_GIT_USERNAME</c>); surfaced messages are scrubbed of the token value.</item>
/// </list>
/// Process + file I/O, so it lives at the host edge — never in Core.
/// </summary>
public sealed partial class GitSyncService(
    GitSyncEnvironment env,
    IStubStore store,
    IEnumerable<IMappingsLoader> loaders,
    IMatcherRegistry matchers) : IGitSync
{
    private const string TokenVariable = "MOCKIFYR_GIT_TOKEN";
    private const string UsernameVariable = "MOCKIFYR_GIT_USERNAME";
    private const int CommandTimeoutSeconds = 120;

    // Dashboard-provided HTTPS credentials (#153). Process memory only — never written to disk,
    // never echoed back; they take precedence over the host environment variables.
    private volatile string? _dashboardToken;
    private volatile string? _dashboardUsername;

    // Git operations mutate one working copy; serialize them so concurrent admin calls can't interleave.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _pinnedInitialized;

    // Resolved per operation (a dashboard configure can land between calls on unpinned hosts).
    private string? _remote;
    private string _branch = "main";
    private string? _configuredBy;

    private string RootDir => env.WorkDir;

    /// <summary>Validates the host flags up front so a bad configuration fails at startup, not at first use.</summary>
    public static void ValidateConfiguration(string remoteUrl, string branch)
    {
        if (ValidateInput(remoteUrl, branch) is { } error)
        {
            throw new InvalidOperationException($"{error.Code}: {error.Description}");
        }
    }

    private static Error? ValidateInput(string remoteUrl, string branch)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.StartsWith('-'))
        {
            return Error.Validation("Git.InvalidRemote", "The Git remote must be a URL (or local path), not an option.");
        }

        return BranchNamePattern().IsMatch(branch)
            ? null
            : Error.Validation("Git.InvalidBranch", $"'{branch}' is not a valid branch name.");
    }

    /// <inheritdoc />
    public async Task<Result<GitSyncStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await StatusCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The status computation; callers hold the gate.</summary>
    private async Task<Result<GitSyncStatus>> StatusCoreAsync(CancellationToken cancellationToken)
    {
        var (error, configured) = await EnsureReadyAsync(cancellationToken);
        if (error is { } initError)
        {
            return initError;
        }

        if (!configured)
        {
            return Result.Success(new GitSyncStatus(false, null, null, false, 0, 0, null, null, RootDir, CredentialsSource));
        }

        // Best-effort fetch so ahead/behind reflect the remote; a failure (offline, auth) is
        // reported in the status rather than failing it — status must always answer. A remote
        // that simply has no branch yet (brand-new repo, push-first flow) is not an error.
        var fetch = await GitAsync(cancellationToken, "fetch", "--quiet", "origin", _branch);
        var fetchError = fetch.ExitCode == 0 || IsMissingRemoteRef(fetch.StdErr) ? null : Scrub(fetch.StdErr).Trim();

        var dirty = (await GitAsync(cancellationToken, "status", "--porcelain")).StdOut.Length > 0;
        var (ahead, behind) = await AheadBehindAsync(cancellationToken);
        return Result.Success(new GitSyncStatus(
            true, ScrubUserInfo(_remote!), _branch, dirty, ahead, behind, fetchError, _configuredBy, RootDir, CredentialsSource));
    }

    /// <inheritdoc />
    public async Task<Result<GitPushOutcome>> PushAsync(string? message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (error, configured) = await EnsureReadyAsync(cancellationToken);
            if (error is { } initError)
            {
                return initError;
            }

            if (!configured)
            {
                return NotConnected<GitPushOutcome>();
            }

            // Refuse a push the remote would reject BEFORE committing anything: the working copy
            // stays untouched, so the operator can still pull (a committed-then-refused push would
            // leave local and remote diverged with no way out short of raw git tooling).
            var fetch = await GitAsync(cancellationToken, "fetch", "--quiet", "origin", _branch);
            if (fetch.ExitCode != 0 && !IsMissingRemoteRef(fetch.StdErr))
            {
                return Failed(fetch); // a brand-new remote branch is fine; auth/network errors are not
            }

            var (_, behind) = await AheadBehindAsync(cancellationToken);
            if (behind > 0)
            {
                return Error.Conflict("Git.RemoteAhead",
                    $"The remote branch has {behind} commit(s) you don't have — pull first.");
            }

            // Stage everything under the working copy: it IS the mock definition
            // (mappings plus __files/grpc assets referenced by stubs).
            var add = await GitAsync(cancellationToken, "add", "-A");
            if (add.ExitCode != 0)
            {
                return Failed(add);
            }

            var staged = await GitAsync(cancellationToken, "diff", "--cached", "--quiet");
            if (staged.ExitCode != 0) // non-zero = there are staged changes
            {
                var commit = await GitAsync(cancellationToken,
                    "-c", "user.name=Mockifyr", "-c", "user.email=mockifyr@localhost",
                    "commit", "-m", string.IsNullOrWhiteSpace(message) ? "mockifyr: stub changes" : message!);
                if (commit.ExitCode != 0)
                {
                    return Failed(commit);
                }
            }

            var (ahead, _) = await AheadBehindAsync(cancellationToken);

            if (ahead == 0)
            {
                return Result.Success(new GitPushOutcome(false, await HeadShaAsync(cancellationToken), "nothing-to-push"));
            }

            var push = await GitAsync(cancellationToken, "push", "--quiet", "-u", "origin", _branch);
            if (push.ExitCode != 0)
            {
                // Someone pushed between our fetch and this push: same answer as the pre-check.
                return push.StdErr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
                       push.StdErr.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                    ? Error.Conflict("Git.RemoteAhead", "The remote branch moved while pushing — pull first.")
                    : Failed(push);
            }

            return Result.Success(new GitPushOutcome(true, await HeadShaAsync(cancellationToken), "pushed"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result<GitPullOutcome>> PullAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (error, configured) = await EnsureReadyAsync(cancellationToken);
            if (error is { } initError)
            {
                return initError;
            }

            if (!configured)
            {
                return NotConnected<GitPullOutcome>();
            }

            var fetch = await GitAsync(cancellationToken, "fetch", "--quiet", "origin", _branch);
            if (fetch.ExitCode != 0)
            {
                return IsMissingRemoteRef(fetch.StdErr)
                    ? Error.NotFound("Git.RemoteBranchMissing", $"The remote has no branch '{_branch}' yet — push first.")
                    : Failed(fetch);
            }

            var fetched = (await GitAsync(cancellationToken, "rev-parse", "FETCH_HEAD")).StdOut.Trim();
            var head = await HeadShaAsync(cancellationToken);
            if (head is not null && head == fetched)
            {
                return Result.Success(new GitPullOutcome(false, head, StubsServed(), "up-to-date"));
            }

            if (head is not null)
            {
                var ancestor = await GitAsync(cancellationToken, "merge-base", "--is-ancestor", "HEAD", "FETCH_HEAD");
                if (ancestor.ExitCode != 0)
                {
                    return Error.Conflict("Git.Diverged",
                        "Local and remote histories have diverged — resolve in Git tooling, then retry.");
                }
            }

            // The all-or-nothing gate: parse every incoming mapping file straight from the Git
            // objects with the strict admin-path reader. Any failure rejects the pull wholesale.
            var invalid = await ValidateTreeAsync(fetched, cancellationToken);
            if (invalid.Count > 0)
            {
                var listed = string.Join("; ", invalid.Take(20));
                var suffix = invalid.Count > 20 ? $" (+{invalid.Count - 20} more)" : string.Empty;
                return Error.Validation("Git.InvalidMappings",
                    $"The remote tree contains invalid mapping file(s); nothing was applied. {listed}{suffix}");
            }

            // Uncommitted local edits are the working norm (admin mutations write straight to the
            // working copy). A fast-forward merge is attempted anyway: git's own no-clobber guarantee
            // keeps non-overlapping local edits intact and refuses when the update would overwrite a
            // locally modified file — which we surface as an explicit overlap, never a silent loss.
            // The unborn-HEAD first sync has no merge to lean on, so there a dirty tree refuses.
            if (head is null)
            {
                var status = await GitAsync(cancellationToken, "status", "--porcelain");
                if (status.StdOut.Length > 0)
                {
                    return Error.Conflict("Git.DirtyWorkingTree",
                        "First sync into a working copy with local changes would overwrite them — push first.");
                }
            }

            var apply = head is null
                ? await GitAsync(cancellationToken, "reset", "--hard", "FETCH_HEAD")
                : await GitAsync(cancellationToken, "merge", "--ff-only", "FETCH_HEAD");
            if (apply.ExitCode != 0)
            {
                return apply.StdErr.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
                    ? Error.Conflict("Git.LocalOverlap",
                        "Local uncommitted changes overlap files the pull would update — push them first (nothing was applied).")
                    : Failed(apply);
            }

            // Reconcile the live store from the updated working copy: upsert-then-prune per tenant,
            // so there is no window in which a live request misses an existing match.
            ChangeFeedReconciler.ReloadStubs(store, loaders);
            return Result.Success(new GitPullOutcome(true, fetched, StubsServed(), "fast-forwarded"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result<GitSyncStatus>> ConfigureAsync(string remoteUrl, string? branch, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (env.PinnedRemote is not null)
            {
                return Error.Conflict("Git.FlagPinned",
                    "This host's Git configuration is pinned by --git-remote at startup and is read-only here.");
            }

            if (env.PersistenceConflict)
            {
                return Error.Conflict("Git.PersistenceConflict",
                    "This host persists stubs to a database; run it with --root-dir to combine that with Git sync.");
            }

            var targetBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch!.Trim();
            if (ValidateInput(remoteUrl.Trim(), targetBranch) is { } invalid)
            {
                return invalid;
            }

            var version = await GitAsync(cancellationToken, "--version");
            if (version.ExitCode != 0)
            {
                return Error.Failure("Git.BinaryMissing", "The git binary is not available on the host.");
            }

            Directory.CreateDirectory(RootDir);
            if (!Directory.Exists(Path.Combine(RootDir, ".git"))) // no rev-parse: it climbs to parent repos
            {
                var init = await GitAsync(cancellationToken, "init", "-b", targetBranch);
                if (init.ExitCode != 0)
                {
                    return Error.Failure("Git.InitFailed", Scrub(init.StdErr).Trim());
                }
            }

            var current = (await GitAsync(cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")).StdOut.Trim();
            if (current.Length > 0 && current != targetBranch)
            {
                return Error.Conflict("Git.WrongBranch",
                    $"The working copy is on branch '{current}'; connect with that branch or check out '{targetBranch}' first.");
            }

            var setRemote = await SetOriginAsync(remoteUrl.Trim(), cancellationToken);
            if (setRemote is { } remoteError)
            {
                return remoteError;
            }

            // A pure in-memory host becomes file-backed at the moment it connects: the current stubs
            // (every tenant) are snapshotted into the working copy so nothing is lost and the first
            // push publishes them, and subsequent admin mutations persist to files like a --root-dir host.
            if (env.Activatable is { IsActive: false } switchable)
            {
                var persistence = new FileSystemStubPersistence(Path.Combine(RootDir, "mappings"));
                foreach (var tenant in store.GetTenants())
                {
                    foreach (var stub in store.GetStubs(tenant))
                    {
                        if (stub.Source is { } source)
                        {
                            persistence.Save(stub, source);
                        }
                    }
                }

                switchable.Activate(persistence);
            }

            return await StatusCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Result<GitSyncStatus>> SetCredentialsAsync(string? username, string? token, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var trimmed = token?.Trim();
            _dashboardToken = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            _dashboardUsername = string.IsNullOrWhiteSpace(username) ? null : username!.Trim();
            return await StatusCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? EffectiveToken => _dashboardToken ?? NullIfEmpty(Environment.GetEnvironmentVariable(TokenVariable));

    private string? EffectiveUsername => _dashboardUsername ?? NullIfEmpty(Environment.GetEnvironmentVariable(UsernameVariable));

    private string CredentialsSource =>
        _dashboardToken is not null ? "dashboard"
        : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TokenVariable)) ? "environment"
        : "none";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static Error NotConnected<T>() =>
        Error.NotFound("Git.NotConfigured",
            "No Git remote is connected. Configure one in Settings → Git sync, or start the host with --git-remote.");

    /// <summary>
    /// Resolves the working configuration. Pinned hosts initialize the repository from the flags
    /// (idempotent, cached); unpinned hosts read the working copy's own remote/branch — absent
    /// either, the host is simply not configured (no error, no side effects).
    /// </summary>
    private async Task<(Error? Error, bool Configured)> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var version = await GitAsync(cancellationToken, "--version");
        if (version.ExitCode != 0)
        {
            return (Error.Failure("Git.BinaryMissing", "The git binary is not available on the host."), false);
        }

        if (env.PinnedRemote is { } pinned)
        {
            if (!_pinnedInitialized)
            {
                Directory.CreateDirectory(RootDir);
                if (!Directory.Exists(Path.Combine(RootDir, ".git"))) // no rev-parse: it climbs to parent repos
                {
                    var init = await GitAsync(cancellationToken, "init", "-b", env.PinnedBranch);
                    if (init.ExitCode != 0)
                    {
                        return (Error.Failure("Git.InitFailed", Scrub(init.StdErr).Trim()), false);
                    }
                }

                var current = (await GitAsync(cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")).StdOut.Trim();
                if (current.Length > 0 && current != env.PinnedBranch)
                {
                    return (Error.Conflict("Git.WrongBranch",
                        $"The root-dir repository is on branch '{current}' but --git-branch is '{env.PinnedBranch}'. Check out the configured branch (or change the flag)."), false);
                }

                if (await SetOriginAsync(pinned, cancellationToken) is { } remoteError)
                {
                    return (remoteError, false);
                }

                _pinnedInitialized = true;
            }

            (_remote, _branch, _configuredBy) = (pinned, env.PinnedBranch, "flags");
            return (null, true);
        }

        // Unpinned: the working copy's .git/config is the single source of truth. The check is a
        // direct .git-directory test, NOT `rev-parse` — which climbs to a parent repository and, when
        // the working copy nests inside one (e.g. <cwd>/mockifyr-data under a project checkout),
        // would wrongly adopt (and later mutate!) that parent repo.
        if (!Directory.Exists(Path.Combine(RootDir, ".git")))
        {
            return (null, false);
        }

        var origin = await GitAsync(cancellationToken, "remote", "get-url", "origin");
        if (origin.ExitCode != 0)
        {
            return (null, false);
        }

        var branch = (await GitAsync(cancellationToken, "symbolic-ref", "--short", "-q", "HEAD")).StdOut.Trim();
        (_remote, _branch, _configuredBy) = (origin.StdOut.Trim(), branch.Length > 0 ? branch : "main", "repository");
        return (null, true);
    }

    private async Task<Error?> SetOriginAsync(string url, CancellationToken cancellationToken)
    {
        var origin = await GitAsync(cancellationToken, "remote", "get-url", "origin");
        var setRemote = origin.ExitCode != 0
            ? await GitAsync(cancellationToken, "remote", "add", "origin", url)
            : origin.StdOut.Trim() == url
                ? origin
                : await GitAsync(cancellationToken, "remote", "set-url", "origin", url);
        return setRemote.ExitCode == 0 ? null : Error.Failure("Git.RemoteFailed", Scrub(setRemote.StdErr).Trim());
    }

    /// <summary>Parses every <c>mappings/**/*.json</c> blob in the given tree; returns per-file failures.</summary>
    private async Task<IReadOnlyList<string>> ValidateTreeAsync(string treeIsh, CancellationToken cancellationToken)
    {
        var listing = await GitAsync(cancellationToken, "ls-tree", "-r", "--name-only", treeIsh, "--", "mappings");
        var failures = new List<string>();
        foreach (var path in listing.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var blob = await GitAsync(cancellationToken, "show", $"{treeIsh}:{path}");
            if (blob.ExitCode != 0)
            {
                failures.Add($"{path}: unreadable");
                continue;
            }

            // mappings/<file>.json = default tenant; mappings/<tenant>/<file>.json = that tenant —
            // mirroring FileSystemStubPersistence's layout.
            var segments = path.Split('/');
            var tenant = segments.Length > 2 ? new TenantId(segments[1]) : TenantId.Default;
            try
            {
                MappingJsonReader.Read(blob.StdOut, tenant, matchers);
            }
            catch (Exception ex)
            {
                failures.Add($"{path}: {ex.Message}");
            }
        }

        return failures;
    }

    private async Task<(int Ahead, int Behind)> AheadBehindAsync(CancellationToken cancellationToken)
    {
        var remoteRef = await GitAsync(cancellationToken, "rev-parse", "--verify", "-q", $"refs/remotes/origin/{_branch}");
        var head = await HeadShaAsync(cancellationToken);
        if (remoteRef.ExitCode != 0)
        {
            // No remote branch yet: everything local is "ahead", nothing to be behind of.
            if (head is null)
            {
                return (0, 0);
            }

            var local = await GitAsync(cancellationToken, "rev-list", "--count", "HEAD");
            return (int.TryParse(local.StdOut.Trim(), out var count) ? count : 0, 0);
        }

        if (head is null)
        {
            var remoteOnly = await GitAsync(cancellationToken, "rev-list", "--count", $"origin/{_branch}");
            return (0, int.TryParse(remoteOnly.StdOut.Trim(), out var count) ? count : 0);
        }

        var counts = await GitAsync(cancellationToken, "rev-list", "--left-right", "--count", $"origin/{_branch}...HEAD");
        var parts = counts.StdOut.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var behind) && int.TryParse(parts[1], out var ahead)
            ? (ahead, behind)
            : (0, 0);
    }

    private async Task<string?> HeadShaAsync(CancellationToken cancellationToken)
    {
        var head = await GitAsync(cancellationToken, "rev-parse", "--verify", "-q", "HEAD");
        return head.ExitCode == 0 ? head.StdOut.Trim() : null;
    }

    private int StubsServed() => store.GetTenants().Sum(tenant => store.GetStubs(tenant).Count);

    private Error Failed(GitResult result)
    {
        var detail = Scrub(result.StdErr.Length > 0 ? result.StdErr : result.StdOut).Trim();
        return IsAuthFailure(detail)
            ? Error.Unauthorized("Git.Auth",
                $"The remote rejected the credentials. Set an HTTPS access token under Settings → Git sync (or {TokenVariable} / an SSH key on the host). Detail: {detail}")
            : Error.Failure("Git.Failed", detail.Length > 0 ? detail : "git command failed.");
    }

    private static bool IsAuthFailure(string detail) =>
        detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingRemoteRef(string stdErr) =>
        stdErr.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the token value (env or dashboard-set, plus any URL userinfo) from text before it can be surfaced.</summary>
    private string Scrub(string text)
    {
        foreach (var token in (string?[])[Environment.GetEnvironmentVariable(TokenVariable), _dashboardToken])
        {
            if (!string.IsNullOrEmpty(token))
            {
                text = text.Replace(token, "***");
            }
        }

        return UserInfoPattern().Replace(text, "$1***@");
    }

    private static string ScrubUserInfo(string url) => UserInfoPattern().Replace(url, "$1***@");

    private sealed record GitResult(int ExitCode, string StdOut, string StdErr);

    /// <summary>
    /// Runs one git command in the working copy. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>
    /// (no shell), prompts are disabled, and — when a token is present in the environment — an inline
    /// credential helper reads it at git's runtime so it never appears in argv.
    /// </summary>
    private async Task<GitResult> GitAsync(CancellationToken cancellationToken, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Directory.Exists(RootDir) ? RootDir : Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        if (EffectiveToken is { } effectiveToken)
        {
            // Dashboard-set credentials win over the host environment; both reach git the same way —
            // through the child process environment, read by an inline credential helper, so the
            // secret never enters argv. The first -c clears any configured helpers (OS keychains
            // could prompt or interfere).
            info.Environment[TokenVariable] = effectiveToken;
            if (EffectiveUsername is { } effectiveUsername)
            {
                info.Environment[UsernameVariable] = effectiveUsername;
            }

            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("credential.helper=");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(
                "credential.helper=!f() { echo \"username=${MOCKIFYR_GIT_USERNAME:-mockifyr}\"; echo \"password=${MOCKIFYR_GIT_TOKEN}\"; }; f");
        }

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new GitResult(127, string.Empty, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(CommandTimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new GitResult(124, stdOut.ToString(), $"git {args.FirstOrDefault()} timed out after {CommandTimeoutSeconds}s.");
        }

        return new GitResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex BranchNamePattern();

    [GeneratedRegex(@"(://)[^@/\s]+@")]
    private static partial Regex UserInfoPattern();
}
