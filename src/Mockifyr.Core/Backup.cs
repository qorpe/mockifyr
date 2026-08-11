using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mockifyr.Core;

/// <summary>
/// One tenant's durable state, in the shape the backup archive is written and read in (#252).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is state an operator <em>authored</em> — stubs, environment keys, sandbox
/// documents, the credentials handed to consumers, and where each scenario currently stands.
/// Deliberately absent: the request journal, the message inbox and quota counters. Those are
/// observations of what happened, not configuration; restoring them would fabricate a history the new
/// host never served, and they are bounded and disposable by design.
/// </para>
/// <para>
/// Host-level configuration (outbound trust, TLS, CLI flags) is not here either. It is deployment
/// configuration that belongs with the Helm values, and a tenant-scoped archive that could carry it
/// would be a hole in the tenant boundary — a tenant principal must not read or write host trust.
/// </para>
/// </remarks>
/// <param name="Tenant">The tenant the archive was taken from.</param>
/// <param name="CreatedAt">When it was taken.</param>
/// <param name="Mappings">Stub mappings as their authored JSON.</param>
/// <param name="Environments">Environment keys with their values and which one is active.</param>
/// <param name="Resources">Sandbox documents, grouped by collection.</param>
/// <param name="ApiKeys">Sandbox API keys, verifier included — see <see cref="BackupArchive"/> remarks.</param>
/// <param name="Scenarios">Scenario name → current state, for scenarios not in their initial state.</param>
public sealed record BackupArchive(
    TenantId Tenant,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Mappings,
    IReadOnlyList<EnvironmentKey> Environments,
    IReadOnlyList<ResourceDocument> Resources,
    IReadOnlyList<ApiKey> ApiKeys,
    IReadOnlyDictionary<string, string> Scenarios)
{
    /// <summary>
    /// The archive format version. Bumped only for a change a previous reader could not handle; a
    /// reader refuses a version it does not know rather than guessing at the contents.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>The property carrying <see cref="FormatVersion"/>, and the marker that this is an archive at all.</summary>
    public const string VersionProperty = "mockifyrBackup";
}

/// <summary>
/// Reads and writes <see cref="BackupArchive"/> as JSON (#252). Pure and I/O-free: the handlers
/// gather and apply state, this decides only what the bytes look like.
/// </summary>
public static class BackupJson
{
    /// <summary>Serializes the archive.</summary>
    public static string Write(BackupArchive archive)
    {
        var root = new JsonObject
        {
            [BackupArchive.VersionProperty] = BackupArchive.FormatVersion,
            ["tenant"] = archive.Tenant.Value,
            ["createdAt"] = archive.CreatedAt.ToString("O"),
        };

        // A mapping is stored as its authored JSON and copied through verbatim: an archive that
        // re-serialized stubs would quietly rewrite what the operator wrote.
        var mappings = new JsonArray();
        foreach (var mapping in archive.Mappings)
        {
            mappings.Add(JsonNode.Parse(mapping));
        }

        root["mappings"] = mappings;

        var environments = new JsonArray();
        foreach (var key in archive.Environments)
        {
            var values = new JsonArray();
            foreach (var value in key.Values)
            {
                // A secret literal never enters a bundle (#348). Bundles are exactly the artefact that
                // gets attached to a ticket and committed to a repository, so redaction that stopped at
                // the API would be redaction that stopped short of where the leak happens. The marker
                // stays so a restore can report what it could not carry rather than inventing "".
                values.Add(value.Secret
                    ? new JsonObject { ["name"] = value.Name, ["secret"] = true }
                    : new JsonObject { ["name"] = value.Name, ["value"] = value.Value, ["secret"] = false });
            }

            environments.Add(new JsonObject
            {
                ["key"] = key.Key,
                ["activeValue"] = key.ActiveValue,
                ["values"] = values,
            });
        }

        root["environments"] = environments;

        var resources = new JsonArray();
        foreach (var document in archive.Resources)
        {
            resources.Add(new JsonObject
            {
                ["collection"] = document.Collection,
                ["id"] = document.Id,
                ["body"] = JsonNode.Parse(document.Body),
            });
        }

        root["resources"] = resources;

        // The salted verifier travels with the key so a restored host keeps honoring credentials
        // consumers already hold — the whole point of a restore. The token itself was never stored and
        // cannot appear here. This is what makes the archive a secret: treat it like a key file.
        var apiKeys = new JsonArray();
        foreach (var key in archive.ApiKeys)
        {
            var entry = new JsonObject
            {
                ["id"] = key.Id,
                ["name"] = key.Name,
                ["salt"] = key.Salt,
                ["hash"] = key.Hash,
                ["prefix"] = key.Prefix,
                ["createdAt"] = key.CreatedAt.ToString("O"),
            };
            if (key.QuotaPerHour is { } quota)
            {
                entry["quotaPerHour"] = quota;
            }

            apiKeys.Add(entry);
        }

        root["apiKeys"] = apiKeys;

        var scenarios = new JsonObject();
        foreach (var (name, state) in archive.Scenarios)
        {
            scenarios[name] = state;
        }

        root["scenarios"] = scenarios;

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Parses an archive, or returns null when the text is not one this reader understands — malformed
    /// JSON, a missing version marker, or a format version from a newer Mockifyr.
    /// </summary>
    public static BackupArchive? Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        // No marker means this is some other document — a mapping bundle, a spec, an unrelated file.
        // Restoring from one would be a silent, destructive guess.
        if (obj[BackupArchive.VersionProperty] is not JsonValue versionValue ||
            !versionValue.TryGetValue<int>(out var version) ||
            version > BackupArchive.FormatVersion)
        {
            return null;
        }

        var tenant = obj["tenant"]?.GetValue<string>() is { Length: > 0 } name
            ? new TenantId(name)
            : TenantId.Default;
        var createdAt = obj["createdAt"]?.GetValue<string>() is { } stamp &&
            DateTimeOffset.TryParse(stamp, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

        var mappings = new List<string>();
        foreach (var mapping in obj["mappings"] as JsonArray ?? [])
        {
            if (mapping is not null)
            {
                mappings.Add(mapping.ToJsonString());
            }
        }

        var environments = new List<EnvironmentKey>();
        foreach (var node in obj["environments"] as JsonArray ?? [])
        {
            if (node is not JsonObject entry || entry["key"]?.GetValue<string>() is not { Length: > 0 } key)
            {
                continue;
            }

            var values = new List<EnvironmentValue>();
            foreach (var valueNode in entry["values"] as JsonArray ?? [])
            {
                if (valueNode is not JsonObject value ||
                    value["name"]?.GetValue<string>() is not { } valueName)
                {
                    continue;
                }

                var secret = value["secret"]?.GetValue<bool>() ?? false;
                if (value["value"]?.GetValue<string>() is { } literal)
                {
                    values.Add(new EnvironmentValue(valueName, literal, secret));
                }
                else if (secret)
                {
                    // Exported redacted (#348). Carried through as a secret with no literal so the
                    // restore reports it rather than writing an empty string that would leave a stub
                    // signing with nothing and reporting success.
                    values.Add(new EnvironmentValue(valueName, string.Empty, Secret: true));
                }
            }

            environments.Add(new EnvironmentKey(key, entry["activeValue"]?.GetValue<string>() ?? "", values));
        }

        var resources = new List<ResourceDocument>();
        foreach (var node in obj["resources"] as JsonArray ?? [])
        {
            if (node is JsonObject document &&
                document["collection"]?.GetValue<string>() is { Length: > 0 } collection &&
                document["id"]?.GetValue<string>() is { Length: > 0 } id &&
                document["body"] is { } body)
            {
                resources.Add(new ResourceDocument(
                    id, collection, body.ToJsonString(), createdAt, createdAt, Version: 1));
            }
        }

        var apiKeys = new List<ApiKey>();
        foreach (var node in obj["apiKeys"] as JsonArray ?? [])
        {
            if (node is not JsonObject key ||
                key["id"]?.GetValue<string>() is not { Length: > 0 } id ||
                key["salt"]?.GetValue<string>() is not { Length: > 0 } salt ||
                key["hash"]?.GetValue<string>() is not { Length: > 0 } hash)
            {
                continue;
            }

            var quota = key["quotaPerHour"] is JsonValue quotaValue && quotaValue.TryGetValue<int>(out var perHour)
                ? perHour
                : (int?)null;
            var created = key["createdAt"]?.GetValue<string>() is { } keyStamp &&
                DateTimeOffset.TryParse(keyStamp, out var keyCreated)
                    ? keyCreated
                    : createdAt;

            apiKeys.Add(new ApiKey(
                id, tenant, key["name"]?.GetValue<string>() ?? id, salt, hash,
                key["prefix"]?.GetValue<string>() ?? "", created, quota));
        }

        var scenarios = new Dictionary<string, string>(StringComparer.Ordinal);
        if (obj["scenarios"] is JsonObject scenarioObject)
        {
            foreach (var (scenario, state) in scenarioObject)
            {
                if (state?.GetValue<string>() is { Length: > 0 } value)
                {
                    scenarios[scenario] = value;
                }
            }
        }

        return new BackupArchive(tenant, createdAt, mappings, environments, resources, apiKeys, scenarios);
    }
}
