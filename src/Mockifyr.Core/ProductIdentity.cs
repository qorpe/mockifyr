namespace Mockifyr.Core;

/// <summary>
/// What this host calls itself when something asks (#396) — the admin health surface, and through it
/// the dashboard's status line and Settings screen.
/// </summary>
/// <remarks>
/// Separate from the dashboard's brand options because the two answer different questions and live at
/// different layers: this is what the API reports, and it is the only piece of branding a facade needs
/// to know about. The default is the product's own name, so an unconfigured host is unchanged.
/// </remarks>
public sealed record ProductIdentity(string Name)
{
    /// <summary>The shipped name.</summary>
    public const string DefaultName = "Mockifyr";

    /// <summary>An unbranded host.</summary>
    public static ProductIdentity Default { get; } = new(DefaultName);
}
