using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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

    #region Issue #2272: workbook.xml の大きさの上限と DTD の禁止

    /// <summary>
    /// xl/workbook.xml だけを持つ最小の ZIP を作る（シート名の読み取りに必要なのはこのパートだけ）
    /// </summary>
    private string CreateZipWithWorkbookXml(string fileName, string workbookXml)
    {
        var path = Path.Combine(_testDirectory, fileName);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("xl/workbook.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(workbookXml);
            }
        }

        return path;
    }

    /// <summary>
    /// 指定した数のシートを持つ workbook.xml を組み立てる
    /// </summary>
    private static string BuildWorkbookXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets>");
        for (var i = 1; i <= sheetCount; i++)
        {
            builder.Append($"<sheet name=\"シート{i}\" sheetId=\"{i}\"/>");
        }

        builder.Append("</sheets></workbook>");
        return builder.ToString();
    }

    /// <summary>
    /// ZIP 内の xl/workbook.xml の展開後サイズ（中央ディレクトリの宣言値）を読む
    /// </summary>
    private static long GetWorkbookPartLength(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            return archive.GetEntry("xl/workbook.xml")!.Length;
        }
    }

    /// <summary>
    /// ZIP のローカルヘッダーと中央ディレクトリが宣言する展開後サイズを、実際より小さい値へ書き換える
    /// （宣言を偽った ZIP 爆弾を模す）
    /// </summary>
    private static void ForgeDeclaredUncompressedSize(string path, uint forgedSize)
    {
        var bytes = File.ReadAllBytes(path);
        var patched = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            var signature = BitConverter.ToUInt32(bytes, i);
            if (signature == 0x04034b50)
            {
                // ローカルファイルヘッダー: 展開後サイズはオフセット 22
                BitConverter.GetBytes(forgedSize).CopyTo(bytes, i + 22);
                patched++;
            }
            else if (signature == 0x02014b50)
            {
                // 中央ディレクトリのファイルヘッダー: 展開後サイズはオフセット 24
                BitConverter.GetBytes(forgedSize).CopyTo(bytes, i + 24);
                patched++;
            }
        }

        patched.Should().Be(2, "エントリ 1 つの ZIP にはローカルヘッダーと中央ディレクトリが 1 つずつある");
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void TryReadSheetNames_WhenWorkbookPartExceedsLimit_ShouldReportUndetermined()
    {
        // Arrange: 実際の帳票ファイルを、その workbook.xml より 1 バイト小さい上限で読ませる
        var path = CreateWorkbook("book.xlsx", "4月", "5月");
        var partLength = GetWorkbookPartLength(path);

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, partLength - 1, out var sheetNames);

        // Assert: 展開せずに「判定不能」とする（ZIP 爆弾でメモリを圧迫させない）
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSheetNames_WhenWorkbookPartIsExactlyAtLimit_ShouldSucceed()
    {
        // Arrange: 上限ちょうどは読める（宣言値の判定・読み込み側の打ち切りのどちらも境界を含む）
        var path = CreateWorkbook("book.xlsx", "4月", "5月");
        var partLength = GetWorkbookPartLength(path);

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, partLength, out var sheetNames);

        // Assert
        succeeded.Should().BeTrue();
        sheetNames.Should().Equal("4月", "5月");
    }

    [Fact]
    public void TryReadSheetNames_WithDefaultLimit_ShouldReadRealReportWorkbook()
    {
        // Arrange: 帳票の最大構成（12 か月＋累計相当）でも既定の上限に収まること
        var sheetNames = new[] { "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月", "1月", "2月", "3月", "累計" };
        var path = CreateWorkbook("book.xlsx", sheetNames);

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, out var actual);

        // Assert
        succeeded.Should().BeTrue();
        actual.Should().Equal(sheetNames);
        GetWorkbookPartLength(path).Should().BeLessThan(XlsxSheetNameReader.MaxWorkbookPartBytes / 10,
            "正当な workbook.xml に対して上限は十分な余裕を持つ");
        new FileInfo(path).Length.Should().BeLessThan(XlsxSheetNameReader.MaxXlsxFileBytes / 10,
            "正当な年度ファイルに対してファイルの上限も十分な余裕を持つ");
    }

    [Fact]
    public void TryReadSheetNames_WhenFileExceedsLimit_ShouldReportUndetermined()
    {
        // Arrange: ファイル自体を、その大きさより 1 バイト小さい上限で読ませる（workbook.xml は上限内）
        var path = CreateWorkbook("book.xlsx", "4月", "5月");
        var fileLength = new FileInfo(path).Length;

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(
            path, fileLength - 1, XlsxSheetNameReader.MaxWorkbookPartBytes, out var sheetNames);

        // Assert: ZipArchive を開く（中央ディレクトリを読む）前に「判定不能」とする
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSheetNames_WhenFileIsExactlyAtLimit_ShouldSucceed()
    {
        // Arrange: 上限ちょうどのファイルは従来どおり読める
        var path = CreateWorkbook("book.xlsx", "4月", "5月");
        var fileLength = new FileInfo(path).Length;

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(
            path, fileLength, XlsxSheetNameReader.MaxWorkbookPartBytes, out var sheetNames);

        // Assert
        succeeded.Should().BeTrue();
        sheetNames.Should().Equal("4月", "5月");
    }

    [Fact]
    public void TryReadSheetNames_WhenDeclaredSizeIsForged_ShouldStopReadingAtLimit()
    {
        // Arrange: 実寸は上限を大きく超えるが、ZIP の宣言は小さく偽った workbook.xml
        var path = CreateZipWithWorkbookXml("forged.xlsx", BuildWorkbookXml(sheetCount: 200));
        var actualLength = GetWorkbookPartLength(path);
        const long limit = 1024;
        actualLength.Should().BeGreaterThan(limit * 4, "前提: 実寸が上限を十分に超えている");
        ForgeDeclaredUncompressedSize(path, forgedSize: 100);

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, limit, out var sheetNames);

        // Assert: 宣言を信じて読み始めても、上限を超えた時点で打ち切って「判定不能」とする
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSheetNames_WhenDeclaredSizeExceedsLimit_ShouldNotStartReading()
    {
        // Arrange: 実寸は上限内だが、ZIP の宣言する展開後サイズが上限を超えている。
        // 宣言の時点で断り、展開を始めない（読み込み側の打ち切りより手前の 1 段目の守り）
        var path = CreateZipWithWorkbookXml("declared_large.xlsx", BuildWorkbookXml(sheetCount: 3));
        const long limit = 1024;
        GetWorkbookPartLength(path).Should().BeLessThan(limit, "前提: 実寸は上限内");
        ForgeDeclaredUncompressedSize(path, forgedSize: (uint)limit + 1);

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, limit, out var sheetNames);

        // Assert
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSheetNames_WithHandmadeWorkbookPartWithinLimit_ShouldSucceed()
    {
        // Arrange: 上の偽装テストと同じ作り方で、上限内の正当な ZIP は読めること（対の表明）
        var path = CreateZipWithWorkbookXml("small.xlsx", BuildWorkbookXml(sheetCount: 3));

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, 1024, out var sheetNames);

        // Assert
        succeeded.Should().BeTrue();
        sheetNames.Should().Equal("シート1", "シート2", "シート3");
    }

    [Fact]
    public void TryReadSheetNames_WhenWorkbookPartHasDtd_ShouldReportUndetermined()
    {
        // Arrange: 内部サブセットの実体展開（billion laughs）は、バイト数の上限を素通りして
        // 小さなファイルから巨大な文字列を作れる。正当な workbook.xml は DTD を持たないため拒否する
        const string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<!DOCTYPE workbook [<!ENTITY a \"展開\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets>" +
            "<sheet name=\"&b;\" sheetId=\"1\"/>" +
            "</sheets></workbook>";
        var path = CreateZipWithWorkbookXml("dtd.xlsx", xml);

        // Act
        var succeeded = XlsxSheetNameReader.TryReadSheetNames(path, out var sheetNames);

        // Assert
        succeeded.Should().BeFalse();
        sheetNames.Should().BeEmpty();
    }

    #endregion
}
