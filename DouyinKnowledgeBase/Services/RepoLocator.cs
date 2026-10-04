namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 定位仓库目录与外部工具路径（用于抖音下载 + 本地转写管线）。
/// </summary>
public static class RepoLocator
{
    /// <summary>从应用输出目录向上查找包含 tools/process_douyin.py 的仓库根目录。</summary>
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

    /// <summary>探测带 FunASR 的 Python 解释器路径。</summary>
    public static string DetectPython()
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

                try
                {
                    string exe = Path.Combine(entry, "python.exe");
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
                catch (ArgumentException)
                {
                    // 非法 PATH 片段，跳过。
                }
            }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string name in new[] { "anaconda3", "miniconda3" })
        {
            string exe = Path.Combine(home, name, "python.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return "";
    }

    /// <summary>默认数据目录（仓库下的 data 目录）。</summary>
    public static string DefaultDataDir()
    {
        string? root = FindRepoRoot();
        return Path.Combine(root ?? AppContext.BaseDirectory, "data");
    }

    /// <summary>默认转写脚本路径。</summary>
    public static string DefaultScriptPath()
    {
        string? root = FindRepoRoot();
        return root is null
            ? Path.Combine(AppContext.BaseDirectory, "tools", "process_douyin.py")
            : Path.Combine(root, "tools", "process_douyin.py");
    }
}