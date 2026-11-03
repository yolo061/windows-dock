using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindowsDock.Services;

public sealed record DiscoveredApp(string Name, string TargetPath, string LnkPath);

/// <summary>
/// 扫描"用户桌面 + 公共桌面",只找出应用:
///   1. .lnk 快捷方式,且解析出的目标为 .exe
///   2. 直接放在桌面上的 .exe
/// 普通文件(文档、图片、压缩包等)一律不添加;文件夹快捷方式也不添加。
/// </summary>
public static class DesktopScanner
{
    static readonly string[] JunkHints = { "uninstall", "unins", "卸载" };

    public static string[] DesktopDirectories => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    };

    public static List<DiscoveredApp> Scan(IEnumerable<string> excludedTargets)
    {
        var excluded = new HashSet<string>(excludedTargets ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredApp>();

        foreach (var dir in DesktopDirectories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    string? target;
                    string name;
                    string lnkPath = "";

                    if (ext == ".lnk")
                    {
                        target = LnkResolver.ResolveTarget(file);
                        if (target == null) continue;
                        name = Path.GetFileNameWithoutExtension(file);
                        lnkPath = file;
                    }
                    else if (ext == ".exe")
                    {
                        target = Path.GetFullPath(file);
                        name = Path.GetFileNameWithoutExtension(file);
                    }
                    else
                    {
                        continue; // 其它一切文件类型不添加
                    }

                    if (!IsExecutable(target)) continue;   // 指向文件夹/文档的快捷方式不添加
                    if (IsJunk(name, target)) continue;    // 卸载器等垃圾条目不添加
                    if (excluded.Contains(target)) continue;
                    if (!seen.Add(target)) continue;       // 用户/公共桌面去重

                    result.Add(new DiscoveredApp(name, target, lnkPath));
                }
                catch { /* 单个文件失败不影响整体 */ }
            }
        }

        return result
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    static bool IsExecutable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    static bool IsJunk(string name, string target)
    {
        var n = name.ToLowerInvariant();
        var f = Path.GetFileName(target).ToLowerInvariant();
        foreach (var hint in JunkHints)
            if (n.Contains(hint) || f.Contains(hint))
                return true;
        return false;
    }
}
