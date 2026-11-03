using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WindowsDock.Models;

public sealed class DockItem : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string LnkPath { get; init; } = "";
    public ImageSource? Icon { get; init; }

    double _iconAreaHeight;
    public double IconAreaHeight
    {
        get => _iconAreaHeight;
        set => Set(ref _iconAreaHeight, value);
    }

    bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    /// <summary>固定功能项(如文件资源管理器),不可移除、不显示运行状态</summary>
    public bool IsSpecial { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
