using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1956: ページ番号の列が組織設定（<c>TemplateMapping.PageNumberColumn</c>）に従うことを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は <c>SetHeaderInfo</c> だけが設定値を読み、<c>SetPageNumber</c> と
/// <c>GetLastPageNumberFromWorksheet</c> は列 12（L 列）を直書きしていた。
/// 既定値が 12 のため既定のままでは直書きと一致して緑になるので、
/// 本クラスのテストは必ず <b>既定と異なる列</b>を設定してから呼ぶ（規約 #1818）。
/// </para>
/// <para>
/// あわせて「既定列のままなら従来どおり動く」対の表明を置く。前者だけだと
/// ページ番号の継続を丸ごと止めた実装でも緑になるため。
/// </para>
/// </remarks>
public class ReportServicePageNumberColumnTests : IDisposable
{
    /// <summary>既定（12=L 列）と異なる列。ヘッダー行の範囲内（A〜L）で衝突しない K 列を選ぶ。</summary>
    private const int CustomPageNumberColumn = 11;

    /// <summary>本番の既定値。テスト側で意味を持たせるために名前を付ける。</summary>
    private const int DefaultPageNumberColumn = 12;

    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly List<string> _tempFiles = new();

    public ReportServicePageNumberColumnTests()
    {
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
    }

    public void Dispose()
    {
        foreach (var tempFile in _tempFiles)
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // 削除失敗は無視
            }
        }
    }

    #region ヘルパー

    private ReportService CreateService(int pageNumberColumn)
    {
        var options = new OrganizationOptions();
        options.TemplateMapping.PageNumberColumn = pageNumberColumn;

        var reportDataBuilder = new ReportDataBuilder(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object);

        return new ReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            reportDataBuilder,
            Options.Create(options));
    }

    /// <summary>指定した名前のヘッダー列だけを既定と異なる値にしたサービスを作る。</summary>
    private ReportService CreateServiceWithHeaderColumn(string settingName, int value)
    {
        var options = new OrganizationOptions();
        typeof(TemplateMappingOptions).GetProperty(settingName)!
            .SetValue(options.TemplateMapping, value);

        var reportDataBuilder = new ReportDataBuilder(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object);

        return new ReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            reportDataBuilder,
            Options.Create(options));
    }

    private string CreateTempFilePath()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ReportPageColTest_{Guid.NewGuid()}.xlsx");
        _tempFiles.Add(tempPath);
        return tempPath;
    }

    private static IcCard CreateCard(int startingPageNumber) => new()
    {
        CardIdm = "0102030405060708",
        CardType = "はやかけん",
        CardNumber = "001",
        StartingPageNumber = startingPageNumber,
    };

    private static Ledger CreateLedger(int id, string cardIdm, DateTime date, string summary, int expense, int balance) => new()
    {
        Id = id,
        CardIdm = cardIdm,
        Date = date,
        Summary = summary,
        Income = 0,
        Expense = expense,
        Balance = balance,
    };

    /// <summary>4月＋5月のデータをモックへ仕込む。<paramref name="aprilRowCount"/> 行分の4月データを作る。</summary>
    private (string cardIdm, int year) ArrangeTwoMonths(int startingPageNumber, int aprilRowCount)
    {
        const string cardIdm = "0102030405060708";
        const int year = 2024;
        var card = CreateCard(startingPageNumber);

        var aprilLedgers = Enumerable.Range(1, aprilRowCount)
            .Select(i => CreateLedger(
                i, cardIdm, new DateTime(year, 4, Math.Min(i, 28)),
                $"鉄道（駅{i}～駅{i + 1}）", 100, 10000 - i * 100))
            .ToList();

        var mayLedgers = new List<Ledger>
        {
            CreateLedger(100, cardIdm, new DateTime(year, 5, 10), "鉄道（博多～天神）", 200, 8500),
        };

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, true)).ReturnsAsync(card);
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(cardIdm, year, 4)).ReturnsAsync(aprilLedgers);
        _ledgerRepositoryMock.Setup(r => r.GetByMonthAsync(cardIdm, year, 5)).ReturnsAsync(mayLedgers);
        _ledgerRepositoryMock.Setup(r => r.GetCarryoverBalanceAsync(cardIdm, year - 1)).ReturnsAsync(10000);
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(cardIdm, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(aprilLedgers.Concat(mayLedgers).ToList());

        return (cardIdm, year);
    }

    #endregion

    #region 欠陥を突く側（設定した列が効くこと）

    /// <summary>
    /// Issue #1956: 既定と異なる列を設定した場合でも、翌月のページ番号が前月から継続すること。
    /// </summary>
    /// <remarks>
    /// 修正前は <c>GetLastPageNumberFromWorksheet</c> が空の L2 を読んで 0 を返し、
    /// <c>StartingPageNumber</c> へフォールバックするため 5月が 5（＝振り出し）になっていた。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_カスタム列_翌月のページ番号が前月から継続すること()
    {
        // Arrange
        var service = CreateService(CustomPageNumberColumn);
        var (cardIdm, year) = ArrangeTwoMonths(startingPageNumber: 5, aprilRowCount: 3);
        var outputPath = CreateTempFilePath();

        // Act
        await service.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);
        var result = await service.CreateMonthlyReportAsync(cardIdm, year, 5, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheet("4月").Cell(2, CustomPageNumberColumn).GetValue<int>()
            .Should().Be(5, "4月は StartingPageNumber=5 のまま設定列へ書かれる");
        workbook.Worksheet("5月").Cell(2, CustomPageNumberColumn).GetValue<int>()
            .Should().Be(6, "5月は4月の最終ページ(5)+1=6 から開始する（振り出しに戻らない）");
    }

    /// <summary>
    /// Issue #1956: 継続ページ（改ページ後のヘッダー）のページ番号も設定列へ書かれること。
    /// </summary>
    /// <remarks>
    /// <c>SetPageNumber</c> の列直書きを突く。修正前は 2 ページ目の頁が L 列へ書かれ、
    /// 設定列（K）にはテンプレートのコピー由来で 1 ページ目と同じ番号が残っていた。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_カスタム列_継続ページのページ番号も設定列へ書かれること()
    {
        // Arrange: 1ページ12行を超える件数で改ページを発生させる
        var service = CreateService(CustomPageNumberColumn);
        var (cardIdm, year) = ArrangeTwoMonths(startingPageNumber: 5, aprilRowCount: 13);
        var outputPath = CreateTempFilePath();

        // Act
        var result = await service.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        var aprilSheet = workbook.Worksheet("4月");
        aprilSheet.PageSetup.RowBreaks.Count.Should().BeGreaterThan(0, "4月は複数ページになる");

        // 2ページ目のヘッダー開始行 = 1ページ目の全 22 行の直後（23 行目）→ ヘッダー情報は 24 行目
        var secondPageHeaderRow = 23 + 1;
        aprilSheet.Cell(secondPageHeaderRow, CustomPageNumberColumn).GetValue<int>()
            .Should().Be(6, "2ページ目の頁は設定列へ 1ページ目+1 で書かれる");
        aprilSheet.Cell(secondPageHeaderRow, DefaultPageNumberColumn).IsEmpty()
            .Should().BeTrue("既定列（L）へは書かれない");
    }

    /// <summary>
    /// Issue #1956: 既定と異なる列を設定した場合、既定列（L2）は使われないこと。
    /// </summary>
    [Fact]
    public async Task CreateMonthlyReportAsync_カスタム列_既定列にはページ番号を書かないこと()
    {
        // Arrange
        var service = CreateService(CustomPageNumberColumn);
        var (cardIdm, year) = ArrangeTwoMonths(startingPageNumber: 5, aprilRowCount: 3);
        var outputPath = CreateTempFilePath();

        // Act
        await service.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);

        // Assert
        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheet("4月").Cell(2, DefaultPageNumberColumn).IsEmpty()
            .Should().BeTrue("設定を変えたら既定列へは書かない");
    }

    #endregion

    #region 対の表明（既定の挙動を壊していないこと）

    /// <summary>
    /// Issue #1956: 既定列（12=L）のままなら従来どおり L2 で継続すること。
    /// </summary>
    /// <remarks>
    /// この対の表明が無いと、ページ番号の継続そのものを止めた実装でも
    /// 上の「カスタム列」テストだけが赤にならず通ってしまう。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_既定列_従来どおりL2で継続すること()
    {
        // Arrange
        var service = CreateService(DefaultPageNumberColumn);
        var (cardIdm, year) = ArrangeTwoMonths(startingPageNumber: 5, aprilRowCount: 3);
        var outputPath = CreateTempFilePath();

        // Act
        await service.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);
        var result = await service.CreateMonthlyReportAsync(cardIdm, year, 5, outputPath);

        // Assert
        result.Success.Should().BeTrue();

        using var workbook = new XLWorkbook(outputPath);
        workbook.Worksheet("4月").Cell(2, DefaultPageNumberColumn).GetValue<int>().Should().Be(5);
        workbook.Worksheet("5月").Cell(2, DefaultPageNumberColumn).GetValue<int>().Should().Be(6);
    }

    #endregion

    #region ページ番号計算メソッドの単体テスト

    /// <summary>
    /// Issue #1956: <c>GetLastPageNumberFromWorksheet</c> は指定された列の 2 行目を読むこと。
    /// </summary>
    [Fact]
    public void GetLastPageNumberFromWorksheet_指定列の値を読むこと()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("4月");
        sheet.Cell(2, CustomPageNumberColumn).Value = 7;

        ReportService.GetLastPageNumberFromWorksheet(sheet, CustomPageNumberColumn)
            .Should().Be(7);
    }

    /// <summary>
    /// Issue #1956: 指定列が空なら、他の列に値があっても 0（無効なシート）を返すこと。
    /// </summary>
    /// <remarks>
    /// 「指定列を読んでいる」ことを表明する対の検査。片側だけだと
    /// 「どこかの列に値があれば読む」広すぎる実装でも緑になる。
    /// </remarks>
    [Fact]
    public void GetLastPageNumberFromWorksheet_指定列が空なら他列に値があっても0を返すこと()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("4月");
        sheet.Cell(2, DefaultPageNumberColumn).Value = 7;

        ReportService.GetLastPageNumberFromWorksheet(sheet, CustomPageNumberColumn)
            .Should().Be(0);
    }

    /// <summary>
    /// Issue #1956: <c>GetStartingPageNumberForMonth</c> にも列が貫通していること。
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_指定列の前月シートから継続すること()
    {
        using var workbook = new XLWorkbook();
        var april = workbook.AddWorksheet("4月");
        april.Cell(2, CustomPageNumberColumn).Value = 5;

        ReportService.GetStartingPageNumberForMonth(workbook, CreateCard(1), 5, CustomPageNumberColumn)
            .Should().Be(6);
    }

    /// <summary>
    /// Issue #1956: 指定列が空の前月シートはスキップされ、StartingPageNumber へフォールバックすること。
    /// </summary>
    [Fact]
    public void GetStartingPageNumberForMonth_指定列が空ならStartingPageNumberへフォールバックすること()
    {
        using var workbook = new XLWorkbook();
        var april = workbook.AddWorksheet("4月");
        april.Cell(2, DefaultPageNumberColumn).Value = 5;   // 別の列にだけ値がある

        ReportService.GetStartingPageNumberForMonth(workbook, CreateCard(3), 5, CustomPageNumberColumn)
            .Should().Be(3);
    }

    #endregion

    #region 帳票幅の外側は帳票を作る前に弾く（設定が効く単位の完結性）

    /// <summary>
    /// Issue #1956: 帳票幅（A〜L = 1〜12）の外側を設定したら、帳票を作らずに理由を返すこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 帳票幅はテンプレートに罫線・結合セル・印刷範囲と一体で埋め込まれており設定では変えられない。
    /// 範囲外の列は<b>書き込みは成功するが印刷範囲の外</b>なので、そのまま作ると
    /// 「頁の欄が空のまま帳票が出来上がる」形で静かに壊れる。
    /// </para>
    /// <para>
    /// 既定値へ倒さないのは、倒すと帳票の中身が管理者の意図と違う場所に出るため
    /// （#1812「定義域外の入力を黙って別の値に丸めない」）。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_帳票幅の外側の列_帳票を作らず理由を返すこと()
    {
        // Arrange
        const int farColumn = 15;  // O 列（帳票幅 A〜L の外側）
        var service = CreateService(farColumn);
        var (cardIdm, year) = ArrangeTwoMonths(startingPageNumber: 5, aprilRowCount: 3);
        var outputPath = CreateTempFilePath();

        // Act
        var result = await service.CreateMonthlyReportAsync(cardIdm, year, 4, outputPath);

        // Assert
        result.Success.Should().BeFalse("印刷されない列に書いた帳票を作らない");
        File.Exists(outputPath).Should().BeFalse("帳票ファイルそのものを作らない");
        result.DetailedErrorMessage.Should().Contain("PageNumberColumn=15", "どの設定が問題かを名指しする");
        result.DetailedErrorMessage.Should().Contain("印刷されません", "なぜ問題かを述べる");
        result.DetailedErrorMessage.Should().EndWith("修正してください。", "行動指示で終わる");
    }

    /// <summary>
    /// Issue #1956: 検証は 5 列すべてに掛かること（ページ番号列だけを見ていない）。
    /// </summary>
    /// <remarks>
    /// ページ番号列だけを検証すると「列だけ可変で他が固定」という半端な状態が
    /// ヘッダー行の中に残る（#1820「設定が効く範囲は、その設定だけで完結する単位で切る」）。
    /// </remarks>
    [Theory]
    [InlineData(nameof(TemplateMappingOptions.ClassificationColumn))]
    [InlineData(nameof(TemplateMappingOptions.CardTypeColumn))]
    [InlineData(nameof(TemplateMappingOptions.CardNumberColumn))]
    [InlineData(nameof(TemplateMappingOptions.UnitColumn))]
    [InlineData(nameof(TemplateMappingOptions.PageNumberColumn))]
    public void ValidateHeaderColumns_どのヘッダー列でも帳票幅の外側を検出すること(string settingName)
    {
        var service = CreateServiceWithHeaderColumn(settingName, 13);

        service.ValidateHeaderColumns().Should().Contain($"{settingName}=13");
    }

    /// <summary>
    /// Issue #1956: 帳票幅の内側なら通すこと（対の表明）。
    /// </summary>
    /// <remarks>
    /// 上の検出テストだけだと、どんな値でも弾く実装でも緑になる。
    /// 境界（1 と 12）を含めて通ることを表明する。
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(12)]
    public void ValidateHeaderColumns_帳票幅の内側は通すこと(int column)
    {
        var service = CreateService(column);

        service.ValidateHeaderColumns().Should().BeNull();
    }

    /// <summary>
    /// Issue #1956: 0 以下も帳票幅の外側として検出すること（下側の境界）。
    /// </summary>
    [Fact]
    public void ValidateHeaderColumns_0以下も検出すること()
    {
        var service = CreateService(0);

        service.ValidateHeaderColumns().Should().Contain("PageNumberColumn=0");
    }

    #endregion
}
