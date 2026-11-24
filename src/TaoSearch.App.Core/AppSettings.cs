using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaoSearch.App.Core;

/// <summary>壳层本地设置：JSON 持久化于 %LOCALAPPDATA%\TaoSearch\settings.json。</summary>
public sealed class AppSettings
{
    public static AppSettings Instance { get; private set; } = Load();

    public bool OnboardingCompleted { get; set; }
    public bool FirstIndexingStarted { get; set; }   // 0.1.0.18：首建已开始但未完成，重开回进度页
    public string Theme { get; set; } = "shuimo";                 // shuimo=浅色 / mobai=深色
    public bool ShowQuickOnStartup { get; set; }
    public bool QuickHotkeyEnabled { get; set; } = true;          // Alt+Q
    public string IndexLocation { get; set; } = @"D:\TaoSearch\Index";
    public string IndexScope { get; set; } = "drive-d";           // drive-d / user-folders / custom
    public string ContentPriority { get; set; } = "idle";         // idle / low / normal
    public List<string> ExcludedDirectories { get; set; } = ["System Volume Information", "$Recycle.Bin"];
    public List<string> RecentSearches { get; set; } = [];

    [JsonIgnore]
    public bool IsDarkTheme => string.Equals(Theme, "mobai", StringComparison.Ordinal);

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoSearch", "settings.json");

    public static AppSettings Load() => Load(FilePath);

    /// <summary>路径可注入的加载（测试接缝）：文件缺失/损坏/null 载荷一律回退默认值。</summary>
    internal static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { /* 损坏设置回退默认值 */ }
        return new AppSettings();
    }

    public void Save() => Save(FilePath);

    /// <summary>路径可注入的保存（测试接缝）：磁盘异常静默不阻断 UI。</summary>
    internal void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 磁盘异常不阻断 UI，真机验收补提示 */ }
    }

    /// <summary>记录最近搜索（去重、最多 3 条）。</summary>
    public void PushRecentSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        RecentSearches.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        RecentSearches.Insert(0, query);
        if (RecentSearches.Count > 3) RecentSearches.RemoveAt(3);
        Save();
    }
}
