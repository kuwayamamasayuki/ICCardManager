using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ClosedXML.Excel;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2042: どの失敗が「全カードに共通する原因」かをサービス側で決めていることを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 一括作成の中断は <c>ReportGenerationResult.IsCommonFailure</c> で判断する。判断の材料を作るのは
/// <see cref="ReportService.CreateMonthlyReportAsync"/> 側なので、<b>結果に実際にフラグが立つこと</b>を
/// ここで固定する。<c>ReportViewModelBulkCreationTests</c> はサービスをモックで差し替えるため、
/// このテストが無いと <c>CommonFailureResult</c> を <c>FailureResult</c> へ戻す変更が全件緑のまま通る
/// （コードレビューで検出）。
/// </para>
/// <para>
/// 対の表明として「カード固有の失敗には立たないこと」を必ず併置する。前者だけだと、すべての失敗を
/// 共通原因とみなす実装（1 枚目の失敗で必ず中断する）でも緑になる。
/// </para>
/// </remarks>
public class ReportServiceCommonFailureTests : IDisposable
{
    private const string CardIdm = "0102030405060708";
    private const string UnknownCardIdm = "FFFFFFFFFFFFFFFF";
    private const int Year = 2024;
    private const int Month = 10;

    /// <summary>帳票幅（A〜L = 1〜12）の外。`ValidateHeaderColumns` が弾く値。</summary>
    private const int OutOfRangeColumn = 20;

    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly string _directory;

    public ReportServiceCommonFailureTests()
    {
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(CardIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = CardIdm, CardType = "はやかけん", CardNumber = "001" });
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(UnknownCardIdm, true))
            .ReturnsAsync((IcCard)null);

        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ledger>());

        _directory = Path.Combine(Path.GetTempPath(), $"ReportCommonFailure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない
        }
    }

    #region ヘルパー

    private ReportDataBuilder CreateDataBuilder()
        => new(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object);

    /// <summary>ヘッダー列番号を指定した組織設定でサービスを作る（既定のままなら検証を通る）。</summary>
    private ReportService CreateService(int? pageNumberColumn = null)
    {
        var options = new OrganizationOptions();
        if (pageNumberColumn.HasValue)
        {
            options.TemplateMapping.PageNumberColumn = pageNumberColumn.Value;
        }

        return new ReportService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            CreateDataBuilder(),
            NullLogger<ReportService>.Instance,
            Options.Create(options));
    }

    private SeamReportService CreateSeamService()
        => new(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            CreateDataBuilder(),
            NullLogger<ReportService>.Instance);

    private string OutputPath(string cardNumber = "001")
        => Path.Combine(_directory, $"物品出納簿_はやかけん_{cardNumber}_2024年度.xlsx");

    /// <summary>保存の直前で任意の例外を投げられるようにした派生クラス（Issue #2040 と同じ継ぎ目）。</summary>
    private sealed class SeamReportService : ReportService
    {
        public SeamReportService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            ISettingsRepository settingsRepository,
            IReportDataBuilder reportDataBuilder,
            ILogger<ReportService> logger)
            : base(cardRepository, ledgerRepository, settingsRepository, reportDataBuilder, logger)
        {
        }

        public Func<Exception>? WriteFailure { get; set; }

        internal override void WriteWorkbookTo(XLWorkbook workbook, string path)
        {
            if (WriteFailure != null)
            {
                throw WriteFailure();
            }

            base.WriteWorkbookTo(workbook, path);
        }
    }

    #endregion

    #region 単票作成の分類

    /// <summary>
    /// 欠陥を突く側: 組織設定のヘッダー列番号の誤りは、全カード共通の失敗として返すこと
    /// </summary>
    /// <remarks>
    /// この文言は「テンプレート」を含まないため、文言の部分一致で中断を判断していた実装では
    /// 一括作成が中断されず、同じ失敗が選択枚数だけ並んでいた。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_ヘッダー列の設定誤りは共通原因の失敗として返すこと()
    {
        var service = CreateService(OutOfRangeColumn);

        var result = await service.CreateMonthlyReportAsync(CardIdm, Year, Month, OutputPath());

        result.Success.Should().BeFalse();
        result.IsCommonFailure.Should().BeTrue("組織設定は全カードで共有するため、続けても同じ結果になる");
        result.ErrorMessage.Should().NotContain("テンプレート", "文言照合に依存していないことを表明する");
    }

    /// <summary>
    /// 対の表明: カード固有の失敗（未登録のカード）は共通原因にしないこと
    /// </summary>
    /// <remarks>
    /// これが無いと、すべての失敗に <c>IsCommonFailure</c> を立てた実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_未登録カードは共通原因の失敗にしないこと()
    {
        var service = CreateService();

        var result = await service.CreateMonthlyReportAsync(UnknownCardIdm, Year, Month, OutputPath());

        result.Success.Should().BeFalse();
        result.IsCommonFailure.Should().BeFalse("そのカードだけの問題なので、残りのカードは作成できる");
    }

    /// <summary>
    /// 欠陥を突く側: 出力先フォルダが見つからない失敗は、全カード共通の失敗として返すこと
    /// </summary>
    /// <remarks>
    /// 出力先フォルダは一括作成の全カードで共有する（本番経路の一括作成はループ前にフォルダを作らない）。
    /// 共有フォルダーが切断された場面では、カード固有として扱うと同じメッセージが選択枚数だけ並ぶ。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_出力先フォルダが見つからない失敗は共通原因として返すこと()
    {
        var service = CreateSeamService();
        service.WriteFailure = () => new DirectoryNotFoundException("出力先が見つかりません");

        var result = await service.CreateMonthlyReportAsync(CardIdm, Year, Month, OutputPath());

        result.Success.Should().BeFalse();
        result.IsCommonFailure.Should().BeTrue();
        result.DetailedErrorMessage.Should().Contain("出力先フォルダが見つかりません");
    }

    /// <summary>
    /// 対の表明: ファイルが開かれている等の入出力エラーは共通原因にしないこと
    /// </summary>
    /// <remarks>
    /// 年度ファイルはカードごとに別なので、1 枚だけ Excel で開かれている形が実在する。
    /// 共通原因にすると、そのカード以降の帳票が作られなくなる。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_入出力エラーは共通原因の失敗にしないこと()
    {
        var service = CreateSeamService();
        service.WriteFailure = () => new IOException("ファイルが使用中です");

        var result = await service.CreateMonthlyReportAsync(CardIdm, Year, Month, OutputPath());

        result.Success.Should().BeFalse();
        result.IsCommonFailure.Should().BeFalse();
    }

    #endregion
}
