using System.Text;
using BenchmarkDotNet.Attributes;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.Matching;
using Mockifyr.Stores.InMemory;
using Mockifyr.Templating;

namespace Mockifyr.Benchmarks;

/// <summary>
/// The engine's hot path, measured without a transport (#249).
/// </summary>
/// <remarks>
/// <para>
/// These numbers answer "what does one request cost inside Mockifyr" — the part a sizing decision
/// actually rests on. Kestrel, the network and the client are deliberately absent: they are measured
/// by the load harness in <c>bench/load</c>, and mixing them in here would make an engine regression
/// invisible under transport noise.
/// </para>
/// <para>
/// The journal is disabled in every case except <see cref="MatchWithJournal"/>. That pair is the point
/// of the comparison: it is what an operator is buying when they set <c>--journal-disabled</c>.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EngineBenchmarks
{
    private StubEngine _engine = null!;
    private StubEngine _journaling = null!;
    private StubEngine _manyStubs = null!;
    private StubEngine _templated = null!;
    private StubEngine _jsonBody = null!;

    private CanonicalRequest _simple = null!;
    private CanonicalRequest _last = null!;
    private CanonicalRequest _templatedRequest = null!;
    private CanonicalRequest _jsonRequest = null!;
    private CanonicalRequest _largeBody = null!;

    /// <summary>How many stubs the store holds in the "realistic host" case.</summary>
    [Params(1000)]
    public int StubCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _engine = EngineWith(["""{"request":{"method":"GET","urlPath":"/orders"},"response":{"status":200,"body":"ok"}}"""]);
        _journaling = EngineWith(
            ["""{"request":{"method":"GET","urlPath":"/orders"},"response":{"status":200,"body":"ok"}}"""],
            journal: true);

        // A store with many stubs, and a request that matches the LAST one: the honest worst case for
        // a linear scan, not the flattering first-hit case.
        var many = Enumerable.Range(0, StubCount)
            .Select(i => $$$"""{"request":{"method":"GET","urlPath":"/resource-{{{i}}}"},"response":{"status":200,"body":"ok"}}""")
            .ToArray();
        _manyStubs = EngineWith(many);

        _templated = EngineWith([
            """
            {"request":{"method":"POST","urlPath":"/echo"},
             "response":{"status":200,
                         "body":"{{request.body}} {{randomValue type='UUID'}} {{now format='yyyy-MM-dd'}}",
                         "transformers":["response-template"]}}
            """]);

        _jsonBody = EngineWith([
            """
            {"request":{"method":"POST","urlPath":"/payment",
                        "bodyPatterns":[{"equalToJson":"{\"amount\":100,\"currency\":\"SAR\"}","ignoreExtraElements":true}]},
             "response":{"status":201,"body":"created"}}
            """]);

        _simple = Request("GET", "/orders");
        _last = Request("GET", $"/resource-{StubCount - 1}");
        _templatedRequest = Request("POST", "/echo", """{"hello":"world"}""");
        _jsonRequest = Request("POST", "/payment", """{"amount":100,"currency":"SAR","reference":"abc"}""");
        _largeBody = Request("POST", "/echo", new string('x', 256 * 1024));
    }

    /// <summary>A single stub, matched by method and path — the floor cost of serving anything.</summary>
    [Benchmark(Baseline = true)]
    public object? Match() => _engine.Handle(TenantId.Default, _simple).Response;

    /// <summary>The same match with the journal on: the cost of remembering what was served.</summary>
    [Benchmark]
    public object? MatchWithJournal() => _journaling.Handle(TenantId.Default, _simple).Response;

    /// <summary>A realistic store, matching the last stub — how a linear scan scales.</summary>
    [Benchmark]
    public object? MatchAmongManyStubs() => _manyStubs.Handle(TenantId.Default, _last).Response;

    /// <summary>Structural JSON body matching, which parses the body rather than comparing bytes.</summary>
    [Benchmark]
    public object? MatchJsonBody() => _jsonBody.Handle(TenantId.Default, _jsonRequest).Response;

    /// <summary>Match plus a templated response — the cost of rendering, not just deciding.</summary>
    [Benchmark]
    public object? MatchAndRenderTemplate() => _templated.Handle(TenantId.Default, _templatedRequest).Response;

    /// <summary>A 256 KiB body through the same path: where the cost becomes the payload, not the logic.</summary>
    [Benchmark]
    public object? MatchAndRenderLargeBody() => _templated.Handle(TenantId.Default, _largeBody).Response;

    private static CanonicalRequest Request(string method, string path, string? body = null) =>
        CanonicalRequestBuilder.Build(
            method, path,
            [new KeyValuePair<string, string>("Accept", "application/json")],
            body is null ? null : Encoding.UTF8.GetBytes(body));

    private static StubEngine EngineWith(IReadOnlyList<string> mappings, bool journal = false)
    {
        var store = new InMemoryStubStore();
        var matchers = new InMemoryMatcherRegistry();
        foreach (var mapping in mappings)
        {
            foreach (var (stub, _) in MappingJsonReader.ReadWithSource(mapping, TenantId.Default, matchers))
            {
                store.Put(stub);
            }
        }

        return new StubEngine(
            store,
            new TemplatingResponseRenderer(),
            new InMemoryScenarioStateStore(),
            journal ? new InMemoryRequestJournal() : new NullRequestJournal(),
            []);
    }
}
