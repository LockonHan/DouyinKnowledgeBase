using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// Obsidian 集成配置持久化；文件不存在或损坏时返回默认配置。
/// </summary>
public sealed class ObsidianSettingsService
{
    private const string FileName = "obsidian-settings.json";

    public ObsidianSettingsService()
    {
        AppPaths.MigrateLegacySettings(FileName);
    }

    private static string FilePath => Path.Combine(AppPaths.LocalDataDir, FileName);

    public async Task<ObsidianSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return CreateDefault();
            }

            string json = await File.ReadAllTextAsync(FilePath, cancellationToken);
            return JsonSerializer.Deserialize<ObsidianSettings>(json, JsonDefaults.CaseInsensitive) ?? CreateDefault();
        }
        catch (Exception)
        {
            return CreateDefault();
        }
    }

    public async Task SaveAsync(ObsidianSettings settings, CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(settings, JsonDefaults.Indented);
        await File.WriteAllTextAsync(FilePath, json, cancellationToken);
    }

    private static ObsidianSettings CreateDefault()
    {
        return new ObsidianSettings();
    }
}
