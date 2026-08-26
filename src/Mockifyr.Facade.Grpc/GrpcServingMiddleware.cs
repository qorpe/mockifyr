using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Mockifyr.Core;

namespace Mockifyr.Facade.Grpc;

/// <summary>
/// The gRPC serving facade (G13). Intercepts <c>application/grpc</c> requests, decodes the protobuf
/// message to JSON via the descriptor-driven <see cref="ProtobufJsonCodec"/>, drives it through the
/// unchanged <see cref="StubEngine"/> as a POST to <c>/{service}/{method}</c> with a JSON body (so
/// <c>equalToJson</c> matching and <c>jsonBody</c> responses just work), then encodes the matched
/// response JSON back to protobuf and frames it as a gRPC reply with a <c>grpc-status</c> trailer.
/// Non-gRPC requests fall through to the next middleware (the HTTP mock-serving fallback).
/// </summary>
public sealed class GrpcServingMiddleware(
    RequestDelegate next, StubEngine engine, ProtoDescriptors descriptors, TenantHeaderOptions tenantHeader)
{
    private const string GrpcContentType = "application/grpc";

    // gRPC status codes used here (google.rpc.Code).
    private const int StatusOk = 0;
    private const int StatusUnimplemented = 12;

    // The gRPC extension expresses an error response via headers: `grpc-status-name` names the status
    // code, `grpc-status-reason` carries the optional message. Mirror the same DSL (G13d).
    private const string GrpcStatusNameHeader = "grpc-status-name";
    private const string GrpcStatusReasonHeader = "grpc-status-reason";

    private static readonly IReadOnlyDictionary<string, int> StatusCodesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["OK"] = 0,
        ["CANCELLED"] = 1,
        ["UNKNOWN"] = 2,
        ["INVALID_ARGUMENT"] = 3,
        ["DEADLINE_EXCEEDED"] = 4,
        ["NOT_FOUND"] = 5,
        ["ALREADY_EXISTS"] = 6,
        ["PERMISSION_DENIED"] = 7,
        ["RESOURCE_EXHAUSTED"] = 8,
        ["FAILED_PRECONDITION"] = 9,
        ["ABORTED"] = 10,
        ["OUT_OF_RANGE"] = 11,
        ["UNIMPLEMENTED"] = 12,
        ["INTERNAL"] = 13,
        ["UNAVAILABLE"] = 14,
        ["DATA_LOSS"] = 15,
        ["UNAUTHENTICATED"] = 16,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (context.Request.Method != HttpMethods.Post || contentType is null ||
            !contentType.StartsWith(GrpcContentType, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        context.Response.ContentType = GrpcContentType;

        var method = descriptors.Resolve(context.Request.Path.Value ?? string.Empty);
        if (method is null)
        {
            WriteStatus(context, StatusUnimplemented, "Method not found");
            return;
        }

        var requestMessage = await ReadFrameAsync(context.Request.Body, context.RequestAborted);
        var requestJson = ProtobufJsonCodec.Decode(method.InputType, requestMessage);

        var tenant = context.Request.Headers.TryGetValue(tenantHeader.Name, out var t) && !string.IsNullOrEmpty(t)
            ? new TenantId(t!)
            : TenantId.Default;

        var canonical = CanonicalRequestBuilder.Build(
            "POST",
            context.Request.Path.Value ?? string.Empty,
            [new KeyValuePair<string, string>("Content-Type", "application/json")],
            Encoding.UTF8.GetBytes(requestJson.ToJsonString()));

        var resolution = engine.Handle(tenant, canonical);
        if (resolution.Response is not { } response)
        {
            WriteStatus(context, StatusUnimplemented, "No matching stub");
            return;
        }

        // An error response (the extension's `grpc-status-name` header) replies with that status and no
        // message frame — the response body is not delivered on an error, matching the oracle.
        if (response.Headers[GrpcStatusNameHeader].FirstOrDefault() is { } statusName &&
            StatusCodesByName.TryGetValue(statusName, out var errorCode))
        {
            WriteStatus(context, errorCode, response.Headers[GrpcStatusReasonHeader].FirstOrDefault());
            return;
        }

        var responseJson = response.Body.Length > 0
            ? JsonNode.Parse(response.Body)!.AsObject()
            : new JsonObject();
        var responseMessage = ProtobufJsonCodec.Encode(method.OutputType, responseJson);

        await WriteFrameAsync(context.Response.Body, responseMessage, context.RequestAborted);
        WriteStatus(context, StatusOk, message: null);
    }

    // A gRPC data frame: [compressed flag: 1][length: 4 BE][message].
    private static async Task<byte[]> ReadFrameAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (bytes.Length < 5)
        {
            return [];
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
        return bytes.AsSpan(5, (int)length).ToArray();
    }

    private static async Task WriteFrameAsync(Stream body, byte[] message, CancellationToken cancellationToken)
    {
        var frame = new byte[5 + message.Length];
        frame[0] = 0; // not compressed
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(frame.AsSpan(5));
        await body.WriteAsync(frame, cancellationToken);
    }

    // gRPC carries the call result in the grpc-status trailer (HTTP status is always 200).
    private static void WriteStatus(HttpContext context, int status, string? message)
    {
        context.Response.AppendTrailer("grpc-status", status.ToString());
        if (!string.IsNullOrEmpty(message))
        {
            context.Response.AppendTrailer("grpc-message", message);
        }
    }
}
