using System.Diagnostics;
using System.Text;

namespace HVO.AgentControl.Provisioning;

// Never executes a shell command string. Both pipes are drained even after their
// retained output reaches the limit, so a noisy build cannot deadlock the runner.
public sealed class BoundedProvisionProcess : IProvisionProcessRunner
{
    public async Task<ProvisionProcessResult> Run(ProvisionProcessRequest request, Action<string>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Timeout);
        using var process = new Process { StartInfo = new(request.Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = request.WorkingDirectory } };
        foreach (var argument in request.Arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Clear();
        foreach (var pair in request.Environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        if (token.IsCancellationRequested) return new(false, null, true, false, "", "");
        try { if (!process.Start()) return new(false, null, false, false, "", ""); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new(false, null, false, false, "", ""); }
        var truncated = 0;
        var progressTruncated = 0;
        async Task<string> Drain(StreamReader reader, bool report)
        {
            var retained = new StringBuilder();
            var buffer = new char[2048];
            while (await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None) is var count && count > 0)
            {
                var keep = Math.Min(count, request.OutputLimit - retained.Length);
                if (keep > 0) retained.Append(buffer, 0, keep);
                if (keep < count) { if (report) Interlocked.Exchange(ref progressTruncated, 1); else Interlocked.Exchange(ref truncated, 1); }
                if (report) { try { progress?.Invoke(new string(buffer, 0, Math.Min(count, 2048))); } catch { /* advisory only */ } }
            }
            return retained.ToString();
        }
        var stdout = Drain(process.StandardOutput, false);
        var stderr = Drain(process.StandardError, true);
        var interrupted = false;
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            interrupted = true;
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        // A killed CLI may have already sent work to Docker. Its cancellation is
        // only a local process fact. Bound pipe draining if a descendant retains it.
        try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return new(true, null, true, true, "", ""); }
        return new(true, process.HasExited ? process.ExitCode : null, interrupted, truncated != 0, await stdout, await stderr, progressTruncated != 0);
    }
}
