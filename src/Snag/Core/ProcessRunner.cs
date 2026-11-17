using System.Diagnostics;
using System.Text;

namespace Snag.Core;

public static class ProcessRunner
{
    /// <summary>Runs a console program without a window, streaming stdout/stderr lines.
    /// Cancelling kills the whole process tree.</summary>
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args,
        Action<string>? onOut, Action<string>? onErr, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var p = new Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        var outTask = Pump(p.StandardOutput, onOut);
        var errTask = Pump(p.StandardError, onErr);
        await p.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(outTask, errTask);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    static async Task Pump(StreamReader r, Action<string>? onLine)
    {
        string? line;
        while ((line = await r.ReadLineAsync()) is not null)
            onLine?.Invoke(line);
    }

    /// <summary>Runs and collects all stdout as one string.</summary>
    public static async Task<(int Code, string Out, string Err)> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var o = new StringBuilder();
        var e = new StringBuilder();
        var code = await RunAsync(exe, args, l => o.AppendLine(l), l => e.AppendLine(l), ct);
        return (code, o.ToString(), e.ToString());
    }
}
