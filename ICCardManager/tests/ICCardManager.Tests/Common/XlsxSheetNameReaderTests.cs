using System;
using System.IO;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #1691: xlsx からワークシート名だけを読み取る <see cref="XlsxSheetNameReader"/> の単体テスト。
/// </summary>
/// <remarks>
/// ClosedXML で実際の xlsx を生成して読ませる。ZIP/OPC の構造解釈が正しいことを
/// 実ファイルで確かめないと、帳票の「出力済み判定」がまるごと嘘になるため。
/// </remarks>
public class XlsxSheetNameReaderTests : IDisposable
{
    private readonly string _testDirectory;

    public XlsxSheetNameReaderTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"XlsxSheetNameTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, true);
            }
        }
        catch (IOException)
        {
            // 後片付けの失敗はテスト結果に影響させない
        }
    }

    /// <summary>
    /// 指定したシート名を持つ xlsx を生成する
    /// </summary>
    private string CreateWorkbook(string fileName, params string[] sheetNames)
    {
        var path = Path.Combine(_testDirectory, fileName);
        using (var workbook = new XLWorkbook())
        {
            foreach (var sheetName in sheetNames)
            {
                var sheet = workbook.Worksheets.Add(sheetName);
                sheet.Cell(1, 1).Value = "ダミー";
            }
            workbook.SaveAs(path);
        }
        return path;
    }

    [Fact]
    public void TryReadSheetNames_ShouldReturnAllSheetNamesInOrder()
    {
        // Arrange
        var path = CreateWorkbook("book.xlsx", "4月", "5月", "6月");

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, out var sheetNames);

        // Assert
        succeeded.Should().BeTrue();
        sheetNames.Should().Equal("4月", "5月", "6月");
    }

    [Fact]
    public void TryReadSheetNames_WithMissingFile_ShouldFail()
    {
        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(
            Path.Combine(_testDirectory, "存在しない.xlsx"), out var sheetNames);

        // Assert
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryReadSheetNames_WithEmptyPath_ShouldFail(string path)
    {
        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, out var sheetNames);

        // Assert
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSheetNames_WithNonXlsxContent_ShouldFail()
    {
        // Arrange: 拡張子だけ xlsx にした非 ZIP ファイル
        var path = Path.Combine(_testDirectory, "壊れたファイル.xlsx");
        File.WriteAllText(path, "これは Excel ファイルではありません");

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, out var sheetNames);

        // Assert
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Fact]
    public void TryContainsSheet_WithExistingSheet_ShouldReportTrue()
    {
        // Arrange
        var path = CreateWorkbook("book.xlsx", "4月", "5月");

        // Act
        var determined = XlsxSheetNameReader.TryContainsSheet(path, "5月", out var contains);

        // Assert
        determined.Should().BeTrue();
        contains.Should().BeTrue();
    }

    [Fact]
    public void TryContainsSheet_WithMissingSheet_ShouldReportFalse()
    {
        // Arrange
        var path = CreateWorkbook("book.xlsx", "4月", "5月");

        // Act
        var determined = XlsxSheetNameReader.TryContainsSheet(path, "6月", out var contains);

        // Assert
        determined.Should().BeTrue("ファイルは読めているため判定自体は成立する");
        contains.Should().BeFalse();
    }

    [Fact]
    public void TryContainsSheet_WhenFileUnreadable_ShouldReportUndetermined()
    {
        // Act
        var determined = XlsxSheetNameReader.TryContainsSheet(
            Path.Combine(_testDirectory, "存在しない.xlsx"), "5月", out var contains);

        // Assert
        determined.Should().BeFalse("読めないファイルは『シートが無い』ではなく『判定不能』");
        contains.Should().BeFalse();
    }

    /// <summary>
    /// 対象ブックを開いたままでもシート名を読めること。
    /// 月次作業中はユーザーが前月分の帳票を Excel で開きっぱなしにするため、
    /// 共有読み取りできないと出力状況が常に「確認できません」になる。
    /// </summary>
    [Fact]
    public void TryReadSheetNames_WhileFileIsOpenedByAnotherProcess_ShouldSucceed()
    {
        // Arrange
        var path = CreateWorkbook("book.xlsx", "7月");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            // Act
            var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, out var sheetNames);

            // Assert
            succeeded.Should().BeTrue();
            sheetNames.Should().Contain("7月");
        }
    }
}
