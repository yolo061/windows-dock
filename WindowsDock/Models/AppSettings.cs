using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WindowsDock.Models;

public sealed class AppSettings
{
    public int IconSize { get; set; } = 48;
    public double MaxMagnification { get; set; } = 1.75;
    public double MaxWidthPercent { get; set; } = 0.65; // Dock 最大宽度(占工作区宽度比例)
    public bool AutoHide { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public List<string> ExcludedTargets { get; set; } = new();

    /// <summary>内置启动计数器:目标路径 → 启动次数,驱动"常用应用"板块(随 config.json 持久化)</summary>
    public Dictionary<string, int> LaunchCounts { get; set; } = new();
}

public static class ConfigService
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsDock");

    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                    s.LaunchCounts ??= new(); // 旧配置文件没有该字段
                    return s;
                }
                catch (Exception ex)
                {
                    Log.Write("LoadConfig: " + ex.Message);
                    try { File.Move(FilePath, FilePath + ".bad", true); } catch { } // 损坏文件留档,避免每次启动都报错
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("LoadConfig: " + ex.Message);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            // 先写临时文件再替换,避免写到一半被中断留下空文件
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Log.Write("SaveConfig: " + ex.Message);
        }
    }
}

public static class Log
{
    static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(ConfigService.Dir);
                File.AppendAllText(Path.Combine(ConfigService.Dir, "log.txt"),
                    DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            }
        }
        catch { /* ignore */ }
    }
}
