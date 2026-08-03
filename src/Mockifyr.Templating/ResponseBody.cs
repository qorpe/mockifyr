using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>
/// Decides what bytes a response definition actually carries, once <c>bodyFileName</c> exists.
/// </summary>
internal static class ResponseBody
{
    /// <summary>
    /// The bytes a response carries: the inline body when the stub has one, else the named file.
    /// Null means the stub named a file the host does not have — the caller must answer 500.
    /// </summary>
    /// <remarks>
    /// Inline wins over the file name; the reference engine's precedence, verified differentially
    /// rather than assumed.
    ///
    /// A named file that is missing is an ERROR, not an empty body — also the oracle's behaviour, and
    /// the first design I tried got this wrong. Serving 200 with nothing in it is the silent-failure
    /// shape this project spent 1.0 removing: it reads as a matching problem and is a misconfigured
    /// deployment. A 500 says which.
    /// </remarks>
    /// <summary>
    /// The response for a stub whose body file the host does not have. Matches the reference engine's
    /// status; the body is deliberately ours rather than a copy of their HTML error page, which is
    /// their presentation and not part of the dialect.
    /// </summary>
    public static CanonicalResponse MissingFile(ResponseDefinition definition) => new()
    {
        Status = 500,
        Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
        Body = System.Text.Encoding.UTF8.GetBytes(
            $"Mockifyr: the response body file '{definition.BodyFileName}' was not found."),
    };

    public static byte[]? Resolve(ResponseDefinition definition, IResponseBodyFiles files)
    {
        if (definition.Body is { } inline)
        {
            return inline;
        }

        return definition.BodyFileName is { Length: > 0 } name
            ? files.Read(name)
            : [];
    }
}
