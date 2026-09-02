using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// SettingsRepositoryの単体テスト
/// </summary>
public class SettingsRepositoryTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly SettingsRepository _repository;

    public SettingsRepositoryTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();

        // キャッシュをバイパスしてファクトリ関数を直接実行するよう設定
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<AppSettings>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<AppSettings>> factory, TimeSpan expiration) => factory());

        _repository = new SettingsRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetAsync テスト

    /// <summary>
    /// 存在するキーの値を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAsync_ExistingKey_ReturnsValue()
    {
        // Arrange - スキーマ初期化時にデフォルト値が設定される
        // warning_balance = '10000', font_size = 'medium'

        // Act
        var result = await _repository.GetAsync(SettingsRepository.KeyWarningBalance);

        // Assert
        result.Should().Be("10000");
    }

    /// <summary>
    /// 存在しないキーでnullを返すことを確認
    /// </summary>
    [Fact]
    public async Task GetAsync_NonExistingKey_ReturnsNull()
    {
        // Act
        var result = await _repository.GetAsync("non_existing_key");

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// font_sizeのデフォルト値を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAsync_FontSizeKey_ReturnsDefaultValue()
    {
        // Act
        var result = await _repository.GetAsync(SettingsRepository.KeyFontSize);

        // Assert
        result.Should().Be("medium");
    }

    #endregion

    #region SetAsync テスト

    /// <summary>
    /// 新しい設定を保存できることを確認
    /// </summary>
    [Fact]
    public async Task SetAsync_NewKey_SavesValue()
    {
        // Arrange
        var key = "test_key";
        var value = "test_value";

        // Act
        var result = await _repository.SetAsync(key, value);

        // Assert
        result.Should().BeTrue();

        var saved = await _repository.GetAsync(key);
        saved.Should().Be(value);
    }

    /// <summary>
    /// 既存の設定を更新できることを確認（UPSERT動作）
    /// </summary>
    [Fact]
    public async Task SetAsync_ExistingKey_UpdatesValue()
    {
        // Arrange - デフォルト値は10000
        var newValue = "5000";

        // Act
        var result = await _repository.SetAsync(SettingsRepository.KeyWarningBalance, newValue);

        // Assert
        result.Should().BeTrue();

        var updated = await _repository.GetAsync(SettingsRepository.KeyWarningBalance);
        updated.Should().Be(newValue);
    }

    /// <summary>
    /// nullを保存できることを確認
    /// </summary>
    [Fact]
    public async Task SetAsync_NullValue_SavesNull()
    {
        // Arrange
        var key = "nullable_key";

        // Act
        var result = await _repository.SetAsync(key, null);

        // Assert
        result.Should().BeTrue();

        var saved = await _repository.GetAsync(key);
        saved.Should().BeNull();
    }

    /// <summary>
    /// 空文字を保存できることを確認
    /// </summary>
    [Fact]
    public async Task SetAsync_EmptyString_SavesEmptyString()
    {
        // Arrange
        var key = "empty_key";

        // Act
        var result = await _repository.SetAsync(key, string.Empty);

        // Assert
        result.Should().BeTrue();

        var saved = await _repository.GetAsync(key);
        saved.Should().Be(string.Empty);
    }

    #endregion

    #region GetAppSettingsAsync テスト

    /// <summary>
    /// デフォルトのアプリ設定を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_WithDefaults_ReturnsCorrectSettings()
    {
        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.Should().NotBeNull();
        result.WarningBalance.Should().Be(10000); // デフォルト値
        result.FontSize.Should().Be(FontSizeOption.Medium); // デフォルト値
        result.BackupPath.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// カスタム設定を取得できることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_WithCustomValues_ReturnsCustomSettings()
    {
        // Arrange
        await _repository.SetAsync(SettingsRepository.KeyWarningBalance, "5000");
        await _repository.SetAsync(SettingsRepository.KeyFontSize, "large");
        await _repository.SetAsync(SettingsRepository.KeyBackupPath, @"C:\Backup");
        await _repository.SetAsync(SettingsRepository.KeyLastVacuumDate, "2024-06-15");

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.WarningBalance.Should().Be(5000);
        result.FontSize.Should().Be(FontSizeOption.Large);
        result.BackupPath.Should().Be(@"C:\Backup");
        result.LastVacuumDate.Should().Be(new DateTime(2024, 6, 15));
    }

    /// <summary>
    /// 不正な残額警告値はAppSettingsのプロパティ初期値（10000）になることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_InvalidWarningBalance_UsesPropertyDefault()
    {
        // Arrange
        // 不正な値を設定（数値に変換できない文字列）
        await _repository.SetAsync(SettingsRepository.KeyWarningBalance, "invalid");

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        // int.TryParseが失敗した場合、AppSettingsのプロパティ初期値（10000）が保持される
        // AppSettings.WarningBalance { get; set; } = 10000 がデフォルト値
        result.WarningBalance.Should().Be(10000);
    }

    /// <summary>
    /// 各フォントサイズオプションが正しくパースされることを確認
    /// </summary>
    [Theory]
    [InlineData("small", FontSizeOption.Small)]
    [InlineData("medium", FontSizeOption.Medium)]
    [InlineData("large", FontSizeOption.Large)]
    [InlineData("xlarge", FontSizeOption.ExtraLarge)]
    [InlineData("extralarge", FontSizeOption.ExtraLarge)]
    [InlineData("SMALL", FontSizeOption.Small)] // 大文字小文字無視
    [InlineData("invalid", FontSizeOption.Medium)] // 不正値はMedium
    public async Task GetAppSettingsAsync_FontSizeOptions_ParsedCorrectly(string value, FontSizeOption expected)
    {
        // Arrange
        await _repository.SetAsync(SettingsRepository.KeyFontSize, value);

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.FontSize.Should().Be(expected);
    }

    #endregion

    #region SaveAppSettingsAsync テスト

    /// <summary>
    /// アプリ設定を保存できることを確認
    /// </summary>
    [Fact]
    public async Task SaveAppSettingsAsync_ValidSettings_SavesAllValues()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 3000,
            FontSize = FontSizeOption.Large,
            BackupPath = @"D:\MyBackup",
            LastVacuumDate = new DateTime(2024, 7, 1)
        };

        // Act
        var result = await _repository.SaveAppSettingsAsync(settings);

        // Assert
        result.Should().BeTrue();

        var loaded = await _repository.GetAppSettingsAsync();
        loaded.WarningBalance.Should().Be(3000);
        loaded.FontSize.Should().Be(FontSizeOption.Large);
        loaded.BackupPath.Should().Be(@"D:\MyBackup");

        // Issue #1997: LastVacuumDate は一括保存の対象外（CAS 経路だけが書く）
        loaded.LastVacuumDate.Should().BeNull();
    }

    /// <summary>
    /// LastVacuumDate は値の有無にかかわらず一括保存では書き込まれないことを確認（Issue #1997）。
    /// </summary>
    /// <remarks>
    /// 値なし（従来からの表明）と値あり（#1997 で反転した表明）を対で固定する。
    /// 値ありの側が無いと、書き込みを復活させた実装でも緑になる。
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("2024-07-01")]
    public async Task SaveAppSettingsAsync_LastVacuumDate_一括保存では書き込まないこと(string lastVacuumDate)
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 5000,
            FontSize = FontSizeOption.Small,
            BackupPath = @"C:\Backup",
            LastVacuumDate = lastVacuumDate == null
                ? (DateTime?)null
                : DateTime.ParseExact(lastVacuumDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        };

        // Act
        var result = await _repository.SaveAppSettingsAsync(settings);

        // Assert
        result.Should().BeTrue();

        (await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate)).Should().BeNull(
            "一括保存は月ガードを持たないため last_vacuum_date を書いてはならない（Issue #1997）");

        // 対の表明: ほかの設定は従来どおり保存されている（書き込みを丸ごと止めた実装では緑にならない）
        (await _repository.GetAsync(SettingsRepository.KeyWarningBalance)).Should().Be("5000");
        (await _repository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(@"C:\Backup");
    }

    /// <summary>
    /// 各フォントサイズオプションが正しく文字列化されることを確認
    /// </summary>
    [Theory]
    [InlineData(FontSizeOption.Small, "small")]
    [InlineData(FontSizeOption.Medium, "medium")]
    [InlineData(FontSizeOption.Large, "large")]
    [InlineData(FontSizeOption.ExtraLarge, "xlarge")]
    public async Task SaveAppSettingsAsync_FontSizeOptions_SerializedCorrectly(FontSizeOption option, string expected)
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 10000,
            FontSize = option,
            BackupPath = @"C:\Backup"
        };

        // Act
        await _repository.SaveAppSettingsAsync(settings);

        // Assert
        var saved = await _repository.GetAsync(SettingsRepository.KeyFontSize);
        saved.Should().Be(expected);
    }

    /// <summary>
    /// 設定の保存と読み込みのラウンドトリップを確認
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAppSettings_RoundTrip_PreservesAllValues()
    {
        // Arrange
        var original = new AppSettings
        {
            WarningBalance = 7500,
            FontSize = FontSizeOption.ExtraLarge,
            BackupPath = @"E:\CustomBackup\ICCard",
            LastVacuumDate = new DateTime(2024, 12, 25)
        };

        // Act
        await _repository.SaveAppSettingsAsync(original);
        var loaded = await _repository.GetAppSettingsAsync();

        // Assert
        loaded.WarningBalance.Should().Be(original.WarningBalance);
        loaded.FontSize.Should().Be(original.FontSize);
        loaded.BackupPath.Should().Be(original.BackupPath);

        // Issue #1997: LastVacuumDate はラウンドトリップしない（一括保存の対象外）。
        // 読み取り側は CAS が書いた値をそのまま返す（GetAppSettingsAsync_WithCustomValues_… が固定）。
        loaded.LastVacuumDate.Should().BeNull();
    }

    #endregion

    #region SkipCompanionCountInputOnReturn テスト（Issue #1906）

    [Fact]
    public async Task GetAppSettingsAsync_Default_SkipCompanionCountInputOnReturnIsFalse()
    {
        var result = await _repository.GetAppSettingsAsync();
        result.SkipCompanionCountInputOnReturn.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAndLoadAppSettings_SkipCompanionCountInputOnReturn_RoundTrip()
    {
        var settings = new AppSettings { WarningBalance = 10000, BackupPath = @"C:\Backup", SkipCompanionCountInputOnReturn = true };

        await _repository.SaveAppSettingsAsync(settings);
        var loaded = await _repository.GetAppSettingsAsync();

        loaded.SkipCompanionCountInputOnReturn.Should().BeTrue();
        _repository.GetAppSettings().SkipCompanionCountInputOnReturn.Should().BeTrue("同期版の読み込みも同じキーを見る");
    }

    #endregion

    #region SkipBusStopInputOnReturn テスト

    /// <summary>
    /// SkipBusStopInputOnReturnのデフォルト値がfalseであることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_Default_SkipBusStopInputOnReturnIsFalse()
    {
        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.SkipBusStopInputOnReturn.Should().BeFalse();
    }

    /// <summary>
    /// SkipBusStopInputOnReturnをtrueに保存して読み込めることを確認
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAppSettings_SkipBusStopInputOnReturn_RoundTrip()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 10000,
            BackupPath = @"C:\Backup",
            SkipBusStopInputOnReturn = true
        };

        // Act
        await _repository.SaveAppSettingsAsync(settings);
        var loaded = await _repository.GetAppSettingsAsync();

        // Assert
        loaded.SkipBusStopInputOnReturn.Should().BeTrue();
    }

    /// <summary>
    /// SkipBusStopInputOnReturnをfalseに保存して読み込めることを確認
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAppSettings_SkipBusStopInputOnReturnFalse_RoundTrip()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 10000,
            BackupPath = @"C:\Backup",
            SkipBusStopInputOnReturn = false
        };

        // Act
        await _repository.SaveAppSettingsAsync(settings);
        var loaded = await _repository.GetAppSettingsAsync();

        // Assert
        loaded.SkipBusStopInputOnReturn.Should().BeFalse();
    }

    /// <summary>
    /// 不正な値の場合はfalse（デフォルト）になることを確認
    /// </summary>
    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("TRUE")] // 大文字→ToLowerInvariantで"true"になるのでtrueを期待
    public async Task GetAppSettingsAsync_SkipBusStopInputOnReturn_ParsesCorrectly(string value)
    {
        // Arrange
        await _repository.SetAsync(SettingsRepository.KeySkipBusStopInputOnReturn, value);

        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        if (value.ToLowerInvariant() == "true")
        {
            result.SkipBusStopInputOnReturn.Should().BeTrue();
        }
        else
        {
            result.SkipBusStopInputOnReturn.Should().BeFalse();
        }
    }

    #endregion

    #region ReportOutputFolder テスト

    /// <summary>
    /// ReportOutputFolderのデフォルト値が空文字であることを確認
    /// </summary>
    [Fact]
    public async Task GetAppSettingsAsync_Default_ReportOutputFolderIsEmpty()
    {
        // Act
        var result = await _repository.GetAppSettingsAsync();

        // Assert
        result.ReportOutputFolder.Should().Be(string.Empty);
    }

    /// <summary>
    /// ReportOutputFolderを保存して読み込めることを確認
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAppSettings_ReportOutputFolder_RoundTrip()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 10000,
            BackupPath = @"C:\Backup",
            ReportOutputFolder = @"D:\Reports\Monthly"
        };

        // Act
        await _repository.SaveAppSettingsAsync(settings);
        var loaded = await _repository.GetAppSettingsAsync();

        // Assert
        loaded.ReportOutputFolder.Should().Be(@"D:\Reports\Monthly");
    }

    /// <summary>
    /// ReportOutputFolderを空文字で保存して読み込めることを確認
    /// </summary>
    [Fact]
    public async Task SaveAndLoadAppSettings_EmptyReportOutputFolder_RoundTrip()
    {
        // Arrange
        var settings = new AppSettings
        {
            WarningBalance = 10000,
            BackupPath = @"C:\Backup",
            ReportOutputFolder = string.Empty
        };

        // Act
        await _repository.SaveAppSettingsAsync(settings);
        var loaded = await _repository.GetAppSettingsAsync();

        // Assert
        loaded.ReportOutputFolder.Should().Be(string.Empty);
    }

    #endregion

    #region 設定キー定数テスト

    /// <summary>
    /// 設定キー定数が正しく定義されていることを確認
    /// </summary>
    [Fact]
    public void SettingsKeys_AreDefinedCorrectly()
    {
        // Assert
        SettingsRepository.KeyWarningBalance.Should().Be("warning_balance");
        SettingsRepository.KeyBackupPath.Should().Be("backup_path");
        SettingsRepository.KeyFontSize.Should().Be("font_size");
        SettingsRepository.KeyLastVacuumDate.Should().Be("last_vacuum_date");
    }

    #endregion

    #region TryAcquireMonthlyVacuumLockAsync テスト (Issue #1482)

    /// <summary>
    /// 前回実行履歴が無い場合は true を返し、当日日付が保存される
    /// </summary>
    [Fact]
    public async Task TryAcquireMonthlyVacuumLockAsync_前回実行なし_trueを返し当日日付を保存する()
    {
        // Arrange: last_vacuum_date キーがまだ無い状態（テスト DB の初期スキーマ）
        var today = new DateTime(2026, 5, 14);

        // Act
        var acquired = await _repository.TryAcquireMonthlyVacuumLockAsync(today);

        // Assert
        acquired.Should().BeTrue("行が存在しないので INSERT が成立し先勝ちになる");
        var stored = await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate);
        stored.Should().Be("2026-05-14");
    }

    /// <summary>
    /// 前月の日付が入っている場合は true を返し、当日日付に更新される
    /// </summary>
    [Fact]
    public async Task TryAcquireMonthlyVacuumLockAsync_前月実行済み_trueを返し当日日付に更新する()
    {
        // Arrange
        var today = new DateTime(2026, 5, 14);
        await _repository.SetAsync(SettingsRepository.KeyLastVacuumDate, "2026-04-15");

        // Act
        var acquired = await _repository.TryAcquireMonthlyVacuumLockAsync(today);

        // Assert
        acquired.Should().BeTrue("既存値が当月外なので CAS の WHERE が真となり UPDATE が成立する");
        var stored = await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate);
        stored.Should().Be("2026-05-14");
    }

    /// <summary>
    /// 当月の日付が入っている場合は false を返し、既存値を変更しない
    /// </summary>
    [Fact]
    public async Task TryAcquireMonthlyVacuumLockAsync_当月実行済み_falseを返し値を変更しない()
    {
        // Arrange
        var today = new DateTime(2026, 5, 14);
        await _repository.SetAsync(SettingsRepository.KeyLastVacuumDate, "2026-05-10");

        // Act
        var acquired = await _repository.TryAcquireMonthlyVacuumLockAsync(today);

        // Assert
        acquired.Should().BeFalse("既存値が当月なので CAS の WHERE が偽となり UPDATE は走らない");
        var stored = await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate);
        stored.Should().Be("2026-05-10", "既存の当月日付を上書きしてはいけない");
    }

    /// <summary>
    /// 同一 DbContext から複数 Repository が同時に呼び出しても、true を返すのは厳密に 1 つだけ
    /// </summary>
    /// <remarks>
    /// SQLite の接続レベルロックで実質シリアル化されるが、その上でアプリ層の CAS（WHERE 句）が
    /// 正しく機能して「先勝ち 1 つ、残りは false」となることを保証する。
    /// </remarks>
    [Fact]
    public async Task TryAcquireMonthlyVacuumLockAsync_並列実行時_trueを返すのは1つだけ()
    {
        // Arrange
        var today = new DateTime(2026, 5, 14);
        const int parallelCount = 10;
        var repos = Enumerable.Range(0, parallelCount)
            .Select(_ => new SettingsRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions())))
            .ToList();

        // Act: 10 並列で同時呼出
        var tasks = repos.Select(r => r.TryAcquireMonthlyVacuumLockAsync(today)).ToList();
        var results = await Task.WhenAll(tasks);

        // Assert
        results.Count(r => r).Should().Be(1, "先勝ちで正確に 1 つだけが true を返すべき");
        var stored = await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate);
        stored.Should().Be("2026-05-14");
    }

    /// <summary>
    /// CAS で獲得した当月の値が、その後の一括保存で巻き戻らないことを確認（Issue #1997）。
    /// </summary>
    /// <remarks>
    /// 共有モードでは、CAS に負けた PC が TTL キャッシュに古い <c>LastVacuumDate</c> を保持したまま
    /// 動き続ける（<c>TryAcquireMonthlyVacuumLockAsync</c> が無効化するのは自プロセスのキャッシュだけ）。
    /// その PC がウィンドウ位置や帳票の出力先を保存すると、一括保存が先月の日付を書き戻し、
    /// 次に起動した PC が CAS を再獲得して同じ月に 2 回目の VACUUM が走っていた。
    /// </remarks>
    [Fact]
    public async Task TryAcquireMonthlyVacuumLockAsync_獲得後の一括保存_当月の値を巻き戻さないこと()
    {
        // Arrange: PC-A が当月分のロックを獲得済み
        var today = new DateTime(2026, 5, 14);
        (await _repository.TryAcquireMonthlyVacuumLockAsync(today)).Should().BeTrue();

        // PC-B が持つ「CAS 獲得前」の古いキャッシュ（先月の日付）を模した設定インスタンス
        var staleSettings = new AppSettings
        {
            WarningBalance = 8000,
            FontSize = FontSizeOption.Large,
            BackupPath = @"D:\Backup",
            LastVacuumDate = new DateTime(2026, 4, 10)
        };

        // Act: PC-B がウィンドウ位置の保存などで一括保存を呼ぶ
        (await _repository.SaveAppSettingsAsync(staleSettings)).Should().BeTrue();

        // Assert: 当月のロックは維持され、再獲得できない
        (await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate)).Should().Be(
            "2026-05-14",
            "一括保存が先月の日付を書き戻すと当月のロックが巻き戻る（Issue #1997）");
        (await _repository.TryAcquireMonthlyVacuumLockAsync(today)).Should().BeFalse(
            "同じ月に 2 回目の VACUUM が走ってはいけない（Issue #1482）");
    }

    /// <summary>
    /// 対の表明: 一括保存の後でも翌月のロックは通常どおり獲得できること（Issue #1997）。
    /// </summary>
    /// <remarks>
    /// 上のテストだけだと、<c>last_vacuum_date</c> を一切更新しない実装（CAS ごと壊した実装）でも緑になる。
    /// </remarks>
    [Fact]
    public async Task TryAcquireMonthlyVacuumLockAsync_一括保存の後でも翌月は獲得できること()
    {
        // Arrange
        (await _repository.TryAcquireMonthlyVacuumLockAsync(new DateTime(2026, 5, 14))).Should().BeTrue();
        await _repository.SaveAppSettingsAsync(new AppSettings
        {
            WarningBalance = 8000,
            FontSize = FontSizeOption.Large,
            BackupPath = @"D:\Backup",
            LastVacuumDate = new DateTime(2026, 4, 10)
        });

        // Act
        var acquired = await _repository.TryAcquireMonthlyVacuumLockAsync(new DateTime(2026, 6, 10));

        // Assert
        acquired.Should().BeTrue("月が変われば CAS の WHERE が真になり、次の月次 VACUUM が実行される");
        (await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate)).Should().Be("2026-06-10");
    }

    #endregion
}
