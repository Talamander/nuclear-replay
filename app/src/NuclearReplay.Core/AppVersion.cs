using System.Reflection;

namespace NuclearReplay.Core;

/// <summary>The product version from version.props (for example 0.5.0-beta.1).</summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
