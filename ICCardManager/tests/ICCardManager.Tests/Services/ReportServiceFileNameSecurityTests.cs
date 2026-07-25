using System.IO;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1703: <see cref="ReportService.GetFiscalYearFileName"/> が、CardType / CardNumber に
/// パス区切りを含む汚染入力（CSV 取込・共有DB 経由）を受けても、出力フォルダ外への
/// パストラバーサルを構成しないことを検証する。3 sink（ReportService 2 箇所・ReportViewModel）は
/// いずれも本関数を経由するため、ここが共通の防御点となる。
/// </summary>
public class ReportServiceFileNameSecurityTests
{
    [Theory]
    [InlineData("x\\..\\..\\..\\Users\\Public\\report", "H001")]
    [InlineData("はやかけん", "..\\..\\..\\Users\\Public\\evil")]
    [InlineData("../../etc", "../../passwd")]
    public void GetFiscalYearFileName_WithPathSeparatorsInCardFields_ProducesNoTraversal(
        string cardType, string cardNumber)
    {
        var fileName = ReportService.GetFiscalYearFileName(cardType, cardNumber, 2024);

        // パス区切りを含まない = Path.Combine(outputFolder, fileName) がフォルダを脱出できない
        fileName.Should().NotContain("/");
        fileName.Should().NotContain("\\");
        // 生成名は単一のファイル名であり、パス構造を持たない
        Path.GetFileName(fileName).Should().Be(fileName);

        // Path.Combine で結合しても意図した出力フォルダ配下に留まる
        var outputFolder = Path.Combine(Path.GetTempPath(), "iccard-report-test");
        var combined = Path.GetFullPath(Path.Combine(outputFolder, fileName));
        combined.Should().StartWith(Path.GetFullPath(outputFolder));
    }

    [Fact]
    public void GetFiscalYearFileName_WithNormalCardFields_KeepsReadableName()
    {
        var fileName = ReportService.GetFiscalYearFileName("はやかけん", "H001", 2024);

        // 正常な入力は従来どおり読みやすい名前を維持する（既定様式: 物品出納簿_{種別}_{番号}_{年度}年度.xlsx）
        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }
}
