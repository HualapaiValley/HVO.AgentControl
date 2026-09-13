using System.Reflection;

namespace HVO.AgentControl;

public static class ApplicationVersion
{
    public static string Current { get; } = typeof(ApplicationVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
}
