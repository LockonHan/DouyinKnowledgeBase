using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 本地知识索引：扫描 data 目录下的转写稿 / 视频 / 音频 / 文章，
/// 合并为按时间倒序的知识条目列表。不依赖数据库，每次按需重建。
/// </summary>
public sealed partial class KnowledgeIndex
{
    /// <summary>视频可能出现的扩展名。</summary>
    private static readonly string[] VideoExtensions = [".mp4", ".webm", ".mov", ".mkv"];

    public async Task<IReadOnlyList<KnowledgeItem>> LoadAsync(
        PipelineSettings settings,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, EntryBuilder> entries = new(StringComparer.OrdinalIgnoreCase);

        string transcriptsDir = Path.Combine(settings.DataDir, "transcripts");
        foreach (string file in EnumerateFiles(transcriptsDir, ".txt"))
        {
            EntryBuilder entry = GetOrAdd(entries, Path.GetFileNameWithoutExtension(file));
            entry.TranscriptPath = file;
            entry.Touch(File.GetLastWriteTime(file));
        }

        string videosDir = Path.Combine(settings.DataDir, "videos");
        foreach (string file in EnumerateFiles(videosDir, VideoExtensions))
        {
            EntryBuilder entry = GetOrAdd(entries, Path.GetFileNameWithoutExtension(file));
            entry.VideoPath = file;
            entry.Touch(File.GetLastWriteTime(file));
        }

        string audioDir = Path.Combine(settings.DataDir, "audio");
        foreach (string file in EnumerateFiles(audioDir, ".wav"))
        {
            EntryBuilder entry = GetOrAdd(entries, Path.GetFileNameWithoutExtension(file));
            entry.AudioPath = file;
            entry.Touch(File.GetLastWriteTime(file));
        }

        await MergeArticlesAsync(Path.Combine(settings.DataDir, "articles"), entries, cancellationToken);

        return entries.Values
            .Select(entry => entry.Build())
            .OrderByDescending(item => item.Timestamp)
            .ToList();
    }

    /// <summary>
    /// 按关键词 / 状态 / 标签过滤。关键词匹配标题与标签；
    /// 正文检索见 <see cref="MatchesBody"/>。
    /// </summary>
    public static IReadOnlyList<KnowledgeItem> Query(
        IEnumerable<KnowledgeItem> items,
        string? search = null,
        KnowledgeStatus? status = null,
        string? tag = null)
    {
        IEnumerable<KnowledgeItem> query = items;

        if (status is not null)
        {
            query = query.Where(item => item.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(item => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            string needle = search.Trim();
            query = query.Where(item =>
                item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.SourceTitle.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        return query.ToList();
    }

    /// <summary>统计所有标签及出现次数，按次数倒序。</summary>
    public static IReadOnlyList<(string Tag, int Count)> TagCounts(IEnumerable<KnowledgeItem> items)
    {
        return items
            .SelectMany(item => item.Tags)
            .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.First(), group.Count()))
            .OrderByDescending(pair => pair.Item2)
            .ThenBy(pair => pair.Item1, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>读取转写稿正文，供全文搜索与阅读使用；失败时返回空串。</summary>
    public static string ReadTranscript(KnowledgeItem item) => ReadTextFile(item.TranscriptPath);

    /// <summary>正文是否包含关键词（全文搜索用）。</summary>
    public static bool MatchesBody(KnowledgeItem item, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle) || string.IsNullOrEmpty(item.TranscriptPath))
        {
            return false;
        }

        string body = ReadTranscript(item);
        return body.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>由原标题生成稳定标识：可读 slug + 短哈希（避免重名与非法字符）。</summary>
    public static string MakeId(string sourceTitle)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceTitle)))
            .ToLowerInvariant()[..8];
        string slug = Slug(sourceTitle);
        return slug.Length == 0 ? hash : $"{slug}-{hash}";
    }

    /// <summary>解析抖音标题里的话题标签（#xxx）。</summary>
    public static string[] ExtractTags(string rawTitle) =>
        TagRegex().Matches(rawTitle)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>去掉 #标签 后的展示标题，过长时截断。</summary>
    public static string CleanTitle(string rawTitle)
    {
        string text = WhitespaceRegex().Replace(TagRegex().Replace(rawTitle, " "), " ").Trim();
        if (text.Length == 0)
        {
            text = rawTitle.Trim();
        }

        return text.Length <= 64 ? text : text[..64].TrimEnd() + "…";
    }

    /// <summary>由 16kHz 单声道 16bit WAV 的字节数推算时长。</summary>
    public static double WavDurationSeconds(string? wavPath)
    {
        if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
        {
            return 0;
        }

        try
        {
            // 16000 Hz × 2 字节 = 每秒 32000 字节；文件头 44 字节可忽略。
            return new FileInfo(wavPath).Length / 32000.0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static async Task MergeArticlesAsync(
        string articlesDir,
        Dictionary<string, EntryBuilder> entries,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(articlesDir))
        {
            return;
        }

        foreach (string metaPath in Directory.EnumerateFiles(articlesDir, "*.json"))
        {
            ArticleMeta? meta;
            try
            {
                string json = await File.ReadAllTextAsync(metaPath, cancellationToken);
                meta = JsonSerializer.Deserialize<ArticleMeta>(json, JsonDefaults.CaseInsensitive);
            }
            catch (Exception)
            {
                // 元数据损坏时忽略该文章，不影响其余条目。
                continue;
            }

            if (meta is null || string.IsNullOrWhiteSpace(meta.SourceTitle))
            {
                continue;
            }

            EntryBuilder entry = GetOrAdd(entries, meta.SourceTitle);
            entry.ArticleId = string.IsNullOrWhiteSpace(meta.Id)
                ? Path.GetFileNameWithoutExtension(metaPath)
                : meta.Id;
            entry.Summary = meta.Summary;
            entry.Touch(meta.UpdatedAt == default ? File.GetLastWriteTime(metaPath) : meta.UpdatedAt);
        }
    }

    private static EntryBuilder GetOrAdd(Dictionary<string, EntryBuilder> entries, string sourceTitle)
    {
        if (!entries.TryGetValue(sourceTitle, out EntryBuilder? entry))
        {
            entry = new EntryBuilder(sourceTitle);
            entries[sourceTitle] = entry;
        }

        return entry;
    }

    private static IEnumerable<string> EnumerateFiles(string directory, params string[] extensions)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Where(file => extensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
    }

    private static string ReadTextFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return "";
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string Slug(string raw)
    {
        StringBuilder builder = new();
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    /// <summary>扫描过程中的可变累加器。</summary>
    private sealed class EntryBuilder(string sourceTitle)
    {
        private DateTime _timestamp = DateTime.MinValue;

        public string TranscriptPath { get; set; } = "";

        public string? VideoPath { get; set; }

        public string? AudioPath { get; set; }

        public string? ArticleId { get; set; }

        public string Summary { get; set; } = "";

        public void Touch(DateTime time)
        {
            if (time > _timestamp)
            {
                _timestamp = time;
            }
        }

        public KnowledgeItem Build() => new()
        {
            Id = MakeId(sourceTitle),
            Title = CleanTitle(sourceTitle),
            SourceTitle = sourceTitle,
            Tags = ExtractTags(sourceTitle),
            TranscriptPath = TranscriptPath,
            VideoPath = VideoPath,
            AudioPath = AudioPath,
            DurationSec = WavDurationSeconds(AudioPath),
            ArticleId = ArticleId,
            Summary = Summary,
            Status = string.IsNullOrEmpty(ArticleId) ? KnowledgeStatus.Transcribed : KnowledgeStatus.Summarized,
            Timestamp = _timestamp == DateTime.MinValue ? DateTime.Now : _timestamp,
        };
    }

    [GeneratedRegex(@"#([^\s#]+)")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
