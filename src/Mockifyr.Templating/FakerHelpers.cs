using System.Globalization;
using Bogus;
using HandlebarsDotNet;

namespace Mockifyr.Templating;

/// <summary>
/// The Faker/<c>random</c> templating helper (G15): <c>{{random 'Class.method'}}</c> renders fake data
/// from a Datafaker-style expression. Mockifyr uses <see cref="Faker"/> (Bogus, Datafaker's .NET
/// counterpart) to back these expressions. The output is non-deterministic, so it is validated
/// <em>structurally</em> (format contract per expression) rather than differentially — the same
/// self-tested approach the random helpers use. A curated subset of the most common providers is
/// supported; an unknown expression yields an inline error string.
/// </summary>
internal static class FakerHelpers
{
    public static void Register(IHandlebars handlebars) =>
        handlebars.RegisterHelper("random", (_, arguments) => Evaluate(Argument(arguments)));

    // Datafaker expression ("Class.method") -> Bogus. A fresh Faker per call keeps it thread-safe.
    private static readonly Dictionary<string, Func<Faker, string>> Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name.firstName"] = f => f.Name.FirstName(),
        ["Name.lastName"] = f => f.Name.LastName(),
        ["Name.fullName"] = f => f.Name.FullName(),
        ["Name.name"] = f => f.Name.FullName(),
        ["Name.username"] = f => f.Internet.UserName(),
        ["Name.prefix"] = f => f.Name.Prefix(),
        ["Internet.emailAddress"] = f => f.Internet.Email(),
        // Datafaker renders a scheme-less "www.<word>-<word>.<tld>"; Bogus's Url() prepends "https://",
        // so it is composed by hand to keep the oracle contract. See docs/parity/g15-message-extras.md.
        ["Internet.url"] = f => $"www.{f.Internet.DomainWord()}.{f.Internet.DomainSuffix()}",
        ["Internet.uuid"] = f => f.Random.Guid().ToString(),
        ["Internet.domainName"] = f => f.Internet.DomainName(),
        ["Internet.ipV4Address"] = f => f.Internet.Ip(),
        ["Internet.macAddress"] = f => f.Internet.Mac(),
        ["Address.city"] = f => f.Address.City(),
        ["Address.country"] = f => f.Address.Country(),
        ["Address.countryCode"] = f => f.Address.CountryCode(),
        ["Address.zipCode"] = f => f.Address.ZipCode(),
        ["Address.state"] = f => f.Address.State(),
        ["Address.stateAbbr"] = f => f.Address.StateAbbr(),
        ["Address.streetAddress"] = f => f.Address.StreetAddress(),
        ["Address.streetName"] = f => f.Address.StreetName(),
        ["Address.buildingNumber"] = f => f.Address.BuildingNumber(),
        ["Address.secondaryAddress"] = f => f.Address.SecondaryAddress(),
        ["Address.fullAddress"] = f => f.Address.FullAddress(),
        // Coordinates are doubles in Bogus; Datafaker renders them as plain decimals, so the culture
        // must be pinned or a comma separator would leak in on a non-invariant host.
        ["Address.latitude"] = f => f.Address.Latitude().ToString("F6", CultureInfo.InvariantCulture),
        ["Address.longitude"] = f => f.Address.Longitude().ToString("F6", CultureInfo.InvariantCulture),
        ["Number.digit"] = f => f.Random.Int(0, 9).ToString(),
        ["Company.name"] = f => f.Company.CompanyName(),
        ["Commerce.productName"] = f => f.Commerce.ProductName(),
        ["Lorem.word"] = f => f.Lorem.Word(),
        ["Lorem.sentence"] = f => f.Lorem.Sentence(),
        ["PhoneNumber.phoneNumber"] = f => f.Phone.PhoneNumber(),
        ["PhoneNumber.cellPhone"] = f => f.Phone.PhoneNumber(),
    };

    private static object Evaluate(string expression) =>
        Providers.TryGetValue(expression, out var provider)
            // The ambient generator (#351): a seeded one inside a dataset load, a fresh unseeded one
            // everywhere else — so serving keeps drawing new values while a load stays reproducible.
            ? provider(FakerSeed.Current)
            : $"[ERROR: Unable to evaluate the expression {expression}]";

    // Pattern-matched rather than index-checked twice: 2.4.3 annotates the indexer as nullable, and
    // `is not null` on one read tells the compiler nothing about the next one.
    private static string Argument(Arguments arguments) =>
        arguments.Length > 0 && arguments[0] is { } first ? first.ToString() ?? string.Empty : string.Empty;
}
