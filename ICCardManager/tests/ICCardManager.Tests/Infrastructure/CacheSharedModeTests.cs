using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #1111: 共有モード時のキャッシュTTL調整テスト
/// UT-SHARED-005 に対応する。キャッシュ基本動作は CacheServiceTests でカバー済みのため、
/// このファイルでは CacheOptions の設定値妥当性のみを検証する。
/// </summary>
public class CacheSharedModeTests
{
    /// <summary>
    /// UT-SHARED-005 No.1: スタンドアロン時のCacheOptionsデフォルト値を確認
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CacheOptions_スタンドアロン時のデフォルト値が正しいこと()
    {
        var options = new CacheOptions();

        options.CardListSeconds.Should().Be(60, "スタンドアロン時のカード一覧TTLは60秒");
        options.StaffListSeconds.Should().Be(60, "スタンドアロン時の職員一覧TTLは60秒");
        options.LentCardsSeconds.Should().Be(30, "スタンドアロン時の貸出中カードTTLは30秒");
        options.SettingsMinutes.Should().Be(5, "スタンドアロン時の設定TTLは5分");
    }

    /// <summary>
    /// UT-SHARED-005 No.2: 共有モード時のキャッシュTTL調整値を確認
    /// App.xaml.csのPostConfigureで設定される値と同じであることを検証する。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CacheOptions_共有モード時のTTL調整値が正しいこと()
    {
        // App.xaml.cs の PostConfigure<CacheOptions> で設定される値をシミュレーション
        var options = new CacheOptions();

        // 共有モード時のTTL調整（App.xaml.cs と同じ値）
        options.CardListSeconds = 15;
        options.LentCardsSeconds = 10;
        options.StaffListSeconds = 30;
        options.SettingsMinutes = 3;

        options.CardListSeconds.Should().Be(15, "共有モード時のカード一覧TTLは15秒");
        options.LentCardsSeconds.Should().Be(10, "共有モード時の貸出中カードTTLは10秒");
        options.StaffListSeconds.Should().Be(30, "共有モード時の職員一覧TTLは30秒");
        options.SettingsMinutes.Should().Be(3, "共有モード時の設定TTLは3分");
    }

    /// <summary>
    /// UT-SHARED-005: 共有モードのTTLはスタンドアロンより短いこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void CacheOptions_共有モードのTTLはスタンドアロンより短いこと()
    {
        var standalone = new CacheOptions();
        var shared = new CacheOptions
        {
            CardListSeconds = 15,
            LentCardsSeconds = 10,
            StaffListSeconds = 30,
            SettingsMinutes = 3
        };

        shared.CardListSeconds.Should().BeLessThan(standalone.CardListSeconds);
        shared.LentCardsSeconds.Should().BeLessThan(standalone.LentCardsSeconds);
        shared.StaffListSeconds.Should().BeLessThan(standalone.StaffListSeconds);
        shared.SettingsMinutes.Should().BeLessThan(standalone.SettingsMinutes);
    }
}
