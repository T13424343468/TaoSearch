using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace TaoSearch.App.Converters;

/// <summary>Snippet 为 null 时隐藏预览区。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>命中段着色：命中词用强调色，普通文本用正文色。</summary>
public sealed class MatchToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language)
    {
        bool isMatch = value is true;
        var brush = (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[
            isMatch ? "AzureBrush" : "InkBrush"];
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>暂停按钮文案：true=恢复索引，false=暂停索引。</summary>
public sealed class PausedToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is true ? "恢复索引" : "暂停索引";

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        throw new NotSupportedException();
}
