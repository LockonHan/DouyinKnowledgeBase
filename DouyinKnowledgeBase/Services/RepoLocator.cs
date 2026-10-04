using System.Diagnostics;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 定位仓库/安装目录与外部工具路径（用于抖音下载 + 本地转写管线）。
/// </summary>
public static class RepoLocator
{
    /// <summary>从应用输出目录向上查找包含 tools/process_douyin.py 的根目录。</summary>
    public static string? FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "process_douyin.py");
            if (File.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>tools 脚本目录。</summary>
    public static string ToolsDir()
    {
        string? root = FindRepoRoot();
        return root is null
            ? Path.Combine(AppContext.BaseDirectory, "tools")
            : Path.Combine(root, "tools");
    }

    /// <summary>tools 目录下的脚本完整路径。</summary>
    public static string ScriptPath(string fileName) => Path.Combine(ToolsDir(), fileName);

    /// <summary>安装目录内置的 ffmpeg（存在时返回路径，否则 null）。</summary>
    public static string? BundledFfmpeg()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string exe = Path.Combine(root, "runtime", "bin", "ffmpeg.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>安装目录内置的模型目录（存在时返回路径，否则 null）。</summary>
    public static string? BundledModelsDir()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string models = Path.Combine(root, "runtime", "models");
        return Directory.Exists(models) ? models : null;
    }

    /// <summary>安装目录内置的模型缓存根目录（存在时返回，否则 null）。</summary>
    public static string? BundledModelsCache()
    {
        return BundledModelsDir();
    }

    /// <summary>默认模型缓存根目录：内置目录优先，其次用户数据目录。</summary>
    public static string DefaultModelsCache()
    {
        return BundledModelsCache() ?? Path.Combine(AppPaths.LocalDataDir, "models");
    }

    /// <summary>
    /// 把用户指定的“离线模型目录”规范化为 ModelScope 缓存根目录。
    /// 用户可能直接指向 models 子目录，这里向上回退一层。
    /// </summary>
    public static string NormalizeModelsCache(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return "";
        }

        string full;
        try
        {
            full = Path.GetFullPath(dir);
        }
        catch (Exception)
        {
            return dir;
        }

        if (!Directory.Exists(full))
        {
            return full;
        }

        // 已经是缓存根：其下存在 models 子目录。
        if (Directory.Exists(Path.Combine(full, "models")))
        {
            return full;
        }

        // 指向了 models 子目录本身（其下直接是 iic），向上回退一层。
        if (Directory.Exists(Path.Combine(full, "iic")))
        {
            DirectoryInfo? parent = Directory.GetParent(full);
            if (parent is not null)
            {
                return parent.FullName;
            }
        }

        return full;
    }

    /// <summary>安装目录内置的 Python 运行时（存在时返回，否则 null）。</summary>
    public static string? BundledPython()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string exe = Path.Combine(root, "runtime", "python", "python.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>安装目录内置的 Playwright 浏览器目录（存在时返回，否则 null）。</summary>
    public static string? BundledBrowsersDir()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string dir = Path.Combine(root, "runtime", "browsers");
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>解析实际使用的 Python：内置运行时优先，其次配置值，最后自动探测。</summary>
    public static string EffectivePython(string? configured)
    {
        string? bundled = BundledPython();
        if (bundled is not null && IsUsablePython(bundled))
        {
            return bundled;
        }

        if (IsUsablePython(configured))
        {
            return configured!;
        }

        string detected = DetectPython();
        if (!string.IsNullOrWhiteSpace(detected))
        {
            return detected;
        }

        return bundled ?? configured ?? "";
    }

    /// <summary>环境准备的目标 Python：内置运行时优先（即便尚未安装依赖）。</summary>
    public static string SetupTargetPython(string? configured)
    {
        string? bundled = BundledPython();
        if (bundled is not null)
        {
            return bundled;
        }

        return EffectivePython(configured);
    }

    /// <summary>内置运行时根目录（runtime），无法定位安装目录时为 null。</summary>
    public static string? RuntimeRoot()
    {
        string? root = FindRepoRoot();
        return root is null ? null : Path.Combine(root, "runtime");
    }

    /// <summary>
    /// 环境准备时 Playwright 浏览器的目标目录：仅当存在内置运行时目录（即安装版）时
    /// 返回 &lt;runtime&gt;\browsers，否则返回空字符串，避免覆盖 Playwright 默认位置。
    /// </summary>
    public static string SetupBrowsersDir()
    {
        string? runtime = RuntimeRoot();
        if (runtime is null || !Directory.Exists(runtime))
        {
            return "";
        }

        return Path.Combine(runtime, "browsers");
    }

    /// <summary>本应用需要的 FunASR 模型（ModelScope 上的仓库名）。</summary>
    public static readonly string[] RequiredModels =
    {
        "speech_paraformer-large-vad-punc_asr_nat-zh-cn-16k-common-vocab8404-pytorch",
        "speech_fsmn_vad_zh-cn-16k-common-pytorch",
        "punc_ct-transformer_cn-en-common-vocab471067-large",
    };

    /// <summary>模型权重文件名（用于判定模型是否真正下载完成）。</summary>
    private static readonly string[] ModelWeightMarkers =
    {
        "model.pt", "model.pth", "model.pb", "model.bin",
    };

    /// <summary>
    /// 枚举某模型在所有已知布局下的候选目录。
    /// 旧版 ModelScope：&lt;cache&gt;\models\iic\&lt;name&gt;、&lt;cache&gt;\iic\&lt;name&gt;
    /// 新版 ModelScope（HF 风格）：&lt;cache&gt;\models\iic--&lt;name&gt;\snapshots\&lt;revision&gt;
    /// </summary>
    private static IEnumerable<string> ModelCandidates(string? cacheRoot, string modelName)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            yield break;
        }

        List<string> roots = new() { cacheRoot };
        string norm = cacheRoot.TrimEnd('\\', '/');
        if (string.Equals(Path.GetFileName(norm), "models", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(norm);
            if (!string.IsNullOrEmpty(parent))
            {
                roots.Add(parent);
            }
        }

        foreach (string root in roots)
        {
            yield return Path.Combine(root, "models", "iic", modelName);
            yield return Path.Combine(root, "iic", modelName);
            yield return Path.Combine(root, "models", "iic--" + modelName);
            yield return Path.Combine(root, "iic--" + modelName);

            string[] parents =
            {
                Path.Combine(root, "models", "iic--" + modelName),
                Path.Combine(root, "iic--" + modelName),
            };
            foreach (string parent in parents)
            {
                string snapshots = Path.Combine(parent, "snapshots");
                if (!Directory.Exists(snapshots))
                {
                    continue;
                }

                string[] revisions;
                try
                {
                    revisions = Directory.GetDirectories(snapshots);
                }
                catch (Exception)
                {
                    continue;
                }

                Array.Sort(revisions, StringComparer.Ordinal);
                foreach (string revision in revisions)
                {
                    yield return revision;
                }
            }
        }
    }

    /// <summary>目录内是否存在已完成的权重文件（排除 *.incomplete 等中断残留）。</summary>
    private static bool HasModelWeights(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (Exception)
        {
            return false;
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            string low = name.ToLowerInvariant();
            if (low.EndsWith(".incomplete") || low.EndsWith(".tmp") || low.EndsWith(".part"))
            {
                continue;
            }

            if (ModelWeightMarkers.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                low.EndsWith(".safetensors"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>返回已就绪的模型目录；未找到返回 null。兼容新旧两种 ModelScope 缓存布局。</summary>
    public static string? FindModelDir(string? cacheRoot, string modelName)
    {
        foreach (string candidate in ModelCandidates(cacheRoot, modelName))
        {
            if (HasModelWeights(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>判断给定 ModelScope 缓存根目录下是否已包含全部所需模型。</summary>
    public static bool HasAllModels(string? cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return false;
        }

        return RequiredModels.All(model => FindModelDir(cacheRoot, model) is not null);
    }

    /// <summary>列出缺失的模型仓库名。</summary>
    public static IReadOnlyList<string> MissingModels(string? cacheRoot)
    {
        List<string> missing = new();
        foreach (string model in RequiredModels)
        {
            if (FindModelDir(cacheRoot, model) is null)
            {
                missing.Add(model);
            }
        }

        return missing;
    }
    /// 解析实际使用的 ModelScope 缓存根目录：优先“已包含全部模型”的目录
    /// （用户指定 → 内置目录 → 本机默认缓存），避免重复下载。
    /// 都没有时回落到内置目录；返回空字符串表示交给 ModelScope 使用默认位置。
    /// </summary>
    public static string EffectiveModelsCache(string? configured)
    {
        string normalized = NormalizeModelsCache(configured);
        if (HasAllModels(normalized))
        {
            return normalized;
        }

        string? bundled = BundledModelsCache();
        if (bundled is not null && HasAllModels(bundled))
        {
            return bundled;
        }

        if (HasAllModels(DefaultModelscopeCache()))
        {
            return DefaultModelscopeCache();
        }

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        return bundled ?? "";
    }

    /// <summary>本机是否已具备全部语音模型（用户目录 / 内置目录 / 默认缓存任一）。</summary>
    public static bool ModelsReady(string? configured)
    {
        return HasAllModels(EffectiveModelsCache(configured));
    }

    /// <summary>ModelScope 默认缓存根目录（%USERPROFILE%\.cache\modelscope\hub）。</summary>
    public static string DefaultModelscopeCache()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "modelscope", "hub");
    }

    /// <summary>
    /// 判断指定解释器是否可用：文件存在，且装齐管线所需模块
    /// （funasr 转写、playwright + curl_cffi 下载）。
    /// </summary>
    public static bool IsUsablePython(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        const string probe =
            "import importlib.util as u,sys;" +
            "sys.exit(0 if all(u.find_spec(m) for m in ('funasr','playwright','curl_cffi')) else 1)";

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(probe);

            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(30000))
            {
                process.Kill(true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>探测带依赖的 Python 解释器；找不到可用的时回退到第一个存在的解释器。</summary>
    public static string DetectPython()
    {
        List<string> candidates = EnumeratePythonCandidates().ToList();
        foreach (string candidate in candidates)
        {
            if (IsUsablePython(candidate))
            {
                return candidate;
            }
        }

        return candidates.Count > 0 ? candidates[0] : "";
    }

    private static IEnumerable<string> EnumeratePythonCandidates()
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (string raw in pathVar.Split(';'))
            {
                string entry = raw.Trim();
                if (entry.Length == 0 || entry.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string exe;
                try
                {
                    exe = Path.Combine(entry, "python.exe");
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(exe))
                {
                    yield return exe;
                }
            }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string name in new[] { "anaconda3", "miniconda3" })
        {
            string exe = Path.Combine(home, name, "python.exe");
            if (File.Exists(exe))
            {
                yield return exe;
            }
        }
    }

    /// <summary>默认数据目录（安装目录或仓库下的 data 目录）。</summary>
    public static string DefaultDataDir()
    {
        string? root = FindRepoRoot();
        return Path.Combine(root ?? AppContext.BaseDirectory, "data");
    }

    /// <summary>默认转写脚本路径。</summary>
    public static string DefaultScriptPath() => ScriptPath("process_douyin.py");
}