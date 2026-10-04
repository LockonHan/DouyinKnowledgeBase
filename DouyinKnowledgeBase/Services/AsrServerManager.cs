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
        if (!settings.AsrResident || IsRunning)
        {
            return;
        }

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
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(settings.Device) ? "auto" : settings.Device);
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
                Append($"常驻转写服务已启动（PID {process.Id}）。");
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