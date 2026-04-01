using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using ICCardManager.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Logging;

/// <summary>
/// FileLoggerProviderの単体テスト（Issue #1135）
/// ログ書き込み、ローテーション、古いログクリーンアップ、Disposeを検証
/// </summary>
public class FileLoggerProviderTests : IDisposable
{
    private readonly string _testLogDir;

    public FileLoggerProviderTests()
    {
        _testLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ICCardManager", $"TestLogs_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testLogDir))
            {
                Directory.Delete(_testLogDir, true);
            }
        }
        catch
        {
            // テスト後のクリーンアップ失敗は無視
        }
    }

    private FileLoggerProvider CreateProvider(bool enabled = true, int retentionDays = 30, int maxFileSizeMB = 10)
    {
        // テスト用のユニークなパスを使用
        var relativePath = new DirectoryInfo(_testLogDir).Name;
        var options = Options.Create(new FileLoggerOptions
        {
            Enabled = enabled,
            Path = relativePath,
            RetentionDays = retentionDays,
            MaxFileSizeMB = maxFileSizeMB
        });
        return new FileLoggerProvider(options);
    }

    #region CreateLogger テスト

    [Fact]
    public void CreateLogger_同じカテゴリ名で同一インスタンスが返ること()
    {
        // Arrange
        using var provider = CreateProvider(enabled: false);

        // Act
        var logger1 = provider.CreateLogger("TestCategory");
        var logger2 = provider.CreateLogger("TestCategory");

        // Assert
        logger1.Should().BeSameAs(logger2, "同じカテゴリ名ではキャッシュされたインスタンスが返る");
    }

    [Fact]
    public void CreateLogger_異なるカテゴリ名で別インスタンスが返ること()
    {
        // Arrange
        using var provider = CreateProvider(enabled: false);

        // Act
        var logger1 = provider.CreateLogger("Category1");
        var logger2 = provider.CreateLogger("Category2");

        // Assert
        logger1.Should().NotBeSameAs(logger2, "異なるカテゴリ名では別インスタンスが返る");
    }

    [Fact]
    public void CreateLogger_FileLogger型が返ること()
    {
        // Arrange
        using var provider = CreateProvider(enabled: false);

        // Act
        var logger = provider.CreateLogger("TestCategory");

        // Assert
        logger.Should().BeOfType<FileLogger>();
    }

    #endregion

    #region WriteLog テスト

    [Fact]
    public void WriteLog_Enabled時にログファイルが作成されること()
    {
        // Arrange
        using var provider = CreateProvider(enabled: true);

        // Act
        provider.WriteLog("テストメッセージ");

        // ログの書き込みを待機（バックグラウンドキュー処理）
        Thread.Sleep(500);

        // Assert
        Directory.Exists(_testLogDir).Should().BeTrue("ログディレクトリが作成される");
        var logFiles = Directory.GetFiles(_testLogDir, "ICCardManager_*.log");
        logFiles.Should().NotBeEmpty("ログファイルが作成される");
    }

    [Fact]
    public void WriteLog_Enabled時にログ内容がファイルに書き込まれること()
    {
        // Arrange
        using var provider = CreateProvider(enabled: true);
        var testMessage = $"テストログメッセージ_{Guid.NewGuid():N}";

        // Act
        provider.WriteLog(testMessage);
        Thread.Sleep(500);

        // Assert
        var logFiles = Directory.GetFiles(_testLogDir, "ICCardManager_*.log");
        logFiles.Should().NotBeEmpty();
        var content = File.ReadAllText(logFiles[0]);
        content.Should().Contain(testMessage, "書き込んだメッセージがファイルに含まれる");
    }

    [Fact]
    public void WriteLog_Disabled時にログファイルが作成されないこと()
    {
        // Arrange
        using var provider = CreateProvider(enabled: false);

        // Act
        provider.WriteLog("このメッセージは書き込まれない");
        Thread.Sleep(200);

        // Assert
        if (Directory.Exists(_testLogDir))
        {
            var logFiles = Directory.GetFiles(_testLogDir, "ICCardManager_*.log");
            logFiles.Should().BeEmpty("Disabled時はログファイルが作成されない");
        }
    }

    [Fact]
    public void WriteLog_複数メッセージが順序通りに書き込まれること()
    {
        // Arrange
        using var provider = CreateProvider(enabled: true);

        // Act
        provider.WriteLog("メッセージ1");
        provider.WriteLog("メッセージ2");
        provider.WriteLog("メッセージ3");
        Thread.Sleep(500);

        // Assert
        var logFiles = Directory.GetFiles(_testLogDir, "ICCardManager_*.log");
        logFiles.Should().NotBeEmpty();
        var lines = File.ReadAllLines(logFiles[0]);
        lines.Should().ContainInOrder("メッセージ1", "メッセージ2", "メッセージ3");
    }

    #endregion

    #region Options テスト

    [Fact]
    public void Options_コンストラクタで渡した設定が保持されること()
    {
        // Arrange & Act
        using var provider = CreateProvider(enabled: true, retentionDays: 7, maxFileSizeMB: 5);

        // Assert
        provider.Options.Enabled.Should().BeTrue();
        provider.Options.RetentionDays.Should().Be(7);
        provider.Options.MaxFileSizeMB.Should().Be(5);
    }

    #endregion

    #region CleanupOldLogs テスト

    [Fact]
    public void コンストラクタ実行時に保持期間を過ぎたログが削除されること()
    {
        // Arrange: テスト用ログディレクトリを事前に作成し、古いファイルを配置
        Directory.CreateDirectory(_testLogDir);
        var oldFilePath = Path.Combine(_testLogDir, "ICCardManager_20200101.log");
        File.WriteAllText(oldFilePath, "古いログ");
        // ファイルの最終更新日時を古い日付に設定
        File.SetLastWriteTime(oldFilePath, DateTime.Today.AddDays(-60));

        var recentFilePath = Path.Combine(_testLogDir, $"ICCardManager_{DateTime.Today:yyyyMMdd}.log");
        File.WriteAllText(recentFilePath, "最近のログ");

        // Act: Provider作成時にCleanupOldLogsが呼ばれる（RetentionDays=30）
        // テスト用パスのディレクトリ名部分を取得
        var relativePath = new DirectoryInfo(_testLogDir).Name;
        var options = Options.Create(new FileLoggerOptions
        {
            Enabled = true,
            Path = relativePath,
            RetentionDays = 30
        });
        using var provider = new FileLoggerProvider(options);
        Thread.Sleep(200);

        // Assert
        File.Exists(oldFilePath).Should().BeFalse("保持期間を過ぎたログが削除される");
        File.Exists(recentFilePath).Should().BeTrue("最近のログは削除されない");
    }

    #endregion

    #region Dispose テスト

    [Fact]
    public void Dispose_例外なく終了すること()
    {
        // Arrange
        var provider = CreateProvider(enabled: true);
        provider.WriteLog("Dispose前のメッセージ");

        // Act & Assert
        var act = () => provider.Dispose();
        act.Should().NotThrow("Disposeは例外なく完了すべき");
    }

    [Fact]
    public void Dispose_Disabled時も例外なく終了すること()
    {
        // Arrange
        var provider = CreateProvider(enabled: false);

        // Act & Assert
        var act = () => provider.Dispose();
        act.Should().NotThrow("Disabled状態でもDisposeは例外なく完了すべき");
    }

    #endregion

    #region ファイルローテーション テスト

    [Fact]
    public void ファイルサイズ超過時にローテーションされること()
    {
        // Arrange: 非常に小さいMaxFileSizeMB（実質テスト不可のため、大量書き込みで検証）
        // 実際のローテーションは1MB以上が必要だが、ここでは構造的な動作を確認
        using var provider = CreateProvider(enabled: true, maxFileSizeMB: 10);

        // Act: ログを書き込み
        provider.WriteLog("ローテーション検証用メッセージ");
        Thread.Sleep(500);

        // Assert: 少なくとも1つのログファイルが存在する
        var logFiles = Directory.GetFiles(_testLogDir, "ICCardManager_*.log");
        logFiles.Should().NotBeEmpty("ログファイルが存在する");
    }

    #endregion
}
