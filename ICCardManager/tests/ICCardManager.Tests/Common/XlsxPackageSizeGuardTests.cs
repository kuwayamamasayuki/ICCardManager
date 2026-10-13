using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2272: 帳票作成で既存の年度ファイルを丸ごと開く前の大きさの検査（<see cref="XlsxPackageSizeGuard"/>）。
/// </summary>
/// <remarks>
/// 上限を超える側と、上限ちょうど・正当なファイルが従来どおり通る側を対で置く。
/// 前者だけだと常に「大きすぎる」を返す実装でも緑になる。
/// </remarks>
public class XlsxPackageSizeGuardTests : IDisposable
{
    private readonly string _testDirectory;

    public XlsxPackageSizeGuardTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"XlsxPackageSizeGuardTests_{Guid.NewGuid():N}");
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

    private string CreateWorkbook(string fileName, params string[] sheetNames)
    {
        var path = Path.Combine(_testDirectory, fileName);
        using (var workbook = new XLWorkbook())
        {
            foreach (var sheetName in sheetNames)
            {
                workbook.Worksheets.Add(sheetName).Cell(1, 1).Value = "ダミー";
            }

            workbook.SaveAs(path);
        }

        return path;
    }

    private static long GetExpandedTotal(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            return archive.Entries.Sum(e => e.Length);
        }
    }

    [Fact]
    public void Check_WithRealReportWorkbook_ShouldBeWithinDefaultLimits()
    {
        // Arrange: 帳票の最大構成（12 か月＋累計相当）
        var path = CreateWorkbook(
            "book.xlsx", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月", "1月", "2月", "3月", "累計");

        // Act & Assert
        XlsxPackageSizeGuard.Check(path).Should().Be(XlsxPackageSizeCheck.WithinLimit);
        GetExpandedTotal(path).Should().BeLessThan(XlsxPackageSizeGuard.MaxExpandedBytes / 10,
            "正当な年度ファイルの展開後に対して上限は十分な余裕を持つ");
    }

    [Fact]
    public void Check_WhenFileExceedsLimit_ShouldReportFileTooLarge()
    {
        var path = CreateWorkbook("book.xlsx", "4月");
        var length = new FileInfo(path).Length;

        XlsxPackageSizeGuard.Check(path, length - 1, long.MaxValue)
            .Should().Be(XlsxPackageSizeCheck.FileTooLarge);
    }

    [Fact]
    public void Check_WhenExpandedTotalExceedsLimit_ShouldReportExpandedTooLarge()
    {
        // Arrange: ファイル自体は上限内（ZIP 爆弾の形）で、展開後の合計だけが上限を 1 バイト超える
        var path = CreateWorkbook("book.xlsx", "4月");
        var expanded = GetExpandedTotal(path);

        // Act & Assert
        XlsxPackageSizeGuard.Check(path, long.MaxValue, expanded - 1)
            .Should().Be(XlsxPackageSizeCheck.ExpandedTooLarge);
    }

    [Fact]
    public void Check_WhenExactlyAtBothLimits_ShouldBeWithinLimit()
    {
        var path = CreateWorkbook("book.xlsx", "4月");

        XlsxPackageSizeGuard.Check(path, new FileInfo(path).Length, GetExpandedTotal(path))
            .Should().Be(XlsxPackageSizeCheck.WithinLimit);
    }

    [Fact]
    public void Check_WithMissingOrBrokenFile_ShouldBeUndetermined()
    {
        // 無い・ZIP として読めないファイルの判断は、開く側の従来の処理（破損の案内）に任せる
        var broken = Path.Combine(_testDirectory, "broken.xlsx");
        File.WriteAllBytes(broken, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 });

        XlsxPackageSizeGuard.Check(Path.Combine(_testDirectory, "missing.xlsx"))
            .Should().Be(XlsxPackageSizeCheck.Undetermined);
        XlsxPackageSizeGuard.Check(broken).Should().Be(XlsxPackageSizeCheck.Undetermined);
    }

    [Fact]
    public void SheetNameReaderFileLimit_ShouldBeTheSameAsGuard()
    {
        // シート名の読み取りと帳票作成で、年度ファイルとして認める大きさを食い違わせない
        XlsxSheetNameReader.MaxXlsxFileBytes.Should().Be(XlsxPackageSizeGuard.MaxFileBytes);
    }
}
