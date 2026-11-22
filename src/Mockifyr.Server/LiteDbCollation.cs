using System.Globalization;
using LiteDB;
using LiteDB.Engine;

namespace Mockifyr.Server;

/// <summary>
/// Makes every string comparison a LiteDB file makes ordinal and case-sensitive, as tenants, stub keys, collection
/// names, resource ids and environment keys are everywhere else in the product (#466, verifier on #468).
/// </summary>
/// <remarks>
/// LiteDB 5 opens a new file with the current culture and <see cref="CompareOptions.IgnoreCase"/>, and that one
/// collation governs the document key and every query comparison in the file. Under it <c>acme|id</c> and
/// <c>ACME|id</c> are one key and <c>Tenant == "acme"</c> also matches <c>ACME</c>, so one tenant's write, delete or
/// reset took a case-variant tenant's data, and the same held for resources, environments and declared tenants.
/// Rather than repair each key and filter, the rule covers the whole class at once, including comparisons nobody
/// has written yet: no LiteDB-backed component reads or writes until its file's collation is ordinal. Every LiteDB
/// persistence and loader calls <see cref="EnsureOrdinal"/> before it takes its collection, so the rule holds
/// whoever builds them, and the host calls it when it opens the file.
/// </remarks>
public static class LiteDbCollation
{
    // One gate for every file: a rebuild happens once per file per process, so contention is not a concern, and a
    // private object cannot be locked by anybody else.
    private static readonly object Gate = new();

    /// <summary>
    /// Rebuilds <paramref name="database"/> under an ordinal collation if it does not already have one, keeping
    /// every document and index, and returns it. A file created by an earlier version has the case-insensitive
    /// default, under which no two documents could differ only in case, so every one of them is distinct under
    /// ordinal too and the rebuild loses nothing. Re-runnable: a file already ordinal is left as it is.
    /// </summary>
    /// <param name="database">The open database; it must not be in use by another thread while it is rebuilt.</param>
    /// <returns><paramref name="database"/>, now ordinal.</returns>
    public static LiteDatabase EnsureOrdinal(LiteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        lock (Gate)
        {
            var current = database.Collation;
            if (current.SortOptions != CompareOptions.Ordinal)
            {
                database.Rebuild(new RebuildOptions { Collation = new Collation(current.LCID, CompareOptions.Ordinal) });
            }
        }

        return database;
    }

    /// <summary>Opens the LiteDB file or connection string <paramref name="connection"/> with an ordinal collation.</summary>
    /// <param name="connection">A file path or a LiteDB connection string, as <c>--litedb</c> takes it.</param>
    /// <returns>The open database.</returns>
    public static LiteDatabase Open(string connection) => EnsureOrdinal(new LiteDatabase(connection));
}
