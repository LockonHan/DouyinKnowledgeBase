using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>管线阶段进度。</summary>
public sealed record PipelineProgress(string Stage, string Message, double Percent);

/// <summary>管线执行结果。</summary>
public sealed class VideoPipelineResult
{
    public string Title { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string TranscriptPath { get; set; } = "";
}

/// <summary>
/// 调用 tools/process_douyin.py，完成“分享链接 → 下载视频 → 提取音频 → 本地 FunASR 转写”。
/// </summary>
public sealed class VideoPipelineService
{
    private const string ProgressPrefix = "@@PROG@@";

    public async Task<VideoPipelineResult> ProcessAsync(
        string shareText,
        PipelineSettings settings,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.PythonPath) || !File.Exists(settings.PythonPath))
        {
            throw new InvalidOperationException("未找到 Python 解释器，请在“设置 → 视频下载与转写”中指定路径。");
        }

        string script = RepoLocator.DefaultScriptPath();
        if (!File.Exists(script))
        {
            throw new InvalidOperationException($"未找到管线脚本：{script}");
        }

        Directory.CreateDirectory(settings.DataDir);

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
        psi.ArgumentList.Add("--share");
        psi.ArgumentList.Add(shareText);
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(settings.DataDir);
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(settings.PythonPath);
        psi.ArgumentList.Add("--device");
        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(settings.Device) ? "auto" : settings.Device);
        psi.ArgumentList.Add("--asr");
        psi.ArgumentList.Add(settings.AsrResident ? "auto" : "oneshot");
        psi.ArgumentList.Add("--runtime-dir");
        psi.ArgumentList.Add(AppPaths.RuntimeDir);

        string? bundledFfmpeg = RepoLocator.BundledFfmpeg();
        if (bundledFfmpeg is not null)
        {
            psi.ArgumentList.Add("--ffmpeg");
            psi.ArgumentList.Add(bundledFfmpeg);
        }

        string modelsDir = !string.IsNullOrWhiteSpace(settings.ModelsDir) && Directory.Exists(settings.ModelsDir)
            ? settings.ModelsDir
            : RepoLocator.BundledModelsDir() ?? "";
        if (modelsDir.Length > 0)
        {
            psi.Environment["MODELSCOPE_CACHE"] = modelsDir;
        }

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        using Process process = new() { StartInfo = psi };
        VideoPipelineResult result = new();
        StringBuilder errorBuffer = new();
        string lastError = "";

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
                errorBuffer.AppendLine(line);
            }
        });

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string json = line[ProgressPrefix.Length..];
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string stage = root.TryGetProperty("stage", out JsonElement s) ? s.GetString() ?? "" : "";
                string message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
                double percent = root.TryGetProperty("percent", out JsonElement p) && p.TryGetDouble(out double value) ? value : -1;

                if (root.TryGetProperty("title", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                {
                    result.Title = t.GetString() ?? result.Title;
                }

                if (root.TryGetProperty("video", out JsonElement v) && v.ValueKind == JsonValueKind.String)
                {
                    result.VideoPath = v.GetString() ?? result.VideoPath;
                }

                if (root.TryGetProperty("audio", out JsonElement a) && a.ValueKind == JsonValueKind.String)
                {
                    result.AudioPath = a.GetString() ?? result.AudioPath;
                }

                if (root.TryGetProperty("transcript", out JsonElement tr) && tr.ValueKind == JsonValueKind.String)
                {
                    result.TranscriptPath = tr.GetString() ?? result.TranscriptPath;
                }

                if (stage == "error")
                {
                    lastError = message;
                }

                progress.Report(new PipelineProgress(stage, message, percent));
            }
            catch (JsonException)
            {
                // 非 JSON 输出，忽略。
            }
        }

        await exitSource.Task;
        await stderrTask;

        if (process.ExitCode != 0)
        {
            string detail = !string.IsNullOrWhiteSpace(lastError)
                ? lastError
                : errorBuffer.ToString().Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"管线执行失败（退出码 {process.ExitCode}）。"
                : detail);
        }

        if (string.IsNullOrWhiteSpace(result.TranscriptPath) || !File.Exists(result.TranscriptPath))
        {
            throw new InvalidOperationException("管线已结束，但未生成转写稿。");
        }

        return result;
    }
}