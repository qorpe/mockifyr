using System.Globalization;
using LiteDB;
using LiteDB.Engine;

namespace Mockifyr.Server;

/// <summary>
/// Makes every string comparison a LiteDB file makes ordinal and case-sensitive, as tenants, stub keys, collection
/// names, resource ids and environment keys are everywhere else in the product (#466, verifier on #468).
/// </summary>
/// <remarks>
/// <para>
/// LiteDB 5 creates a file with the current culture and <see cref="CompareOptions.IgnoreCase"/>, and that one
/// collation governs the document key and every query comparison in the file. Under it <c>acme|id</c> and
/// <c>ACME|id</c> are one key and <c>Tenant == "acme"</c> also matches <c>ACME</c>, so one tenant's write, delete or
/// reset took a case-variant tenant's data, and the same held for resources, environments and declared tenants.
/// Rather than repair each key and filter, the rule covers the whole class at once, including comparisons nobody
/// has written yet: no LiteDB-backed component reads or writes a file whose collation is not ordinal.
/// </para>
/// <para>
/// Only <see cref="Open"/> changes a file, because only it knows what the connection said about the file. A new
/// file, an empty one and <c>:memory:</c> are created ordinal through the connection string and never rebuilt. A
/// file created by an earlier version is rebuilt once, and the rebuild carries the connection's own password, so
/// an encrypted file stays encrypted. A component handed a database it did not open (<see cref="RequireOrdinal"/>)
/// cannot know that password, so it refuses a case-insensitive file rather than rebuild it: a rebuild without the
/// password would write the file back unencrypted.
/// </para>
/// </remarks>
public static class LiteDbCollation
{
    // One gate for every file: a rebuild happens once per file per process, so contention is not a concern, and a
    // private object cannot be locked by anybody else.
    private static readonly object Gate = new();

    /// <summary>
    /// Opens the LiteDB file or connection string <paramref name="connection"/> with an ordinal collation. A file
    /// that does not exist yet is created ordinal, whatever collation the connection string names. A file created
    /// with another collation is rebuilt under an ordinal one, keeping every document and index and the
    /// connection's password, so an encrypted file stays encrypted with the same password. Under the
    /// case-insensitive default no two documents could differ only in case, so every one of them is distinct under
    /// ordinal too. Re-runnable: a file already ordinal is left as it is.
    /// </summary>
    /// <param name="connection">A file path or a LiteDB connection string, as <c>--litedb</c> takes it.</param>
    /// <returns>The open database, ordinal.</returns>
    public static LiteDatabase Open(string connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var settings = new ConnectionString(connection);

        // LiteDB reads a connection's collation only when it creates the file, and refuses to open an existing file
        // whose header names another one. So a file about to be created is created ordinal, and nothing is rebuilt;
        // an existing file is opened with its own collation and, if that is not ordinal, rebuilt below.
        settings.Collation = IsCreatedByThisOpen(settings.Filename)
            ? new Collation(CultureInfo.InvariantCulture.LCID, CompareOptions.Ordinal)
            : null;

        var database = new LiteDatabase(settings);
        try
        {
            lock (Gate)
            {
                var current = database.Collation;
                if (current.SortOptions != CompareOptions.Ordinal)
                {
                    // RebuildOptions.Password is the password the REBUILT file gets, and null means none: it must be
                    // the connection's own, or the rebuild decrypts the file.
                    database.Rebuild(new RebuildOptions
                    {
                        Collation = new Collation(current.LCID, CompareOptions.Ordinal),
                        Password = settings.Password,
                    });
                }
            }
        }
        catch
        {
            database.Dispose();
            throw;
        }

        return database;
    }

    // ":memory:" and ":temp:" never exist on disk; a missing or zero-length file is created by this open. A file that
    // appears between this check and the open is opened under the ordinal collation it was not created with, and
    // LiteDB refuses that loudly rather than open it wrong.
    private static bool IsCreatedByThisOpen(string filename)
        => filename is ":memory:" or ":temp:" || !File.Exists(filename) || new FileInfo(filename).Length == 0;

    /// <summary>
    /// Returns <paramref name="database"/> when its collation is ordinal, and refuses it otherwise. Every LiteDB
    /// persistence and loader calls this before it takes its collection, so none of them can compare a tenant, a key
    /// or an id case-insensitively, whoever built them.
    /// </summary>
    /// <param name="database">The open database.</param>
    /// <returns><paramref name="database"/>.</returns>
    /// <exception cref="InvalidOperationException">The file compares strings case-insensitively.</exception>
    public static LiteDatabase RequireOrdinal(LiteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (database.Collation.SortOptions != CompareOptions.Ordinal)
        {
            throw new InvalidOperationException(
                $"This LiteDB database compares strings with '{database.Collation}', not ordinally, so two tenants, keys or ids that "
                + "differ only in case would be one. Open it with LiteDbCollation.Open, which rebuilds it once with the "
                + "connection's own password; it is not rebuilt here, because without that password the file would be written back unencrypted.");
        }

        return database;
    }
}
