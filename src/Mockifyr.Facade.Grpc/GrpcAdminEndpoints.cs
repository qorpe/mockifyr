using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mockifyr.Facade.Grpc;

/// <summary>
/// Admin management of the compiled proto descriptors (G18-pre, ADR 0010): list the loaded
/// services/methods, upload a <c>*.dsc</c>, delete one — writing to the conventional
/// <c>&lt;root-dir&gt;/grpc/</c> directory and hot-reloading the shared <see cref="ProtoDescriptors"/>
/// index, so a freshly uploaded descriptor serves without a restart. Registered by the composition
/// root next to the other facade-owned admin surfaces (the WebSocket facade's
/// <c>/__admin/message-mappings</c> is the precedent).
/// </summary>
public static class GrpcAdminEndpoints
{
    /// <summary>Maps <c>/__admin/grpc/descriptors</c> (GET/POST/DELETE) backed by <paramref name="grpcDirectory"/>.</summary>
    public static IEndpointRouteBuilder MapGrpcAdminEndpoints(
        this IEndpointRouteBuilder app, ProtoDescriptors descriptors, string grpcDirectory)
    {
        app.MapGet("/__admin/grpc/descriptors", () => Results.Json(new
        {
            descriptors = ListFiles(grpcDirectory),
            services = descriptors.Methods
                .GroupBy(m => m.Service)
                .Select(g => new { service = g.Key, methods = g.Select(MethodShape).ToList() })
                .ToList(),
        }));

        // The body is the raw descriptor-set bytes (protoc --descriptor_set_out). ?name= names the
        // stored file; ".dsc" is appended when missing. The set is validated by parsing before anything
        // is written, so a bad upload can never wedge serving.
        app.MapPost("/__admin/grpc/descriptors", async (HttpRequest request) =>
        {
            var name = SafeName(request.Query["name"].FirstOrDefault());
            if (name is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "A ?name= query parameter is required (the stored descriptor file name).");
            }

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            if (bytes.Length == 0 || !TryParse(bytes))
            {
                return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "The body is not a valid compiled descriptor set (*.dsc).");
            }

            Directory.CreateDirectory(grpcDirectory);
            await File.WriteAllBytesAsync(Path.Combine(grpcDirectory, name), bytes);
            descriptors.Reload(ReadAll(grpcDirectory));
            return Results.Json(new { name, serving = descriptors.HasMethods }, statusCode: StatusCodes.Status201Created);
        });

        app.MapDelete("/__admin/grpc/descriptors/{name}", (string name) =>
        {
            var safe = SafeName(name);
            var path = safe is null ? null : Path.Combine(grpcDirectory, safe);
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            File.Delete(path);
            descriptors.Reload(ReadAll(grpcDirectory));
            return Results.Ok(new { serving = descriptors.HasMethods });
        });

        return app;
    }

    private static object MethodShape(GrpcMethod method) => new
    {
        method = method.Method,
        path = $"/{method.Service}/{method.Method}",
        input = method.InputType.FullName,
        output = method.OutputType.FullName,
    };

    private static List<object> ListFiles(string grpcDirectory) =>
        Directory.Exists(grpcDirectory)
            ? Directory.EnumerateFiles(grpcDirectory, "*.dsc")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (object)new { name = Path.GetFileName(path), size = new FileInfo(path).Length })
                .ToList()
            : [];

    /// <summary>Reads every <c>*.dsc</c> in the directory, in ordinal filename order (the startup shape).</summary>
    public static IReadOnlyList<byte[]> ReadAll(string grpcDirectory) =>
        Directory.Exists(grpcDirectory)
            ? [.. Directory.EnumerateFiles(grpcDirectory, "*.dsc").OrderBy(p => p, StringComparer.Ordinal).Select(File.ReadAllBytes)]
            : [];

    // The stored name must be a bare file name — no separators, no traversal — and end in .dsc.
    private static string? SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..", StringComparison.Ordinal) ||
            name.IndexOfAny(['/', '\\']) >= 0)
        {
            return null;
        }

        return name.EndsWith(".dsc", StringComparison.OrdinalIgnoreCase) ? name : name + ".dsc";
    }

    private static bool TryParse(byte[] bytes)
    {
        try
        {
            var set = FileDescriptorSet.Parser.ParseFrom(bytes);
            return set.File.Count > 0;
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return false;
        }
    }
}
