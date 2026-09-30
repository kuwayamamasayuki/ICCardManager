using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1687: <see cref="UpdateNotificationService"/> の更新通知チェックを検証する。
/// DBと同じフォルダの latest_version.txt を読み、自バージョンより新しい場合のみ
/// 通知を返すこと、およびファイル不在・内容不正・I/Oエラーで起動を阻害しない
/// （nullを返す）ことを保証する。
/// </summary>
public class UpdateNotificationServiceTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly Mock<IDatabaseInfo> _databaseInfoMock;

    /// <summary>テストで固定する「このPCのバージョン」</summary>
    private static readonly Version CurrentVersion = new(2, 10, 0);

    public UpdateNotificationServiceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"UpdateNotif_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _databaseInfoMock.Setup(x => x.DatabasePath)
            .Returns(Path.Combine(_testDirectory, "iccard.db"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    private UpdateNotificationService CreateService()
        => new(_databaseInfoMock.Object, CurrentVersion);

    private string LatestVersionFilePath
        => Path.Combine(_testDirectory, UpdateNotificationService.LatestVersionFileName);

    private void WriteLatestVersionFile(string content)
        => File.WriteAllText(LatestVersionFilePath, content);

    private string ReadFirstLineOfLatestVersionFile()
        => File.ReadAllLines(LatestVersionFilePath).First(line => !string.IsNullOrWhiteSpace(line));

    private string[] TempFilesInDbFolder()
        => Directory.GetFiles(_testDirectory, "*.tmp");

    [Fact]
    public void CheckForNewerVersion_ファイルが存在しない場合はnullを返すこと()
    {
        var result = CreateService().CheckForNewerVersion();

        result.Should().BeNull("latest_version.txt 未配置は更新通知を使わない正常運用");
    }

    [Fact]
    public void CheckForNewerVersion_新しいバージョンが記載されている場合は検出すること()
    {
        WriteLatestVersionFile("2.11.0");

        var result = CreateService().CheckForNewerVersion();

        result.Should().NotBeNull();
        result.LatestVersion.Should().Be("2.11.0");
        result.CurrentVersion.Should().Be("2.10.0");
    }

    [Fact]
    public void CheckForNewerVersion_同一バージョンの場合はnullを返すこと()
    {
        WriteLatestVersionFile("2.10.0");

        CreateService().CheckForNewerVersion().Should().BeNull("同一バージョンは更新不要");
    }

    [Fact]
    public void CheckForNewerVersion_古いバージョンの場合はnullを返すこと()
    {
        WriteLatestVersionFile("2.9.0");

        CreateService().CheckForNewerVersion().Should().BeNull("記載が古い場合は通知しない");
    }

    [Theory]
    [InlineData("v2.11.0")]
    [InlineData("  2.11.0  ")]
    [InlineData("2.11")]
    [InlineData("\r\n\r\n2.11.0\r\n")]  // 空行スキップして最初の非空白行を採用
    public void CheckForNewerVersion_表記ゆれを許容すること(string content)
    {
        WriteLatestVersionFile(content);

        var result = CreateService().CheckForNewerVersion();

        result.Should().NotBeNull();
        result.LatestVersion.Should().Be("2.11.0");
    }

    [Fact]
    public void CheckForNewerVersion_4要素表記はRevisionを無視して比較すること()
    {
        // "2.10.0.5" を素朴に Version 比較すると 2.10.0 より新しいと誤判定される
        WriteLatestVersionFile("2.10.0.5");

        CreateService().CheckForNewerVersion().Should().BeNull("3要素正規化後は同一バージョン");
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("   \r\n   ")]
    [InlineData("最新版があります")]
    public void CheckForNewerVersion_内容が不正な場合はnullを返すこと(string content)
    {
        WriteLatestVersionFile(content);

        CreateService().CheckForNewerVersion().Should().BeNull("不正な内容で起動処理を阻害しない");
    }

    [Fact]
    public void CheckForNewerVersion_DBフォルダが存在しない場合はnullを返すこと()
    {
        // ネットワーク切断等で共有フォルダにアクセスできないケースの模擬
        _databaseInfoMock.Setup(x => x.DatabasePath)
            .Returns(Path.Combine(_testDirectory, "no_such_dir", "iccard.db"));

        CreateService().CheckForNewerVersion().Should().BeNull("フォルダ不在でも例外を漏らさない");
    }

    #region 起動時の自動配置（Issue #2149）

    /// <summary>
    /// 判定の境界。文字列ではなくバージョンとして比べること（"2.9.0" は "2.10.0" より古い）、
    /// 読み取り側と同じ正規化（"v" 接頭辞・4 要素の Revision 無視）を通すことを固定する。
    /// </summary>
    [Theory]
    [InlineData(null, "2.10.0", true)]         // ファイルなし
    [InlineData("", "2.10.0", true)]
    [InlineData("abc", "2.10.0", true)]        // 解釈できない記載は上書きしてよい
    [InlineData("2.9.0", "2.10.0", true)]      // 文字列比較なら "2.9.0" > "2.10.0" と誤る
    [InlineData("2.10.9", "2.11.0", true)]
    [InlineData("2.10.0", "2.10.0", false)]    // 同じ版は書き換えない
    [InlineData("v2.10.0", "2.10.0", false)]
    [InlineData("2.10.0.5", "2.10.0", false)]  // 3 要素へ正規化すると同じ版
    [InlineData("2.10.1", "2.10.0", false)]    // 新しい版は下げない（単調増加）
    [InlineData("v2.99", "2.10.0", false)]
    public void ShouldPublish_記載が欠落_解釈不能_古いときだけtrueを返すこと(
        string? firstLine, string current, bool expected)
    {
        UpdateNotificationService.ShouldPublish(firstLine, Version.Parse(current))
            .Should().Be(expected);
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_ファイルが無ければ自バージョンで作成すること()
    {
        CreateService().PublishCurrentVersionIfNewer();

        ReadFirstLineOfLatestVersionFile().Should().Be("2.10.0");
        CreateService().CheckForNewerVersion().Should().BeNull(
            "配置した PC 自身は起動直後の更新チェックで「新しいバージョンがあります」を出さない");
        TempFilesInDbFolder().Should().BeEmpty("書き込み後に一時ファイルを残さない");
    }

    [Theory]
    [InlineData("2.9.0")]
    [InlineData("abc")]
    [InlineData("")]
    public void PublishCurrentVersionIfNewer_記載が古い_解釈できない場合は自バージョンへ書き換えること(string existing)
    {
        WriteLatestVersionFile(existing);

        CreateService().PublishCurrentVersionIfNewer();

        ReadFirstLineOfLatestVersionFile().Should().Be("2.10.0");
        CreateService().CheckForNewerVersion().Should().BeNull();
        TempFilesInDbFolder().Should().BeEmpty("書き込み後に一時ファイルを残さない");
    }

    [Theory]
    [InlineData("2.10.0")]
    [InlineData("v2.99")]
    public void PublishCurrentVersionIfNewer_記載が同じ_新しい場合は内容も更新日時も変えないこと(string existing)
    {
        WriteLatestVersionFile(existing);
        // 書き換えの有無を更新日時で確実に判別できるよう、過去の日時へ固定しておく
        var pastWriteTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(LatestVersionFilePath, pastWriteTime);

        CreateService().PublishCurrentVersionIfNewer();

        File.ReadAllText(LatestVersionFilePath).Should().Be(existing,
            "新しい版の記載を古い版の PC が下げてはならない（単調増加）");
        File.GetLastWriteTimeUtc(LatestVersionFilePath).Should().Be(pastWriteTime,
            "同じ版の PC が起動のたびに書き直すと、最大 20 台の起動で共有フォルダへ無駄な書き込みが集中する");
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_書いた内容は古い版のPCから新しいバージョンとして読めること()
    {
        // 2 行目に書き込み元の PC 名と日時を残すため、読み手（1 行目のみ解釈）との互換を往復で固定する
        CreateService().PublishCurrentVersionIfNewer();

        var oldPc = new UpdateNotificationService(_databaseInfoMock.Object, new Version(2, 9, 0));
        var result = oldPc.CheckForNewerVersion();

        result.Should().NotBeNull();
        result.LatestVersion.Should().Be("2.10.0");
        File.ReadAllLines(LatestVersionFilePath).Should().Contain(
            line => line.StartsWith("#") && line.Contains(Environment.MachineName),
            "どの PC の起動で上がったかを管理者が追えること");
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_書き換えたときは以前の記載とともにInformationログを残すこと()
    {
        WriteLatestVersionFile("2.9.0");
        var logger = new RecordingLogger<UpdateNotificationService>();

        new UpdateNotificationService(_databaseInfoMock.Object, CurrentVersion, logger)
            .PublishCurrentVersionIfNewer();

        logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Information && e.Message.Contains("2.10.0") && e.Message.Contains("2.9.0"),
            logger.FormatEntries());
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_差し替えに失敗しても例外を投げずWarningを1件残し一時ファイルを消すこと()
    {
        // Arrange: 他 PC が読み取り中（削除の共有を許さずに開いている）で差し替えられない状態。
        // 読み取りは許すので、判定までは進んで書き込み段で失敗する
        WriteLatestVersionFile("2.9.0");
        var logger = new RecordingLogger<UpdateNotificationService>();
        var service = new UpdateNotificationService(_databaseInfoMock.Object, CurrentVersion, logger);

        using (new FileStream(LatestVersionFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Act
            Action act = () => service.PublishCurrentVersionIfNewer();

            // Assert
            act.Should().NotThrow("更新通知ファイルの配置の失敗で起動を妨げない");
        }

        logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Warning && e.Message.Contains("更新できませんでした"),
            logger.FormatEntries());
        File.ReadAllText(LatestVersionFilePath).Should().Be("2.9.0", "失敗しても元のファイルは残る");
        TempFilesInDbFolder().Should().BeEmpty("一時ファイルは作り直せる内容なので失敗時も残さない");
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_DBフォルダが存在しない場合はフォルダを作らず何もしないこと()
    {
        var missingDirectory = Path.Combine(_testDirectory, "no_such_dir");
        _databaseInfoMock.Setup(x => x.DatabasePath)
            .Returns(Path.Combine(missingDirectory, "iccard.db"));

        Action act = () => CreateService().PublishCurrentVersionIfNewer();

        act.Should().NotThrow();
        Directory.Exists(missingDirectory).Should().BeFalse(
            "共有フォルダの切断時にローカル側へ無関係なフォルダを作らない");
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_取り残された古い一時ファイルだけを回収すること()
    {
        // Arrange
        var stale = Path.Combine(_testDirectory, UpdateNotificationService.LatestVersionFileName + ".0123abcd.tmp");
        var fresh = Path.Combine(_testDirectory, UpdateNotificationService.LatestVersionFileName + ".89abcdef.tmp");
        var unrelated = Path.Combine(_testDirectory, "backup_20260101_000000.db.tmp");
        // 列挙のワイルドカード（latest_version.txt.*.tmp）には一致するが、一時名の形ではないもの。
        // 名前の形の照合を外すと消えてしまう（回収は厳密な形に限る。service-conventions #2040）
        var lookalikes = new[]
        {
            Path.Combine(_testDirectory, UpdateNotificationService.LatestVersionFileName + ".old.tmp"),
            Path.Combine(_testDirectory, UpdateNotificationService.LatestVersionFileName + ".0123abcd0.tmp"),
        };
        var old = DateTime.UtcNow - UpdateNotificationService.StaleTempFileAge - TimeSpan.FromHours(1);
        foreach (var path in new[] { stale, fresh, unrelated }.Concat(lookalikes))
        {
            File.WriteAllText(path, "x");
            if (path != fresh)
                File.SetLastWriteTimeUtc(path, old);
        }

        // 記載が同じ（＝書き込みは起きない）起動でも回収は行う
        WriteLatestVersionFile("2.10.0");

        // Act
        CreateService().PublishCurrentVersionIfNewer();

        // Assert
        File.Exists(stale).Should().BeFalse("強制終了で取り残された一時ファイルは回収する");
        File.Exists(fresh).Should().BeTrue("他 PC が書き込み中の可能性がある新しい一時ファイルは消さない");
        File.Exists(unrelated).Should().BeTrue("形の違う .tmp（バックアップの一時ファイル等）は消さない");
        foreach (var path in lookalikes)
            File.Exists(path).Should().BeTrue($"一時名の形に一致しない {Path.GetFileName(path)} は消さない");
    }

    [Fact]
    public void PublishCurrentVersionIfNewer_一時ファイルに書き切ってから最終名を差し替えること()
    {
        // 最終名へ直接書く実装だと、最大 20 台の同時起動で他 PC が書きかけのファイルを読む（service-conventions #1748）。
        // 差し替えの瞬間に「最終名はまだ古い内容・一時ファイルは新しい内容を書き切っている」ことを観測する
        WriteLatestVersionFile("2.9.0");
        var service = new ObservingReplaceService(_databaseInfoMock.Object, CurrentVersion);

        service.PublishCurrentVersionIfNewer();

        service.FinalContentAtReplace.Should().Be("2.9.0", "差し替えまで最終名には触れない");
        service.TempFirstLineAtReplace.Should().Be("2.10.0", "一時ファイルは差し替えの前に書き切っている");
        Path.GetFileName(service.TempPathAtReplace).Should().MatchRegex(
            @"^latest_version\.txt\.[0-9a-f]{8}\.tmp$", "一時名は最終名の読み取りに一致しない形");
        ReadFirstLineOfLatestVersionFile().Should().Be("2.10.0");
    }

    [Theory]
    [InlineData(false)]  // NAS 等で ReplaceFile が置換先に触れずに失敗する
    [InlineData(true)]   // ReplaceFile が置換先を消したまま失敗する（ERROR_UNABLE_TO_MOVE_REPLACEMENT）
    public void PublishCurrentVersionIfNewer_ReplaceFileが失敗したら削除して移動する方法で差し替えること(
        bool destinationLost)
    {
        WriteLatestVersionFile("2.9.0");
        var logger = new RecordingLogger<UpdateNotificationService>();
        var service = new FailingReplaceService(_databaseInfoMock.Object, CurrentVersion, logger, destinationLost);

        service.PublishCurrentVersionIfNewer();

        ReadFirstLineOfLatestVersionFile().Should().Be("2.10.0",
            "ReplaceFile が恒常的に失敗する共有フォルダーでも、更新通知ファイルは書き換わる" + logger.FormatEntries());
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning, logger.FormatEntries());
        TempFilesInDbFolder().Should().BeEmpty();
    }

    /// <summary>差し替えの瞬間のファイルの状態を記録する</summary>
    private sealed class ObservingReplaceService : UpdateNotificationService
    {
        public ObservingReplaceService(IDatabaseInfo databaseInfo, Version currentVersion)
            : base(databaseInfo, currentVersion)
        {
        }

        public string? FinalContentAtReplace { get; private set; }

        public string? TempFirstLineAtReplace { get; private set; }

        public string? TempPathAtReplace { get; private set; }

        internal override void ReplaceFile(string tempPath, string filePath)
        {
            FinalContentAtReplace = File.ReadAllText(filePath);
            TempFirstLineAtReplace = File.ReadAllLines(tempPath)[0];
            TempPathAtReplace = tempPath;
            base.ReplaceFile(tempPath, filePath);
        }
    }

    /// <summary>ReplaceFile の失敗を再現する</summary>
    private sealed class FailingReplaceService : UpdateNotificationService
    {
        private readonly bool _destinationLost;

        public FailingReplaceService(
            IDatabaseInfo databaseInfo, Version currentVersion,
            ILogger<UpdateNotificationService> logger, bool destinationLost)
            : base(databaseInfo, currentVersion, logger)
        {
            _destinationLost = destinationLost;
        }

        internal override void ReplaceFile(string tempPath, string filePath)
        {
            if (_destinationLost)
                File.Delete(filePath);
            throw new IOException("ReplaceFile に失敗しました（テストで再現）");
        }
    }

    #endregion
}
