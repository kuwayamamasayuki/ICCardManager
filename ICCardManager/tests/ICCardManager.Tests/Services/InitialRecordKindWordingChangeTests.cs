#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2371: 組織設定の繰越の文言を変えた後も、変更前に登録した導入行が導入行として扱われること。
/// </summary>
/// <remarks>
/// <para>
/// 導入行かどうかを摘要と<b>現在の</b>組織設定の文言の比較で判定していたため、文言を変えると変更前に登録した
/// 3 月繰越のカード（導入行「前年度より繰越」）へ紙の累計を再び加算し、導入行が利用実績にも数えられていた。
/// 判定を導入行の種別の列（<c>ledger.initial_record_kind</c>）へ移した。
/// </para>
/// <para>
/// 欠陥を突く側（文言を変えた後の旧文言の導入行）と、正当な動作を塞いでいない側（年度途中の繰越のカードは従来どおり加算する・
/// 種別を持たない通常の行は同じ摘要でも導入行にならない）を対で置く。DB は実物（TestDbContextFactory）を使い、
/// 種別が保存・読み戻しを経ても残ることも併せて確かめる。<see cref="SummaryGenerator"/> の静的設定を変えるので
/// <see cref="SummaryGeneratorCollection"/> に属させる。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class InitialRecordKindWordingChangeTests : IDisposable
{
    private const string CardIdm = "AAAA000000000001";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;
    private readonly ReportDataBuilder _builder;

    public InitialRecordKindWordingChangeTests()
    {
        SummaryGenerator.ResetToDefaults();
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());

        _ledgerRepository = new LedgerRepository(_dbContext);
        _cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _builder = new ReportDataBuilder(_cardRepository, _ledgerRepository);
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>組織設定の繰越の文言（前年度より繰越・○月から繰越）を変える</summary>
    private static void ChangeCarryoverWording()
    {
        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromPreviousYear = "前年より持越";
        options.SummaryText.MidYearCarryoverFormat = "{0}月分より持越";
        options.SummaryText.MidYearCarryoverPattern = @"^(1[0-2]|[1-9])月分より持越$";
        SummaryGenerator.Configure(options);
    }

    private async Task InsertCardAsync(int paperIncome, int paperExpense, int fiscalYear)
    {
        await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = CardIdm,
            CardType = "はやかけん",
            CardNumber = "A-001",
            CarryoverIncomeTotal = paperIncome,
            CarryoverExpenseTotal = paperExpense,
            CarryoverFiscalYear = fiscalYear,
        });
    }

    private Task<int> InsertLedgerAsync(
        DateTime date, string summary, int income, int expense, int balance, InitialRecordKind? kind = null)
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardIdm,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            InitialRecordKind = kind,
        });

    /// <summary>
    /// 欠陥を突く側: 3 月繰越で登録したカード（#2326 より前に紙の累計も保存済み）は、組織設定の文言を変えた後も
    /// 紙の累計を年度累計に加算しない。
    /// </summary>
    [Fact]
    public async Task 文言を変えた後も変更前に登録した前年度より繰越の導入行で紙の累計を加算しないこと()
    {
        await InsertCardAsync(paperIncome: 120000, paperExpense: 95000, fiscalYear: 2025);
        // 既定の文言で登録した導入行（カード登録が種別を書く）
        await InsertLedgerAsync(new DateTime(2025, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            6000, 0, 6000, InitialRecordKind.FiscalYearStartCarryover);
        await InsertLedgerAsync(new DateTime(2025, 5, 10), "鉄道（天神～博多）", 0, 210, 5790);
        await InsertLedgerAsync(new DateTime(2025, 6, 3), "鉄道（博多～天神）", 0, 300, 5490);

        ChangeCarryoverWording();
        var result = await _builder.BuildAsync(CardIdm, 2025, 6);

        result.Should().NotBeNull();
        result!.CumulativeTotal.Income.Should().Be(6000, "旧年度の紙の累計受入 120,000 を加算しない（導入行の受入 6,000 だけ）");
        result.CumulativeTotal.Expense.Should().Be(510, "旧年度の紙の累計払出 95,000 を加算しない");
        result.CumulativeTotal.Balance.Should().Be(5490);
    }

    /// <summary>
    /// 欠陥を突く側: 4 月の帳票も、導入行が年度の始まりを担うので前年度繰越の行を合成しない（#2418 の判定が文言に依らないこと）。
    /// </summary>
    [Fact]
    public async Task 文言を変えた後も4月の帳票で前年度繰越を合成しないこと()
    {
        await InsertCardAsync(paperIncome: 0, paperExpense: 0, fiscalYear: 2025);
        // 3 月中の貸出中ではない手入力の行（導入行より前の日付）があっても、導入行が年度の始まりを担う
        await InsertLedgerAsync(new DateTime(2025, 3, 25), "鉄道（天神～博多）", 0, 210, 5790);
        await InsertLedgerAsync(new DateTime(2025, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            6000, 0, 6000, InitialRecordKind.FiscalYearStartCarryover);

        ChangeCarryoverWording();
        var result = await _builder.BuildAsync(CardIdm, 2025, 4);

        result.Should().NotBeNull();
        result!.Carryover.Should().BeNull("前年度より繰越は DB の導入行が担うので帳票は合成しない");
        result.MonthlyTotal.Income.Should().Be(6000, "前年度繰越を 4 月計の受入へ二重に加算しない");
    }

    /// <summary>
    /// 正当な動作を塞いでいない側: 年度途中の繰越（「○月から繰越」）のカードは、文言を変えた後も従来どおり紙の累計を加算し、
    /// 導入行の受入（#1219 より前の受入＝残高）は累計から除く。
    /// </summary>
    [Fact]
    public async Task 年度途中の繰越のカードは文言を変えた後も従来どおり紙の累計を加算すること()
    {
        await InsertCardAsync(paperIncome: 10000, paperExpense: 3200, fiscalYear: 2025);
        // #1219 より前の形（受入＝残高）の「7月から繰越」
        await InsertLedgerAsync(new DateTime(2025, 8, 1), SummaryGenerator.GetMidYearCarryoverSummary(7),
            7000, 0, 7000, InitialRecordKind.MidYearCarryover);
        await InsertLedgerAsync(new DateTime(2025, 9, 28), "鉄道（天神～博多）", 0, 200, 6800);
        await InsertLedgerAsync(new DateTime(2025, 10, 3), "鉄道（博多～天神）", 0, 210, 6590);

        ChangeCarryoverWording();
        var result = await _builder.BuildAsync(CardIdm, 2025, 10);

        result.Should().NotBeNull();
        result!.CumulativeTotal.Income.Should().Be(10000, "紙の累計受入を加算し、旧文言の繰越行の受入 7,000 は除く");
        result.CumulativeTotal.Expense.Should().Be(3200 + 410);
    }

    /// <summary>
    /// 正当な動作を塞いでいない側: 種別を持たない行（履歴の「追加」で入れた行）は、摘要が「前年度より繰越」で年度の先頭にあっても
    /// 導入行ではない。年度途中の繰越のカードの紙の累計は加算され続ける。
    /// </summary>
    [Fact]
    public async Task 種別を持たない通常の行は摘要が導入行の文言でも導入行と判定しないこと()
    {
        await InsertCardAsync(paperIncome: 10000, paperExpense: 3200, fiscalYear: 2025);
        await InsertLedgerAsync(new DateTime(2025, 4, 1), SummaryGenerator.GetCarryoverFromPreviousYearSummary(),
            0, 0, 7000);
        await InsertLedgerAsync(new DateTime(2025, 5, 10), "鉄道（天神～博多）", 0, 210, 6790);

        var result = await _builder.BuildAsync(CardIdm, 2025, 5);

        result.Should().NotBeNull();
        result!.CumulativeTotal.Income.Should().Be(10000, "導入行ではないので紙の累計を加算する");
        (await _ledgerRepository.GetPurchaseDateAsync(CardIdm)).Should().BeNull("導入行が無い");
    }

    /// <summary>
    /// 欠陥を突く側: 帳票の導入月（導入前の月をスキップ）・利用実績の集計・返却時の記録の下限（明細の無い行の最新日）も、
    /// 文言を変えた後に変更前の導入行を導入行として扱うこと。
    /// </summary>
    [Fact]
    public async Task 文言を変えた後も導入行が導入日と利用実績と返却時の下限から正しく扱われること()
    {
        await InsertCardAsync(paperIncome: 0, paperExpense: 0, fiscalYear: 2025);
        await InsertLedgerAsync(new DateTime(2025, 8, 1), SummaryGenerator.GetMidYearCarryoverSummary(7),
            0, 0, 7000, InitialRecordKind.MidYearCarryover);

        ChangeCarryoverWording();

        (await _ledgerRepository.GetPurchaseDateAsync(CardIdm)).Should().Be(new DateTime(2025, 8, 1));
        (await _ledgerRepository.GetUsageStatsByCardAsync(new DateTime(2025, 4, 1), new DateTime(2026, 3, 31)))
            .Should().BeEmpty("導入行は利用実績ではない");
        (await _ledgerRepository.GetAllLastUsageDatesAsync()).Should().NotContainKey(CardIdm);
        (await _ledgerRepository.GetLatestLedgerDateWithoutDetailsAsync(CardIdm))
            .Should().BeNull("導入行は明細を持たないが返却時の下限の対象外");
    }

    /// <summary>
    /// 種別は登録時に決まり、行編集（UpdateAsync）で摘要・日付を変えても変わらない。
    /// </summary>
    [Fact]
    public async Task 行の更新で摘要と日付を変えても種別は変わらないこと()
    {
        await InsertCardAsync(paperIncome: 0, paperExpense: 0, fiscalYear: 2025);
        var id = await InsertLedgerAsync(new DateTime(2025, 4, 1), "新規購入", 5000, 0, 5000, InitialRecordKind.NewPurchase);

        var loaded = (await _ledgerRepository.GetByIdAsync(id))!;
        loaded.InitialRecordKind.Should().Be(InitialRecordKind.NewPurchase, "保存・読み戻しで種別が残る");
        loaded.Summary = "購入";
        loaded.Date = new DateTime(2025, 4, 3);
        loaded.InitialRecordKind = null;
        (await _ledgerRepository.UpdateAsync(loaded, null)).Should().BeTrue();

        var reloaded = (await _ledgerRepository.GetByIdAsync(id))!;
        reloaded.Summary.Should().Be("購入");
        reloaded.InitialRecordKind.Should().Be(InitialRecordKind.NewPurchase, "UpdateAsync は種別を SET しない");
    }

    /// <summary>
    /// 同じ年度の先頭の日に種別を持つ導入行があるかどうかは、摘要に依らない（PaperCarryoverPolicy の単体）。
    /// </summary>
    [Fact]
    public void HasFiscalYearStartCarryoverRecord_種別だけで決まること()
    {
        var summary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
        var withKind = new Ledger { Date = new DateTime(2025, 4, 1), Summary = "前年より持越", InitialRecordKind = InitialRecordKind.FiscalYearStartCarryover };
        var withoutKind = new Ledger { Date = new DateTime(2025, 4, 1), Summary = summary };

        PaperCarryoverPolicy.HasFiscalYearStartCarryoverRecord(new[] { withKind }).Should().BeTrue();
        PaperCarryoverPolicy.HasFiscalYearStartCarryoverRecord(new[] { withoutKind }).Should().BeFalse();
        PaperCarryoverPolicy.GetChainRestartDates(
                new DateTime(2025, 4, 1),
                new[]
                {
                    new Ledger { Date = new DateTime(2025, 6, 1), Summary = SummaryGenerator.GetMidYearCarryoverSummary(5) },
                    new Ledger { Date = new DateTime(2025, 7, 1), Summary = "6月分より持越", InitialRecordKind = InitialRecordKind.MidYearCarryover },
                },
                Enumerable.Empty<Ledger>())
            .Should().Equal(new DateTime(2025, 7, 1));
    }
}
