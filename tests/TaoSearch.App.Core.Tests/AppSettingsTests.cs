using TaoSearch.App.Core;
using Xunit;

namespace TaoSearch.App.Core.Tests;

/// <summary>AppSettings 全分支：加载（缺失/损坏/null/有效）、保存（成功/异常）、最近搜索、主题判定。</summary>
public class AppSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "taosearch-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var s = AppSettings.Load(_path);
        Assert.False(s.OnboardingCompleted);
        Assert.Equal("shuimo", s.Theme);
        Assert.True(s.QuickHotkeyEnabled);
        Assert.Equal(2, s.ExcludedDirectories.Count);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileCorrupt()
    {
        File.WriteAllText(_path, "{not valid json");
        var s = AppSettings.Load(_path);
        Assert.Equal("shuimo", s.Theme);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenJsonNull()
    {
        File.WriteAllText(_path, "null");
        var s = AppSettings.Load(_path);
        Assert.False(s.OnboardingCompleted);
    }

    [Fact]
    public void Load_RoundTrips_SavedValues()
    {
        var s = new AppSettings { OnboardingCompleted = true, Theme = "mobai", IndexLocation = @"E:\Idx" };
        s.RecentSearches.Add("旧词");
        s.Save(_path);

        var loaded = AppSettings.Load(_path);
        Assert.True(loaded.OnboardingCompleted);
        Assert.Equal("mobai", loaded.Theme);
        Assert.Equal(@"E:\Idx", loaded.IndexLocation);
        Assert.Single(loaded.RecentSearches);
    }

    [Fact]
    public void Save_SwallowsException_WhenDirectoryIsFile()
    {
        // 把父目录位置放一个文件，CreateDirectory 必失败 → 静默吞掉
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var badPath = Path.Combine(blocker, "settings.json");
        var s = new AppSettings();
        s.Save(badPath); // 不抛即过
    }

    [Fact]
    public void IsDarkTheme_TrueOnlyForMobai()
    {
        Assert.True(new AppSettings { Theme = "mobai" }.IsDarkTheme);
        Assert.False(new AppSettings { Theme = "shuimo" }.IsDarkTheme);
        Assert.False(new AppSettings { Theme = "" }.IsDarkTheme);
    }

    [Fact]
    public void PushRecentSearch_IgnoresEmpty()
    {
        var s = new AppSettings();
        s.PushRecentSearch("  ");
        s.PushRecentSearch("");
        Assert.Empty(s.RecentSearches);
    }

    [Fact]
    public void PushRecentSearch_DedupesCaseInsensitive_AndMovesToFront()
    {
        var s = new AppSettings();
        s.PushRecentSearch("abc");
        s.PushRecentSearch("ABC");
        s.PushRecentSearch("xyz");
        Assert.Equal(["xyz", "ABC"], s.RecentSearches);
    }

    [Fact]
    public void PushRecentSearch_KeepsAtMostThree()
    {
        var s = new AppSettings();
        s.PushRecentSearch("1");
        s.PushRecentSearch("2");
        s.PushRecentSearch("3");
        s.PushRecentSearch("4");
        Assert.Equal(["4", "3", "2"], s.RecentSearches);
    }
}
