using System.Text;
using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for journal masking (#227): what gets replaced, what is left untouched, and
/// the invariants that keep masking from corrupting a recorded payload.
/// </summary>
public sealed class JournalMaskingTests
{
    private const string PanBody = "{\"pan\":\"4111\"}";
    private const string ArrayBody = "{\"items\":[{\"token\":\"t-1\"}]}";
    private const string PlainBody = "{\"amount\":1}";

    private static CanonicalRequest Request(string body, params (string Name, string Value)[] headers) =>
        CanonicalRequestBuilder.Build(
            "POST", "/pay",
            [.. headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value))],
            Encoding.UTF8.GetBytes(body), "https");

    private static string BodyOf(CanonicalRequest request) => Encoding.UTF8.GetString(request.Body);

    [Fact]
    public void Configured_headers_are_masked_case_insensitively()
    {
        var options = JournalMaskingOptions.Parse("Authorization, X-Api-Key", null);

        var masked = JournalMasker.Mask(
            Request("{}", ("authorization", "Bearer secret"), ("X-API-KEY", "k-123"), ("Accept", "application/json")),
            options);

        Assert.Equal("***", masked.Headers["authorization"].Single());
        Assert.Equal("***", masked.Headers["x-api-key"].Single());
        // Everything else survives untouched.
        Assert.Equal("application/json", masked.Headers["accept"].Single());
    }

    [Fact]
    public void Multi_valued_headers_keep_their_arity()
    {
        var options = JournalMaskingOptions.Parse("Cookie", null);

        var masked = JournalMasker.Mask(Request("{}", ("Cookie", "a=1"), ("Cookie", "b=2")), options);

        Assert.Equal(["***", "***"], masked.Headers["cookie"]);
    }

    [Fact]
    public void Body_fields_are_masked_at_any_depth_including_arrays()
    {
        var options = JournalMaskingOptions.Parse(null, "pan,cvv");
        var body = """{"amount":10,"card":{"pan":"4111111111111111","cvv":"123"},"items":[{"pan":"5555444433332222"}]}""";

        var masked = JournalMasker.Mask(Request(body), options);
        var result = BodyOf(masked);

        Assert.DoesNotContain("4111111111111111", result);
        Assert.DoesNotContain("5555444433332222", result);
        Assert.DoesNotContain("123", result);
        // The envelope stays readable — that is the point of field-level masking.
        Assert.Contains("\"amount\":10", result);
    }

    [Fact]
    public void A_non_json_body_is_returned_byte_for_byte()
    {
        var options = JournalMaskingOptions.Parse(null, "pan");
        var original = Request("pan=4111111111111111&amount=10");

        var masked = JournalMasker.Mask(original, options);

        // Masking must never corrupt a payload it cannot parse structurally.
        Assert.Same(original.Body, masked.Body);
        Assert.Equal("pan=4111111111111111&amount=10", BodyOf(masked));
    }

    [Fact]
    public void Nothing_configured_returns_the_very_same_request()
    {
        var original = Request("""{"pan":"4111"}""", ("Authorization", "Bearer x"));

        Assert.Same(original, JournalMasker.Mask(original, JournalMaskingOptions.None));
        Assert.Same(original, JournalMasker.Mask(original, JournalMaskingOptions.Parse("", "")));

        // Unset flags (null, not empty string) must also mean "mask nothing" — otherwise the host
        // would wrap the journal in a decorator that can never mask anything.
        Assert.True(JournalMaskingOptions.Parse(null, null).IsEmpty);
        Assert.Empty(JournalMaskingOptions.Parse(null, null).Headers);
    }

    [Fact]
    public void A_request_naming_no_configured_value_is_not_rebuilt()
    {
        var options = JournalMaskingOptions.Parse("Authorization", "pan");
        var original = Request("""{"amount":10}""", ("Accept", "application/json"));

        // No configured header and no configured field present → same instance, no allocation.
        Assert.Same(original, JournalMasker.Mask(original, options));
    }

    [Fact]
    public void An_empty_body_and_a_field_only_mask_are_both_handled()
    {
        // Field mask configured, but the request carries no body at all: nothing to walk, and the
        // empty array must come back as-is rather than as a rebuilt one.
        var empty = CanonicalRequestBuilder.Build("GET", "/pay", [], [], "https");
        Assert.Same(empty.Body, JournalMasker.Mask(empty, JournalMaskingOptions.Parse(null, "pan")).Body);

        // Header mask configured but no field mask: the body is never touched.
        var withBody = Request(PanBody, ("Authorization", "Bearer x"));
        var masked = JournalMasker.Mask(withBody, JournalMaskingOptions.Parse("Authorization", null));
        Assert.Equal(PanBody, BodyOf(masked));
        Assert.Equal("***", masked.Headers["authorization"].Single());
    }

    [Fact]
    public void Masking_an_array_element_deep_inside_still_rewrites_the_body()
    {
        // The array branch must report the change upward — otherwise a body whose ONLY masked field
        // sits inside an array element would be stored unmasked.
        var options = JournalMaskingOptions.Parse(null, "token");
        var masked = JournalMasker.Mask(Request(ArrayBody), options);

        Assert.DoesNotContain("t-1", BodyOf(masked));
        Assert.Contains("***", BodyOf(masked));
    }

    [Fact]
    public void Whitespace_around_configured_names_is_trimmed()
    {
        var options = JournalMaskingOptions.Parse(" Authorization , X-Api-Key ", " pan , cvv ");

        var masked = JournalMasker.Mask(Request(PanBody, ("Authorization", "Bearer x")), options);

        Assert.Equal("***", masked.Headers["authorization"].Single());
        Assert.DoesNotContain("4111", BodyOf(masked));
    }

    [Fact]
    public void The_decorator_stores_the_masked_request_so_it_can_never_be_read_back()
    {
        var inner = new InMemoryRequestJournal(limit: 10);
        var journal = new MaskingRequestJournal(inner, JournalMaskingOptions.Parse("Authorization", "pan"));
        var tenant = new TenantId("acme");

        journal.Record(new ServeEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Request = Request("""{"pan":"4111111111111111"}""", ("Authorization", "Bearer secret")),
            Timestamp = DateTimeOffset.UnixEpoch,
        });

        // An event with nothing to mask passes through as the very same instance — no copy, so
        // sub-events appended later (webhook deliveries) still land on the recorded event.
        var untouched = new ServeEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Request = Request(PlainBody),
            Timestamp = DateTimeOffset.UnixEpoch,
        };
        journal.Record(untouched);
        Assert.Same(untouched, journal.Query(tenant, new ServeEventQuery { Id = untouched.Id }).Single());

        var stored = journal.Query(tenant, new ServeEventQuery()).First(e => e.Id != untouched.Id);
        Assert.Equal("***", stored.Request.Headers["authorization"].Single());
        Assert.DoesNotContain("4111111111111111", BodyOf(stored.Request));
        Assert.DoesNotContain("secret", BodyOf(stored.Request));
    }
}
