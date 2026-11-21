using System.ComponentModel;

namespace TaoSearch.App.ViewModels;

/// <summary>结果行 ViewModel：列表绑定单元，预览高亮区间转高亮段。</summary>
public sealed class HitViewModel : INotifyPropertyChanged
{
    public required long FileReference { get; init; }
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required string SizeText { get; init; }
    public required string ModifiedText { get; init; }
    public double Score { get; init; }

    /// <summary>全文命中预览（文件名命中为 null）。</summary>
    public string? Snippet { get; init; }

    /// <summary>预览高亮段（Snippet 切片，IsMatch 标记命中区间）。</summary>
    public IReadOnlyList<PreviewSegment> Segments { get; init; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>预览高亮段。</summary>
public sealed record PreviewSegment(string Text, bool IsMatch);
