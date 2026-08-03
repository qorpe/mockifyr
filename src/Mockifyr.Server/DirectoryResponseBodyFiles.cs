using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// Serves <c>bodyFileName</c> bodies from a directory — by convention <c>&lt;root-dir&gt;/__files</c>,
/// matching the layout of the mapping sets this dialect comes from.
/// </summary>
/// <remarks>
/// <para>
/// The file name arrives from a stub, and a stub can be authored by anyone who can reach the admin
/// API. So the name is treated as hostile: the resolved path must sit inside the root, or nothing is
/// served. Without that check a stub could name <c>../../etc/passwd</c> and the mock host would
/// happily read it out — turning a convenience feature into arbitrary file disclosure.
/// </para>
/// <para>
/// Read per request, deliberately: editing a body file changes the next response with no reload,
/// which is what the reference engine does and what anyone iterating on a fixture expects. A miss
/// (absent file, refused name) returns null, and the renderer degrades to an empty body — the request
/// path must not throw over a name someone typed.
/// </para>
/// </remarks>
public sealed class DirectoryResponseBodyFiles(string rootDirectory) : IResponseBodyFiles
{
    private readonly string _root = Path.GetFullPath(rootDirectory);

    /// <inheritdoc />
    public byte[]? Read(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            // Rooted or drive-qualified names never resolve against the root, so they are refused
            // outright rather than combined — Path.Combine would silently return the absolute path.
            if (Path.IsPathRooted(name))
            {
                return null;
            }

            var resolved = Path.GetFullPath(Path.Combine(_root, name));

            // The containment check is on the RESOLVED path, so `a/../../secret` is caught after
            // normalisation rather than by pattern-matching for "..", which is the check people write
            // and attackers walk around.
            var boundary = _root.EndsWith(Path.DirectorySeparatorChar)
                ? _root
                : _root + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(boundary, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"mockifyr: refused a bodyFileName that resolves outside the file store ('{name}').");
                return null;
            }

            return File.Exists(resolved) ? File.ReadAllBytes(resolved) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable or malformed name is a miss, never an exception on the serving path.
            return null;
        }
    }
}
