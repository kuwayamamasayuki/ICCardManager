using System.IO;
using System.Text.RegularExpressions;
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
    /// </summary>
    /// <remarks>
    /// Issue #2107: 以前は App.xaml.cs と「同じ値」をテスト内で代入してから読み返しており、
    /// 本番の値を変えても緑だった。本番が呼ぶ <see cref="CacheOptions.ApplySharedModeTtl"/> を通す。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void CacheOptions_共有モード時のTTL調整値が正しいこと()
    {
        var options = new CacheOptions();

        options.ApplySharedModeTtl();

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
        var shared = new CacheOptions();
        shared.ApplySharedModeTtl();

        shared.CardListSeconds.Should().BeLessThan(standalone.CardListSeconds);
        shared.LentCardsSeconds.Should().BeLessThan(standalone.LentCardsSeconds);
        shared.StaffListSeconds.Should().BeLessThan(standalone.StaffListSeconds);
        shared.SettingsMinutes.Should().BeLessThan(standalone.SettingsMinutes);
    }

    /// <summary>
    /// Issue #2107: 起動時の共有モード判定の分岐が、TTL の短縮を <see cref="CacheOptions.ApplySharedModeTtl"/> へ委ねていること
    /// </summary>
    /// <remarks>
    /// 上のテストとヘルスチェック間隔の一致（<c>SharedModeMonitorTests</c>）は <see cref="CacheOptions.ApplySharedModeTtl"/>
    /// の値を見る。App.xaml.cs が再び値を直書きすると、それらは緑のまま本番の値だけが変わる。
    /// 「直書きの不在」と「委譲の存在」を対で表明する（前者だけだと、短縮そのものを消した実装でも緑になる）。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void App_共有モードのTTL短縮はApplySharedModeTtlへ委ねていること()
    {
        var code = TestSourceInspection.ToCodeOnly(
            File.ReadAllText(Path.Combine(TestPaths.GetProductionSourceRoot(), "App.xaml.cs")));

        Regex.IsMatch(code, @"\bIsSharedModePath\s*\([^)]*\)\s*\)\s*\{\s*cacheOptions\s*\.\s*ApplySharedModeTtl\s*\(\s*\)\s*;")
            .Should().BeTrue("共有モード判定の分岐の中で ApplySharedModeTtl を呼ぶべき");
        Regex.IsMatch(code, @"\bcacheOptions\s*\.\s*(CardListSeconds|LentCardsSeconds|StaffListSeconds|SettingsMinutes)\s*=")
            .Should().BeFalse("TTL の値を App.xaml.cs に直書きしない（テストが本番の値を見られなくなる）");
    }
}
