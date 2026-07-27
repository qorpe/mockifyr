using Mediant.Results;
using Mockifyr.Core;

namespace Mockifyr.Application;

/// <summary>
/// The single definition of what makes a resource operation acceptable, shared by every resource
/// handler (and mutation-tested per the ADR 0011 addendum). Collections are identifier-shaped so
/// the G19b state directive can reference them unambiguously; ids are opaque but bounded; bodies
/// must be well-formed JSON under the configured size cap — Core itself never parses them.
/// </summary>
internal static class ResourceRules
{
    /// <summary>Ids are opaque keys, but bounded — a URL segment, not a payload.</summary>
    public const int MaxIdLength = 256;

    public static Error? ValidateCollection(string collection)
    {
        if (!ReservedEnvironmentKeys.IsWellFormed(collection) || collection.Length > 64)
        {
            return Error.Validation(
                "Resource.InvalidCollection",
                "A collection name must start with a letter or underscore, contain only letters, digits, underscores or hyphens, and be at most 64 characters.");
        }

        return null;
    }

    public static Error? ValidateId(string id)
    {
        if (id.Length == 0 || id.Length > MaxIdLength || id.Any(char.IsControl))
        {
            return Error.Validation(
                "Resource.InvalidId",
                $"An id must be 1..{MaxIdLength} characters and contain no control characters.");
        }

        return null;
    }

    public static Error? ValidateBody(string body, ResourceOptions options)
    {
        if (ResourceGuards.ExceedsCap(body, options.MaxBodyBytes))
        {
            return Error.Validation(
                "Resource.BodyTooLarge",
                $"The document exceeds the {options.MaxBodyBytes}-byte body cap.");
        }

        if (!ResourceGuards.IsWellFormedJson(body))
        {
            return Error.Validation("Resource.InvalidBody", "The document body is not well-formed JSON.");
        }

        return null;
    }
}
