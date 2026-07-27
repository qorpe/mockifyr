using Microsoft.OpenApi.Models;
using Mockifyr.Adapters.OpenApi;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Direct coverage for <see cref="SchemaSample"/>'s corners the spec-level goldens cannot reach
/// cheaply: absent schemas/items, the exact depth-guard boundary, type-less objects, and allOf
/// composition.
/// </summary>
public sealed class G19cSchemaSampleTests
{
    [Fact]
    public void An_absent_schema_is_json_null_and_an_itemless_array_is_empty()
    {
        Assert.Equal("null", SchemaSample.Write(null));
        Assert.Equal("[]", SchemaSample.Write(new OpenApiSchema { Type = "array" }));
    }

    [Fact]
    public void A_typeless_schema_with_properties_still_writes_an_object()
    {
        var schema = new OpenApiSchema
        {
            Properties = new Dictionary<string, OpenApiSchema> { ["n"] = new() { Type = "integer" } },
        };

        Assert.Equal("""{"n":1}""", SchemaSample.Write(schema));
    }

    [Fact]
    public void AllOf_merges_every_branch_in_order()
    {
        var schema = new OpenApiSchema
        {
            AllOf =
            [
                new OpenApiSchema { Properties = new Dictionary<string, OpenApiSchema> { ["a"] = new() { Type = "boolean" } } },
                new OpenApiSchema { Properties = new Dictionary<string, OpenApiSchema> { ["b"] = new() { Type = "number" } } },
            ],
        };

        Assert.Equal("""{"a":true,"b":1.5}""", SchemaSample.Write(schema));
    }

    [Fact]
    public void Every_string_format_maps_to_its_documented_sample()
    {
        static string For(string? format) => SchemaSample.Write(new OpenApiSchema { Type = "string", Format = format });

        Assert.Equal("\"{{randomValue type='UUID'}}\"", For("uuid"));
        Assert.Equal("\"{{random 'Internet.emailAddress'}}\"", For("email"));
        Assert.Equal("\"{{random 'Internet.url'}}\"", For("uri"));
        Assert.Equal("\"{{random 'Internet.url'}}\"", For("url"));
        Assert.Equal("\"2026-01-01T12:00:00Z\"", For("date-time"));
        Assert.Equal("\"2026-01-01\"", For("date"));
        Assert.Equal("\"string\"", For(null));
    }

    [Fact]
    public void A_propertyless_object_writes_an_empty_object_and_the_refusal_message_is_explained()
    {
        Assert.Equal("{}", SchemaSample.Write(new OpenApiSchema { Type = "object" }));

        var deep = new OpenApiSchema { Type = "string" };
        for (var i = 0; i <= SchemaSample.MaxDepth; i++)
        {
            deep = new OpenApiSchema { Type = "array", Items = deep };
        }

        var refusal = Assert.Throws<OpenApiImportException>(() => SchemaSample.Write(deep));
        Assert.False(string.IsNullOrEmpty(refusal.Message));
    }

    [Fact]
    public void The_depth_guard_boundary_is_exact()
    {
        static OpenApiSchema Nested(int depth)
        {
            var schema = new OpenApiSchema { Type = "string" };
            for (var i = 0; i < depth; i++)
            {
                schema = new OpenApiSchema { Type = "array", Items = schema };
            }

            return schema;
        }

        // MaxDepth nested arrays still render; one more is the typed refusal.
        Assert.StartsWith("[", SchemaSample.Write(Nested(SchemaSample.MaxDepth)));
        var refusal = Assert.Throws<OpenApiImportException>(() => SchemaSample.Write(Nested(SchemaSample.MaxDepth + 1)));
        Assert.Equal(OpenApiImportError.TooDeep, refusal.Error);
    }
}
