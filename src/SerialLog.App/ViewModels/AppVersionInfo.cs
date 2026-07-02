using System.Reflection;
using SerialLog.Core.Collaboration;

namespace SerialLog.App.ViewModels;

public static class AppVersionInfo
{
    public static string VersionText
    {
        get
        {
            var version = typeof(AppVersionInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(version))
            {
                version = typeof(AppVersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            }

            var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
            if (plusIndex >= 0)
            {
                version = version[..plusIndex];
            }

            return $"v{version}";
        }
    }

    public static string ProtocolVersionText => CollaborationProtocol.CurrentVersionText;

    public static string BuildStatusText => $"{VersionText} / {ProtocolVersionText}";
}
