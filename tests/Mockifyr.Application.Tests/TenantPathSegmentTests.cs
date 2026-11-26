using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// #467, verifier on PR #483: a tenant name is caller-supplied, so the file providers never use it as a path. The class is
/// "a tenant value that is not one safe path segment" — a separator, a climb, a root, a name Windows refuses or strips,
/// a character outside ASCII, the escape character itself — and the rule is an allow-list with a reversible escape for
/// everything else: every member below round-trips, lands as one direct child, and a directory name the encoder would
/// not have written is never read as a tenant.
/// </summary>
public sealed class TenantPathSegmentTests : IDisposable
{
    private static readonly string[] Names =
    [
        "a/b", "a\\b", "..", ".", "...", "../x", "..\\x", "/tmp/x", "C:\\x", "C:", "Acme Corp", "\u0130stanbul", "a%b", "%41",
        "trailing.", "trailing ", "nul\u0000byte", "tab\tname", "a*b?c\"d<e>f|g", "\U0001F600", "default-ish", "acme",
        "\u00130", ":3", "%C4%B0", // names this release writes as an earlier writer's spelling of another tenant (PR #483, round 3)
    ];

    public static TheoryData<string> Hostile => [.. Names];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mockifyr-segment-{Guid.NewGuid():N}");

    public TenantPathSegmentTests() => Directory.CreateDirectory(Path.Combine(_root, "mappings"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Every_tenant_name_is_one_reversible_segment(string name)
    {
        var segment = TenantPathSegment.Encode(new TenantId(name));

        Assert.DoesNotContain('/', segment);
        Assert.DoesNotContain('\\', segment);
        Assert.DoesNotContain(segment, Path.GetInvalidFileNameChars().Select(c => c.ToString()));
        Assert.NotEqual("", segment.Trim('.'));
        Assert.False(Path.IsPathRooted(segment));
        Assert.Equal(-1, segment.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.True(TenantPathSegment.TryDecode(segment, out var back));
        Assert.Equal(name, back.Value);
    }

    [Fact]
    public void A_name_that_was_already_a_safe_segment_keeps_its_directory()
    {
        foreach (var name in new[] { "acme", "Acme Corp", "other-team", "team_1.v2", "a(b)" })
        {
            Assert.Equal(name, TenantPathSegment.Encode(new TenantId(name)));
        }
    }

    [Theory]
    [InlineData("a%2fb")]     // a lower-case escape: not what the encoder writes
    [InlineData("%")]         // a truncated escape
    [InlineData("a%4")]
    [InlineData("%G0")]
    [InlineData("%41")]       // "A" escaped: a second spelling of a name the encoder keeps
    [InlineData("%C3")]       // not UTF-8
    [InlineData("a:b")]       // a character outside the allow-list, unescaped
    [InlineData("caf\u00E9")] // non-ASCII, unescaped
    [InlineData("trailing.")] // the encoder escapes a trailing dot
    [InlineData("..")]
    [InlineData("")]
    [InlineData("\u0141")]   // a raw non-ASCII character whose low byte is "A"
    [InlineData("a\u007Fb")]  // a raw control character: the encoder escapes it
    public void A_directory_name_the_encoder_would_not_write_is_not_a_tenant(string segment) =>
        Assert.False(TenantPathSegment.TryDecode(segment, out _));

    [Fact]
    public void An_empty_or_ill_formed_name_has_no_segment()
    {
        Assert.Contains("empty name", Assert.Throws<ArgumentException>(() => TenantPathSegment.Encode(new TenantId(""))).Message,
            StringComparison.Ordinal);

        // A lone surrogate wherever it stands — mid-name, at the end, after an escaped character, or a low one alone.
        foreach (var name in new[] { "a\uD800b", "a\uD800", "a/\uDC00", "\uDC00x" })
        {
            Assert.Contains("not well-formed",
                Assert.Throws<ArgumentException>(() => TenantPathSegment.Encode(new TenantId(name))).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The verifier's probe, on the provider and its loader: each hostile tenant's stub is written as a direct child of
    /// <c>mappings/</c> — nothing outside it, nothing nested — and comes back as that tenant on reload.
    /// </summary>
    [Fact]
    public void Every_hostile_tenant_stub_stays_inside_mappings_and_is_reloaded_as_its_tenant()
    {
        var mappings = Path.Combine(_root, "mappings");
        var persistence = new FileSystemStubPersistence(mappings);
        var expected = new List<(string, Guid)>();
        foreach (var name in Names)
        {
            var id = Guid.NewGuid();
            const string json = """{"request":{"method":"GET","url":"/x"},"response":{"status":200}}""";
            var stub = Mockifyr.Adapters.MappingJson.MappingJsonReader.Read(json, new TenantId(name)).Single() with { Id = id };
            persistence.Save(stub, json);
            expected.Add((name, id));
        }

        Assert.Equal([Path.GetFullPath(mappings)], Directory.GetDirectories(_root).Select(Path.GetFullPath));
        Assert.All(Directory.GetDirectories(mappings), directory => Assert.Empty(Directory.GetDirectories(directory)));

        var loader = new DirectoryMappingsLoader(mappings);
        Assert.Equal(
            expected.OrderBy(pair => pair.Item1, StringComparer.Ordinal),
            loader.LoadAllTenants().Select(stub => (stub.TenantId.Value, stub.Id)).OrderBy(pair => pair.Item1, StringComparer.Ordinal));
        Assert.Empty(loader.SkippedDirectories);

        persistence.Clear(new TenantId("../x"));
        Assert.Equal(Names.Length - 1, loader.LoadAllTenants().Count);
    }

    /// <summary>The same class on the tenant declarations: each one's file is a direct child, and every one reloads and removes.</summary>
    [Fact]
    public void Every_hostile_tenant_declaration_is_a_file_inside_its_directory()
    {
        var tenants = Path.Combine(_root, "tenants");
        var persistence = new FileSystemTenantPersistence(tenants);
        foreach (var name in Names)
        {
            persistence.Save(new TenantRecord(new TenantId(name), name, DateTimeOffset.UnixEpoch));
        }

        Assert.Equal(Names.Length, Directory.GetFiles(tenants).Length);
        Assert.Empty(Directory.GetDirectories(tenants));
        Assert.Equal(Names.Order(StringComparer.Ordinal), persistence.LoadAll().Select(t => t.Id.Value).Order(StringComparer.Ordinal));

        persistence.Remove(new TenantId("../x"));
        Assert.DoesNotContain(persistence.LoadAll(), t => t.Id.Value == "../x");
        Assert.Equal(Names.Length - 1, persistence.LoadAll().Count);
    }

    /// <summary>
    /// The same class on the environments provider, where <c>Clear</c> deletes recursively: a tenant named <c>..</c> must
    /// clear its own directory, not the root it would otherwise climb to; and every hostile tenant reloads as itself.
    /// </summary>
    [Fact]
    public void Clearing_a_climbing_tenants_environment_deletes_only_its_own_directory()
    {
        var environments = Path.Combine(_root, "environments");
        var persistence = new FileSystemEnvironmentPersistence(environments);
        foreach (var name in Names)
        {
            persistence.Save(new TenantId(name), new EnvironmentKey("token", "main", [new EnvironmentValue("main", "v-" + name)]));
        }

        persistence.Clear(new TenantId(".."));

        Assert.True(Directory.Exists(Path.Combine(_root, "mappings")));
        var loaded = new FileSystemEnvironmentsLoader(environments).LoadAll();
        Assert.Equal(Names.Length - 1, loaded.Count);
        Assert.All(loaded, pair => Assert.Equal("v-" + pair.Key.Value, pair.Value.Single().Resolve()));
        Assert.DoesNotContain(new TenantId(".."), loaded.Keys);
    }

    /// <summary>
    /// The resources provider's writer before #467 escaped per UTF-16 code unit, which is reversible only for printable
    /// ASCII. Every printable-ASCII name that writer could have produced reads back as itself (unless this release writes
    /// the same segment, which then wins and means the same tenant); anything it could not have produced, or that is
    /// ambiguous, is refused rather than guessed.
    /// </summary>
    [Theory]
    [InlineData("Acme Corp", "Acme%20Corp")]
    [InlineData("..", "%2E%2E")]
    [InlineData("a b*c", "a%20b%2Ac")]
    [InlineData("x(y)", "x%28y%29")]
    [InlineData("a b.c", "a%20b.c")]     // and kept a dot anywhere else
    [InlineData("a b-c_d", "a%20b-c_d")] // it kept '-' and '_'
    [InlineData("a~b", "a%7Eb")]         // '~' is the last printable ASCII character
    [InlineData("plain", "plain")]       // and the names both writers spell alike read as the same tenant
    [InlineData("a*b", "a%2Ab")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData(".", "%2E")]
    [InlineData("a%b", "a%25b")]
    public void An_earlier_resource_directory_reads_as_its_tenant(string tenant, string earlier)
    {
        Assert.Equal(earlier, EarlierSafeName(tenant));
        Assert.True(TenantPathSegment.TryDecodeEarlierResourceName(earlier, out var back));
        Assert.Equal(tenant, back.Value);
    }

    /// <summary>
    /// PR #483, round 3: the earlier resources writer escaped a code unit above <c>0xFF</c> with three or four hexadecimal
    /// digits, so wherever an escape is followed by a digit or <c>A</c>–<c>F</c> its name could mean more than one tenant.
    /// Every such name, written here by that writer's own rule, is refused — the Turkish letters, the rest of Latin
    /// Extended, Greek, four-digit code units — and so is a one-byte code unit outside printable ASCII.
    /// </summary>
    [Theory]
    [InlineData("\u0131")]          // ı  → %131
    [InlineData("\u0130stanbul")]   // İ  → %130stanbul
    [InlineData("\u015E")]          // Ş  → %15E
    [InlineData("\u011F")]          // ğ  → %11F
    [InlineData("\u0220")]          // Ƞ  → %220, which this release reads as "0
    [InlineData("\u03A3")]          // Σ  → %3A3, which this release reads as :3
    [InlineData("\u03C0")]          // π  → %3C0
    [InlineData("\u1E9E")]          // ẞ  → %1E9E
    [InlineData("\u7F00")]          // a CJK ideograph → %7F00
    [InlineData("caf\u00E9")]       // é  → %E9: one byte, not printable ASCII
    [InlineData("\U0001F600")]      // a surrogate pair → %D83D%DE00
    public void An_earlier_resource_name_that_could_mean_another_tenant_is_refused(string tenant)
    {
        var earlier = EarlierSafeName(tenant);
        Assert.False(TenantPathSegment.TryDecodeEarlierResourceName(earlier, out _));
    }

    /// <summary>
    /// The one ambiguity the earlier resources reading accepts, pinned so the documentation's statement of it stays true:
    /// after a space, <c>Acme%20Corp</c> is <c>Acme Corp</c>, so U+0200–U+020F and U+2000–U+20FF are read as a space and
    /// hexadecimal digits.
    /// </summary>
    [Theory]
    [InlineData("\u0200", " 0")]
    [InlineData("x\u020C", "x C")]
    [InlineData("\u20AC", " AC")]
    public void The_accepted_ambiguity_after_a_space_reads_as_the_space(string tenant, string readAs)
    {
        Assert.True(TenantPathSegment.TryDecodeEarlierResourceName(EarlierSafeName(tenant), out var back));
        Assert.Equal(readAs, back.Value);
    }

    [Theory]
    [InlineData("%130")]      // U+0130 as the earlier writer spelled it: also reads as U+0013 and "0", so refused
    [InlineData("%E9")]       // a Latin-1 code unit: not printable ASCII
    [InlineData("a%2fb")]     // lower-case: the earlier writer used upper-case
    [InlineData("a%7F")]      // DEL, just past '~': not printable
    [InlineData("a%2")]       // truncated
    [InlineData("a b")]       // an unescaped space: not that writer's output
    [InlineData("%41")]       // "A" escaped: that writer kept letters
    [InlineData("%2E%2E0")]   // an escape followed by a digit: the second could be U+2E0
    [InlineData("%3A3")]      // followed by a digit: U+03A3 (Σ) or ":3"
    [InlineData("%281")]      // U+0281 or "(1"
    [InlineData("%C4%B0")]    // this release's İ: the earlier writer's Ä° is not printable ASCII
    [InlineData("")]
    public void A_name_the_earlier_resource_writer_did_not_produce_is_refused(string segment) =>
        Assert.False(TenantPathSegment.TryDecodeEarlierResourceName(segment, out _));

    [Theory]
    [InlineData("a*b")]
    [InlineData("x:y")]
    [InlineData("a%41")]
    [InlineData("caf\u00E9")]
    [InlineData("plain")]     // the same name in both releases
    [InlineData("Acme Corp")]
    [InlineData("%C4%B0")]    // an earlier tenant literally named so: unmarked, it is not this release's İ
    public void An_earlier_raw_directory_reads_as_its_tenant(string segment)
    {
        Assert.True(TenantPathSegment.TryDecodeEarlierRawName(segment, out var back));
        Assert.Equal(segment, back.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/tmp")]
    [InlineData("\\b")]      // a separator first: not rooted on Linux, still not one segment
    public void A_raw_name_that_is_not_one_earlier_directory_is_refused(string segment) =>
        Assert.False(TenantPathSegment.TryDecodeEarlierRawName(segment, out _));

    /// <summary>
    /// An earlier directory whose tenant already has this release's directory beside it is left alone: merging two
    /// directories silently could resurrect what one of them deleted, so the operator is told and decides.
    /// </summary>
    [Fact]
    public void An_earlier_directory_beside_this_releases_one_is_skipped_not_merged()
    {
        var resources = Path.Combine(_root, "resources");
        var persistence = new FileSystemResourcePersistence(resources);
        var document = new ResourceDocument("1", "orders", "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
        persistence.Save(new TenantId("Acme Corp"), document);
        Directory.CreateDirectory(Path.Combine(resources, "Acme%20Corp", "orders"));
        File.WriteAllText(Path.Combine(resources, "Acme%20Corp", "orders", "2.json"), File.ReadAllText(Path.Combine(resources, "Acme Corp", "orders", "1.json")).Replace("\"1\"", "\"2\""));

        var loaded = new FileSystemResourcesLoader(resources).LoadAll();

        Assert.Equal("1", Assert.Single(Assert.Single(loaded).Value).Id);
        Assert.True(Directory.Exists(Path.Combine(resources, "Acme%20Corp")));
    }

    /// <summary>
    /// The sweep for a provider nobody has named yet (#467): no path in the shipped source is built from a tenant except
    /// through <see cref="TenantPathSegment.Encode"/>. The resources provider was the member a reading of the other three
    /// missed, because it had an encoder of its own; a fifth would be missed the same way.
    /// </summary>
    [Fact]
    public void No_path_in_the_source_is_built_from_a_tenant_but_through_the_one_encoder()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Mockifyr.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(row => (row.line.Contains("Path.Combine(", StringComparison.Ordinal) || row.line.Contains("Path.Join(", StringComparison.Ordinal))
                && row.line.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                && !row.line.Contains("TenantPathSegment.Encode(", StringComparison.Ordinal)
                && !row.line.Contains("\"tenants\"", StringComparison.Ordinal))
            .Select(row => $"{Path.GetRelativePath(root, row.file)}:{row.index + 1}: {row.line.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_directory_is_read_in_place_renamed_or_skipped_and_the_notice_says_which()
    {
        var area = Path.Combine(_root, "environments");
        Directory.CreateDirectory(Path.Combine(area, "plain"));
        Directory.CreateDirectory(Path.Combine(area, "a*b"));
        Directory.CreateDirectory(Path.Combine(area, "a%2fb"));

        Assert.True(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "plain"), "environments", TenantPathSegment.TryDecodeEarlierRawName,
            out var notice, out var tenant, out var path));
        Assert.Equal(("", "plain", Path.Combine(area, "plain")), (notice, tenant.Value, path));

        Assert.True(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "a*b"), "environments", TenantPathSegment.TryDecodeEarlierRawName,
            out notice, out tenant, out path));
        Assert.Equal(("environments/a*b: written by an earlier release for tenant 'a*b' — renamed to environments/a%2Ab.", "a*b", Path.Combine(area, "a%2Ab")),
            (notice, tenant.Value, path));
        Assert.False(Directory.Exists(Path.Combine(area, "a*b")));

        // A raw name of an earlier release beside this release's directory for the same tenant: not merged.
        Directory.CreateDirectory(Path.Combine(area, "a*b"));
        Assert.False(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "a*b"), "environments", TenantPathSegment.TryDecodeEarlierRawName,
            out notice, out tenant, out _));
        Assert.Equal("environments/a*b: written by an earlier release for tenant 'a*b', which also has environments/a%2Ab — "
            + "skipped, not merged; move what it holds into the second directory to keep it.", notice);
        Assert.Equal(default, tenant);
        Assert.True(Directory.Exists(Path.Combine(area, "a*b")));

        // A name no writer produced: skipped.
        Assert.False(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "a%2fb"), "resources", TenantPathSegment.TryDecodeEarlierResourceName,
            out notice, out tenant, out _));
        Assert.Equal("resources/a%2fb: it has no .mockifyr-tenant, so an earlier release wrote it, and its name does not name exactly "
            + "one tenant by that release's rule — skipped, not guessed; to keep it, rename it to its tenant's name as this release "
            + "writes it (docs/parity/g16-persistence.md) and put an empty .mockifyr-tenant in it.", notice);
        Assert.Equal(default, tenant);

        // A directory that vanished between the listing and the rename (another process removed it): skipped, not thrown.
        Assert.False(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "x:y"), "environments", TenantPathSegment.TryDecodeEarlierRawName,
            out notice, out tenant, out _));
        Assert.Equal("environments/x:y: written by an earlier release for tenant 'x:y', and renaming it to environments/x%3Ay failed "
            + "(DirectoryNotFoundException) — skipped; rename it by hand to keep it.", notice);
        Assert.Equal(default, tenant);
    }

    /// <summary>
    /// PR #483, round 3, on the providers themselves: a resources directory an earlier release wrote for a tenant whose
    /// old name is ambiguous is skipped with the line that names it — never read in place under a tenant nobody can
    /// address — and is left where it is. The documents of the same names written by THIS release reload as their own
    /// tenants, because this release marks what it writes.
    /// </summary>
    [Fact]
    public void An_ambiguous_earlier_resources_directory_is_skipped_and_this_releases_ghost_names_reload()
    {
        var resources = Path.Combine(_root, "resources");
        var earlierTenants = new[] { "\u0131", "\u0130stanbul", "\u015E", "\u011F", "\u0220", "\u03A3", "\u1E9E", "caf\u00E9", "\U0001F600" };
        var persistence = new FileSystemResourcePersistence(resources);
        var document = new ResourceDocument("1", "orders", "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
        persistence.Save(new TenantId("seed"), document);
        var json = File.ReadAllText(Path.Combine(resources, "seed", "orders", "1.json"));
        Directory.Delete(Path.Combine(resources, "seed"), recursive: true);
        foreach (var tenant in earlierTenants)
        {
            var directory = Path.Combine(resources, EarlierSafeName(tenant), "orders");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "1.json"), json);
        }

        // This release's tenants spelled the way the earlier writer spelled a letter above 0xFF: "\u00135" is %135 (Ĵ then),
        // ":7" is %3A7 (Χ), "\"9" is %229 (ȩ).
        var ghosts = new[] { "\u00135", ":7", "\"9" };
        foreach (var tenant in ghosts)
        {
            persistence.Save(new TenantId(tenant), document);
        }

        var loaded = new FileSystemResourcesLoader(resources).LoadAll();

        Assert.Equal(ghosts.Order(StringComparer.Ordinal), loaded.Keys.Select(t => t.Value).Order(StringComparer.Ordinal));
        foreach (var tenant in earlierTenants)
        {
            Assert.True(Directory.Exists(Path.Combine(resources, EarlierSafeName(tenant))), tenant);
        }
    }

    /// <summary>The line an ambiguous earlier directory prints names it and says how to keep it; a marked one is never re-guessed.</summary>
    [Fact]
    public void An_unmarked_ambiguous_directory_and_a_marked_foreign_name_each_print_their_line()
    {
        var area = Path.Combine(_root, "resources");
        Directory.CreateDirectory(Path.Combine(area, "%130stanbul"));
        Assert.False(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "%130stanbul"), "resources",
            TenantPathSegment.TryDecodeEarlierResourceName, out var notice, out var tenant, out _));
        Assert.Equal("resources/%130stanbul: it has no .mockifyr-tenant, so an earlier release wrote it, and its name does not name exactly "
            + "one tenant by that release's rule — skipped, not guessed; to keep it, rename it to its tenant's name as this release "
            + "writes it (docs/parity/g16-persistence.md) and put an empty .mockifyr-tenant in it.", notice);
        Assert.Equal(default, tenant);
        Assert.True(Directory.Exists(Path.Combine(area, "%130stanbul")));

        // A marked directory renamed by hand to a name this release would not write is not read as a tenant either.
        TenantPathSegment.CreateTenantDirectory(Path.Combine(area, "a%2fb"));
        Assert.False(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "a%2fb"), "resources",
            TenantPathSegment.TryDecodeEarlierResourceName, out notice, out tenant, out _));
        Assert.Equal("resources/a%2fb: it carries .mockifyr-tenant, but its name is not one this provider writes for a tenant — skipped, "
            + "not read as a tenant.", notice);
        Assert.Equal(default, tenant);

        // A marked %130 is this release's tenant U+0013 "0"; an unmarked one with the same name is the ambiguous one above.
        TenantPathSegment.CreateTenantDirectory(Path.Combine(area, "%130"));
        Assert.True(TenantPathSegment.TryResolveDirectory(Path.Combine(area, "%130"), "resources",
            TenantPathSegment.TryDecodeEarlierResourceName, out notice, out tenant, out _));
        Assert.Equal(("", "\u00130"), (notice, tenant.Value));
    }

    /// <summary>
    /// The same class on the raw-name providers: an earlier tenant literally named <c>%C4%B0</c> is that tenant, renamed to
    /// this release's <c>%25C4%25B0</c> — not this release's <c>İ</c>, which it would have been read as before the marker.
    /// </summary>
    [Fact]
    public void An_earlier_raw_tenant_spelled_like_an_escape_is_read_as_itself_in_environments_and_mappings()
    {
        var environments = Path.Combine(_root, "environments");
        var persistence = new FileSystemEnvironmentPersistence(environments);
        persistence.Save(new TenantId("seed"), new EnvironmentKey("token", "main", [new EnvironmentValue("main", "v")]));
        Directory.Move(Path.Combine(environments, "seed"), Path.Combine(environments, "%C4%B0"));
        File.Delete(Path.Combine(environments, "%C4%B0", TenantPathSegment.MarkerFileName));

        var loaded = new FileSystemEnvironmentsLoader(environments).LoadAll();

        Assert.Equal("%C4%B0", Assert.Single(loaded).Key.Value);
        Assert.True(File.Exists(Path.Combine(environments, "%25C4%25B0", TenantPathSegment.MarkerFileName)));
        Assert.Equal("%C4%B0", Assert.Single(new FileSystemEnvironmentsLoader(environments).LoadAll()).Key.Value);

        var mappings = Path.Combine(_root, "mappings");
        var id = Guid.NewGuid();
        const string stubJson = """{"request":{"method":"GET","url":"/x"},"response":{"status":200}}""";
        Directory.CreateDirectory(Path.Combine(mappings, "%C4%B0"));
        File.WriteAllText(Path.Combine(mappings, "%C4%B0", $"{id:D}.json"), stubJson.Insert(1, $"\"id\":\"{id:D}\","));

        var loader = new DirectoryMappingsLoader(mappings);
        Assert.Equal(("%C4%B0", id), Assert.Single(loader.LoadAllTenants()) is var stub ? (stub.TenantId.Value, stub.Id) : default);
        Assert.Empty(loader.SkippedDirectories);
        Assert.True(Directory.Exists(Path.Combine(mappings, "%25C4%25B0")));
    }

    // The resources provider's tenant directory name before #467 (ResourceJson.SafeName as it was on main).
    private static string EarlierSafeName(string value)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var safe = builder.ToString();
        return safe is "." or ".." ? safe.Replace(".", "%2E", StringComparison.Ordinal) : safe;
    }
}
