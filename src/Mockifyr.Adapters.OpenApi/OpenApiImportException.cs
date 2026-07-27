namespace Mockifyr.Adapters.OpenApi;

/// <summary>The typed refusal codes of the OpenAPI importer (G19c, ADR 0011 addendum).</summary>
public enum OpenApiImportError
{
    /// <summary>The spec text exceeds the size guard — a spec bomb, refused before parsing.</summary>
    TooLarge,

    /// <summary>A <c>$ref</c> points outside the document (URL or file) — never fetched (no SSRF).</summary>
    ExternalRef,

    /// <summary>The document does not parse as OpenAPI 3.x.</summary>
    Invalid,

    /// <summary>The document parses but declares no operations to import.</summary>
    Empty,

    /// <summary>Schema recursion exceeded the depth guard (cyclic or absurdly nested schemas).</summary>
    TooDeep,
}

/// <summary>
/// Raised when a spec cannot be imported. <see cref="Pointer"/> names the offending reference or
/// location when one exists, so the operator fixes the spec instead of guessing.
/// </summary>
public sealed class OpenApiImportException(OpenApiImportError error, string message, string? pointer = null)
    : Exception(message)
{
    /// <summary>The typed refusal.</summary>
    public OpenApiImportError Error { get; } = error;

    /// <summary>The offending <c>$ref</c>/location, when known.</summary>
    public string? Pointer { get; } = pointer;
}
