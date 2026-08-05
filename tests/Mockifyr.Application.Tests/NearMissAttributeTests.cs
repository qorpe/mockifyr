using Mockifyr.Core;
using Mockifyr.Matching;
using Mockifyr.Templating;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for near-miss attribution (#288): which slot of the pattern each verdict
/// belongs to, and what the request carried there. The ranking itself is older and covered by
/// <c>G6NearMissTests</c>; what is new is being able to say <em>why</em>.
/// </summary>
/// <remarks>
/// No oracle: the reference engine reports near misses in a 404 body with its own wording, so only the
/// ranking is comparable, never the shape. Self-tested, per the standing rule.
/// </remarks>
public sealed class NearMissAttributeTests
{
    private static readonly TenantId Acme = new("acme");

    private static StubEngine EngineWith(params StubMapping[] stubs)
    {
        var store = new InMemoryStubStore();
        foreach (var stub in stubs)
        {
            store.Put(stub);
        }

        return new StubEngine(
            store, new StaticResponseRenderer(), new InMemoryScenarioStateStore(),
            new InMemoryRequestJournal(), [], []);
    }

    private static StubMapping Stub(RequestPattern pattern) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Acme,
        Request = pattern,
        Response = new ResponseDefinition
        {
            Status = 200,
            Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
            Transformers = [],
        },
    };

    private static RequestPattern Pattern(
        IMatcher? url = null,
        IMatcher? method = null,
        IReadOnlyList<IMatcher>? headers = null,
        IReadOnlyList<IMatcher>? body = null) => new()
    {
        Url = url,
        Method = method,
        Headers = headers ?? [],
        Query = [],
        FormParameters = [],
        Cookies = [],
        Body = body ?? [],
        Custom = [],
    };

    private static CanonicalRequest Request(
        string method = "GET", string url = "/orders", IEnumerable<KeyValuePair<string, string>>? headers = null) =>
        CanonicalRequestBuilder.Build(method, url, headers, null, "http");

    [Fact]
    public void Each_verdict_names_the_slot_it_came_from()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/orders"),
            method: new MethodMatcher("POST"))));

        var misses = engine.FindNearMisses(Acme, Request(method: "GET"), detailed: true);

        var attributes = misses[0].Attributes;
        Assert.Equal(["url", "method"], attributes.Select(a => a.Attribute));
        Assert.True(attributes[0].Matched);
        Assert.False(attributes[1].Matched);
    }

    [Fact]
    public void A_header_reports_by_name_not_by_position()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/orders"),
            headers: [new HeaderMatcher("X-Api-Key", new EqualToValueMatcher("expected", false))])));

        var misses = engine.FindNearMisses(
            Acme, Request(headers: [new("X-Api-Key", "actual")]), detailed: true);

        // "headers[0]" would send the reader counting entries in their own stub; the name is what they
        // can search for.
        var header = misses[0].Attributes.Single(a => a.Attribute.StartsWith("headers", StringComparison.Ordinal));
        Assert.Equal("headers['X-Api-Key']", header.Attribute);
        Assert.False(header.Matched);
        Assert.Equal("actual", header.Actual);
    }

    [Fact]
    public void A_missing_header_reports_no_value_rather_than_an_empty_one()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/orders"),
            headers: [new HeaderMatcher("X-Api-Key", new EqualToValueMatcher("expected", false))])));

        var misses = engine.FindNearMisses(Acme, Request(), detailed: true);

        // "sent nothing" and "sent an empty string" are different bugs and must not read the same.
        var header = misses[0].Attributes.Single(a => a.Attribute.StartsWith("headers", StringComparison.Ordinal));
        Assert.Null(header.Actual);
    }

    [Fact]
    public void A_repeated_header_is_joined_rather_than_truncated()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/orders"),
            headers: [new HeaderMatcher("Accept", new EqualToValueMatcher("application/xml", false))])));

        var misses = engine.FindNearMisses(
            Acme,
            Request(headers: [new("Accept", "application/json"), new("Accept", "text/plain")]),
            detailed: true);

        // Which of the two the client meant is exactly the question being asked, so both are shown.
        var header = misses[0].Attributes.Single(a => a.Attribute.StartsWith("headers", StringComparison.Ordinal));
        Assert.Equal("application/json, text/plain", header.Actual);
    }

    [Fact]
    public void Body_patterns_report_by_position()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/orders"),
            body: [new BodyMatcher(new EqualToValueMatcher("{}", false))])));

        var misses = engine.FindNearMisses(Acme, Request(), detailed: true);

        // A body pattern has no name in the dialect, so the index is the honest label — and the body
        // itself is already in the journal entry the caller is holding.
        var body = misses[0].Attributes.Single(a => a.Attribute.StartsWith("bodyPatterns", StringComparison.Ordinal));
        Assert.Equal("bodyPatterns[0]", body.Attribute);
        Assert.Null(body.Actual);
    }

    [Fact]
    public void The_url_and_method_report_what_the_request_carried()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/expected"), method: new MethodMatcher("POST"))));

        var misses = engine.FindNearMisses(Acme, Request(method: "GET", url: "/actual"), detailed: true);

        Assert.Equal("/actual", misses[0].Attributes.Single(a => a.Attribute == "url").Actual);
        Assert.Equal("GET", misses[0].Attributes.Single(a => a.Attribute == "method").Actual);
    }

    [Fact]
    public void Attribution_is_off_unless_asked_for()
    {
        var engine = EngineWith(Stub(Pattern(url: new UrlEqualToMatcher("/orders"))));

        // The serve path only ever needs the sum of the distances; re-running every matcher to attribute
        // them is a debugging cost, and a debugging cost belongs to whoever is debugging.
        Assert.Empty(engine.FindNearMisses(Acme, Request()).Single().Attributes);
    }

    [Fact]
    public void A_custom_matcher_that_names_nothing_still_reports_a_findable_slot()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Url = new UrlEqualToMatcher("/orders"),
            Headers = [],
            Query = [],
            FormParameters = [],
            Cookies = [],
            Body = [],
            Custom = [new AlwaysFailsMatcher()],
        }));

        var misses = engine.FindNearMisses(Acme, Request(), detailed: true);

        // A G10 matcher written before INamedTargetMatcher existed keeps working and reports by index.
        var custom = misses[0].Attributes.Single(a => a.Attribute.StartsWith("customMatcher", StringComparison.Ordinal));
        Assert.Equal("customMatcher[0]", custom.Attribute);
        Assert.False(custom.Matched);
    }

    [Fact]
    public void Every_slot_the_dialect_has_reports_under_its_own_name()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Url = new UrlPathEqualToMatcher("/orders"),
            Method = new MethodMatcher("GET"),
            Scheme = new SchemeMatcher("https"),
            Host = new HostMatcher(new EqualToValueMatcher("example.com", false)),
            Port = new PortMatcher(443),
            Headers = [new HeaderMatcher("Accept", new EqualToValueMatcher("*/*", false))],
            Query = [new QueryMatcher("page", new EqualToValueMatcher("1", false))],
            FormParameters = [new FormParameterMatcher("field", new EqualToValueMatcher("v", false))],
            Cookies = [new CookieMatcher("sid", new EqualToValueMatcher("abc", false))],
            Body = [new BodyMatcher(new EqualToValueMatcher("{}", false))],
            Custom = [],
        }));

        var attributes = engine.FindNearMisses(Acme, Request(), detailed: true)[0]
            .Attributes.Select(a => a.Attribute).ToList();

        // Every slot a stub author can write must come back under the name they wrote, or the diagnostic
        // sends them looking in the wrong place — which is worse than saying nothing.
        Assert.Equal(
            [
                "urlPath", "method", "scheme", "host", "port",
                "headers['Accept']", "queryParameters['page']", "formParameters['field']",
                "cookies['sid']", "bodyPatterns[0]",
            ],
            attributes);
    }

    [Fact]
    public void A_query_parameter_and_a_cookie_report_what_arrived()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Url = new UrlPathEqualToMatcher("/orders"),
            Headers = [],
            Query = [new QueryMatcher("page", new EqualToValueMatcher("9", false))],
            FormParameters = [],
            Cookies = [new CookieMatcher("sid", new EqualToValueMatcher("expected", false))],
            Body = [],
            Custom = [],
        }));

        var request = CanonicalRequestBuilder.Build(
            "GET", "/orders?page=2", [new("Cookie", "sid=actual")], null, "http");

        var attributes = engine.FindNearMisses(Acme, request, detailed: true)[0].Attributes
            .ToDictionary(a => a.Attribute, a => a.Actual);

        Assert.Equal("2", attributes["queryParameters['page']"]);
        Assert.Equal("actual", attributes["cookies['sid']"]);
    }

    [Fact]
    public void A_urlPath_matcher_reports_the_path_without_the_query_string()
    {
        var engine = EngineWith(Stub(Pattern(url: new UrlPathEqualToMatcher("/expected"))));

        var request = CanonicalRequestBuilder.Build("GET", "/orders?page=2", null, null, "http");
        var attribute = engine.FindNearMisses(Acme, request, detailed: true)[0].Attributes.Single();

        // A urlPath matcher is judging the path, so echoing the query back would invite the reader to
        // hunt for a difference that is not being compared.
        Assert.Equal("urlPath", attribute.Attribute);
        Assert.Equal("/orders", attribute.Actual);
    }

    [Fact]
    public void A_url_matcher_reports_the_whole_url()
    {
        var engine = EngineWith(Stub(Pattern(url: new UrlEqualToMatcher("/expected"))));

        var request = CanonicalRequestBuilder.Build("GET", "/orders?page=2", null, null, "http");
        var attribute = engine.FindNearMisses(Acme, request, detailed: true)[0].Attributes.Single();

        Assert.Equal("url", attribute.Attribute);
        Assert.Equal("/orders?page=2", attribute.Actual);
    }

    [Fact]
    public void The_closest_stub_comes_first()
    {
        var oneWrong = Stub(Pattern(url: new UrlEqualToMatcher("/orders"), method: new MethodMatcher("POST")));
        var allWrong = Stub(Pattern(url: new UrlEqualToMatcher("/elsewhere"), method: new MethodMatcher("DELETE")));
        var engine = EngineWith(allWrong, oneWrong);

        var misses = engine.FindNearMisses(Acme, Request(method: "GET", url: "/orders"), detailed: true);

        // Ranking is the older half of this feature and the half a reader trusts blindly: if the list is
        // not ordered by distance, the first entry is a guess wearing a ranking's clothes.
        Assert.Equal(oneWrong.Id, misses[0].Stub.Id);
        Assert.True(misses[0].Distance < misses[1].Distance);
    }

    [Fact]
    public void A_missing_query_parameter_reports_no_value()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Url = new UrlPathEqualToMatcher("/orders"),
            Headers = [],
            Query = [new QueryMatcher("page", new EqualToValueMatcher("1", false))],
            FormParameters = [], Cookies = [], Body = [], Custom = [],
        }));

        var attribute = engine.FindNearMisses(Acme, Request(), detailed: true)[0]
            .Attributes.Single(a => a.Attribute.StartsWith("queryParameters", StringComparison.Ordinal));

        // Same distinction as headers: "you sent nothing" and "you sent an empty value" are different
        // bugs and must not read alike.
        Assert.Null(attribute.Actual);
    }

    [Fact]
    public void A_repeated_query_parameter_is_joined()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Url = new UrlPathEqualToMatcher("/orders"),
            Headers = [],
            Query = [new QueryMatcher("tag", new EqualToValueMatcher("z", false))],
            FormParameters = [], Cookies = [], Body = [], Custom = [],
        }));

        var request = CanonicalRequestBuilder.Build("GET", "/orders?tag=a&tag=b", null, null, "http");
        var attribute = engine.FindNearMisses(Acme, request, detailed: true)[0]
            .Attributes.Single(a => a.Attribute.StartsWith("queryParameters", StringComparison.Ordinal));

        Assert.Equal("a, b", attribute.Actual);
    }

    [Fact]
    public void A_form_parameter_does_not_borrow_a_cookies_value()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Url = new UrlPathEqualToMatcher("/orders"),
            Headers = [],
            Query = [],
            FormParameters = [new FormParameterMatcher("sid", new EqualToValueMatcher("v", false))],
            Cookies = [], Body = [], Custom = [],
        }));

        // A cookie and a form field can share a name. Reporting the cookie's value as the form field's
        // would send a reader after a value the matcher never looked at.
        var request = CanonicalRequestBuilder.Build("POST", "/orders", [new("Cookie", "sid=from-cookie")], null, "http");
        var attribute = engine.FindNearMisses(Acme, request, detailed: true)[0]
            .Attributes.Single(a => a.Attribute.StartsWith("formParameters", StringComparison.Ordinal));

        Assert.Null(attribute.Actual);
    }

    [Theory]
    [InlineData("urlPattern", "/orders?page=2")]
    [InlineData("urlPathPattern", "/orders")]
    [InlineData("urlPathTemplate", "/orders")]
    public void Every_url_spelling_reports_the_part_it_judges(string slot, string expectedActual)
    {
        IMatcher matcher = slot switch
        {
            "urlPattern" => new UrlPatternMatcher("/nope.*"),
            "urlPathPattern" => new UrlPathPatternMatcher("/nope.*"),
            _ => new UrlPathTemplateMatcher("/nope/{id}"),
        };

        var engine = EngineWith(Stub(Pattern(url: matcher)));
        var request = CanonicalRequestBuilder.Build("GET", "/orders?page=2", null, null, "http");

        var attribute = engine.FindNearMisses(Acme, request, detailed: true)[0].Attributes.Single();

        Assert.Equal(slot, attribute.Attribute);
        Assert.Equal(expectedActual, attribute.Actual);
    }

    [Fact]
    public void The_scheme_reports_what_the_request_arrived_over()
    {
        var engine = EngineWith(Stub(new RequestPattern
        {
            Scheme = new SchemeMatcher("https"),
            Headers = [], Query = [], FormParameters = [], Cookies = [], Body = [], Custom = [],
        }));

        var attribute = engine.FindNearMisses(Acme, Request(), detailed: true)[0].Attributes.Single();

        Assert.Equal("scheme", attribute.Attribute);
        Assert.Equal("http", attribute.Actual);
    }

    [Fact]
    public void A_custom_url_matcher_falls_back_to_the_generic_slot_name()
    {
        var engine = EngineWith(Stub(Pattern(url: new AlwaysFailsMatcher())));

        // A G10 matcher in the URL slot cannot say which spelling it emulates, so it reports the generic
        // one rather than guessing.
        var attribute = engine.FindNearMisses(Acme, Request(), detailed: true)[0].Attributes.Single();
        Assert.Equal("url", attribute.Attribute);
    }

    [Fact]
    public void A_custom_matcher_in_a_named_collection_reports_by_position()
    {
        var engine = EngineWith(Stub(Pattern(
            url: new UrlEqualToMatcher("/orders"),
            headers: [new AlwaysFailsMatcher()])));

        var attribute = engine.FindNearMisses(Acme, Request(), detailed: true)[0]
            .Attributes.Single(a => a.Attribute.StartsWith("headers", StringComparison.Ordinal));

        Assert.Equal("headers[0]", attribute.Attribute);
        Assert.Null(attribute.Actual);
    }

    private sealed class AlwaysFailsMatcher : IMatcher
    {
        public MatchResult Match(MatchInput input) => MatchResult.NoMatch(1d);
    }
}
