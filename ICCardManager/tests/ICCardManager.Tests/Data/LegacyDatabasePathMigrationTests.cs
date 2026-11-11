#nullable enable
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #2389: 廃止した appsettings.json の DatabaseOptions:Path を、起動時に一度だけ database_config.txt へ移すこと。
/// </summary>
/// <remarks>
/// 欠陥を突く側（appsettings.json に値があり database_config.txt が空なら移す・2 回目以降は移さない・
/// 移した後に「デフォルトに戻す」を押しても移し直さない）と、正当な動作を塞いでいない側
/// （database_config.txt の値・既定のパスは従来どおり）を対で置く。書き込み・読み取りの失敗は実際のファイルで再現する。
/// </remarks>
public class LegacyDatabasePathMigrationTests : IDisposable
{
    private const string UncPath = @"\\server\share\ICCardManager\iccard.db";
    private const string LocalPath = @"C:\data\iccard.db";
    private const string RelativePath = @"data\iccard.db";
    private const string OtherRelativePath = @"other\iccard.db";

    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _markerPath;
    private readonly RecordingLogger<LegacyDatabasePathMigrationTests> _logger = new();

    public LegacyDatabasePathMigrationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ICCardManagerTests_LegacyDbPath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, AppConfigFiles.DatabaseConfigFileName);
        _markerPath = Path.Combine(_directory, AppConfigFiles.LegacyDatabasePathMigratedMarkerFileName);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 一時フォルダーの後始末に失敗してもテストの結果は変わらない
        }
    }

    private LegacyDatabasePathMigrationResult Run(string? appSettingsValue) =>
        LegacyDatabasePathMigration.Run(appSettingsValue, _configPath, _markerPath, _logger);

    [Fact]
    public void appsettingsに値がありdatabase_config_txtが無ければ書き移してその値で開くこと()
    {
        var result = Run(UncPath);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.Migrated);
        result.EffectiveConfigFileValue.Should().Be(UncPath, "旧版と同じ共有フォルダーの DB を開く（既定のローカル DB へ切り替えると台帳が分裂する）");
        AppConfigFiles.Read(_configPath).Should().Be(UncPath, "設定画面の欄が実際に開く DB を示すように、database_config.txt へ移す");
        File.Exists(_markerPath).Should().BeTrue("移行の判断を済ませた印を残す");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains(UncPath),
            _logger.FormatEntries());
    }

    [Fact]
    public void database_config_txtが空のファイルでも書き移すこと()
    {
        AppConfigFiles.Write(_configPath, string.Empty);

        var result = Run(UncPath);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.Migrated);
        AppConfigFiles.Read(_configPath).Should().Be(UncPath);
    }

    [Fact]
    public void 移した次の起動では移さず_database_config_txtの値で開くこと()
    {
        Run(UncPath);
        AppConfigFiles.Write(_configPath, LocalPath); // 移した後に設定画面で別の保存先へ変えた
        _logger.Entries.Count.Should().Be(1, "前提: 1 回目の起動の移行のログ");

        var second = Run(UncPath);

        second.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.AlreadyDecided);
        second.EffectiveConfigFileValue.Should().Be(LocalPath);
        AppConfigFiles.Read(_configPath).Should().Be(LocalPath, "2 回目の起動では書き移さない");
        _logger.Entries.Skip(1).Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains(UncPath),
            "使われない値が残っていることを起動のたびに記録する: " + _logger.FormatEntries());
    }

    [Fact]
    public void 移した後にデフォルトに戻すを押したら_次の起動は既定で開き移し直さないこと()
    {
        Run(UncPath);
        File.Delete(_configPath); // 設定画面の「デフォルトに戻す」は database_config.txt を削除する

        var second = Run(UncPath);

        second.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.AlreadyDecided);
        second.EffectiveConfigFileValue.Should().BeEmpty("既定に戻した後に appsettings.json の値で開くと、既定に戻せない");
        File.Exists(_configPath).Should().BeFalse();
    }

    [Fact]
    public void database_config_txtに値があれば移さずその値で開き_appsettingsの値を使わないことを記録すること()
    {
        AppConfigFiles.Write(_configPath, LocalPath);

        var result = Run(UncPath);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.KeptConfigFile);
        result.EffectiveConfigFileValue.Should().Be(LocalPath, "database_config.txt が優先（旧版と同じ）");
        AppConfigFiles.Read(_configPath).Should().Be(LocalPath);
        File.Exists(_markerPath).Should().BeTrue("後で database_config.txt を消しても appsettings.json の値へ戻らないように");
        _logger.Entries.Should().ContainSingle(e =>
                e.Level == LogLevel.Warning && e.Message.Contains(UncPath) && e.Message.Contains(LocalPath),
            _logger.FormatEntries());

        File.Delete(_configPath);
        Run(UncPath).EffectiveConfigFileValue.Should().BeEmpty("移さないと判断した値を、後から移さない");
    }

    /// <summary>
    /// 旧版は形式不正の database_config.txt を棄却して appsettings.json の値で開いていた（レビュー 1 回目で検出）。
    /// 「database_config.txt が優先」として移さないと、新版は既定のローカル DB を開いて台帳が分裂し、印が残るので以後も戻らない。
    /// </summary>
    [Fact]
    public void database_config_txtが形式不正でappsettingsが有効なら上書きして移し_上書き前の値をログに残すこと()
    {
        AppConfigFiles.Write(_configPath, RelativePath);

        var result = Run(UncPath);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.Migrated);
        result.EffectiveConfigFileValue.Should().Be(UncPath, "旧版で実際に開いていた共有 DB と同じ保存先で開く");
        AppConfigFiles.Read(_configPath).Should().Be(UncPath);
        DatabaseLocationResolver.Resolve(result.EffectiveConfigFileValue).Path.Should().Be(UncPath, "前提: 解決も棄却しない");
        File.Exists(_markerPath).Should().BeTrue();
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains(RelativePath) && e.Message.Contains(UncPath),
            "上書きで失われる値を残す: " + _logger.FormatEntries());
    }

    [Fact]
    public void database_config_txtもappsettingsも形式不正なら移さず_解決が既定で開いて警告すること()
    {
        AppConfigFiles.Write(_configPath, RelativePath);

        var result = Run(OtherRelativePath);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.KeptConfigFile, "旧版も両方棄却して既定で開いていた");
        AppConfigFiles.Read(_configPath).Should().Be(RelativePath, "不正な値どうしで上書きしない");
        var resolution = DatabaseLocationResolver.Resolve(result.EffectiveConfigFileValue);
        resolution.Path.Should().BeNull("既定で開く");
        resolution.Rejected.Should().ContainSingle().Which.Value.Should().Be(RelativePath, "起動後の警告で再設定を案内する");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void appsettingsに値が無ければ何もせず_database_config_txtの値で開くこと(string? appSettingsValue)
    {
        AppConfigFiles.Write(_configPath, UncPath);

        var result = Run(appSettingsValue);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.NoLegacyValue);
        result.EffectiveConfigFileValue.Should().Be(UncPath);
        File.Exists(_markerPath).Should().BeFalse("値が無い起動では判断していない（後で値が書かれたら、その起動で移す）");
        _logger.Entries.Should().BeEmpty("通常の起動でログを増やさない");
    }

    [Fact]
    public void どちらも空なら既定で開き_ファイルを作らないこと()
    {
        var result = Run(null);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.NoLegacyValue);
        result.EffectiveConfigFileValue.Should().BeEmpty("既定のローカル DB");
        File.Exists(_configPath).Should().BeFalse("database_config.txt が無い＝既定、のまま（インストーラーと同じ扱い）");
        File.Exists(_markerPath).Should().BeFalse();
    }

    [Fact]
    public void database_config_txtへ書けなければ起動を止めず_この起動はappsettingsの値で開き_次の起動で再試行すること()
    {
        // 一時ファイルの名前にフォルダーを置き、書き込みを実際に失敗させる
        var blocker = _configPath + ".tmp";
        Directory.CreateDirectory(blocker);

        var result = Run(UncPath);

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.WriteFailed);
        result.EffectiveConfigFileValue.Should().Be(UncPath, "既定のローカル DB へ切り替えると台帳が分裂する");
        File.Exists(_configPath).Should().BeFalse("書きかけの値を最終名で残さない");
        File.Exists(_markerPath).Should().BeFalse("印を残すと次の起動で再試行されない");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Exception != null && e.Message.Contains(UncPath),
            _logger.FormatEntries());

        Directory.Delete(blocker);
        var retry = Run(UncPath);

        retry.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.Migrated);
        AppConfigFiles.Read(_configPath).Should().Be(UncPath);
    }

    [Fact]
    public void database_config_txtを読めなければ空とみなして上書きせず_この起動はappsettingsの値で開くこと()
    {
        AppConfigFiles.Write(_configPath, LocalPath);

        LegacyDatabasePathMigrationResult result;
        using (new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Run(UncPath);
        }

        result.Outcome.Should().Be(LegacyDatabasePathMigrationOutcome.ConfigFileUnreadable);
        result.EffectiveConfigFileValue.Should().Be(UncPath, "読めない database_config.txt は既定扱いになるため、旧版と同じく appsettings.json の値で開く");
        AppConfigFiles.Read(_configPath).Should().Be(LocalPath, "読めなかったファイルを空とみなして上書きしない");
        File.Exists(_markerPath).Should().BeFalse("次の起動で判断し直す");
    }

    [Fact]
    public void ロガーは必須であること()
    {
        var act = () => LegacyDatabasePathMigration.Run(UncPath, _configPath, _markerPath, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
