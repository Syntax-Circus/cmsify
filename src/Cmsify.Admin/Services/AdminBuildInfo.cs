using System.Reflection;

namespace Cmsify.Admin.Services;

public static class AdminBuildInfo
{
    public static string Version { get; } = typeof(AdminBuildInfo).Assembly
        .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), inherit: false)
        .OfType<AssemblyInformationalVersionAttribute>()
        .SingleOrDefault()?.InformationalVersion
        ?? typeof(AdminBuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
