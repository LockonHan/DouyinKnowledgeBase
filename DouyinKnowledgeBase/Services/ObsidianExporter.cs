using System.Text;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 导出 AI 知识文章到 Obsidian vault：组装 frontmatter + 正文，
/// 文件直写 <vaultPath>/<subfolder>/<file>.md，Obsidian 打开 vault 时自动索引。
/// （obsidian CLI 增强——导出后打开、触发同步——由后续步骤接入。）
/// </summary>
public sealed class ObsidianExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public ObsidianExporter()
    {
        SettingsService = new ObsidianSettingsService();
    }

    internal ObsidianSettingsService SettingsService { get; }

    /// <summary>
    /// 导出文章到配置的 Obsidian vault。
    /// </summary>
    /// <returns>成功时 FilePath 为落盘文件的绝对路径。</returns>
    public async Task<ObsidianExportResult> ExportAsync(
        ArticleMeta meta,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        ObsidianSettings settings = await SettingsService.LoadAsync(cancellationToken);
        return await ExportAsync(settings, meta, markdown, cancellationToken);
    }

    public async Task<ObsidianExportResult> ExportAsync(
        ObsidianSettings settings,
        ArticleMeta meta,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.VaultPath))
        {
            return ObsidianExportResult.Fail("尚未配置 Obsidian Vault 路径。请到「设置 → Obsidian 集成」填写。");
        }

        if (!Directory.Exists(settings.VaultPath))
        {
            return ObsidianExportResult.Fail($"Obsidian Vault 路径不存在：{settings.VaultPath}");
        }

        string folder = CombineVaultFolder(settings.VaultPath, settings.Subfolder);
        Directory.CreateDirectory(folder);

        string baseName = MakeFileName(meta.Title);
        string fullPath = Path.Combine(folder, baseName + ".md");

        // 冲突策略：overwrite 幂等覆盖同名文件；suffix 追加序号保留旧文件。
        if (File.Exists(fullPath) && !string.Equals(settings.ConflictPolicy, "overwrite", StringComparison.OrdinalIgnoreCase))
        {
            fullPath = NextAvailablePath(folder, baseName);
        }

        string content = settings.AddFrontmatter ? BuildContent(meta, markdown) : markdown;
        await File.WriteAllTextAsync(fullPath, content, Utf8NoBom, cancellationToken);

        // CLI 增强：导出后打开笔记（需 Obsidian 运行中且开启命令行界面；失败不影响导出结果）。
        bool opened = false;
        if (settings.OpenAfterExport)
        {
            string relativeFolder = CombineVaultFolder("", settings.Subfolder);
            string relativePath = Path.Combine(relativeFolder, Path.GetFileName(fullPath));
            opened = await ObsidianCli.OpenNoteAsync(settings.VaultName, relativePath, cancellationToken);
        }

        return ObsidianExportResult.Ok(fullPath, opened);
    }

    /// <summary>
    /// 组装导出文件：YAML frontmatter（标题/来源/标签/模型/时间等）+ 文章正文。
    /// </summary>
    public static string BuildContent(ArticleMeta meta, string markdown)
    {
        StringBuilder sb = new();
        sb.AppendLine("---");
        sb.Append("title: ").AppendLine(YamlQuote(meta.Title));
        sb.Append("source: ").AppendLine(YamlQuote("抖音视频"));
        if (!string.IsNullOrWhiteSpace(meta.SourceTitle))
        {
            sb.Append("source_title: ").AppendLine(YamlQuote(meta.SourceTitle));
        }

        if (!string.IsNullOrWhiteSpace(meta.Summary))
        {
            sb.Append("summary: ").AppendLine(YamlQuote(meta.Summary));
        }

        if (meta.Tags is { Length: > 0 })
        {
            sb.AppendLine("tags:");
            foreach (string tag in meta.Tags)
            {
                sb.Append("  - ").AppendLine(YamlQuote(tag));
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.Id))
        {
            sb.Append("article_id: ").AppendLine(YamlQuote(meta.Id));
        }

        if (!string.IsNullOrWhiteSpace(meta.Model))
        {
            sb.Append("model: ").AppendLine(YamlQuote(meta.Model));
        }

        sb.Append("created: ").AppendLine(FormatTime(meta.CreatedAt));
        sb.Append("updated: ").AppendLine(FormatTime(meta.UpdatedAt));
        sb.AppendLine("---");
        sb.AppendLine();

        sb.Append(markdown);
        return sb.ToString();
    }

    /// <summary>
    /// 把 vault 路径与子目录拼接为绝对路径；子目录为空时使用 vault 根目录。
    /// 兼容子目录以 \ 或 / 开头。
    /// </summary>
    internal static string CombineVaultFolder(string vaultPath, string subfolder)
    {
        string trimmed = subfolder?.Trim().Trim('\\', '/') ?? "";
        return trimmed.Length == 0 ? vaultPath : Path.Combine(vaultPath, trimmed);
    }

    /// <summary>
    /// 生成 Obsidian 安全文件名：仅保留字母数字，其余转 '-'，截断 40 字符。
    /// Obsidian 禁用的字符（\ / : * ? " &lt; &gt; | 及前导点）都会被过滤。
    /// </summary>
    internal static string MakeFileName(string title)
    {
        StringBuilder builder = new();
        foreach (char ch in title)
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

        string name = builder.ToString().Trim('-');
        return name.Length == 0 ? "note" : name;
    }

    /// <summary>同名文件已存在时，追加序号（-2、-3 …）找到空闲路径。</summary>
    internal static string NextAvailablePath(string folder, string baseName)
    {
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(folder, $"{baseName}-{i}.md");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>YAML 双引号字符串：转义反斜杠与双引号，避免标题中的冒号/特殊字符破坏 frontmatter。</summary>
    private static string YamlQuote(string value)
    {
        string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "\"" + escaped + "\"";
    }

    private static string FormatTime(DateTime time) =>
        time == default ? "" : time.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>Obsidian 导出结果；Opened 表示是否通过 obsidian CLI 打开了笔记。</summary>
public sealed record ObsidianExportResult(bool Success, string FilePath = "", string Error = "", bool Opened = false)
{
    public static ObsidianExportResult Ok(string filePath, bool opened = false) => new(true, filePath, "", opened);

    public static ObsidianExportResult Fail(string error) => new(false, "", error);
}
