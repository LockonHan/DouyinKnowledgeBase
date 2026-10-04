using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>负责将大模型配置持久化到应用本地数据目录。</summary>
public sealed class SettingsService
{
    private const string FileName = "llm-settings.json";

    public SettingsService()
    {
        AppPaths.MigrateLegacySettings(FileName);
    }

    private static string FilePath => Path.Combine(AppPaths.LocalDataDir, FileName);

    /// <summary>从本地数据目录加载配置；文件不存在或损坏时返回默认配置。</summary>
    public async Task<LlmSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new LlmSettings();
            }

            string json = await File.ReadAllTextAsync(FilePath);
            return JsonSerializer.Deserialize<LlmSettings>(json) ?? new LlmSettings();
        }
        catch (Exception)
        {
            return new LlmSettings();
        }
    }

    /// <summary>将配置保存到本地数据目录。</summary>
    public async Task SaveAsync(LlmSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(FilePath, json);
    }
}