using System.Text;
using Mockifyr.Core;
using Mockifyr.Matching;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Coverage for candidate indexing (#265). The optimization is only allowed to change how many stubs
/// are evaluated, never which ones can match — so these tests are mostly about what the index must
/// NOT hide, and about the store order the engine's tie-break depends on.
/// </summary>
public sealed class StubIndexTests
{
    private static StubMapping Stub(string method, IMatcher? url, int priority = 5) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantId.Default,
        Request = new RequestPattern
        {
            Method = new MethodMatcher(method),
            Url = url,
            Headers = [],
            Query = [],
            Cookies = [],
            Body = [],
        },
        Response = new ResponseDefinition
        {
            Status = 200,
            Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
            Transformers = [],
        },
        Priority = priority,
    };

    private static CanonicalRequest Request(string method, string path) =>
        CanonicalRequestBuilder.Build(method, path, [], null);

    [Fact]
    public void An_exactly_addressed_stub_is_a_candidate()
    {
        var stub = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        var index = new StubIndex([stub]);

        Assert.Same(stub, Assert.Single(index.Candidates(Request("GET", "/orders"))));
    }

    [Fact]
    public void Stubs_on_other_paths_and_methods_are_not_candidates()
    {
        var wanted = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        var index = new StubIndex([
            Stub("GET", new UrlPathEqualToMatcher("/customers")),
            Stub("POST", new UrlPathEqualToMatcher("/orders")),
            wanted,
        ]);

        // The entire point: 3 stubs in, 1 evaluated.
        Assert.Same(wanted, Assert.Single(index.Candidates(Request("GET", "/orders"))));
    }

    [Fact]
    public void The_method_comparison_is_case_insensitive_like_the_matcher()
    {
        var index = new StubIndex([Stub("get", new UrlPathEqualToMatcher("/orders"))]);

        // MethodMatcher compares case-insensitively; an index that did not would hide the stub for a
        // client that sent a differently-cased verb.
        Assert.Single(index.Candidates(Request("GET", "/orders")));
    }

    [Fact]
    public void An_ANY_method_stub_is_a_candidate_for_every_method()
    {
        var any = Stub("ANY", new UrlPathEqualToMatcher("/orders"));
        var index = new StubIndex([any]);

        // ANY matches every request, so it belongs in no method bucket — it must be offered whatever
        // the verb, or a wildcard stub would stop working the moment indexing arrived.
        Assert.Same(any, Assert.Single(index.Candidates(Request("GET", "/orders"))));
        Assert.Same(any, Assert.Single(index.Candidates(Request("DELETE", "/orders"))));
    }

    [Fact]
    public void A_regex_url_stub_is_a_candidate_for_every_request()
    {
        var regex = Stub("GET", new UrlPatternMatcher("/orders/[0-9]+"));
        var index = new StubIndex([regex]);

        // A pattern pins no single path, so it cannot be bucketed. Being offered for everything is
        // the safe answer — the engine still evaluates it and decides.
        Assert.Same(regex, Assert.Single(index.Candidates(Request("GET", "/orders/42"))));
        Assert.Same(regex, Assert.Single(index.Candidates(Request("GET", "/somewhere-else"))));
    }

    [Fact]
    public void A_full_url_stub_is_a_candidate_for_every_request()
    {
        // `url` pins path PLUS query, which the path-keyed index cannot reproduce. Treating it as a
        // path would bucket it under "/orders?status=new" and hide it from a request whose path is
        // "/orders" — the exact class of bug an index introduces if it is careless.
        var fullUrl = Stub("GET", new UrlEqualToMatcher("/orders?status=new"));
        var index = new StubIndex([fullUrl]);

        Assert.Same(fullUrl, Assert.Single(index.Candidates(Request("GET", "/orders"))));
    }

    [Fact]
    public void A_stub_with_no_url_matcher_is_a_candidate_for_every_request()
    {
        var anyUrl = Stub("GET", url: null);
        var index = new StubIndex([anyUrl]);

        Assert.Same(anyUrl, Assert.Single(index.Candidates(Request("GET", "/anything"))));
    }

    [Fact]
    public void Candidates_come_back_in_store_order_across_both_buckets()
    {
        var first = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        var second = Stub("GET", new UrlPatternMatcher("/orders"));
        var third = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        var fourth = Stub("GET", new UrlPatternMatcher("/or.*"));
        // The last stub is indexable on purpose: the two buckets must interleave correctly whichever
        // one runs out first.
        var fifth = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        var index = new StubIndex([first, second, third, fourth, fifth]);

        // The engine breaks priority ties by insertion order, so interleaving the indexed and
        // un-indexable buckets in the wrong order would silently change which stub wins.
        Assert.Equal(
            [first, second, third, fourth, fifth],
            index.Candidates(Request("GET", "/orders")));
    }

    [Fact]
    public void An_unknown_path_still_offers_the_un_indexable_stubs()
    {
        var catchAll = Stub("GET", new UrlPatternMatcher(".*"));
        var index = new StubIndex([Stub("GET", new UrlPathEqualToMatcher("/orders")), catchAll]);

        Assert.Same(catchAll, Assert.Single(index.Candidates(Request("GET", "/nothing-here"))));
    }

    [Fact]
    public void The_store_serves_candidates_and_notices_when_stubs_change()
    {
        var store = new InMemoryStubStore();
        var first = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        store.Put(first);

        Assert.Same(first, Assert.Single(store.GetCandidates(TenantId.Default, Request("GET", "/orders"))));

        var second = Stub("GET", new UrlPathEqualToMatcher("/orders"));
        store.Put(second);

        // A cached index that outlived the stub it was built from would serve a host that silently
        // ignores everything added after startup.
        Assert.Equal(2, store.GetCandidates(TenantId.Default, Request("GET", "/orders")).Count);

        store.Remove(TenantId.Default, first.Id);
        Assert.Same(second, Assert.Single(store.GetCandidates(TenantId.Default, Request("GET", "/orders"))));
    }

    [Fact]
    public void One_tenants_stubs_are_never_candidates_for_another()
    {
        var store = new InMemoryStubStore();
        var mine = Stub("GET", new UrlPathEqualToMatcher("/orders")) with { TenantId = new TenantId("alpha") };
        store.Put(mine);

        Assert.Single(store.GetCandidates(new TenantId("alpha"), Request("GET", "/orders")));
        Assert.Empty(store.GetCandidates(new TenantId("beta"), Request("GET", "/orders")));
    }

    [Fact]
    public void An_empty_tenant_yields_no_candidates() =>
        Assert.Empty(new InMemoryStubStore().GetCandidates(TenantId.Default, Request("GET", "/orders")));

    [Fact]
    public void Every_stub_that_matches_is_offered_whatever_the_pattern_shape()
    {
        // The invariant the whole optimization rests on, checked across the shapes at once: for each
        // stub, a request it genuinely matches must find it among the candidates.
        var cases = new (StubMapping Stub, CanonicalRequest Request)[]
        {
            (Stub("GET", new UrlPathEqualToMatcher("/a")), Request("GET", "/a")),
            (Stub("ANY", new UrlPathEqualToMatcher("/b")), Request("PATCH", "/b")),
            (Stub("POST", new UrlPatternMatcher("/c/[0-9]+")), Request("POST", "/c/7")),
            (Stub("GET", new UrlPathPatternMatcher("/d/.*")), Request("GET", "/d/deep/path")),
            (Stub("GET", new UrlEqualToMatcher("/e?q=1")), Request("GET", "/e")),
            (Stub("GET", url: null), Request("GET", "/f")),
        };

        var index = new StubIndex([.. cases.Select(c => c.Stub)]);

        foreach (var (stub, request) in cases)
        {
            Assert.Contains(stub, index.Candidates(request));
        }
    }
}
