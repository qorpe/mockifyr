using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Mockifyr.Differential.Harness;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Validation of the Faker <c>random</c> helper (G15a). Its output is non-deterministic, so it can't
/// be byte-diffed — it is validated <em>structurally</em> (the racy-feature method): the same faker
/// stub is served by the oracle (WireMock + the faker extension) and Mockifyr, and over many iterations
/// each generated field must satisfy a format contract on <b>both</b> sides. The oracle satisfying the
/// contract proves it is real WireMock/Datafaker behavior; Mockifyr (Bogus) satisfying the same
/// contract is the parity claim. Requires Docker.
/// </summary>
public sealed class G15aFakerTests : IAsyncLifetime
{
    private const string Template =
        "firstName=[{{random 'Name.firstName'}}]|lastName=[{{random 'Name.lastName'}}]|" +
        "fullName=[{{random 'Name.fullName'}}]|email=[{{random 'Internet.emailAddress'}}]|" +
        "city=[{{random 'Address.city'}}]|zip=[{{random 'Address.zipCode'}}]|" +
        "digit=[{{random 'Number.digit'}}]|uuid=[{{random 'Internet.uuid'}}]|word=[{{random 'Lorem.word'}}]|" +
        // Long-tail providers (G15e): each still has a stable-enough structural contract satisfied by
        // both the oracle (Datafaker) and Mockifyr (Bogus).
        "username=[{{random 'Name.username'}}]|prefix=[{{random 'Name.prefix'}}]|" +
        "domain=[{{random 'Internet.domainName'}}]|ipv4=[{{random 'Internet.ipV4Address'}}]|" +
        "mac=[{{random 'Internet.macAddress'}}]|state=[{{random 'Address.state'}}]|" +
        "street=[{{random 'Address.streetAddress'}}]|product=[{{random 'Commerce.productName'}}]|" +
        "sentence=[{{random 'Lorem.sentence'}}]|" +
        // Geo/address/phone providers requested by issue #164: the categories exist in Datafaker, so
        // they are exposed under the same `{{random 'Class.method'}}` syntax rather than reinvented.
        "countryCode=[{{random 'Address.countryCode'}}]|latitude=[{{random 'Address.latitude'}}]|" +
        "longitude=[{{random 'Address.longitude'}}]|streetName=[{{random 'Address.streetName'}}]|" +
        "buildingNumber=[{{random 'Address.buildingNumber'}}]|fullAddress=[{{random 'Address.fullAddress'}}]|" +
        "cellPhone=[{{random 'PhoneNumber.cellPhone'}}]|country=[{{random 'Address.country'}}]|" +
        "company=[{{random 'Company.name'}}]|url=[{{random 'Internet.url'}}]|" +
        "phone=[{{random 'PhoneNumber.phoneNumber'}}]|name=[{{random 'Name.name'}}]";

    private static readonly string MappingJson =
        "{\"request\":{\"method\":\"GET\",\"urlPath\":\"/faker\"}," +
        "\"response\":{\"status\":200,\"transformers\":[\"response-template\"],\"body\":\"" + Template + "\"}}";

    private readonly WireMockFakerOracle _oracle = new();
    private readonly WebApplicationFactory<Program> _mockifyr = new();

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync()
    {
        await _mockifyr.DisposeAsync();
        await _oracle.DisposeAsync();
    }

    private sealed record Field(string Name, Regex Contract);

    private static readonly Field[] Fields =
    [
        new("firstName", new Regex(@"^[\p{L}'.\- ]+$", RegexOptions.Compiled)),
        new("lastName", new Regex(@"^[\p{L}'.\- ]+$", RegexOptions.Compiled)),
        new("fullName", new Regex(@"^[\p{L}'.\- ]+$", RegexOptions.Compiled)),
        new("email", new Regex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled)),
        new("city", new Regex(@"^[\p{L}'.\- ]+$", RegexOptions.Compiled)),
        new("zip", new Regex(@"^\d{5}(-\d{4})?$", RegexOptions.Compiled)),
        new("digit", new Regex(@"^\d$", RegexOptions.Compiled)),
        new("uuid", new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled)),
        new("word", new Regex(@"^\p{L}+$", RegexOptions.Compiled)),
        // Long-tail (G15e). Contracts are loose enough for both Datafaker and Bogus, tight enough to
        // catch a wrong provider (an IP that isn't dotted-quad, a MAC that isn't colon-hex, etc.).
        new("username", new Regex(@"^[\p{L}\d._-]+$", RegexOptions.Compiled)),
        new("prefix", new Regex(@"^[\p{L}.\- ]+$", RegexOptions.Compiled)),
        new("domain", new Regex(@"^[\p{L}\d.\-]+\.[\p{L}]+$", RegexOptions.Compiled)),
        new("ipv4", new Regex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$", RegexOptions.Compiled)),
        new("mac", new Regex(@"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$", RegexOptions.Compiled)),
        new("state", new Regex(@"^[\p{L}.\- ]+$", RegexOptions.Compiled)),
        new("street", new Regex(@"^[\p{L}\d'.,\-# ]+$", RegexOptions.Compiled)),
        new("product", new Regex(@"^[\p{L} ]+$", RegexOptions.Compiled)),
        new("sentence", new Regex(@"^[\p{L} ,.'-]+$", RegexOptions.Compiled)),
        // Geo/address/phone (#164). Coordinates are the tight ones: a wrong provider (a zip code, a
        // city name) cannot satisfy a signed decimal, and latitude is bounded to two integer digits.
        new("countryCode", new Regex(@"^[A-Za-z]{2}$", RegexOptions.Compiled)),
        new("latitude", new Regex(@"^-?\d{1,2}(\.\d+)?$", RegexOptions.Compiled)),
        new("longitude", new Regex(@"^-?\d{1,3}(\.\d+)?$", RegexOptions.Compiled)),
        new("streetName", new Regex(@"^[\p{L}\d'.,\-# ]+$", RegexOptions.Compiled)),
        new("buildingNumber", new Regex(@"^[\p{L}\d\- /]+$", RegexOptions.Compiled)),
        // fullAddress embeds a country name, so it must permit everything `country` does — including
        // the parentheses and ampersand of "British Indian Ocean Territory (Chagos Archipelago)" and
        // "Svalbard & Jan Mayen Islands". Both were rare enough to pass a 15-draw sample and fail later.
        new("fullAddress", new Regex(@"^[\p{L}\d'.,&\-#/() ]+$", RegexOptions.Compiled)),
        new("cellPhone", new Regex(@"^[\d\p{L}()+\-. x/]+$", RegexOptions.Compiled)),
        // Previously registered but untested providers — pinned here so the catalog is fully covered.
        // Country names carry digits, parentheses and ampersands in both catalogs ("Antarctica (the
        // territory South of 60 deg S)", "Svalbard & Jan Mayen Islands"), so the contract only pins
        // "no control/delimiter characters". The permitted set was derived by exhaustively sampling
        // every provider 300k times, not from a handful of draws — see docs/parity/g15-extras.md.
        new("country", new Regex(@"^[\p{L}\d'.,&\-() ]+$", RegexOptions.Compiled)),
        new("company", new Regex(@"^[\p{L}\d'.,\-&/ ]+$", RegexOptions.Compiled)),
        // Datafaker's Internet.url is scheme-less ("www.foo.co"); see docs/parity/g15-extras.md.
        new("url", new Regex(@"^www\.[\p{L}\d\-]+\.\p{L}+$", RegexOptions.Compiled)),
        new("phone", new Regex(@"^[\d\p{L}()+\-. x/]+$", RegexOptions.Compiled)),
        new("name", new Regex(@"^[\p{L}'.\- ]+$", RegexOptions.Compiled)),
    ];

    [Fact]
    public async Task FakerHelper_StructurallyMatchesTheOracle()
    {
        using var mockifyrClient = _mockifyr.CreateClient();
        await LoadAsync(_oracle.Client);
        await LoadAsync(mockifyrClient);

        var failures = new List<string>();
        // Contracts are asserted against random draws, so the iteration count is what decides whether a
        // rare value (a parenthesised country, an apostrophised street) is ever seen. 15 was too few —
        // it let a fullAddress contract that rejects parentheses pass locally and fail in CI.
        for (var iteration = 0; iteration < 60; iteration++)
        {
            var oracle = await _oracle.Client.GetStringAsync("/faker");
            var mockifyr = await mockifyrClient.GetStringAsync("/faker");

            foreach (var field in Fields)
            {
                var oracleValue = Extract(oracle, field.Name);
                var mockifyrValue = Extract(mockifyr, field.Name);

                if (!field.Contract.IsMatch(oracleValue))
                {
                    failures.Add($"{field.Name}: ORACLE value violates the contract: \"{oracleValue}\"");
                }

                if (!field.Contract.IsMatch(mockifyrValue))
                {
                    failures.Add($"{field.Name}: mockifyr value violates the contract: \"{mockifyrValue}\"");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} faker divergence(s):\n{string.Join("\n", failures.Distinct())}");
    }

    private static async Task LoadAsync(HttpClient client)
    {
        await client.PostAsync("/__admin/mappings/reset", content: null);
        using var content = new StringContent(MappingJson, Encoding.UTF8, "application/json");
        await client.PostAsync("/__admin/mappings", content);
    }

    private static string Extract(string body, string field)
    {
        // Anchored to a field delimiter: an unanchored search for "name=[" would otherwise match
        // inside "username=[...]" and silently compare the wrong provider's output.
        var match = Regex.Match(body, @"(?:^|\|)" + Regex.Escape(field) + @"=\[(.*?)\]");
        return match.Success ? match.Groups[1].Value : "<absent>";
    }
}
