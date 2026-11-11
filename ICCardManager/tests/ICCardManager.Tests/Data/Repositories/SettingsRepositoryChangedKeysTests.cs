using System;
using System.Collections.Generic;
using System.Linq;
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
/// Issue #2388: 設定画面の保存が、変えた項目のキーだけを書くための API
/// （<see cref="SettingsRepository.SaveAppSettingsAsync(AppSettings, IReadOnlyCollection{string})"/> と
/// <see cref="SettingsRepository.GetChangedAppSettingKeys"/>）を固定する。
/// </summary>
/// <remarks>
/// 共有モードの 2 台の PC は、同じ DB を指す 2 つのリポジトリ（キャッシュは別々）で表す。
/// キーの期待値は本番の定数から導出せずリテラルで書く（#1884 / #1940）。
/// </remarks>
public sealed class SettingsRepositoryChangedKeysTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheOfThisPc;
    private readonly CacheService _cacheOfOtherPc;
    private readonly SettingsRepository _thisPc;
    private readonly SettingsRepository _otherPc;

    public SettingsRepositoryChangedKeysTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheOfThisPc = new CacheService(NullLogger<CacheService>.Instance);
        _cacheOfOtherPc = new CacheService(NullLogger<CacheService>.Instance);
        _thisPc = new SettingsRepository(_dbContext, _cacheOfThisPc, Options.Create(new CacheOptions()));
        _otherPc = new SettingsRepository(_dbContext, _cacheOfOtherPc, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _cacheOfThisPc.Dispose();
        _cacheOfOtherPc.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 指定したキーだけを書き_他のPCが変えた項目を書き戻さないこと()
    {
        // Arrange: この PC が読んだ後で、他の PC が部署種別を変える
        var stale = await _thisPc.GetAppSettingsAsync();
        stale.DepartmentType.Should().Be(DepartmentType.MayorOffice, "前提: 変更前は既定の部署種別");
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();

        // Act: この PC は文字サイズだけを変えて、そのキーだけを保存する（部署種別は古い値のまま渡す）
        stale.FontSize = FontSizeOption.Large;
        (await _thisPc.SaveAppSettingsAsync(stale, new[] { "font_size" })).Should().BeTrue();

        // Assert
        (await _thisPc.GetAsync("font_size")).Should().Be("large");
        (await _thisPc.GetAsync("department_type")).Should().Be(
            "enterprise_account", "渡さなかったキーは、渡した設定の値が古くても書かない（Issue #2388）");
    }

    /// <summary>
    /// 対の表明: 全項目を書く <see cref="SettingsRepository.SaveAppSettingsAsync(AppSettings)"/> は、同じ形で書き戻すこと。
    /// これが成り立たないと、上のテストは部分保存の効果を表明していない（空振り）。
    /// </summary>
    [Fact]
    public async Task 前提_全項目を書く保存は他のPCが変えた項目を書き戻すこと()
    {
        var stale = await _thisPc.GetAppSettingsAsync();
        (await _otherPc.SetAsync("department_type", "enterprise_account")).Should().BeTrue();

        stale.FontSize = FontSizeOption.Large;
        (await _thisPc.SaveAppSettingsAsync(stale)).Should().BeTrue();

        (await _thisPc.GetAsync("department_type")).Should().Be("mayor_office");
    }

    [Fact]
    public async Task 複数のキーを指定したら指定したキーをすべて書くこと()
    {
        var settings = new AppSettings
        {
            WarningBalance = 3000,
            DepartmentType = DepartmentType.EnterpriseAccount,
            ShowHistoryOnReturn = false,
        };

        (await _thisPc.SaveAppSettingsAsync(settings, new[] { "warning_balance", "department_type", "show_history_on_return" }))
            .Should().BeTrue();

        (await _thisPc.GetAsync("warning_balance")).Should().Be("3000");
        (await _thisPc.GetAsync("department_type")).Should().Be("enterprise_account");
        (await _thisPc.GetAsync("show_history_on_return")).Should().Be("false");
        (await _thisPc.GetAsync("sound_mode")).Should().BeNull("指定しなかったキーは書かない（前提の DB に無いキー）");
    }

    [Fact]
    public async Task キーが空なら何も書かずに成功を返すこと()
    {
        var before = await ReadAllSettingsAsync();

        (await _thisPc.SaveAppSettingsAsync(new AppSettings { WarningBalance = 3000 }, Array.Empty<string>()))
            .Should().BeTrue("何も変えていない保存は成功として扱う（設定画面は「設定を保存しました」で閉じる）");

        (await ReadAllSettingsAsync()).Should().Equal(before, "何も書かない");
    }

    [Fact]
    public async Task 保存したキーはキャッシュを破棄して次の読み取りに現れること()
    {
        (await _thisPc.GetAppSettingsAsync()).WarningBalance.Should().Be(10000, "前提: 既定値をキャッシュしている");

        (await _thisPc.SaveAppSettingsAsync(new AppSettings { WarningBalance = 3000 }, new[] { "warning_balance" }))
            .Should().BeTrue();

        (await _thisPc.GetAppSettingsAsync()).WarningBalance.Should().Be(3000, "保存後はキャッシュを破棄して DB の値を読む（Issue #2305）");
    }

    /// <summary>
    /// 画面で編集しないキーは、渡されても書かずに拒むこと（黙って無視すると、呼び出し元の誤りが表に出ない）。
    /// </summary>
    [Theory]
    [InlineData("backup_path")]
    [InlineData("backup_path_changed_at")]
    [InlineData("window_left")]
    [InlineData("window_maximized")]
    [InlineData("last_vacuum_date")]
    [InlineData("last_backup_success_at")]
    [InlineData("transfer_station_groups")]
    [InlineData("no_such_key")]
    public async Task 画面で編集しないキーを渡したら何も書かずに例外を投げること(string key)
    {
        var settings = new AppSettings { WarningBalance = 3000, BackupPath = @"C:\Other" };
        var before = await ReadAllSettingsAsync();

        Func<Task> act = () => _thisPc.SaveAppSettingsAsync(settings, new[] { "warning_balance", key });

        await act.Should().ThrowAsync<ArgumentException>();
        (await ReadAllSettingsAsync()).Should().Equal(before, "一緒に渡した正当なキーも書かない");
    }

    [Fact]
    public void 同じ値なら変えた項目は無いこと()
    {
        SettingsRepository.GetChangedAppSettingKeys(new AppSettings(), new AppSettings()).Should().BeEmpty();
    }

    /// <summary>
    /// 画面で編集する項目それぞれについて、変えたらそのキーだけが変えた項目になること。
    /// </summary>
    [Theory]
    [MemberData(nameof(EditedSettings))]
    public void 変えた項目のキーだけを返すこと(string expectedKey, Action<AppSettings> edit)
    {
        var edited = new AppSettings();
        edit(edited);

        SettingsRepository.GetChangedAppSettingKeys(new AppSettings(), edited).Should().Equal(expectedKey);
    }

    public static IEnumerable<object[]> EditedSettings()
    {
        yield return Case("warning_balance", s => s.WarningBalance = 3000);
        yield return Case("font_size", s => s.FontSize = FontSizeOption.ExtraLarge);
        yield return Case("sound_mode", s => s.SoundMode = SoundMode.None);
        yield return Case("toast_position", s => s.ToastPosition = ToastPosition.BottomLeft);
        yield return Case("department_type", s => s.DepartmentType = DepartmentType.EnterpriseAccount);
        yield return Case("skip_bus_stop_input_on_return", s => s.SkipBusStopInputOnReturn = true);
        yield return Case("skip_companion_count_input_on_return", s => s.SkipCompanionCountInputOnReturn = false);
        yield return Case("companion_count_input_timeout_seconds", s => s.CompanionCountInputTimeoutSeconds = 45);
        yield return Case("long_term_unreturned_days", s => s.LongTermUnreturnedDays = 10);
        yield return Case("show_history_on_return", s => s.ShowHistoryOnReturn = false);
        yield return Case("report_output_folder", s => s.ReportOutputFolder = @"C:\Reports");

        static object[] Case(string key, Action<AppSettings> edit) => new object[] { key, edit };
    }

    /// <summary>
    /// 画面で編集しない値（バックアップ先・ウィンドウ位置・最終 VACUUM 日）の違いは、変えた項目に数えないこと。
    /// これらは一括保存に載らない（#2264 / #2305 / #1997）ので、数えると保存で例外になる。
    /// </summary>
    [Fact]
    public void 画面で編集しない値の違いは変えた項目に数えないこと()
    {
        var edited = new AppSettings
        {
            BackupPath = @"C:\Other",
            LastVacuumDate = new DateTime(2026, 11, 1),
            MainWindowSettings = new WindowSettings { Left = 10, IsMaximized = true },
        };

        SettingsRepository.GetChangedAppSettingKeys(new AppSettings(), edited).Should().BeEmpty();
    }

    private async Task<Dictionary<string, string?>> ReadAllSettingsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";
        using var reader = await command.ExecuteReaderAsync();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return result;
    }
}
