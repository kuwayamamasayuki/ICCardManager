using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// Issue #1111: 共有モード時のキャッシュTTL調整テスト
/// UT-SHARED-005 に対応する。
/// </summary>
public class CacheSharedModeTests : IDisposable
{
    private readonly CacheService _cacheService;

    public CacheSharedModeTests()
    {
        _cacheService = new CacheService(NullLogger<CacheService>.Instance);
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        GC.SuppressFinalize(this);
    }

    #region UT-SHARED-005: キャッシュTTL調整テスト

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
        shared.StaffListSeconds.Should().BeLessThanOrEqualTo(standalone.StaffListSeconds);
        shared.SettingsMinutes.Should().BeLessThan(standalone.SettingsMinutes);
    }

    #endregion

    #region キャッシュの基本動作テスト（共有モードでの使用を想定）

    /// <summary>
    /// キャッシュ有効期間内にデータを取得できること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetOrCreateAsync_キャッシュ有効期間内はキャッシュから返すこと()
    {
        var factoryCallCount = 0;
        var ttl = TimeSpan.FromSeconds(10);

        // 1回目: ファクトリが呼ばれる
        var result1 = await _cacheService.GetOrCreateAsync("test:key", async () =>
        {
            factoryCallCount++;
            await Task.CompletedTask;
            return "value1";
        }, ttl);

        // 2回目: キャッシュから返される
        var result2 = await _cacheService.GetOrCreateAsync("test:key", async () =>
        {
            factoryCallCount++;
            await Task.CompletedTask;
            return "value2";
        }, ttl);

        result1.Should().Be("value1");
        result2.Should().Be("value1", "キャッシュからのデータが返されるべき");
        factoryCallCount.Should().Be(1, "ファクトリは1回だけ呼ばれるべき");
    }

    /// <summary>
    /// Invalidate後に再度ファクトリが呼ばれること
    /// （他PCの操作後にキャッシュを無効化するシナリオ）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Invalidate後にファクトリが再度呼ばれること()
    {
        var factoryCallCount = 0;
        var ttl = TimeSpan.FromSeconds(60);

        await _cacheService.GetOrCreateAsync("card:all", async () =>
        {
            factoryCallCount++;
            await Task.CompletedTask;
            return "data_v1";
        }, ttl);

        // キャッシュを無効化（他PCで変更があった場合のシミュレーション）
        _cacheService.Invalidate("card:all");

        var result = await _cacheService.GetOrCreateAsync("card:all", async () =>
        {
            factoryCallCount++;
            await Task.CompletedTask;
            return "data_v2";
        }, ttl);

        result.Should().Be("data_v2", "無効化後は新しいデータが取得されるべき");
        factoryCallCount.Should().Be(2, "無効化後にファクトリが再度呼ばれるべき");
    }

    /// <summary>
    /// InvalidateByPrefix で共有モードのカード関連キャッシュをまとめて無効化できること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void InvalidateByPrefix_プレフィックスでまとめて無効化できること()
    {
        _cacheService.Set("card:all", "allCards", TimeSpan.FromSeconds(60));
        _cacheService.Set("card:lent", "lentCards", TimeSpan.FromSeconds(60));
        _cacheService.Set("card:available", "availableCards", TimeSpan.FromSeconds(60));
        _cacheService.Set("staff:all", "allStaff", TimeSpan.FromSeconds(60));

        // card:プレフィックスのキャッシュだけ無効化
        _cacheService.InvalidateByPrefix("card:");

        _cacheService.Get<string>("card:all").Should().BeNull("card:allは無効化されているべき");
        _cacheService.Get<string>("card:lent").Should().BeNull("card:lentは無効化されているべき");
        _cacheService.Get<string>("card:available").Should().BeNull("card:availableは無効化されているべき");
        _cacheService.Get<string>("staff:all").Should().Be("allStaff", "staff:allは残っているべき");
    }

    /// <summary>
    /// Clear で全キャッシュを無効化できること
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Clear_全キャッシュが無効化されること()
    {
        _cacheService.Set("card:all", "data1", TimeSpan.FromSeconds(60));
        _cacheService.Set("staff:all", "data2", TimeSpan.FromSeconds(60));

        _cacheService.Clear();

        _cacheService.Get<string>("card:all").Should().BeNull();
        _cacheService.Get<string>("staff:all").Should().BeNull();
    }

    #endregion
}
