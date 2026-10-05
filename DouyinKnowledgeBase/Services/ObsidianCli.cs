using System.Diagnostics;
using System.Text;

namespace DouyinKnowledgeBase.Services;

/// <summary>
/// obsidian CLI 封装：探测可执行文件、枚举 vault、打开笔记。
/// CLI 是"客户端 → 运行中 Obsidian 实例"模型：需要 Obsidian 正在运行，
/// 且在「设置 → 关于 → 高级」开启「命令行界面」开关。
/// </summary>
public sealed class ObsidianCli
{
    /// <summary>Obsidian CLI 输出为 UTF-8（实测 Obsidian.com）。</summary>
    private static readonly Encoding OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>CLI 命令结果。</summary>
    public sealed record CliResult(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>一个已注册的 vault。</summary>
    public sealed record ObsidianVaultInfo(string Name, string Path);

    /// <summary>
    /// 探测 obsidian 可执行文件：先按 PATH 找（.com 优先，可捕获输出），
    /// 再查常见安装路径；找不到返回 null。
    /// </summary>
    public static string? FindExecutable()
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVar))
        {
            string[] names = { "obsidian.com", "obsidian.exe", "obsidian.cmd", "obsidian.bat", "obsidian" };
            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                string trimmed = dir.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                foreach (string name in names)
                {
                    string candidate = Path.Combine(trimmed, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        foreach (string candidate in new[]
        {
            @"C:\Program Files\Obsidian\Obsidian.com",
            @"C:\Program Files\Obsidian\Obsidian.exe",
            @"C:\Program Files (x86)\Obsidian\Obsidian.com",
            @"C:\Program Files (x86)\Obsidian\Obsidian.exe",
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 运行 obsidian CLI 命令并捕获输出。带 10 秒超时：Obsidian 未运行时命令可能挂起。
    /// </summary>
    public static async Task<CliResult> RunAsync(
        IEnumerable<string> args,
        CancellationToken cancellationToken = default)
    {
        string? exe = FindExecutable();
        if (exe is null)
        {
            return new CliResult(-1, "", "未找到 obsidian 命令（可安装 Obsidian 后重试）。");
        }

        ProcessStartInfo psi = new()
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = new() { StartInfo = psi };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 清理失败忽略。
            }

            return new CliResult(-2, "", "obsidian CLI 响应超时（Obsidian 可能未运行）。");
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>CLI 是否可用（可执行文件存在 + Obsidian 运行中且开启命令行界面）。</summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        CliResult result = await RunAsync(new[] { "vaults" }, cancellationToken);
        return result.Ok;
    }

    /// <summary>枚举已知 vault；输出形如 "名称\t路径"。CLI 不可用时返回空列表。</summary>
    public static async Task<IReadOnlyList<ObsidianVaultInfo>> ListVaultsAsync(
        CancellationToken cancellationToken = default)
    {
        CliResult result = await RunAsync(new[] { "vaults", "verbose" }, cancellationToken);
        if (!result.Ok || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        List<ObsidianVaultInfo> vaults = [];
        foreach (string rawLine in result.Output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int tab = line.IndexOf('\t');
            if (tab <= 0)
            {
                continue;
            }

            string name = line[..tab].Trim();
            string path = line[(tab + 1)..].Trim();
            if (name.Length > 0 && path.Length > 0)
            {
                vaults.Add(new ObsidianVaultInfo(name, path));
            }
        }

        return vaults;
    }

    /// <summary>
    /// 在 Obsidian 中打开笔记。path 为 vault 内相对路径（如 "抖音知识库/xxx.md"）。
    /// vaultName 为空时作用于当前活跃 vault。
    /// </summary>
    public static async Task<bool> OpenNoteAsync(
        string vaultName,
        string vaultRelativePath,
        CancellationToken cancellationToken = default)
    {
        List<string> args = [];
        if (!string.IsNullOrWhiteSpace(vaultName))
        {
            args.Add("vault=" + vaultName);
        }

        args.Add("open");
        args.Add("path=" + vaultRelativePath.Replace('\\', '/'));

        CliResult result = await RunAsync(args, cancellationToken);
        return result.Ok;
    }
}
