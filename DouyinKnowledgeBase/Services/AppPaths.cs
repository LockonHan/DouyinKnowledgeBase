namespace DouyinKnowledgeBase.Services;

/// <summary>
/// 应用路径约定：打包（MSIX）与非打包模式统一使用
/// %LOCALAPPDATA%\DouyinKnowledgeBase 存放配置与运行时数据。
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "DouyinKnowledgeBase";

    /// <summary>应用本地数据根目录。</summary>
    public static string LocalDataDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>常驻转写服务发现文件（asr-server.json）所在目录。</summary>
    public static string RuntimeDir
    {
        get
        {
            string dir = Path.Combine(LocalDataDir, "runtime");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// 把旧版 MSIX 打包目录（Packages\&lt;包标识&gt;\LocalState）里的配置迁移到新目录。
    /// 只在目标文件不存在时复制，失败不影响使用。
    /// </summary>
    public static void MigrateLegacySettings(params string[] fileNames)
    {
        try
        {
            string packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packages))
            {
                return;
            }

            foreach (string packageDir in Directory.GetDirectories(packages, "E3D3CF54*"))
            {
                // 旧版打包应用：配置可能位于 LocalState，也可能因文件系统虚拟化落在 LocalCache。
                string[] legacyDirs =
                {
                    Path.Combine(packageDir, "LocalState"),
                    Path.Combine(packageDir, "LocalCache", "Local", AppFolderName),
                    Path.Combine(packageDir, "LocalCache", "Local", "Packages"),
                };

                foreach (string legacyDir in legacyDirs)
                {
                    if (!Directory.Exists(legacyDir))
                    {
                        continue;
                    }

                    foreach (string name in fileNames)
                    {
                        string source = Path.Combine(legacyDir, name);
                        string target = Path.Combine(LocalDataDir, name);
                        if (File.Exists(source) && !File.Exists(target))
                        {
                            File.Copy(source, target, overwrite: false);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 迁移失败不影响正常使用。
        }
    }
}