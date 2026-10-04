using System.Text.Json;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 管线配置持久化；文件不存在或字段为空时回落到自动探测结果。
/// </summary>
public sealed class PipelineSettingsService
{
    private const string FileName = "pipeline-settings.json";

    public PipelineSettingsService()
    {
        AppPaths.MigrateLegacySettings(FileName);
    }

    private static string FilePath => Path.Combine(AppPaths.LocalDataDir, FileName);

    public async Task<PipelineSettings> LoadAsync()
    {
        PipelineSettings settings;
        try
        {
            if (!File.Exists(FilePath))
            {
                return CreateDefault();
            }

            string json = await File.ReadAllTextAsync(FilePath);
            settings = JsonSerializer.Deserialize<PipelineSettings>(json) ?? CreateDefault();
        }
        catch (Exception)
        {
            return CreateDefault();
        }

        if (!RepoLocator.IsUsablePython(settings.PythonPath))
        {
            // 配置里的解释器不存在或缺少 funasr / playwright 等依赖时，自动改用探测到的可用解释器。
            string detected = RepoLocator.DetectPython();
            if (!string.IsNullOrWhiteSpace(detected))
            {
                settings.PythonPath = detected;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.DataDir))
        {
            settings.DataDir = RepoLocator.DefaultDataDir();
        }

        if (string.IsNullOrWhiteSpace(settings.Device))
        {
            settings.Device = "auto";
        }

        return settings;
    }

    public async Task SaveAsync(PipelineSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(FilePath, json);
    }

    private static PipelineSettings CreateDefault()
    {
        return new PipelineSettings
        {
            PythonPath = RepoLocator.DetectPython(),
            DataDir = RepoLocator.DefaultDataDir(),
            Device = "auto",
            AsrResident = true,
        };
    }
}