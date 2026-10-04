using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DouyinKnowledgeBase.Services;

/// <summary>环境自检报告（对应 tools/env_setup.py --check 输出的 @@ENV@@ 行）。</summary>
public sealed class EnvironmentReport
{
    [JsonPropertyName("pythonPath")] public string PythonPath { get; set; } = "";
    [JsonPropertyName("pythonVersion")] public string PythonVersion { get; set; } = "";
    [JsonPropertyName("packages")] public Dictionary<string, string> Packages { get; set; } = new();
    [JsonPropertyName("torchCuda")] public bool TorchCuda { get; set; }
    [JsonPropertyName("nvidia")] public NvidiaInfo Nvidia { get; set; } = new();
    [JsonPropertyName("ffmpeg")] public string Ffmpeg { get; set; } = "";
    [JsonPropertyName("browsersDir")] public string BrowsersDir { get; set; } = "";
    [JsonPropertyName("browserReady")] public bool BrowserReady { get; set; }
    [JsonPropertyName("modelsDir")] public string ModelsDir { get; set; } = "";
    [JsonPropertyName("models")] public Dictionary<string, bool> Models { get; set; } = new();
    [JsonPropertyName("device")] public string Device { get; set; } = "";
    [JsonPropertyName("ready")] public bool Ready { get; set; }
    [JsonPropertyName("issues")] public List<string> Issues { get; set; } = new();

    /// <summary>转写模型标签，用于界面展示。</summary>
    public static string ModelLabel(string key) => key switch
    {
        "asr" => "语音识别 Paraformer-large",
        "vad" => "人声切分 FSMN-VAD",
        "punc" => "标点恢复 CT-Transformer",
        _ => key,
    };
}

/// <summary>NVIDIA 显卡检测结果。</summary>
public sealed class NvidiaInfo
{
    [JsonPropertyName("present")] public bool Present { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";

    /// <summary>形如“NVIDIA GeForce RTX 4060 Laptop GPU（驱动 560.94）”。</summary>
    public string Summary => Present
        ? string.IsNullOrWhiteSpace(Driver) ? Name : $"{Name}（驱动 {Driver}）"
        : "未检测到 NVIDIA 显卡";
}

/// <summary>环境准备进度。</summary>
public sealed record SetupProgress(string Step, double Percent, string Message, string Level)
{
    public bool IsError => string.Equals(Level, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>环境准备请求参数。</summary>
public sealed class SetupRequest
{
    /// <summary>目标 Python 解释器。</summary>
    public string PythonPath { get; set; } = "";

    /// <summary>true 安装 CUDA 版 PyTorch，false 安装 CPU 版。</summary>
    public bool UseGpu { get; set; }

    /// <summary>ModelScope 缓存根目录（模型位于其下 models/iic/）。</summary>
    public string ModelsDir { get; set; } = "";

    /// <summary>Playwright 浏览器存放目录。</summary>
    public string BrowsersDir { get; set; } = "";

    /// <summary>ffmpeg 路径（自检提示）。</summary>
    public string Ffmpeg { get; set; } = "";
}

/// <summary>
/// 调用 tools/env_setup.py 完成环境自检与按需安装（PyTorch / 依赖 / 浏览器 / 模型）。
/// </summary>
public sealed class EnvironmentService
{
    private const string SetupPrefix = "@@SETUP@@";
    private const string EnvPrefix = "@@ENV@@";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.CaseInsensitive;

    /// <summary>快速检测 NVIDIA 显卡（直接调用 nvidia-smi，不依赖 Python）。</summary>
    public static NvidiaInfo DetectNvidia()
    {
        List<string> candidates = new();
        string system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        candidates.Add(system32);

        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string raw in pathVar.Split(';'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0)
                {
                    continue;
                }

                try
                {
                    candidates.Add(Path.Combine(entry, "nvidia-smi.exe"));
                }
                catch (ArgumentException)
                {
                    // 忽略非法 PATH 项。
                }
            }
        }

        foreach (string exe in candidates)
        {
            if (!File.Exists(exe))
            {
                continue;
            }

            try
            {
                ProcessStartInfo psi = new()
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("--query-gpu=name,driver_version");
                psi.ArgumentList.Add("--format=csv,noheader");

                using Process? process = Process.Start(psi);
                if (process is null)
                {
                    continue;
                }

                string stdout = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                if (!process.WaitForExit(15000) || process.ExitCode != 0)
                {
                    process.Kill(true);
                    continue;
                }

                string first = stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim() ?? "";
                if (first.Length == 0)
                {
                    continue;
                }

                string[] parts = first.Split(',', StringSplitOptions.TrimEntries);
                return new NvidiaInfo
                {
                    Present = true,
                    Name = parts.Length > 0 ? parts[0] : "",
                    Driver = parts.Length > 1 ? parts[1] : "",
                };
            }
            catch (Exception)
            {
                // 换下一个候选路径。
            }
        }

        return new NvidiaInfo();
    }

    /// <summary>运行自检；失败返回 null（可通过 <paramref name="error"/> 取得原因）。</summary>
    public async Task<EnvironmentReport?> CheckAsync(
        SetupRequest request,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        string script = EnsureScript();
        string stdout = await RunAsync(script, request, check: true, progress: null, cancellationToken, log);

        foreach (string line in stdout.Split('\n'))
        {
            string text = line.Trim();
            if (!text.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<EnvironmentReport>(text[EnvPrefix.Length..], JsonOptions);
            }
            catch (JsonException)
            {
                // 继续找下一行。
            }
        }

        return null;
    }

    /// <summary>按需安装；返回最终自检报告（失败返回 null）。</summary>
    public async Task<EnvironmentReport?> InstallAsync(
        SetupRequest request,
        IProgress<SetupProgress> progress,
        CancellationToken cancellationToken,
        Action<string>? log = null)
    {
        string script = EnsureScript();
        string stdout = await RunAsync(script, request, check: false, progress, cancellationToken, log);

        EnvironmentReport? report = null;
        foreach (string line in stdout.Split('\n'))
        {
            string text = line.Trim();
            if (!text.StartsWith(EnvPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                report = JsonSerializer.Deserialize<EnvironmentReport>(text[EnvPrefix.Length..], JsonOptions);
            }
            catch (JsonException)
            {
                // 忽略损坏行。
            }
        }

        return report;
    }

    private static string EnsureScript()
    {
        string script = RepoLocator.ScriptPath("env_setup.py");
        if (!File.Exists(script))
        {
            throw new InvalidOperationException($"未找到环境准备脚本：{script}");
        }

        return script;
    }

    private static async Task<string> RunAsync(
        string script,
        SetupRequest request,
        bool check,
        IProgress<SetupProgress>? progress,
        CancellationToken cancellationToken,
        Action<string>? log)
    {
        ProcessStartInfo psi = new()
        {
            FileName = request.PythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(check ? "--check" : "--install");
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(request.PythonPath);
        if (!check)
        {
            psi.ArgumentList.Add("--device");
            psi.ArgumentList.Add(request.UseGpu ? "gpu" : "cpu");
        }

        if (!string.IsNullOrWhiteSpace(request.ModelsDir))
        {
            psi.ArgumentList.Add("--models-dir");
            psi.ArgumentList.Add(request.ModelsDir);
            psi.Environment["MODELSCOPE_CACHE"] = request.ModelsDir;
        }

        if (!string.IsNullOrWhiteSpace(request.BrowsersDir))
        {
            psi.ArgumentList.Add("--browsers-dir");
            psi.ArgumentList.Add(request.BrowsersDir);
            psi.Environment["PLAYWRIGHT_BROWSERS_PATH"] = request.BrowsersDir;
        }

        if (!string.IsNullOrWhiteSpace(request.Ffmpeg))
        {
            psi.ArgumentList.Add("--ffmpeg");
            psi.ArgumentList.Add(request.Ffmpeg);
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using Process process = new() { StartInfo = psi };
        StringBuilder buffer = new();
        StringBuilder errors = new();
        StringBuilder collected = new();

        TaskCompletionSource exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitSource.TrySetResult();

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Python 进程。");
        }

        Task stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) is not null)
            {
                errors.AppendLine(line);
            }
        });

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            collected.AppendLine(line);

            if (line.StartsWith(SetupPrefix, StringComparison.Ordinal))
            {
                string json = line[SetupPrefix.Length..];
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    string step = root.TryGetProperty("step", out JsonElement s) ? s.GetString() ?? "" : "";
                    string message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
                    string level = root.TryGetProperty("level", out JsonElement l) ? l.GetString() ?? "info" : "info";
                    double percent = root.TryGetProperty("percent", out JsonElement p) && p.TryGetDouble(out double value)
                        ? value
                        : -1;

                    progress?.Report(new SetupProgress(step, percent, message, level));
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        log?.Invoke($"[{step}] {message}");
                    }
                }
                catch (JsonException)
                {
                    // 非 JSON 行，忽略。
                }
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                buffer.AppendLine(line);
                log?.Invoke(line);
            }
        }

        await exitSource.Task;
        await stderrTask;

        if (process.ExitCode != 0 && collected.Length == 0)
        {
            string detail = errors.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"环境准备脚本执行失败（退出码 {process.ExitCode}）。"
                : detail);
        }

        return collected.ToString();
    }
}