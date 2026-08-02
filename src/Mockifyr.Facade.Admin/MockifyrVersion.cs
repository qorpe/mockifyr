using System.Reflection;

namespace Mockifyr.Facade.Admin;

/// <summary>
/// The running build's version, as reported by <c>/__admin/health</c>.
/// </summary>
/// <remarks>
/// Read from the assembly rather than written down, so it cannot drift from what was actually shipped
/// — the previous hard-coded <c>"1.0"</c> did exactly that, and the documentation had to warn readers
/// not to trust it. Falls back to <c>0.0.0</c> when no version attribute is present (a local build),
/// which is honest about not knowing rather than inventing a number.
/// </remarks>
internal static class MockifyrVersion
{
    /// <summary>The informational version of the assembly this code is in.</summary>
    public static string Current { get; } =
        typeof(MockifyrVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            // A build stamped by CI carries metadata after a '+'; the version is the part before it.
            ?.Split('+')[0]
        ?? typeof(MockifyrVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
