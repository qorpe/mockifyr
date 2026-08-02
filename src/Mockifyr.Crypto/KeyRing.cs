using System.Buffers.Text;

namespace Mockifyr.Crypto;

/// <summary>One key: its material and, when the operator named it, an identifier (#250).</summary>
/// <param name="Id">
/// The operator's label for this key, or null for an unnamed one. Emitted as the JWE <c>kid</c> so a
/// counterparty can tell which key a token was produced with; an unnamed key emits no header at all,
/// which keeps a single-key host byte-identical to what it produced before rotation existed.
/// </param>
/// <param name="Material">The 256-bit key.</param>
public sealed record CryptoKey(string? Id, byte[] Material);

/// <summary>
/// An ordered set of active keys (#250). The <see cref="Primary"/> is what new tokens and signatures
/// are produced with; every key in <see cref="Keys"/> is accepted on the way in.
/// </summary>
/// <remarks>
/// That asymmetry is the whole point of a ring. During a rollover the new key goes in first and
/// becomes primary, while the old one keeps working for traffic already signed or encrypted with it —
/// so rotation is "add, then remove later", never a coordinated restart of every client at one
/// instant.
/// </remarks>
public sealed class KeyRing
{
    /// <summary>An empty ring — the host holds no key for this capability.</summary>
    public static readonly KeyRing Empty = new([]);

    /// <summary>The active keys, newest first.</summary>
    public IReadOnlyList<CryptoKey> Keys { get; }

    /// <summary>The key new tokens and signatures are produced with, or null when the ring is empty.</summary>
    public CryptoKey? Primary => Keys.Count > 0 ? Keys[0] : null;

    /// <summary>True when there is nothing to encrypt, decrypt, sign or verify with.</summary>
    public bool IsEmpty => Keys.Count == 0;

    /// <summary>Builds a ring from keys already in newest-first order.</summary>
    public KeyRing(IReadOnlyList<CryptoKey> keys) => Keys = keys;

    /// <summary>Builds a single-key ring, the shape an inline <c>--decrypt-key</c> produces.</summary>
    public static KeyRing Of(byte[] material) => new([new CryptoKey(null, material)]);

    /// <summary>
    /// Parses a key file (#250). One key per line, newest first; a line may be <c>id: base64</c> to
    /// name the key. Blank lines and <c>#</c> comments are ignored, and a line that is not a 256-bit
    /// base64 key is skipped rather than failing the file.
    /// </summary>
    /// <remarks>
    /// Skipping a bad line rather than refusing the file is deliberate: a key file is edited during a
    /// rollover, often by a script, and a host that refused to start over one malformed line would
    /// turn a typo into an outage. What matters is that a bad line can never become a usable key —
    /// it cannot, because it never parses to 32 bytes. The count of keys actually loaded is reported
    /// at startup and on <c>/__admin/health</c>, so a silently skipped line is visible.
    /// </remarks>
    public static KeyRing Parse(string text)
    {
        var keys = new List<CryptoKey>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string? id = null;
            var value = line;
            // `id: material` — split on the LAST colon. Base64 and base64url never contain one, so
            // this is unambiguous, and it lets an id be a namespaced path like `vault:prod:2026`,
            // which is exactly the shape a secret manager hands out.
            var separator = line.LastIndexOf(':');
            if (separator >= 0)
            {
                id = line[..separator].Trim();
                value = line[(separator + 1)..].Trim();
                if (id.Length == 0)
                {
                    id = null;
                }
            }

            if (ReadKey(value) is { } material)
            {
                keys.Add(new CryptoKey(id, material));
            }
        }

        return new KeyRing(keys);
    }

    /// <summary>
    /// Reads a 256-bit key from base64 or base64url, or null when the text is not one.
    /// </summary>
    public static byte[]? ReadKey(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var normalized = configured.Trim().Replace('-', '+').Replace('_', '/');
        // Only the one-character pad is possible for something that could be a 256-bit key: 32 bytes
        // encode to 43 characters unpadded (43 % 4 == 3) or 44 padded (44 % 4 == 0). A length that
        // would need two pad characters decodes to 31.5 bytes, so it is not a key whatever we do to
        // it — leaving it unpadded lets the validity check below reject it, which is the same answer
        // by a shorter road than a branch no valid key can reach.
        normalized += normalized.Length % 4 == 3 ? "=" : string.Empty;
        return Base64.IsValid(normalized) && Convert.TryFromBase64String(normalized, new byte[32], out var written) && written == 32
            ? Convert.FromBase64String(normalized)
            : null;
    }
}

/// <summary>
/// Where a key ring comes from (#250). The seam a Vault or KMS integration plugs into without
/// Mockifyr taking a dependency on either.
/// </summary>
/// <remarks>
/// Read on every use rather than captured once, because that is what makes rotation work without a
/// restart: a source backed by a file notices the file changed, and the next request uses the new
/// ring. Implementations must therefore be cheap to call and safe to call concurrently.
/// </remarks>
public interface IKeySource
{
    /// <summary>The currently active keys, newest first.</summary>
    KeyRing Current { get; }
}

/// <summary>A ring fixed at startup — what an inline <c>--decrypt-key</c> produces.</summary>
public sealed class StaticKeySource(KeyRing ring) : IKeySource
{
    /// <summary>Convenience for the single-key case.</summary>
    public StaticKeySource(byte[] material) : this(KeyRing.Of(material))
    {
    }

    /// <inheritdoc />
    public KeyRing Current { get; } = ring;
}

/// <summary>
/// A ring read from a file, re-read when the file changes (#250).
/// </summary>
/// <remarks>
/// <para>
/// Polling the modification time rather than watching for filesystem events, because the deployment
/// that matters most does not produce the events: Kubernetes updates a mounted Secret by swapping a
/// symlink, which a <c>FileSystemWatcher</c> on the visible path routinely misses. Reading the
/// timestamp follows the symlink, so a swapped Secret is seen. The check is rate-limited so a busy
/// host stats the file at most once per interval, not once per request.
/// </para>
/// <para>
/// A file that disappears or becomes unreadable leaves the last good ring in place. A rotation script
/// that truncates before writing must not take the host's keys away for the moment in between.
/// </para>
/// </remarks>
public sealed class FileKeySource : IKeySource
{
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private KeyRing _ring = KeyRing.Empty;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private DateTime _writtenAt = DateTime.MinValue;

    /// <summary>How often the file's timestamp is consulted, when no interval is given.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    /// <summary>Opens a file-backed source and loads it immediately.</summary>
    public FileKeySource(string path, TimeSpan? interval = null, TimeProvider? timeProvider = null)
    {
        _path = path;
        _interval = interval ?? DefaultInterval;
        _time = timeProvider ?? TimeProvider.System;
        // No "force" flag needed: the initial timestamps are MinValue, so the first call always
        // reads. Carrying a flag that can never be false would be a branch nothing exercises.
        Reload();
    }

    /// <inheritdoc />
    public KeyRing Current
    {
        get
        {
            Reload();
            return _ring;
        }
    }

    private void Reload()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now - _checkedAt < _interval)
            {
                return;
            }

            _checkedAt = now;

            try
            {
                var written = File.GetLastWriteTimeUtc(_path);
                if (written == _writtenAt)
                {
                    return;
                }

                var ring = KeyRing.Parse(File.ReadAllText(_path));
                // An empty parse result is treated as "nothing new to see": a half-written file
                // during a rotation must not disarm the host between two writes.
                if (!ring.IsEmpty)
                {
                    _ring = ring;
                    _writtenAt = written;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the last good ring. A transient read failure is not a reason to stop
                // decrypting traffic that is still arriving.
            }
        }
    }
}
