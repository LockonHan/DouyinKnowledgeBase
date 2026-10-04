using System.Text.Json;
using Windows.Storage;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 负责将大模型配置持久化到应用本地数据目录。
/// </summary>
public sealed class SettingsService
{
    private const string FileName = "llm-settings.json";

    /// <summary>从本地数据目录加载配置；文件不存在时返回默认配置。</summary>
    public async Task<LlmSettings> LoadAsync()
    {
        try
        {
            StorageFolder folder = ApplicationData.Current.LocalFolder;
            StorageFile? file = await folder.TryGetItemAsync(FileName) as StorageFile;
            if (file is null)
            {
                return new LlmSettings();
            }

            string json = await FileIO.ReadTextAsync(file);
            return JsonSerializer.Deserialize<LlmSettings>(json) ?? new LlmSettings();
        }
        catch (Exception)
        {
            // 配置损坏时回退到默认值，避免应用启动失败。
            return new LlmSettings();
        }
    }

    /// <summary>将配置保存到本地数据目录。</summary>
    public async Task SaveAsync(LlmSettings settings)
    {
        StorageFolder folder = ApplicationData.Current.LocalFolder;
        StorageFile file = await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await FileIO.WriteTextAsync(file, json);
    }
}