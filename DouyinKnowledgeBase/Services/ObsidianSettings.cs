using System.Text.Json.Serialization;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// Obsidian 集成配置：导出 AI 知识文章到 Obsidian vault。
/// </summary>
public sealed class ObsidianSettings
{
    /// <summary>目标 vault 名称（用于 obsidian CLI 的 vault=&lt;name&gt; 参数）。</summary>
    [JsonPropertyName("vaultName")]
    public string VaultName { get; set; } = "";

    /// <summary>目标 vault 绝对路径（文件直写通道使用，Obsidian 打开时自动索引）。</summary>
    [JsonPropertyName("vaultPath")]
    public string VaultPath { get; set; } = "";

    /// <summary>vault 内导出子目录；留空表示 vault 根目录。</summary>
    [JsonPropertyName("subfolder")]
    public string Subfolder { get; set; } = "抖音知识库";

    /// <summary>导出时是否写入 YAML frontmatter（标题/标签/来源/时间等元数据）。</summary>
    [JsonPropertyName("addFrontmatter")]
    public bool AddFrontmatter { get; set; } = true;

    /// <summary>导出后是否通过 obsidian CLI 打开该笔记。</summary>
    [JsonPropertyName("openAfterExport")]
    public bool OpenAfterExport { get; set; }

    /// <summary>导出模式：auto（文件直写 + CLI 可用时增强）/ cli / file。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "auto";

    /// <summary>文件冲突策略：overwrite（按文章 Id 幂等覆盖）/ suffix（重名时追加序号）。</summary>
    [JsonPropertyName("conflictPolicy")]
    public string ConflictPolicy { get; set; } = "overwrite";
}
