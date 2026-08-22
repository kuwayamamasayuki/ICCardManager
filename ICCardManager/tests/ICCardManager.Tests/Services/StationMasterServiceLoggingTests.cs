using FluentAssertions;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using System;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1819: 駅マスタ読み込みの本番ログを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は読み込み失敗時の痕跡が <c>#if DEBUG</c> の <c>Debug.WriteLine</c> のみで、
/// Release ビルド（本番）では完全に無言だった。埋め込みリソースの欠落
/// （csproj の <c>&lt;EmbeddedResource&gt;</c> 削除等のビルド・パッケージング退行）で
/// 駅マスタが 5,988 行からフォールバックの約 136 駅へ静かに縮退し、
/// 「鉄道（A 駅～?）」が 6 年保存の台帳へ恒久記録されても原因を特定できなかった。
/// </para>
/// <para>
/// 本番のログ出力は <c>appsettings.json</c> の <c>Logging:LogLevel:Default = Information</c> に
/// よってフィルタされるため、レベルが Information 以上であることを表明する
/// （<c>.claude/rules/development-conventions.md</c> のロギング規約、Issue #1716 / #1730）。
/// </para>
/// </remarks>
public class StationMasterServiceLoggingTests
{
    /// <summary>存在しないリソース名。読み込み失敗経路（リソース欠落）を再現する。</summary>
    private const string MissingResourceName = "ICCardManager.Resources.NotExisting.StationCode.csv";

    [Fact]
    public void EnsureLoaded_読み込み成功時に駅数と路線数をInformationで残すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<StationMasterService>>();
        var service = new StationMasterService(orgOptions: null, logger: loggerMock.Object);

        // Act
        service.EnsureLoaded();

        // Assert: フォールバックへの縮退を件数で判別できるよう、正常時も件数を残す
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("駅マスタを読み込みました")
                                              && v.ToString().Contains(service.StationCount.ToString())),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "正常時の件数が無いと、フォールバックへ縮退した際に件数を比較して判別できない");
    }

    [Fact]
    public void EnsureLoaded_読み込み成功時はErrorもWarningも出さないこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<StationMasterService>>();
        var service = new StationMasterService(orgOptions: null, logger: loggerMock.Object);

        // Act
        service.EnsureLoaded();

        // Assert: 「常に出す」実装へ緩めても気付けるよう、正常時に出さないことも固定する
        loggerMock.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == LogLevel.Error || l == LogLevel.Warning),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            "正常に読み込めた場合は縮退していないため警告・エラーを出さない");
    }

    [Fact]
    public void EnsureLoaded_リソース欠落時に例外つきのErrorを残すこと()
    {
        // Arrange: 埋め込みリソースの欠落（ビルド・パッケージング退行）を再現
        var loggerMock = new Mock<ILogger<StationMasterService>>();
        var service = new StationMasterService(
            orgOptions: null, logger: loggerMock.Object, resourceName: MissingResourceName);

        // Act
        service.EnsureLoaded();

        // Assert: 例外オブジェクトごと Error で残す（Release でも出力される水準）
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("駅マスタの読み込みに失敗")
                                              && v.ToString().Contains(MissingResourceName)),
                It.Is<Exception>(e => e != null),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "Issue #1819: 修正前は Release で完全に無言だった。原因（どのリソースが取れなかったか）を残す");
    }

    [Fact]
    public void EnsureLoaded_リソース欠落時にフォールバック縮退の件数をWarningで残すこと()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<StationMasterService>>();
        var service = new StationMasterService(
            orgOptions: null, logger: loggerMock.Object, resourceName: MissingResourceName);

        // Act
        service.EnsureLoaded();

        // Assert: 縮退後の規模を件数で示す（正常時の Information と突き合わせられる）
        var fallbackCount = service.StationCount;
        fallbackCount.Should().BeGreaterThan(0, "フォールバックの主要駅データが読み込まれる");

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("フォールバック")
                                              && v.ToString().Contains(fallbackCount.ToString())),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "Issue #1730: レベルだけ上げて「失敗しました」とだけ書いても切り分けはできない。件数を載せる");
    }

    [Fact]
    public void EnsureLoaded_リソース欠落時でも駅名解決は継続できること()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<StationMasterService>>();
        var service = new StationMasterService(
            orgOptions: null, logger: loggerMock.Object, resourceName: MissingResourceName);

        // Act: フォールバックに含まれる駅（福岡市地下鉄 空港線 天神 = 0xE70F）
        var stationName = service.GetStationName(0xE70F, ICCardManager.Common.CardType.Hayakaken);

        // Assert: ログを足しただけで縮退時の動作は変えていない
        stationName.Should().Be("天神", "読み込み失敗時もフォールバックで動作を継続する（従来どおり）");
    }

    [Fact]
    public void EnsureLoaded_ロガー未指定でも例外にならないこと()
    {
        // Arrange: 既存の呼び出し（new StationMasterService()）を壊していないことの回帰
        var service = new StationMasterService();

        // Act
        var act = () => service.EnsureLoaded();

        // Assert
        act.Should().NotThrow("ロガー未指定時は NullLogger にフォールバックする");
        service.StationCount.Should().BeGreaterThan(0);
    }
}
