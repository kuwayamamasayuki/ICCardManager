using System;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1820: 帳票ファイル名の<b>消費側</b>が組織設定 <c>ReportLayout.FileNameFormat</c> に
/// 追従することを検証する（<see cref="ReportFileNameFactoryTests"/> は生成側の検証）。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は 3 経路（<c>ReportService</c> / <c>ReportExportStatusService</c> / <c>ReportViewModel</c>）
/// のすべてが <c>static ReportService.GetFiscalYearFileName</c> を呼んでおり、そこが
/// <c>new OrganizationOptions()</c> の既定値をハードコードしていたため設定は無視されていた。
/// </para>
/// <para>
/// <b>必ず既定と異なる書式へ設定してから呼ぶ。</b> 既定のままだとハードコードと偶然一致し、
/// 修正前のコードでも緑になる（Issue #1818 で確立した作法）。
/// </para>
/// </remarks>
public class ReportFileNameConfigurationConsumerTests : IDisposable
{
    private const string CustomFormat = "出納簿【{0}】{1}（{2}年度）.xlsx";
    private const string CardType = "はやかけん";
    private const string CardNumber = "H-001";
    private const string CardIdm = "0123456789ABCDEF";

    private readonly string _testDirectory;

    public ReportFileNameConfigurationConsumerTests()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(), $"ReportFileNameConsumerTests_{Guid.NewGuid():N}");
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
            // 後始末の失敗はテスト結果に影響させない
        }
    }

    private static IOptions<OrganizationOptions> CustomOptions()
    {
        var options = new OrganizationOptions();
        options.ReportLayout.FileNameFormat = CustomFormat;
        return Options.Create(options);
    }

    #region ReportService

    private static ReportService CreateReportService(IOptions<OrganizationOptions> orgOptions)
    {
        var cardRepository = new Mock<ICardRepository>();
        var ledgerRepository = new Mock<ILedgerRepository>();
        var settingsRepository = new Mock<ISettingsRepository>();
        settingsRepository.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        return new ReportService(
            cardRepository.Object,
            ledgerRepository.Object,
            settingsRepository.Object,
            new ReportDataBuilder(cardRepository.Object, ledgerRepository.Object),
            orgOptions);
    }

    [Fact]
    public void ReportService_注入された組織設定のファイル名書式に追従する()
    {
        var service = CreateReportService(CustomOptions());

        var fileName = service.GetFiscalYearFileName(CardType, CardNumber, 2024);

        fileName.Should().Be("出納簿【はやかけん】H-001（2024年度）.xlsx");
    }

    [Fact]
    public void ReportService_組織設定を変更したとき既定の書式では生成されない()
    {
        // 対のテスト（新旧どちらでも通る広すぎる実装を検出する）
        var service = CreateReportService(CustomOptions());

        var fileName = service.GetFiscalYearFileName(CardType, CardNumber, 2024);

        fileName.Should().NotBe("物品出納簿_はやかけん_H-001_2024年度.xlsx");
    }

    [Fact]
    public void ReportService_組織設定未指定なら既定の書式で生成される()
    {
        var service = CreateReportService(null);

        var fileName = service.GetFiscalYearFileName(CardType, CardNumber, 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H-001_2024年度.xlsx");
    }

    #endregion

    #region ReportExportStatusService

    /// <summary>
    /// カスタム書式で名付けた年度ファイルを作る（＝本番が出力するファイル名と同じ形）
    /// </summary>
    private string CreateFiscalYearFileWithCustomFormat(int fiscalYear, params int[] sheetMonths)
    {
        var fileName = string.Format(CustomFormat, CardType, CardNumber, fiscalYear);
        var path = Path.Combine(_testDirectory, fileName);

        using (var workbook = new XLWorkbook())
        {
            foreach (var sheetMonth in sheetMonths)
            {
                var sheet = workbook.Worksheets.Add(ReportService.GetMonthSheetName(sheetMonth));
                sheet.Cell(1, 1).Value = "ダミー";
            }
            workbook.SaveAs(path);
        }

        return path;
    }

    private static ReportExportTarget CreateTarget() => new()
    {
        CardIdm = CardIdm,
        CardType = CardType,
        CardNumber = CardNumber,
    };

    [Fact]
    public void ReportExportStatusService_カスタム書式で出力済みのファイルを出力済みと判定する()
    {
        // Arrange: 本番（ReportService）がカスタム書式で出力したのと同じファイル名で作る
        var path = CreateFiscalYearFileWithCustomFormat(2026, 4, 5, 6);
        var service = new ReportExportStatusService(new ReportFileNameFactory(CustomOptions()));

        // Act
        var statuses = service.GetStatuses(new[] { CreateTarget() }, _testDirectory, 2026, 6);

        // Assert: 修正前は既定書式のファイル名を探すため NotExported（出力済みなのに未出力）になった
        var status = statuses.Single();
        status.State.Should().Be(ReportExportState.Exported);
        status.FilePath.Should().Be(path);
    }

    [Fact]
    public void ReportExportStatusService_設定に追従しない既定書式のファイルは出力済みと見なさない()
    {
        // 対のテスト: 旧書式のファイルを拾ってしまう広すぎる実装を検出する
        var legacyFileName = string.Format(
            "物品出納簿_{0}_{1}_{2}年度.xlsx", CardType, CardNumber, 2026);
        using (var workbook = new XLWorkbook())
        {
            workbook.Worksheets.Add(ReportService.GetMonthSheetName(6)).Cell(1, 1).Value = "ダミー";
            workbook.SaveAs(Path.Combine(_testDirectory, legacyFileName));
        }

        var service = new ReportExportStatusService(new ReportFileNameFactory(CustomOptions()));

        var statuses = service.GetStatuses(new[] { CreateTarget() }, _testDirectory, 2026, 6);

        statuses.Single().State.Should().Be(ReportExportState.NotExported);
    }

    [Fact]
    public void ReportExportStatusService_ファイル名生成が未注入なら構築時に失敗する()
    {
        // 既定値で組み立てるフォールバックを置くと DI の配線漏れが
        // 「設定した書式が静かに無視される」形で潜在化するため、明示的に失敗させる
        var act = () => new ReportExportStatusService(null);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
