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
}
