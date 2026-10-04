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