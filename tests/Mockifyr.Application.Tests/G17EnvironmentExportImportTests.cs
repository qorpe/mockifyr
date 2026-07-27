using System.Text;
using Mediant.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Behavioral coverage for environments riding along in export/import bundles (issue #198). The
/// bundle format is a Mockifyr extension of the mapping wrapper — WireMock has no environments
/// concept, so as with the rest of G17 there is no oracle to diff against and this is a self-test.
/// The claims under test:
/// <list type="bullet">
/// <item>a wrapper's <c>environments</c> section is restored — keys, values, and the active
/// selection — so the imported stubs' <c>{{key}}</c> references resolve from the same bundle;</item>
/// <item>older exports (bare array, wrapper without the section) import unchanged;</item>
/// <item>an imported key replaces an existing key of the same name (an import restores the
/// exported state), while an invalid entry is skipped without failing the import;</item>
/// <item>imported keys land in the importing tenant only.</item>
/// </list>
/// </summary>
public sealed class G17EnvironmentExportImportTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");

    private static ServiceProvider Host() => new ServiceCollection().AddMockifyr().BuildServiceProvider();

    // Concatenated rather than interpolated: the bodies under test are full of {{…}}, which fights
    // every raw-string interpolation form.
    private static string Mapping(string body) =>
        """{"request":{"method":"GET","urlPath":"/x"},"response":{"status":200,"body":" """.TrimEnd()
        + body + """ "}}""".TrimStart();

    private static string Bundle(string body, string environments) =>
        """{"mappings":[""" + Mapping(body) + """],"environments":""" + environments + "}";

    private const string BaseUrlSection =
        """[{"key":"baseUrl","activeValue":"dev","values":[{"name":"dev","value":"https://dev.example.com"},{"name":"prod","value":"https://api.example.com"}]}]""";

    private static string Serve(ServiceProvider provider, TenantId tenant)
    {
        var engine = provider.GetRequiredService<StubEngine>();
        var result = engine.Handle(tenant, CanonicalRequestBuilder.Build("GET", "/x", [], []));
        return Encoding.UTF8.GetString(result.Response!.Body);
    }

    private static async Task<IReadOnlyList<EnvironmentKey>> Keys(ISender sender, TenantId tenant) =>
        (await sender.Send(new GetEnvironmentsQuery(tenant))).Value;

    [Fact]
    public async Task Import_restores_keys_values_and_active_selection_alongside_the_mappings()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        var import = await sender.Send(new ImportMappingsCommand(Bundle("{{baseUrl}}/users", BaseUrlSection), Acme));
        Assert.True(import.IsSuccess);
        Assert.Equal(1, import.Value);

        var key = Assert.Single(await Keys(sender, Acme));
        Assert.Equal("baseUrl", key.Key);
        Assert.Equal("dev", key.ActiveValue);
        Assert.Equal(["dev", "prod"], key.Values.Select(v => v.Name));

        // The stub and the key it references arrived in one bundle — the reference resolves.
        Assert.Equal("https://dev.example.com/users", Serve(provider, Acme));
    }

    [Fact]
    public async Task Bare_array_and_wrapper_without_the_section_import_unchanged()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        var bare = await sender.Send(new ImportMappingsCommand("[" + Mapping("plain") + "]", Acme));
        Assert.True(bare.IsSuccess);

        var wrapper = await sender.Send(new ImportMappingsCommand("""{"mappings":[""" + Mapping("plain") + "]}", Acme));
        Assert.True(wrapper.IsSuccess);

        Assert.Empty(await Keys(sender, Acme));
    }

    [Fact]
    public async Task Imported_key_replaces_an_existing_key_of_the_same_name()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        Assert.True((await sender.Send(new PutEnvironmentKeyCommand(
            new EnvironmentKey("baseUrl", "local", [new EnvironmentValue("local", "http://localhost:9000")]),
            Acme))).IsSuccess);

        Assert.True((await sender.Send(new ImportMappingsCommand(Bundle("{{baseUrl}}", BaseUrlSection), Acme))).IsSuccess);

        // The import restores the exported state: values and active selection are the bundle's.
        var key = Assert.Single(await Keys(sender, Acme));
        Assert.Equal("dev", key.ActiveValue);
        Assert.Equal(["dev", "prod"], key.Values.Select(v => v.Name));
    }

    [Fact]
    public async Task Invalid_entry_is_skipped_without_failing_the_import()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        // "now" is a built-in helper name — the same rule the admin PUT enforces refuses it here.
        var section =
            """[{"key":"now","activeValue":"a","values":[{"name":"a","value":"x"}]},""" +
            """{"key":"good","activeValue":"a","values":[{"name":"a","value":"kept"}]}]""";

        var import = await sender.Send(new ImportMappingsCommand(Bundle("{{good}}", section), Acme));
        Assert.True(import.IsSuccess);
        Assert.Equal(1, import.Value);

        var key = Assert.Single(await Keys(sender, Acme));
        Assert.Equal("good", key.Key);
        Assert.Equal("kept", Serve(provider, Acme));
    }

    [Fact]
    public async Task Entry_without_an_active_value_falls_back_to_its_first_value()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        var section = """[{"key":"baseUrl","values":[{"name":"dev","value":"https://dev.example.com"},{"name":"prod","value":"https://api.example.com"}]}]""";
        Assert.True((await sender.Send(new ImportMappingsCommand(Bundle("{{baseUrl}}", section), Acme))).IsSuccess);

        Assert.Equal("dev", (Assert.Single(await Keys(sender, Acme))).ActiveValue);
    }

    [Fact]
    public async Task Active_value_other_than_the_first_is_preserved()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        // "prod" is deliberately NOT the first value: a reader that ignores activeValue and falls
        // back to the first would restore "dev" and go unnoticed by the first-value cases above.
        var section =
            """[{"key":"baseUrl","activeValue":"prod","values":[{"name":"dev","value":"https://dev.example.com"},{"name":"prod","value":"https://api.example.com"}]}]""";
        Assert.True((await sender.Send(new ImportMappingsCommand(Bundle("{{baseUrl}}", section), Acme))).IsSuccess);

        Assert.Equal("prod", (Assert.Single(await Keys(sender, Acme))).ActiveValue);
        Assert.Equal("https://api.example.com", Serve(provider, Acme));
    }

    [Fact]
    public async Task Hostile_section_shapes_are_dropped_without_failing_the_import()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        // A section that is not an array is ignored wholesale.
        Assert.True((await sender.Send(new ImportMappingsCommand(
            Bundle("plain", """{"baseUrl":"not-an-array"}"""), Acme))).IsSuccess);

        // Entries with a non-string key, a non-array values, or value items missing a field are
        // each dropped — never stored half-formed, never a 500.
        var section =
            """["just-a-string",""" +
            """{"key":123,"values":[{"name":"a","value":"x"}]},""" +
            """{"key":"badValues","values":"not-an-array"},""" +
            """{"key":"halfItems","values":[{"name":"only-name"},{"value":"only-value"}]}]""";
        var import = await sender.Send(new ImportMappingsCommand(Bundle("plain", section), Acme));
        Assert.True(import.IsSuccess);

        Assert.Empty(await Keys(sender, Acme));

        // A half-formed value item next to a complete one is dropped alone — the key survives with
        // exactly the complete values, not padded with a null-name/null-value phantom.
        var mixed = """[{"key":"mixed","values":[{"name":"a","value":"x"},{"name":"b"}]}]""";
        Assert.True((await sender.Send(new ImportMappingsCommand(Bundle("plain", mixed), Acme))).IsSuccess);

        var key = Assert.Single(await Keys(sender, Acme));
        var value = Assert.Single(key.Values);
        Assert.Equal(("a", "x"), (value.Name, value.Value));
    }

    [Fact]
    public async Task Imported_environments_land_in_the_importing_tenant_only()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        Assert.True((await sender.Send(new ImportMappingsCommand(Bundle("{{baseUrl}}", BaseUrlSection), Acme))).IsSuccess);

        Assert.Single(await Keys(sender, Acme));
        Assert.Empty(await Keys(sender, Globex));
    }

    [Fact]
    public async Task Malformed_bundle_fails_before_anything_is_applied()
    {
        using var provider = Host();
        var sender = provider.GetRequiredService<ISender>();

        var import = await sender.Send(new ImportMappingsCommand("{not json", Acme));
        Assert.True(import.IsFailure);

        Assert.Empty(await Keys(sender, Acme));
    }
}
