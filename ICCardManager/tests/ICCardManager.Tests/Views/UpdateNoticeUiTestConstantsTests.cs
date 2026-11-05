using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2394: UI テストの基盤が扱う更新通知ファイルの名前と、撮影テストが探す更新の案内の文言が、
/// 本体（<see cref="UpdateNotificationService.LatestVersionFileName"/> と <see cref="WarningService.CheckUpdateNotificationWarning"/> の文言）と
/// 一致することを固定する。
/// </summary>
/// <remarks>
/// <para>
/// UITests は本体への ProjectReference を持たないので、UITests 側の定数をソーステキストとして読んで突き合わせる
/// （<c>BusStopAutoFillUiTestConstantsTests</c> と同じ形）。
/// </para>
/// <para>
/// ずれると、どちらも<b>緑のまま素通りする</b>。ファイル名がずれれば <c>UiTestDatabaseGuard</c> は本体が読まないファイルを退避し、
/// 開発機の更新通知ファイルがそのまま撮影画像へ写る。文言がずれれば、撮影テストの「更新の案内が出ていないこと」は探す文言が
/// そもそも無いので常に成り立つ。UI テストは CI で走らないため、ここで止める。
/// </para>
/// </remarks>
public class UpdateNoticeUiTestConstantsTests
{
    [Fact]
    public void UIテストが退避する更新通知ファイルの名前は本体が読むファイルの名前と一致すること()
    {
        var source = ReadUiTestSource("UiTestDatabaseGuard.cs");

        ExtractConst(source, "LatestVersionFileName").Should().Be(UpdateNotificationService.LatestVersionFileName,
            "本体が読まないファイルを退避しても、開発機の更新通知ファイルは撮影画像へ写る");
    }

    [Fact]
    public void UIテストが探す更新の案内の文言は本体の案内に含まれること()
    {
        var source = ReadUiTestSource("TestConstants.cs");
        var marker = ExtractConst(source, "NewVersionNoticeMarker");

        var updateService = new Mock<IUpdateNotificationService>();
        updateService.Setup(x => x.CheckForNewerVersion()).Returns(new UpdateCheckResult
        {
            LatestVersion = "99.0.0",
            CurrentVersion = "2.13.0",
        });
        var service = new WarningService(
            Mock.Of<ILedgerRepository>(), Mock.Of<IDatabaseInfo>(), Mock.Of<ICardRepository>(), updateService.Object);

        var warning = service.CheckUpdateNotificationWarning();

        warning.Should().NotBeNull();
        warning!.Type.Should().Be(WarningType.NewVersionAvailable);
        marker.Should().NotBeNullOrWhiteSpace();
        warning.DisplayText.Should().Contain(marker,
            "撮影テストの「更新の案内が出ていないこと」は、この語を含む要素が無いことで確かめている");
    }

    private static string ReadUiTestSource(string fileName)
    {
        var path = Path.Combine(
            TestPaths.GetSolutionRoot(), "tests", "ICCardManager.UITests", "Infrastructure", fileName);
        File.Exists(path).Should().BeTrue($"検査対象のファイルが見つからない: {path}");

        // 文字列リテラルは検査対象そのものなので、コメントだけを剥がす
        return TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));
    }

    private static string ExtractConst(string source, string constName)
    {
        var match = Regex.Match(source, @"\bconst\s+string\s+" + Regex.Escape(constName) + @"\s*=\s*""(?<value>[^""]*)""");
        match.Success.Should().BeTrue($"定数 {constName} が文字列リテラルで宣言されていること");
        return match.Groups["value"].Value;
    }
}
