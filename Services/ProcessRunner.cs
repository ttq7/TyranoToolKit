using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TyranoToolKit.Services;

/// <summary>
/// 统一的外部进程调用封装，支持同步、按行流式回调以及取消。
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// 同步运行一个外部命令，返回退出码与 stdout/stderr。
    /// </summary>
    public static async Task<(int exitCode, string stdout, string stderr)> RunSync(
        string cmd, string args, int timeoutMs = 8000)
    {
        return await RunSync(cmd, args, null, timeoutMs);
    }

    /// <summary>
    /// 同步运行一个外部命令（带自定义环境变量），返回退出码与 stdout/stderr。
    /// </summary>
    public static async Task<(int exitCode, string stdout, string stderr)> RunSync(
        string cmd, string args, Dictionary<string, string?>? env, int timeoutMs = 8000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var stdoutBuf = new List<string>();
        var stderrBuf = new List<string>();

        try
        {
            var (exit, _, _) = await RunAsync(cmd, args, null, env,
                (stream, line) =>
                {
                    if (stream == "stdout") stdoutBuf.Add(line);
                    else stderrBuf.Add(line);
                }, cts.Token);
            return (exit, string.Join(Environment.NewLine, stdoutBuf),
                string.Join(Environment.NewLine, stderrBuf));
        }
        catch (OperationCanceledException)
        {
            return (-1, string.Join(Environment.NewLine, stdoutBuf),
                string.Join(Environment.NewLine, stderrBuf));
        }
    }

    /// <summary>
    /// 异步运行一个外部命令，按行回调输出。支持取消。
    /// 返回 (exitCode, stderr)。
    /// </summary>
    public static async Task<(int exitCode, string stderr)> RunAsync(
        string cmd, string args, string? cwd, Action<string, string> onLine, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunAsync(cmd, args, cwd, null, onLine, ct);
        return (exitCode, stderr);
    }

    /// <summary>
    /// 异步运行一个外部命令，按行回调输出。支持取消。
    /// 返回 (exitCode, stdout, stderr)。
    /// </summary>
    public static async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        string cmd, string args, string? cwd, Action<string, string>? onLine, CancellationToken ct,
        int timeoutMs = 0)
    {
        return await RunAsync(cmd, args, cwd, null, onLine, ct, timeoutMs);
    }

    /// <summary>
    /// 异步运行一个外部命令，支持自定义环境变量、stdin 输入、按行回调、取消、超时。
    /// 返回 (exitCode, stdout, stderr)。
    /// </summary>
    public static async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        string cmd, string args,
        string? cwd,
        Dictionary<string, string?>? env,
        Action<string, string>? onLine,
        CancellationToken ct,
        int timeoutMs = 0)
    {
        using var timeoutCts = timeoutMs > 0
            ? new CancellationTokenSource(timeoutMs)
            : null;
        using var linkedCts = timeoutCts != null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        // Windows 上通过 cmd /c 执行，便于解析 npm/cordova 等脚本
        // 用 psi.Arguments 而非 ArgumentList，避免引号被双重转义
        // 如果 cmd 路径包含空格，用引号包裹避免被 cmd /c 错误解析
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.FileName = "cmd.exe";
            var cmdPart = cmd.Contains(' ') && !cmd.StartsWith('"') ? "\"" + cmd + "\"" : cmd;
            psi.Arguments = "/c " + cmdPart + " " + args;
        }
        else
        {
            psi.FileName = cmd;
            foreach (var a in SplitArgs(args))
                psi.ArgumentList.Add(a);
        }

        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
            psi.WorkingDirectory = cwd;

        // 注入 npm 镜像环境变量
        psi.EnvironmentVariables["npm_config_registry"] = "https://registry.npmmirror.com";

        // 注入自定义环境变量
        if (env != null)
        {
            foreach (var kv in env)
            {
                if (kv.Value != null)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (!proc.Start())
        {
            onLine?.Invoke("stderr", $"无法启动进程：{cmd}");
            return (-1, "", $"无法启动进程：{cmd}");
        }

        // 如果有 stdin 输入，写入后关闭
        if (env != null && !string.IsNullOrEmpty(args))
        {
            // stdin 由调用方通过 stdin 参数控制（已移除简化版）
        }

        var stdoutBuf = new List<string>();
        var stderrBuf = new List<string>();
        var stdoutTask = ReadStreamAsync(proc.StandardOutput, "stdout",
            (stream, line) => { stdoutBuf.Add(line); onLine?.Invoke(stream, line); },
            linkedCts.Token);
        var stderrTask = ReadStreamAsync(proc.StandardError, "stderr",
            (stream, line) => { stderrBuf.Add(line); onLine?.Invoke(stream, line); },
            linkedCts.Token);

        // 注册取消：取消时 Kill 进程
        await using var reg = linkedCts.Token.Register(() =>
        {
            try
            {
                if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            }
            catch { }
        });

        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync(linkedCts.Token);

        return (proc.ExitCode,
            string.Join(Environment.NewLine, stdoutBuf),
            string.Join(Environment.NewLine, stderrBuf));
    }

    private static async Task ReadStreamAsync(StreamReader reader, string stream,
        Action<string, string> onLine, CancellationToken ct)
    {
        var buf = new char[8192];
        var pending = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            pending.Append(buf, 0, read);
            var text = pending.ToString();
            var lastCut = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    var end = i;
                    if (end > 0 && text[end - 1] == '\r') end--;
                    var line = text.Substring(lastCut + 1, end - (lastCut + 1));
                    onLine(stream, line);
                    lastCut = i;
                }
            }
            if (lastCut >= 0)
                pending.Remove(0, lastCut + 1);
        }
        if (pending.Length > 0)
        {
            var line = pending.ToString().TrimEnd('\r');
            onLine(stream, line);
        }
    }

    private static List<string> SplitArgs(string args)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return result;
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (var ch in args)
        {
            if (ch == '"')
            {
                inQuote = !inQuote;
            }
            else if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(ch);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// 在系统 PATH 中查找可执行文件。
    /// </summary>
    public static string? FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("Path") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, fileName);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
