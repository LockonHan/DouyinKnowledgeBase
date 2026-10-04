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
            settings = JsonSerializer.Deserialize<PipelineSettings>(json, JsonDefaults.CaseInsensitive) ?? CreateDefault();
        }
        catch (Exception)
        {
            return CreateDefault();
        }

        // 配置里的解释器不可用时，依次回落到内置运行时与自动探测结果。
        string effective = RepoLocator.EffectivePython(settings.PythonPath);
        if (!string.IsNullOrWhiteSpace(effective))
        {
            settings.PythonPath = effective;
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
        string json = JsonSerializer.Serialize(settings, JsonDefaults.Indented);
        await File.WriteAllTextAsync(FilePath, json);
    }

    private static PipelineSettings CreateDefault()
    {
        return new PipelineSettings
        {
            PythonPath = RepoLocator.EffectivePython(""),
            DataDir = RepoLocator.DefaultDataDir(),
            Device = "auto",
            AsrResident = true,
        };
    }
}