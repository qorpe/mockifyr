using System.IO.Compression;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Outbound;

/// <summary>A recorded exchange: the generated stub JSON plus the response that was captured.</summary>
public sealed record RecordedExchange(string StubJson, CanonicalResponse CapturedResponse);

/// <summary>
/// Record mode (G9, verified by the differential suite): proxy a request to the target upstream,
/// capture the response, and generate a stub that replays it. Reuses <see cref="ProxyResponder"/> for the outbound
/// call (I/O at the facade edge) and <see cref="RecordingJsonWriter"/> for the stub JSON. Filters,
/// body-file extraction, and repeat-request → scenario generation are deferred.
/// </summary>
public sealed class StubRecorder(HttpClient? client = null)
{
    private readonly ProxyResponder _proxy = new(client);

    public async Task<RecordedExchange> RecordAsync(
        string targetBaseUrl, CanonicalRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _proxy.ProxyAsync(new ProxyDirective(targetBaseUrl), request, cancellationToken)
            .ConfigureAwait(false);

        // The caller gets the wire response untouched; only the GENERATED STUB sees the decoded copy.
        return new RecordedExchange(RecordingJsonWriter.ToStubJson(request, DecodedForStub(response)), response);
    }

    /// <summary>
    /// The stub must hold the payload a client would SEE, not the bytes that crossed the wire: a
    /// compressed upstream body (a browser sends Accept-Encoding, real APIs compress) written as-is
    /// becomes mojibake through the UTF-8 round-trip and can never replay. The oracle records the
    /// replayable payload, so the body is decoded and the Content-Encoding header dropped; an
    /// encoding that cannot be decoded falls back to the raw capture unchanged.
    /// </summary>
    private static CanonicalResponse DecodedForStub(CanonicalResponse response)
    {
        var encoding = response.Headers["Content-Encoding"].FirstOrDefault();
        if (encoding is null || response.Body.Length == 0)
        {
            return response;
        }

        var decoded = TryDecode(response.Body, encoding.Trim());
        if (decoded is null)
        {
            return response;
        }

        var headers = response.Headers
            .Where(group => !group.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => group.Select(value => new KeyValuePair<string, string>(group.Key, value)))
            .ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return new CanonicalResponse { Status = response.Status, Headers = headers, Body = decoded };
    }

    private static byte[]? TryDecode(byte[] body, string encoding)
    {
        try
        {
            using var source = new MemoryStream(body);
            using Stream? decoder = encoding.ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(source, CompressionMode.Decompress),
                "deflate" => new ZLibStream(source, CompressionMode.Decompress),
                "br" => new BrotliStream(source, CompressionMode.Decompress),
                _ => null,
            };

            if (decoder is null)
            {
                return null;
            }

            using var output = new MemoryStream();
            decoder.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return null;
        }
    }
}
