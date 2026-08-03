using System.Text;
using Mockifyr.Core;
using Mockifyr.Differential.Generator;
using Mockifyr.Differential.Harness;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential coverage for <c>bodyFileName</c> — response bodies held as files rather than inlined.
/// </summary>
/// <remarks>
/// Both engines are handed the same stub AND the same file: the oracle gets it mapped into its
/// container's <c>__files</c>, Mockifyr gets a store holding the same content. So the questions that
/// used to be answered by reading someone's documentation — does the file get templated, what wins
/// when a stub carries both an inline body and a file name, what happens when the file is missing —
/// are answered by the reference engine instead.
/// </remarks>
public sealed class BodyFileNameTests : IAsyncLifetime
{
    private const string BodyFile = "order.json";
    private const string FileContent = """{"orderId":"A-1","status":"CONFIRMED"}""";

    private WireMockOracle _oracle = null!;

    public async Task InitializeAsync()
    {
        _oracle = new WireMockOracle(new Dictionary<string, string>
        {
            [BodyFile] = FileContent,
            ["templated.json"] = """{"path":"{{request.path}}","method":"{{request.method}}"}""",
        });
        await _oracle.StartAsync();
    }

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    /// <summary>The same files the oracle container was given, for the Mockifyr side.</summary>
    private static IResponseBodyFiles Files() => new InMemoryBodyFiles(new Dictionary<string, string>
    {
        [BodyFile] = FileContent,
        ["templated.json"] = """{"path":"{{request.path}}","method":"{{request.method}}"}""",
    });

    private async Task VerifyAsync(string mappingJson, RequestSpec request)
    {
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(mappingJson);
        var oracle = await _oracle.SendAsync(request);

        var mockifyr = new MockifyrUnderTest(Files());
        mockifyr.ImportMappingJson(mappingJson);
        var mine = mockifyr.Send(request);

        var diff = ResponseDiffer.Compare(oracle, mine, ["Content-Type"]);
        Assert.True(diff.IsMatch, diff.ToString());
    }

    [Fact]
    public Task A_file_backed_body_is_served_like_the_oracle_serves_it() => VerifyAsync(
        """
        {"request":{"method":"GET","urlPath":"/orders/A-1"},
         "response":{"status":200,"bodyFileName":"order.json","headers":{"Content-Type":"application/json"}}}
        """,
        new RequestSpec { Method = "GET", Url = "/orders/A-1" });

    [Fact]
    public Task A_file_backed_body_is_templated_when_the_transformer_is_declared() => VerifyAsync(
        """
        {"request":{"method":"GET","urlPath":"/templated"},
         "response":{"status":200,"bodyFileName":"templated.json","transformers":["response-template"],
                     "headers":{"Content-Type":"application/json"}}}
        """,
        new RequestSpec { Method = "GET", Url = "/templated" });

    [Fact]
    public Task A_file_backed_body_is_NOT_templated_without_the_transformer() => VerifyAsync(
        """
        {"request":{"method":"GET","urlPath":"/raw"},
         "response":{"status":200,"bodyFileName":"templated.json","headers":{"Content-Type":"application/json"}}}
        """,
        new RequestSpec { Method = "GET", Url = "/raw" });

    [Fact]
    public Task An_inline_body_and_a_file_name_together_resolve_the_way_the_oracle_resolves_them() => VerifyAsync(
        // Precedence is a question, not an assumption — the oracle answers it, and this test records
        // the answer so a later refactor cannot quietly change sides.
        """
        {"request":{"method":"GET","urlPath":"/both"},
         "response":{"status":200,"body":"inline wins?","bodyFileName":"order.json"}}
        """,
        new RequestSpec { Method = "GET", Url = "/both" });

    [Fact]
    public async Task A_missing_file_is_an_error_on_both_sides()
    {
        const string Mapping =
            """
            {"request":{"method":"GET","urlPath":"/missing"},
             "response":{"status":200,"bodyFileName":"not-there.json"}}
            """;
        var request = new RequestSpec { Method = "GET", Url = "/missing" };

        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(Mapping);
        var oracle = await _oracle.SendAsync(request);

        var mockifyr = new MockifyrUnderTest(Files());
        mockifyr.ImportMappingJson(Mapping);
        var mine = mockifyr.Send(request);

        // The finding that changed the design: the oracle answers 500, not 200-with-an-empty-body.
        // A missing file is a misconfigured deployment, and an empty 200 reads as a matching problem
        // — the silent-failure shape 1.0 set out to remove. So the STATUS is compared, and only the
        // status: the oracle's body here is its own HTML error page, which is presentation rather
        // than dialect, and copying it would be imitating a product instead of matching a behaviour.
        Assert.Equal(500, oracle.Status);
        Assert.Equal(oracle.Status, mine.Status);
        Assert.Contains("not-there.json", Encoding.UTF8.GetString(mine.Body));
    }

    /// <summary>A file store over an in-memory dictionary — the harness's mirror of the container's files.</summary>
    private sealed class InMemoryBodyFiles(IReadOnlyDictionary<string, string> files) : IResponseBodyFiles
    {
        public byte[]? Read(string name) =>
            files.TryGetValue(name, out var content) ? Encoding.UTF8.GetBytes(content) : null;
    }
}
