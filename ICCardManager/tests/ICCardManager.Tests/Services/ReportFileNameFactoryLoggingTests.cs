using System;
using FluentAssertions;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1820: 帳票ファイル名の書式が使えず既定書式へ縮退したことを、本番ログに残すことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 縮退（フォールバック）は<b>正常終了する</b>ため、記録が無いと管理者から見て
/// 「設定したのに反映されない」＝本 Issue が是正した状態そのものと区別が付かない
/// （<c>.claude/rules/development-conventions.md</c> #1744 の「フォールバックが働いたことを
/// 呼び出し元が知れるか」、#1819 の「縮退を伴う処理では正常時にも規模を記録する」）。
/// </para>
/// <para>
/// 本番のログ出力は <c>appsettings.json</c> の <c>Logging:LogLevel:Default = Information</c> で
/// フィルタされるため、<c>LogDebug</c> では残らない（Issue #1716 / #1730）。
/// </para>
/// </remarks>
public class ReportFileNameFactoryLoggingTests
{
    private static ReportFileNameFactory CreateFactory(
        string fileNameFormat, Mock<ILogger<ReportFileNameFactory>> loggerMock)
    {
        var options = new OrganizationOptions();
        options.ReportLayout.FileNameFormat = fileNameFormat;
        return new ReportFileNameFactory(Options.Create(options), loggerMock.Object);
    }

    private static void VerifyFallbackLogged(
        Mock<ILogger<ReportFileNameFactory>> loggerMock, Times times, string because)
    {
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("FileNameFormat")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            times,
            because);
    }

    [Theory]
    [InlineData("物品出納簿_{3}.xlsx")]                  // 存在しないプレースホルダ
    [InlineData(@"..\..\evil\{0}_{1}_{2}.xlsx")]        // パス構造
    [InlineData("物品出納簿_{0}_{1}_{2}年度*.xlsx")]     // ファイル名に使えない文字
    public void 使えない書式へ倒したときInformationで残すこと(string format)
    {
        var loggerMock = new Mock<ILogger<ReportFileNameFactory>>();

        CreateFactory(format, loggerMock).GetFiscalYearFileName("はやかけん", "H001", 2024);

        VerifyFallbackLogged(loggerMock, Times.Once(),
            "記録が無いと『設定したのに反映されない』本 Issue の欠陥と区別が付かない");
    }

    [Fact]
    public void ログに設定値と既定書式の両方を載せること()
    {
        // レベルだけを表明すると、原因を切り分けられない空虚なログでも通る（#1730）。
        // 管理者が「自分が書いた値」と「実際に使われた書式」を突き合わせられること。
        var loggerMock = new Mock<ILogger<ReportFileNameFactory>>();

        CreateFactory("物品出納簿_{3}.xlsx", loggerMock)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) =>
                    v.ToString().Contains("物品出納簿_{3}.xlsx")                       // 設定された値
                    && v.ToString().Contains("物品出納簿_{0}_{1}_{2}年度.xlsx")),      // 実際に使われた書式
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once,
            "設定値と既定書式の両方が無いと、どちらが使われたのかを突き合わせられない");
    }

    [Fact]
    public void 正当な書式では何も出さないこと()
    {
        // 対のテスト: 常に出す実装でも緑になるのを防ぐ（#1730「成功時に出さないことも固定する」）
        var loggerMock = new Mock<ILogger<ReportFileNameFactory>>();

        CreateFactory("出納簿【{0}】{1}（{2}年度）.xlsx", loggerMock)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        VerifyFallbackLogged(loggerMock, Times.Never(), "正当な書式は縮退ではない");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空設定では何も出さないこと(string format)
    {
        // 空欄は「既定を使う」という正当な設定であり、縮退ではない。
        // ここで出すと、既定配布（OrganizationOptions セクションなし）の全環境で
        // 帳票を作るたびにログが出て、本当の設定ミスが埋もれる。
        var loggerMock = new Mock<ILogger<ReportFileNameFactory>>();

        CreateFactory(format, loggerMock).GetFiscalYearFileName("はやかけん", "H001", 2024);

        VerifyFallbackLogged(loggerMock, Times.Never(), "空欄は既定を使うという正当な設定");
    }

    [Fact]
    public void 同じ書式で繰り返し呼んでも一度しか出さないこと()
    {
        // 帳票の一括出力ではカード枚数ぶん呼ばれる。無条件に出すとログが肥大化し、
        // 他の事象が埋もれる（#1819 の「高頻度で回る処理のログ」と同じ判断）。
        var loggerMock = new Mock<ILogger<ReportFileNameFactory>>();
        var factory = CreateFactory("物品出納簿_{3}.xlsx", loggerMock);

        for (var i = 0; i < 20; i++)
        {
            factory.GetFiscalYearFileName("はやかけん", $"H{i:D3}", 2024);
        }

        VerifyFallbackLogged(loggerMock, Times.Once(), "一括出力でログが肥大化しないこと");
    }

    [Fact]
    public void ロガー未注入でも例外にならないこと()
    {
        // ReportService は DI 未配線時に自前で ReportFileNameFactory を生成する経路を持つ
        var act = () => new ReportFileNameFactory(
                Options.Create(new OrganizationOptions { ReportLayout = { FileNameFormat = "{3}.xlsx" } }))
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        act.Should().NotThrow();
    }
}
