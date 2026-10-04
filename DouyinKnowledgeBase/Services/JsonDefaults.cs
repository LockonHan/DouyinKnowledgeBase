using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 统一的 System.Text.Json 配置。显式指定反射类型解析器，
/// 避免在启用裁剪（PublishTrimmed）时运行时抛 InvalidOperationException。
/// </summary>
internal static class JsonDefaults
{
    /// <summary>缩进输出，用于写入配置文件。</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>属性名大小写不敏感，用于解析配置文件与外部脚本输出。</summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
