using System.Text;
using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 文章库：把生成的知识文章落盘到 data/articles，
/// 每篇一个 &lt;id&gt;.md（正文）与 &lt;id&gt;.json（元数据），供知识索引与阅读视图使用。
/// </summary>
public sealed class ArticleStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ArticleStore(PipelineSettings settings)
        : this(settings.DataDir)
    {
    }

    public ArticleStore(string dataDir)
    {
        ArticlesDirectory = Path.Combine(dataDir, "articles");
    }

    /// <summary>文章目录（data/articles）。</summary>
    public string ArticlesDirectory { get; }

    public string MarkdownPath(string articleId) => Path.Combine(ArticlesDirectory, articleId + ".md");

    public string MetaPath(string articleId) => Path.Combine(ArticlesDirectory, articleId + ".json");

    public bool Exists(string articleId) => File.Exists(MarkdownPath(articleId));

    /// <summary>
    /// 保存文章：同一来源覆盖旧正文，保留首次生成的 CreatedAt。
    /// 返回写入的元数据（含解析出的标题与摘要）。
    /// </summary>
    public async Task<ArticleMeta> SaveAsync(
        string sourceTitle,
        string transcriptPath,
        string markdown,
        string model,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ArticlesDirectory);

        string id = KnowledgeIndex.MakeId(sourceTitle);
        DateTime now = DateTime.Now;

        ArticleMeta? existing = await ReadMetaAsync(id, cancellationToken);
        DateTime createdAt = now;
        if (existing is not null && existing.CreatedAt != default)
        {
            createdAt = existing.CreatedAt;
        }

        await File.WriteAllTextAsync(MarkdownPath(id), markdown, Utf8NoBom, cancellationToken);

        ArticleMeta meta = new()
        {
            Id = id,
            Title = ExtractTitle(markdown, sourceTitle),
            Summary = ExtractSummary(markdown),
            SourceTitle = sourceTitle,
            TranscriptPath = transcriptPath,
            Tags = KnowledgeIndex.ExtractTags(sourceTitle),
            Model = model,
            CreatedAt = createdAt,
            UpdatedAt = now,
        };

        string json = JsonSerializer.Serialize(meta, JsonDefaults.Indented);
        await File.WriteAllTextAsync(MetaPath(id), json, Utf8NoBom, cancellationToken);

        return meta;
    }

    public async Task<string?> ReadMarkdownAsync(string articleId, CancellationToken cancellationToken = default)
    {
        string path = MarkdownPath(articleId);
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    public async Task<ArticleMeta?> ReadMetaAsync(string articleId, CancellationToken cancellationToken = default)
    {
        string path = MetaPath(articleId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<ArticleMeta>(json, JsonDefaults.CaseInsensitive);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>取 Markdown 中第一个一级标题；缺失时回落到清洗后的原标题。</summary>
    public static string ExtractTitle(string markdown, string fallbackSourceTitle)
    {
        foreach (string raw in EnumerateLines(markdown))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }

            // 正文已经开始却仍没有一级标题，就不再往下找。
            if (!line.StartsWith('#'))
            {
                break;
            }
        }

        return KnowledgeIndex.CleanTitle(fallbackSourceTitle);
    }

    /// <summary>取标题后的引用行作为一句话摘要；没有引用行时取第一个正文段落。</summary>
    public static string ExtractSummary(string markdown)
    {
        bool seenTitle = false;
        foreach (string raw in EnumerateLines(markdown))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                seenTitle = true;
                continue;
            }

            if (line.StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith('>'))
            {
                return line.TrimStart('>', ' ').Trim();
            }

            if (seenTitle)
            {
                return line;
            }
        }

        return "";
    }

    private static IEnumerable<string> EnumerateLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}
