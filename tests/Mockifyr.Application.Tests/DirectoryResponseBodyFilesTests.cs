using System.Text;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Coverage for the file store behind <c>bodyFileName</c>. The file name comes from a stub, and a stub
/// can be authored by anyone who can reach the admin API — so most of this is about names that are
/// trying to get out of the directory.
/// </summary>
public sealed class DirectoryResponseBodyFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mockifyr-files-{Guid.NewGuid():N}");
    private readonly string _outside;

    public DirectoryResponseBodyFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "body.json"), """{"ok":true}""");
        File.WriteAllText(Path.Combine(_root, "nested", "deep.json"), """{"deep":true}""");

        // A file the store must never reach, one level above its root.
        _outside = Path.Combine(Path.GetDirectoryName(_root)!, $"mockifyr-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_outside, "TOP SECRET");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (File.Exists(_outside))
        {
            File.Delete(_outside);
        }
    }

    private DirectoryResponseBodyFiles Store() => new(_root);

    [Fact]
    public void A_file_in_the_store_is_read() =>
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(Store().Read("body.json")!));

    [Fact]
    public void A_file_in_a_subdirectory_is_read() =>
        // Mapping sets organise fixtures into folders; a store that only served the top level would
        // reject perfectly ordinary layouts.
        Assert.Equal("""{"deep":true}""", Encoding.UTF8.GetString(Store().Read("nested/deep.json")!));

    [Fact]
    public void An_absent_file_is_a_miss_not_an_exception() => Assert.Null(Store().Read("nope.json"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_a_miss(string name) => Assert.Null(Store().Read(name));

    [Fact]
    public void A_traversing_name_cannot_escape_the_store()
    {
        var store = Store();
        var secret = Path.GetFileName(_outside);

        // Each of these resolves outside the root. Refusing them is the difference between a
        // convenience feature and arbitrary file disclosure on a host anyone can author stubs on.
        Assert.Null(store.Read($"../{secret}"));
        Assert.Null(store.Read($"nested/../../{secret}"));
        Assert.Null(store.Read($"./../{secret}"));
        Assert.Null(store.Read($"nested/../nested/../../{secret}"));
    }

    [Fact]
    public void An_absolute_path_is_refused()
    {
        // Path.Combine returns an absolute second argument verbatim, so an absolute name would
        // otherwise sail past a naive "combine then check" implementation.
        Assert.Null(Store().Read(_outside));
        Assert.Null(Store().Read(Path.Combine(_root, "body.json")));
    }

    [Fact]
    public void A_sibling_directory_with_the_same_prefix_is_refused()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "body.json"), "not ours");

            // The classic off-by-one in a containment check: comparing against the root WITHOUT a
            // trailing separator lets "/data-evil/body.json" pass a StartsWith("/data") test.
            Assert.Null(Store().Read($"../{Path.GetFileName(sibling)}/body.json"));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void A_directory_name_is_a_miss_rather_than_a_crash() => Assert.Null(Store().Read("nested"));

    [Fact]
    public void A_missing_root_directory_is_a_miss_rather_than_a_crash() =>
        Assert.Null(new DirectoryResponseBodyFiles(Path.Combine(Path.GetTempPath(), "does-not-exist-at-all"))
            .Read("body.json"));

    [Fact]
    public void A_root_given_with_a_trailing_separator_still_serves_its_files()
    {
        // The containment check appends a separator to the root before comparing. A root that already
        // ends in one must not end up compared against a doubled separator, or every read is refused
        // — and a trailing slash is exactly how a path arrives from a config file or an env var.
        var store = new DirectoryResponseBodyFiles(_root + Path.DirectorySeparatorChar);

        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(store.Read("body.json")!));
    }

    [Fact]
    public void A_name_the_filesystem_cannot_express_is_a_miss_rather_than_a_crash() =>
        // A NUL byte makes path resolution itself throw, before any existence check. The name comes
        // from a stub, so this reaches the serving path — it has to be a miss, not a 500 from an
        // escaping ArgumentException.
        Assert.Null(Store().Read("bo\0dy.json"));

    [Fact]
    public void An_edited_file_changes_the_next_read()
    {
        var store = Store();
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(store.Read("body.json")!));

        File.WriteAllText(Path.Combine(_root, "body.json"), """{"ok":false}""");

        // Read per request on purpose: iterating on a fixture must not need a reload.
        Assert.Equal("""{"ok":false}""", Encoding.UTF8.GetString(store.Read("body.json")!));
    }
}
