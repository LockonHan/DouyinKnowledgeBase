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

        // 用户明确配置的解释器不再被覆盖（EffectivePython 已优先校验用户配置）；
        // 仅当配置为空时补一个默认值，保证管线有解释器可用。
        if (string.IsNullOrWhiteSpace(settings.PythonPath))
        {
            settings.PythonPath = RepoLocator.EffectivePython("");
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