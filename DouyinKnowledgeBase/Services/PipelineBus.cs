namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 跨页面进度总线：快速获取页发布管线进度，知识库首页订阅并显示“处理中条”。
/// 单进程内使用，无需持久化；订阅方在页面卸载时退订。
/// </summary>
public static class PipelineBus
{
    /// <summary>有新进度时触发（UI 线程外发布时由订阅方自行调度回 UI 线程）。</summary>
    public static event EventHandler<PipelineProgress>? Progress;

    /// <summary>管线结束（成功或失败）时触发一次。</summary>
    public static event EventHandler<PipelineResultNotice>? Finished;

    /// <summary>当前是否有一条管线在跑。</summary>
    public static bool IsRunning { get; private set; }

    /// <summary>当前处理中的标题（来自解析阶段上报）。</summary>
    public static string CurrentTitle { get; private set; } = "";

    public static void Publish(PipelineProgress progress)
    {
        if (progress.Stage == "error" || progress.Stage == "done")
        {
            return;
        }

        IsRunning = true;
        if (!string.IsNullOrWhiteSpace(progress.Message) && progress.Stage == "parse" && progress.Percent >= 100)
        {
            CurrentTitle = progress.Message;
        }

        Progress?.Invoke(null, progress);
    }

    public static void Finish(bool success, string title)
    {
        IsRunning = false;
        CurrentTitle = "";
        Finished?.Invoke(null, new PipelineResultNotice(success, title));
    }

    /// <summary>重置状态（应用启动或页面重载时调用，避免残留）。</summary>
    public static void Reset()
    {
        IsRunning = false;
        CurrentTitle = "";
    }
}

/// <summary>管线结束通知。</summary>
public sealed record PipelineResultNotice(bool Success, string Title);
