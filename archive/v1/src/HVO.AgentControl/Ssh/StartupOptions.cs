using System.Security.Cryptography;
using System.Text;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.Ssh;

public static class StartupOptions
{
    public static string Arguments(RuntimeRecord runtime)
    {
        if (runtime.LogLevel is not ("" or "DEBUG" or "INFO" or "WARN" or "ERROR"))
            throw new ControlException("Choose a supported OpenCode log level: DEBUG, INFO, WARN or ERROR.", 400);
        return (runtime.PureMode ? " --pure" : "") + (runtime.PrintLogs ? " --print-logs" : "") +
            (runtime.LogLevel.Length > 0 ? " --log-level " + BootstrapScript.Quote(runtime.LogLevel) : "");
    }
    public static string Fingerprint(RuntimeRecord runtime) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Arguments(runtime))));
}
