namespace Mockifyr.Core;

/// <summary>
/// Reads response bodies the host holds as files — the seam behind the dialect's
/// <c>bodyFileName</c>.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a path, because Core does no I/O: it knows a stub named a body, not where bodies
/// live. The host binds this to a directory; a library embedding could bind it to resources, a
/// database, or nothing at all.
/// </para>
/// <para>
/// Resolved on every request rather than captured at import, which is what the reference engine does
/// and what people expect: editing the file changes the next response, with no reload. The cost is a
/// read per request, which the implementation is free to cache.
/// </para>
/// </remarks>
public interface IResponseBodyFiles
{
    /// <summary>
    /// The named body, or null when it does not exist or the name is not one this store will serve.
    /// Never throws: a stub naming a missing file must degrade to an empty body, not a 500 — the name
    /// comes from authored input, and an exception here would take down the request path.
    /// </summary>
    byte[]? Read(string name);
}

/// <summary>
/// The default store: it holds nothing. A host with no file store configured serves an empty body for
/// a stub that names one, which is what the import warning tells the author will happen.
/// </summary>
public sealed class NoResponseBodyFiles : IResponseBodyFiles
{
    /// <inheritdoc />
    public byte[]? Read(string name) => null;
}
