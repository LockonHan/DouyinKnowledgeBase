using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 常驻转写服务（tools/asr_server.py）的生命周期管理。
/// 开启后模型只加载一次，后续转写免去加载耗时（代价是常驻占用内存/显存）。
/// </summary>
public sealed class AsrServerManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly StringBuilder _log = new();
    private Process? _process;
    private string _runningDevice = "";

    /// <summary>常驻服务进程是否仍在运行。</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>最近的服务日志，便于诊断。</summary>
    public string Log => _log.ToString();

    private static string InfoPath => Path.Combine(AppPaths.RuntimeDir, "asr-server.json");

    /// <summary>已就绪时返回设备描述（如 cpu / cuda:0），否则返回 null。</summary>
    public string? ReadyDevice()
    {
        try
        {
            if (!File.Exists(InfoPath))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(InfoPath));
            return doc.RootElement.TryGetProperty("device", out JsonElement device)
                ? device.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>按配置启动常驻服务；未开启或已在运行时不重复启动。</summary>
    public void Start(PipelineSettings settings)
    {
        if (!settings.AsrResident)
        {
            return;
        }

        string device = string.IsNullOrWhiteSpace(settings.Device) ? "auto" : settings.Device;
        if (IsRunning && string.Equals(_runningDevice, device, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsRunning)
        {
            // 设备变了（例如从 CPU 换成 GPU）：重启常驻服务，否则仍会按旧设备转写。
            Stop();
        }

        StopStaleServers();

        if (string.IsNullOrWhiteSpace(settings.PythonPath) || !File.Exists(settings.PythonPath))
        {
            return;
        }

        string script = RepoLocator.ScriptPath("asr_server.py");
        if (!File.Exists(script))
        {
            return;
        }

        ProcessStartInfo psi = new()
        {
            FileName = settings.PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--runtime-dir");
        psi.ArgumentList.Add(AppPaths.RuntimeDir);
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(device);
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());

        string modelsDir = ResolveModelsDir(settings);
        if (!string.IsNullOrWhiteSpace(modelsDir))
        {
            psi.Environment["MODELSCOPE_CACHE"] = modelsDir;
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        try
        {
            Process process = new() { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            if (process.Start())
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _process = process;
                _runningDevice = device;
                Append($"常驻转写服务已启动（PID {process.Id}，设备 {device}）。");
            }
        }
        catch (Exception ex)
        {
            Append("启动常驻转写服务失败：" + ex.Message);
        }
    }

    /// <summary>停止常驻服务：先请求优雅退出，超时则结束进程树。</summary>
    public void Stop()
    {
        Process? process = _process;
        _process = null;
        _runningDevice = "";
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                RequestShutdown().GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
            // 忽略，下面强制结束。
        }

        try
        {
            if (!process.WaitForExit(4000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能已退出。
        }
    }

    /// <summary>
    /// 关闭上一次残留的常驻服务（例如应用被强杀后留下的进程）。
    /// 这些进程会各占约 2 GB 内存，堆积后会让模型加载因内存不足而原生崩溃。
    /// </summary>
    private void StopStaleServers()
    {
        try
        {
            if (!File.Exists(InfoPath))
            {
                return;
            }

            int pid;
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(InfoPath)))
            {
                pid = doc.RootElement.TryGetProperty("pid", out JsonElement p) ? p.GetInt32() : 0;
            }

            if (pid <= 0 || pid == Environment.ProcessId)
            {
                return;
            }

            try
            {
                RequestShutdown().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 服务可能已经不可达，下面直接结束进程。
            }

            try
            {
                using Process stale = Process.GetProcessById(pid);
                // PID 可能已被复用：只有确认是 python 进程才结束，避免误杀无关程序。
                if (!stale.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!stale.HasExited && !stale.WaitForExit(3000))
                {
                    stale.Kill(entireProcessTree: true);
                }

                Append($"已清理残留的常驻转写服务（PID {pid}）。");
            }
            catch (Exception)
            {
                // 进程不存在或无权限结束：忽略。
            }

            try
            {
                File.Delete(InfoPath);
            }
            catch (Exception)
            {
                // 忽略。
            }
        }
        catch (Exception)
        {
            // 残留服务清理属于尽力而为，失败不影响正常启动。
        }
    }

    private static string ResolveModelsDir(PipelineSettings settings)
    {
        // 优先复用已有模型的目录，避免把空目录当作缓存导致重复下载。
        return RepoLocator.EffectiveModelsCache(settings.ModelsDir);
    }

    private async Task RequestShutdown()
    {
        string json = File.Exists(InfoPath) ? await File.ReadAllTextAsync(InfoPath) : "";
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        string? token = doc.RootElement.TryGetProperty("token", out JsonElement t) ? t.GetString() : null;
        int port = doc.RootElement.TryGetProperty("port", out JsonElement p) ? p.GetInt32() : 0;
        if (token is null || port == 0)
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{port}/shutdown");
        request.Headers.Add("X-DouKB-Token", token);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(4));
        using HttpResponseMessage response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
    }

    private void Append(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_log)
        {
            _log.AppendLine(line);
            if (_log.Length > 8000)
            {
                _log.Remove(0, _log.Length - 6000);
            }
        }
    }
}