using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Services;
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

    private void WriteLatestVersionFile(string content)
        => File.WriteAllText(
            Path.Combine(_testDirectory, UpdateNotificationService.LatestVersionFileName), content);

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
}
