using System.Reflection;

namespace BCSTool.Infrastructure;

public static class AppVersion
{
    public static string Version
    {
        get
        {
            var informationalVersion =
                typeof(App).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;

            if (string.IsNullOrWhiteSpace(informationalVersion))
                return "0.0.0";

            // .NET/Git builds may append metadata such as:
            //
            // 0.3.3+abc123...
            //
            // We only want the public application version.
            var plusIndex =
                informationalVersion.IndexOf('+');

            return plusIndex >= 0
                ? informationalVersion[..plusIndex]
                : informationalVersion;
        }
    }

    public static string DisplayVersion =>
        $"v{Version}";

    public static string DisplayName =>
        $"BCS Tool {DisplayVersion}";
}
