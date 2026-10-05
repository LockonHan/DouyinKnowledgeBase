namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 把视频转写文稿交给大模型，生成结构化知识科普文章。
/// </summary>
public sealed class SummaryService
{
    private const string SystemPrompt = """
你是一名资深中文知识科普编辑。用户会提供一段由视频自动语音识别（ASR）得到的中文文稿，其中可能存在错别字、口语化表达、重复语句，以及英文术语被音译的错误。

请你在忠于文稿事实的前提下完成：
1. 结合上下文纠正明显的语音识别错别字；不要编造文稿中没有的事实、数据或引用。
2. 将内容整理为一篇结构清晰的知识科普文章，严格使用如下 Markdown 结构输出：

# 标题（一句话概括主题）

> 一句话摘要

## 背景
（这个主题要解决什么问题、为什么重要）

## 核心概念
（关键术语与概念解释，可用列表）

## 工作原理 / 主要内容
（按逻辑分小节，讲清楚核心机制或观点）

## 关键要点
- 3～6 条可快速复习的要点

## 应用场景
（对观众或从业者的实际意义）

## 一句话总结
（收尾）

要求：全程使用简体中文；语言准确、通俗；Markdown 标题层级规范；如果文稿信息不足以支撑某个小节，用一两句说明，不要编造。
""";

    private readonly LlmClient _llmClient;

    public SummaryService(LlmClient llmClient)
    {
        _llmClient = llmClient;
    }

    /// <summary>调用大模型，将转写文稿总结为结构化文章。</summary>
    public async Task<string> SummarizeAsync(LlmSettings settings, string transcript)
    {
        string userMessage = "以下是视频转写文稿：\n\n" + transcript.Trim();
        return await _llmClient.ChatAsync(settings, SystemPrompt, userMessage);
    }

    /// <summary>
    /// 流式版：调用大模型逐块返回文章内容，onDelta 每次收到增量文本时回调（调用方可用于显示进度）。
    /// </summary>
    public Task<string> SummarizeStreamAsync(
        LlmSettings settings,
        string transcript,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        string userMessage = "以下是视频转写文稿：\n\n" + transcript.Trim();
        return _llmClient.ChatStreamAsync(settings, SystemPrompt, userMessage, onDelta, cancellationToken);
    }
}