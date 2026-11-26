using System.Text.Json;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// Loads stub mappings from a directory of JSON files at startup (G12f, verified by the
/// differential suite) using the <c>&lt;root-dir&gt;/mappings/*.json</c> layout Mockifyr
/// serves from. Each file is a single stub or a
/// <c>{"mappings":[…]}</c> bundle; files are read in filename order for deterministic ids/priority
/// tie-breaks. This is the <see cref="IMappingsLoader"/> extension seam (public since G10), wired
/// here for the standalone host. It does file I/O, so it lives at the host edge — never in Core.
/// </summary>
/// <remarks>
/// <see cref="LoadAllTenants"/> also reads the tenants <see cref="FileSystemStubPersistence"/> wrote (#467): a
/// subdirectory <c>mappings/&lt;tenant&gt;/</c> is read as that tenant only when EVERY entry in it is a file this
/// provider writes — <c>&lt;guid&gt;.json</c> holding one stub whose <c>id</c> is that guid, and the
/// <see cref="TenantPathSegment.MarkerFileName"/> that says which release named the directory. Any other subdirectory (a
/// hand-organised tree, notes beside the stubs, a bundle, a file that does not parse, a nested directory, one named
/// like the default tenant) is skipped whole, logged once, and listed in <see cref="SkippedDirectories"/>, so a
/// user's own layout can never start creating tenants. The flat files stay the default tenant's, read as before.
/// </remarks>
public sealed class DirectoryMappingsLoader(string mappingsDirectory, IMatcherRegistry? matchers = null) : IMappingsLoader, IMultiTenantMappingsLoader
{
    private readonly object _gate = new();
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

    /// <summary>The subdirectories <see cref="LoadAllTenants"/> did not read as tenants, and why — each logged once.</summary>
    public IReadOnlyCollection<string> SkippedDirectories
    {
        get
        {
            lock (_gate)
            {
                return [.. _skipped];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> Load(TenantId tenant)
    {
        if (!Directory.Exists(mappingsDirectory))
        {
            return [];
        }

        var stubs = new List<StubMapping>();
        foreach (var file in Directory.EnumerateFiles(mappingsDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            stubs.AddRange(MappingJsonReader.Read(File.ReadAllText(file), tenant, matchers));
        }

        return stubs;
    }

    /// <summary>
    /// The default tenant's stubs (the flat files, as <see cref="Load"/> reads them) and every tenant whose
    /// subdirectory holds only files <see cref="FileSystemStubPersistence"/> wrote (#467).
    /// </summary>
    public IReadOnlyList<StubMapping> LoadAllTenants()
    {
        var stubs = new List<StubMapping>(Load(TenantId.Default));
        if (!Directory.Exists(mappingsDirectory))
        {
            return stubs;
        }

        foreach (var directory in Directory.EnumerateDirectories(mappingsDirectory).OrderBy(path => path, StringComparer.Ordinal).ToList())
        {
            var name = Path.GetFileName(directory);
            if (ReadTenant(directory, name, out var reason) is not { } tenantStubs)
            {
                Skip($"{name}: {reason}");
            }
            else if (!TenantPathSegment.TryResolveDirectory(
                         directory, "mappings", TenantPathSegment.TryDecodeEarlierRawName, out var notice, out _, out _))
            {
                // Only now, with every file proven the provider's: a directory an earlier release named by the raw tenant
                // value is renamed to this release's name, so the next delete reaches the file the reload read (#467).
                Skip(notice["mappings/".Length..], line: notice);
            }
            else
            {
                if (notice.Length > 0)
                {
                    Console.WriteLine($"mockifyr: {notice}");
                }

                stubs.AddRange(tenantStubs);
            }
        }

        return stubs;
    }

    // All or nothing: one entry this provider would not have written means the directory is not one of its tenants.
    private List<StubMapping>? ReadTenant(string directory, string name, out string reason)
    {
        if (string.Equals(name, TenantId.Default.Value, StringComparison.Ordinal))
        {
            reason = "the default tenant's stubs are the flat files, so a directory of that name is not one this provider wrote";
            return null;
        }

        if (Directory.EnumerateDirectories(directory).Any())
        {
            reason = "it holds a directory, and a tenant directory this provider writes holds only stub files";
            return null;
        }

        if (!TenantPathSegment.TryReadDirectoryName(directory, TenantPathSegment.TryDecodeEarlierRawName, out var tenant, out _))
        {
            reason = "its name is not one this provider writes for a tenant (see TenantPathSegment)";
            return null;
        }

        var stubs = new List<StubMapping>();
        foreach (var file in Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, TenantPathSegment.MarkerFileName, StringComparison.Ordinal))
            {
                continue; // the provider writes this beside its stubs, so it is one of its files
            }

            if (!fileName.EndsWith(".json", StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "D", out var id))
            {
                reason = "it holds a file that is not named <guid>.json";
                return null;
            }

            IReadOnlyList<StubMapping> read;
            try
            {
                read = MappingJsonReader.Read(File.ReadAllText(file), tenant, matchers);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException
                or KeyNotFoundException or IOException or UnauthorizedAccessException)
            {
                // What reading a mapping can throw: a parse, a missing or mistyped field, a bad value; and the file itself.
                reason = "it holds a file that could not be read as a stub mapping";
                return null;
            }

            if (read.Count != 1 || read[0].Id != id)
            {
                reason = "it holds a file that is not one stub whose id is its file name";
                return null;
            }

            stubs.Add(read[0]);
        }

        reason = "";
        return stubs;
    }

    private void Skip(string entry, string? line = null)
    {
        bool first;
        lock (_gate)
        {
            first = _skipped.Add(entry);
        }

        if (first)
        {
            Console.WriteLine(line is null ? $"mockifyr: mappings/{entry} — skipped, not read as a tenant." : $"mockifyr: {line}");
        }
    }
}
