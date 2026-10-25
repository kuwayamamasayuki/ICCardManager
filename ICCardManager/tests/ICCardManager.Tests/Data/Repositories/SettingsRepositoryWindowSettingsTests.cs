using System;
using System.Data.SQLite;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2305: 終了時のウィンドウ位置の保存（<see cref="SettingsRepository.SaveMainWindowSettingsAsync"/>）が
/// ウィンドウの位置・サイズ以外の設定を書かないこと。
/// </summary>
/// <remarks>
/// <para>
/// 以前の終了時の保存は、<see cref="SettingsRepository.GetAppSettingsAsync"/>（TTL キャッシュ）で読んだ設定の
/// ウィンドウ値だけを差し替えて一括保存（<see cref="SettingsRepository.SaveAppSettingsAsync"/>）へ丸ごと渡していた。
/// 共有モードでは、他の PC が設定画面で部署種別・残額警告を変えた後にキャッシュを持ったままの PC を終了すると、
/// その PC が読んだ時点の古い値が共有 DB へ書き戻され、変更が黙って巻き戻っていた。
/// </para>
/// <para>
/// 2 台の PC は、同じ DB を指す 2 つのリポジトリ（キャッシュはそれぞれ別の実物の <see cref="CacheService"/>）で表す。
/// 故障の起点は「終了する PC のキャッシュが古いこと」なので、それが成立していることをテストの中で表明してから終了時の保存を呼ぶ。
/// </para>
/// </remarks>
public class SettingsRepositoryWindowSettingsTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheOfClosingPc;
    private readonly CacheService _cacheOfOtherPc;
    private readonly SettingsRepository _closingPc;
    private readonly SettingsRepository _otherPc;

    public SettingsRepositoryWindowSettingsTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheOfClosingPc = new CacheService(NullLogger<CacheService>.Instance);
        _cacheOfOtherPc = new CacheService(NullLogger<CacheService>.Instance);
        _closingPc = new SettingsRepository(_dbContext, _cacheOfClosingPc, Options.Create(new CacheOptions()));
        _otherPc = new SettingsRepository(_dbContext, _cacheOfOtherPc, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _cacheOfClosingPc.Dispose();
        _cacheOfOtherPc.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 終了時の保存は他のPCが変えた設定を巻き戻さないこと()
    {
        // Arrange: 終了する PC が起動時に設定を読んでキャッシュしている（既定値: 市長事務部局・10,000 円）
        var cached = await _closingPc.GetAppSettingsAsync();
        cached.DepartmentType.Should().Be(DepartmentType.MayorOffice, "前提: 変更前は既定の部署種別");
        cached.WarningBalance.Should().Be(10000, "前提: 変更前は既定の残額警告");

        // 他の PC が設定画面で部署種別と残額警告を変える
        (await _otherPc.SaveAppSettingsAsync(new AppSettings
        {
            DepartmentType = DepartmentType.EnterpriseAccount,
            WarningBalance = 3000,
            LongTermUnreturnedDays = 30,
        })).Should().BeTrue();

        var stale = await _closingPc.GetAppSettingsAsync();
        stale.DepartmentType.Should().Be(
            DepartmentType.MayorOffice,
            "前提: 終了する PC のキャッシュは他の PC の変更より古い（これが巻き戻りの起点。成立しないとテストは空振りする）");

        // Act: 終了時のウィンドウ位置の保存
        (await _closingPc.SaveMainWindowSettingsAsync(CreateWindowSettings())).Should().BeTrue();

        // Assert: DB から読み直すと、他の PC の変更が残っている
        var reloaded = await ReadFromDbAsync();
        reloaded.DepartmentType.Should().Be(
            DepartmentType.EnterpriseAccount,
            "終了時の保存が部署種別を古い値へ戻すと、以後のチャージの摘要が誤った方で 6 年保存の台帳へ入る（Issue #2305）");
        reloaded.WarningBalance.Should().Be(3000);
        reloaded.LongTermUnreturnedDays.Should().Be(30);
    }

    /// <summary>
    /// 対の表明: 終了時の保存はウィンドウの位置・サイズを実際に保存し、次回起動時に読み戻せること。
    /// これが無いと、終了時の保存から書き込みを丸ごと消した実装でも上のテストは緑になる。
    /// </summary>
    [Fact]
    public async Task 終了時に保存したウィンドウ位置を次回起動時に読み戻せること()
    {
        (await _closingPc.SaveMainWindowSettingsAsync(CreateWindowSettings())).Should().BeTrue();

        var window = (await ReadFromDbAsync()).MainWindowSettings;
        window.Left.Should().Be(-1280);
        window.Top.Should().Be(40);
        window.Width.Should().Be(1024);
        window.Height.Should().Be(768);
        window.IsMaximized.Should().BeTrue("既定値（false）と異なる値で、保存の欠落を既定値との一致で見逃さない");
    }

    /// <summary>
    /// 終了時の保存は自分のキャッシュを破棄する（保存した位置を同じプロセスで読み直したときに古い位置を返さない）。
    /// </summary>
    [Fact]
    public async Task 終了時の保存の後は自分のキャッシュも新しい位置を返すこと()
    {
        (await _closingPc.GetAppSettingsAsync()).MainWindowSettings.HasValidSettings
            .Should().BeFalse("前提: 位置を保存する前の値がキャッシュされている");

        (await _closingPc.SaveMainWindowSettingsAsync(CreateWindowSettings())).Should().BeTrue();

        (await _closingPc.GetAppSettingsAsync()).MainWindowSettings.Left.Should().Be(-1280);
    }

    /// <summary>
    /// 設定画面（F5）の一括保存は、編集しないウィンドウの位置・サイズを書き換えないこと（#1726「SET 句は編集する列に限る」）。
    /// </summary>
    /// <remarks>
    /// 設定画面は画面の値から <c>new AppSettings { … }</c> を組み立てるため、<see cref="AppSettings.MainWindowSettings"/> は既定値
    /// （位置なし・最大化なし）のまま渡される。一括保存が window_* を書いていた間は、保存のたびに window_maximized が false になっていた。
    /// </remarks>
    [Fact]
    public async Task 設定画面の保存はウィンドウ位置を書き換えないこと()
    {
        (await _closingPc.SaveMainWindowSettingsAsync(CreateWindowSettings())).Should().BeTrue();

        (await _otherPc.SaveAppSettingsAsync(new AppSettings { WarningBalance = 5000 })).Should().BeTrue();

        (await ReadRawAsync(SettingsRepository.KeyWindowMaximized)).Should().Be(
            "true", "設定画面で編集しない最大化状態を、画面の既定値（false）で上書きしない");
        (await ReadRawAsync(SettingsRepository.KeyWindowLeft)).Should().Be("-1280");
        (await ReadFromDbAsync()).WarningBalance.Should().Be(5000, "前提: 一括保存そのものは書き込めている");
    }

    private static WindowSettings CreateWindowSettings() => new()
    {
        // 主モニターの左にあるモニターでは座標が負になる。最大化は既定値（false）と異なる値にする（#2106）
        Left = -1280,
        Top = 40,
        Width = 1024,
        Height = 768,
        IsMaximized = true,
    };

    /// <summary>
    /// どちらの PC のキャッシュも経由せずに DB から読む（新しく起動した PC に相当）。
    /// </summary>
    private async Task<AppSettings> ReadFromDbAsync()
    {
        using var cache = new CacheService(NullLogger<CacheService>.Instance);
        var fresh = new SettingsRepository(_dbContext, cache, Options.Create(new CacheOptions()));
        return await fresh.GetAppSettingsAsync();
    }

    private async Task<string?> ReadRawAsync(string key)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = new SQLiteCommand("SELECT value FROM settings WHERE key = @key", lease.Connection);
        command.Parameters.AddWithValue("@key", key);
        return (await command.ExecuteScalarAsync()) as string;
    }
}
