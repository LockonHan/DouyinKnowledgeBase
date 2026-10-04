using System.Text.Json;
using Windows.Storage;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 管线配置持久化；文件不存在或字段为空时回落到自动探测结果。
/// </summary>
public sealed class PipelineSettingsService
{
    private const string FileName = "pipeline-settings.json";

    public async Task<PipelineSettings> LoadAsync()
    {
        PipelineSettings settings;
        try
        {
            StorageFolder folder = ApplicationData.Current.LocalFolder;
            StorageFile? file = await folder.TryGetItemAsync(FileName) as StorageFile;
            if (file is null)
            {
                return CreateDefault();
            }

            string json = await FileIO.ReadTextAsync(file);
            settings = JsonSerializer.Deserialize<PipelineSettings>(json) ?? CreateDefault();
        }
        catch (Exception)
        {
            return CreateDefault();
        }

        if (string.IsNullOrWhiteSpace(settings.PythonPath))
        {
            settings.PythonPath = RepoLocator.DetectPython();
        }

        if (string.IsNullOrWhiteSpace(settings.DataDir))
        {
            settings.DataDir = RepoLocator.DefaultDataDir();
        }

        if (string.IsNullOrWhiteSpace(settings.Device))
        {
            settings.Device = "cuda:0";
        }

        return settings;
    }

    public async Task SaveAsync(PipelineSettings settings)
    {
        StorageFolder folder = ApplicationData.Current.LocalFolder;
        StorageFile file = await folder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await FileIO.WriteTextAsync(file, json);
    }

    private static PipelineSettings CreateDefault()
    {
        return new PipelineSettings
        {
            PythonPath = RepoLocator.DetectPython(),
            DataDir = RepoLocator.DefaultDataDir(),
            Device = "cuda:0",
        };
    }
}