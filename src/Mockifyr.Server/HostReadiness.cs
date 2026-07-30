namespace Mockifyr.Server;

/// <summary>
/// The host's readiness state (#242). Liveness answers "the process is up"; readiness answers "route
/// traffic here". They are different questions: a host still loading its startup mappings is alive
/// but must not receive requests, and a host that is shutting down must stop receiving them while
/// staying alive long enough to finish what it is serving. Conflating the two makes a rolling update
/// drop requests and lets a slow start get a pod restarted.
/// </summary>
public sealed class HostReadiness
{
    private volatile bool _ready;
    private volatile bool _draining;

    /// <summary>True once startup work finished and the host has not begun shutting down.</summary>
    public bool IsReady => _ready && !_draining;

    /// <summary>Why the host is not ready — surfaced in the probe body so an operator can see it.</summary>
    public string State => _draining ? "draining" : _ready ? "ready" : "starting";

    /// <summary>Marks startup complete (mappings, environments and keys loaded).</summary>
    public void MarkReady() => _ready = true;

    /// <summary>Marks the host as draining, so readiness fails before in-flight work is finished.</summary>
    public void BeginDraining() => _draining = true;
}
