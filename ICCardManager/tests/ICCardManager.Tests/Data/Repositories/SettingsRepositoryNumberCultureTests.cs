using System;
using System.Data.SQLite;
using System.Globalization;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// 設定（<c>settings</c> テーブル）の数値が、OS の地域設定によらず同じ文字列で保存・解釈されること（Issue #2162）
/// </summary>
/// <remarks>
/// <para>
/// .NET アナライザーの CA1305（書式の文化圏を指定する）で検出した。ウィンドウ位置は
/// <c>double.ToString("F0")</c>（現在カルチャ）で保存し、<c>double.TryParse(string, out)</c>（現在カルチャ）で読んでいた。
/// マルチモニターでは左端の座標が負になり、負号は地域設定の <c>NumberFormatInfo.NegativeSign</c> で書かれる。
/// </para>
/// <para>
/// 保存側と読み取り側が同じカルチャで動く限り往復は一致するため、<b>往復だけでは検出できない</b>
/// （#1985 の和暦と同じ）。DB に実際に入ったテキストを生 SQL で読んで表明し、往復は対の表明として置く。
/// さらに「共有モードで地域設定の異なる PC が読む」「旧版が書いた値を読む」形を、
/// 書いたときと読むときでカルチャを変えて表明する。
/// </para>
/// </remarks>
[Collection(CurrentCultureCollection.Name)]
public class SettingsRepositoryNumberCultureTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly SettingsRepository _repository;

    public SettingsRepositoryNumberCultureTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<AppSettings>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<AppSettings>> factory, TimeSpan expiration) => factory());

        _repository = new SettingsRepository(_dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 負号が異なる地域設定でもウィンドウ位置は不変の書式で保存されること()
    {
        using (new NegativeSignCultureScope())
        {
            AssertCultureActive();
            (await _repository.SaveAppSettingsAsync(CreateSettings())).Should().BeTrue();

            (await ReadSettingAsync(SettingsRepository.KeyWindowLeft)).Should().Be("-1280",
                "settings の値は地域設定によらず同じ文字列で保存する。他の PC（共有モード）や地域設定を変えた後に読めなくなる");
            (await ReadSettingAsync(SettingsRepository.KeyWindowTop)).Should().Be("-40");
            (await ReadSettingAsync(SettingsRepository.KeyWarningBalance)).Should().Be("7000");
        }
    }

    [Fact]
    public async Task 負号が異なる地域設定でも保存したウィンドウ位置を読み戻せること()
    {
        using (new NegativeSignCultureScope())
        {
            AssertCultureActive();
            (await _repository.SaveAppSettingsAsync(CreateSettings())).Should().BeTrue();

            AssertCultureActive();
            var loaded = await _repository.GetAppSettingsAsync();

            loaded.MainWindowSettings.Left.Should().Be(-1280);
            loaded.MainWindowSettings.Top.Should().Be(-40);
            loaded.MainWindowSettings.Width.Should().Be(1024);
            loaded.WarningBalance.Should().Be(7000);
        }
    }

    /// <summary>
    /// 読み取り側だけの表明。既定の地域設定（日本語）の PC が書いた値を、負号の異なる地域設定で読む。
    /// 保存側だけを直して読み取り側を取り残すと、ここで位置が失われる（null になり既定位置へ戻る）。
    /// </summary>
    [Fact]
    public async Task 負号が異なる地域設定でも既存の設定値を読めること()
    {
        await WriteSettingAsync(SettingsRepository.KeyWindowLeft, "-1280");
        await WriteSettingAsync(SettingsRepository.KeyWindowTop, "-40");
        await WriteSettingAsync(SettingsRepository.KeyWarningBalance, "7000");

        using (new NegativeSignCultureScope())
        {
            AssertCultureActive();
            var loaded = await _repository.GetAppSettingsAsync();

            loaded.MainWindowSettings.Left.Should().Be(-1280);
            loaded.MainWindowSettings.Top.Should().Be(-40);
            loaded.WarningBalance.Should().Be(7000);
        }
    }

    private static AppSettings CreateSettings() => new()
    {
        // 既定値（10,000 円）と異なる値にする（既定値のままだと読み取りが失敗しても既定で一致する。#2106）
        WarningBalance = 7000,
        BackupPath = @"D:\Backup",
        MainWindowSettings = new WindowSettings
        {
            // 主モニターの左にあるモニターでは座標が負になる
            Left = -1280,
            Top = -40,
            Width = 1024,
            Height = 768,
        },
    };

    /// <summary>
    /// 差し替えたカルチャが実際に効いていることを、SUT を呼ぶ直前に表明する
    /// （await の継続で差し替えが失われると、修正前のコードでも緑になる。#1985）。
    /// </summary>
    private static void AssertCultureActive()
    {
        (-1280.0).ToString("F0").Should().Be("~1280", "負号が異なる地域設定がこのテストの故障の起点");
    }

    private async Task<string> ReadSettingAsync(string key)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = new SQLiteCommand("SELECT value FROM settings WHERE key = @key", lease.Connection);
        command.Parameters.AddWithValue("@key", key);
        return (await command.ExecuteScalarAsync()) as string;
    }

    private async Task WriteSettingAsync(string key, string value)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = new SQLiteCommand(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            lease.Connection);
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 現在スレッドとスレッド既定のカルチャを「負号が <c>~</c> の日本語」にする使い捨てスコープ
    /// </summary>
    /// <remarks>
    /// 実在の地域設定でも負号は一様ではない（U+2212 を使う地域がある）。ここでは記号を確実に区別できるよう
    /// <c>~</c> にした。スレッド既定も差し替えるのは、await の継続で現在カルチャが戻るため（#1985 で実測）。
    /// プロセス全体へ漏れるので、本クラスは <see cref="CurrentCultureCollection"/> で直列化する。
    /// </remarks>
    private sealed class NegativeSignCultureScope : IDisposable
    {
        private readonly CultureInfo _previousCulture;
        private readonly CultureInfo _previousDefaultCulture;

        public NegativeSignCultureScope()
        {
            _previousCulture = CultureInfo.CurrentCulture;
            _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;

            var culture = (CultureInfo)CultureInfo.GetCultureInfo("ja-JP").Clone();
            culture.NumberFormat.NegativeSign = "~";

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
            CultureInfo.CurrentCulture = _previousCulture;
        }
    }
}
