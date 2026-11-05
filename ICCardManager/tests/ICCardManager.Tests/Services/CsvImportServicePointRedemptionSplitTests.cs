#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2384: 利用履歴詳細（明細）CSV の取り込みで、利用履歴 ID が空欄の行から利用履歴を新しく作るとき、
/// 同じ日の鉄道利用とポイント還元を別々の台帳の行にし、ポイント還元の行の<b>受入</b>に金額を計上すること
/// （07_テスト設計書 UT-016 No.59）を、CSV の取り込みの経路をつないだ形で表明する。
/// </summary>
/// <remarks>
/// <para>
/// 分割（<c>LendingHistoryAnalyzer.SplitAtChargeBoundaries</c>）と受入への計上
/// （<c>LedgerSplitService.CalculateGroupFinancials</c>）は部品ごとのテストが別々に固定しているだけで、
/// 取り込み側（<c>NewLedgerFromSegmentsBuilder</c>）がポイント還元を境界として渡さない・受入ではなく払出に入れる、
/// といった変更は部品のテストを緑のまま通る。ここでは実 <see cref="LedgerRepository"/> と実 DB を使い、
/// <b>DB に実際に書かれた台帳の行と明細</b>を読み返して表明する。
/// </para>
/// </remarks>
public class CsvImportServicePointRedemptionSplitTests : IDisposable
{
    private const string TestCardIdm = "0123456789ABCDEF";

    /// <summary>UTF-8 with BOM（<c>ReadCsvFileAsync</c> の文字コード判別を通すため）。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private const string Header =
        "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

    private static readonly DateTime Day = new DateTime(2024, 1, 15);

    private readonly string _testDirectory;
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CsvImportService _service;

    public CsvImportServicePointRedemptionSplitTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportPointRedemption_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _dbContext = TestDbContextFactory.Create();
        _ledgerRepository = new LedgerRepository(_dbContext);

        var cardRepositoryMock = new Mock<ICardRepository>();
        cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "001" });

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _service = new CsvImportService(
            cardRepositoryMock.Object,
            new Mock<IStaffRepository>().Object,
            _ledgerRepository,
            new Mock<IValidationService>().Object,
            _dbContext,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object,
            new OperationLogger(new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>()),
            NullLogger<CsvImportService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region ヘルパー

    /// <summary>台帳の外部キー（ic_card）を満たすカードを投入する。</summary>
    private async Task SeedCardAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        await command.ExecuteNonQueryAsync();
    }

    private string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(_testDirectory, $"details_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, Header + "\n" + string.Join("\n", lines) + "\n", CsvEncoding);
        return path;
    }

    /// <summary>その日の台帳の行を、明細込みで作成順（id の昇順）に DB から読み返す。</summary>
    private async Task<List<Ledger>> ReadLedgersOfDayAsync()
    {
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(TestCardIdm, Day, Day);
        var result = new List<Ledger>();
        foreach (var id in ledgers.Select(l => l.Id).OrderBy(id => id))
        {
            var ledger = await _ledgerRepository.GetByIdAsync(id);
            ledger.Should().NotBeNull();
            result.Add(ledger!);
        }

        return result;
    }

    #endregion

    /// <summary>
    /// 欠陥を突く側（UT-016 No.59）: 同じ日の鉄道利用＋ポイント還元の明細 CSV は、利用の行とポイント還元の行の
    /// 2 行になり、ポイント還元の行は受入に金額を計上し払出は 0 であること。
    /// </summary>
    /// <remarks>
    /// ポイント還元は 2 つの形で CSV に現れる。明示のフラグ（ポイント還元列 = 1、金額は正の入金額。現行の読み取りの形）と、
    /// フラグの無い暗黙のポイント還元（金額が負。<c>SummaryGenerator.IsImplicitPointRedemption</c>。
    /// Issue #942 以前に記録した明細を出力した CSV の形）。どちらも受入は金額の絶対値（240 円）になる。
    /// 利用の行の残額（740 円）とポイント還元の行の残額（980 円）が違うので、行の取り違えも検出できる。
    /// </remarks>
    [Theory]
    [InlineData("240", "1", true)]
    [InlineData("-240", "0", false)]
    public async Task 利用とポイント還元が混在する日_別々の台帳の行になりポイント還元の行の受入に計上されること(
        string pointAmount, string pointFlag, bool expectedFlagInDetail)
    {
        // Arrange: 08:00 博多→天神（260 円、残額 740）→ 12:00 ポイント還元 240 円（残額 980）
        await SeedCardAsync();
        var csvPath = WriteCsv(
            $",2024-01-15 08:00:00,{TestCardIdm},001,博多,天神,,260,740,0,0,0,",
            $",2024-01-15 12:00:00,{TestCardIdm},001,,,,{pointAmount},980,0,{pointFlag},0,");

        // Act
        var result = await _service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        result.ImportedCount.Should().Be(2);

        var ledgers = await ReadLedgersOfDayAsync();
        ledgers.Should().HaveCount(2, "ポイント還元は利用と同じ台帳の行にまとめず、別の行にする（UT-016 No.59）");

        var usage = ledgers[0];
        usage.Summary.Should().Be("鉄道（博多～天神）");
        usage.Income.Should().Be(0);
        usage.Expense.Should().Be(260);
        usage.Balance.Should().Be(740);
        usage.Details.Should().ContainSingle().Which.EntryStation.Should().Be("博多");

        var redemption = ledgers[1];
        redemption.Summary.Should().Be(SummaryGenerator.GetPointRedemptionSummary());
        redemption.Income.Should().Be(240, "ポイント還元は受入に計上する（Issue #1053）");
        redemption.Expense.Should().Be(0, "ポイント還元を払出に入れない");
        redemption.Balance.Should().Be(980);
        var redemptionDetail = redemption.Details.Should().ContainSingle().Subject;
        redemptionDetail.IsPointRedemption.Should().Be(expectedFlagInDetail, "明細はフラグを CSV のまま保存する");
        redemptionDetail.Balance.Should().Be(980);
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側（UT-016 No.60 の対）: 利用だけの日は、明細が複数でも 1 つの台帳の行のままであること。
    /// </summary>
    /// <remarks>
    /// これが無いと「明細ごとに別の行にする」実装でも上のテストは緑になる。往復の 2 件を 1 行にまとめ、
    /// 払出は 2 件の合計、残額は最後の明細の残額。
    /// </remarks>
    [Fact]
    public async Task 利用だけの日_明細が複数でも1つの台帳の行のままであること()
    {
        // Arrange
        await SeedCardAsync();
        var csvPath = WriteCsv(
            $",2024-01-15 08:00:00,{TestCardIdm},001,博多,天神,,260,740,0,0,0,",
            $",2024-01-15 18:00:00,{TestCardIdm},001,天神,博多,,260,480,0,0,0,");

        // Act
        var result = await _service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        result.ImportedCount.Should().Be(2);

        var ledger = (await ReadLedgersOfDayAsync()).Should().ContainSingle().Subject;
        ledger.Summary.Should().Be("鉄道（博多～天神 往復）");
        ledger.Income.Should().Be(0);
        ledger.Expense.Should().Be(520);
        ledger.Balance.Should().Be(480);
        ledger.Details.Should().HaveCount(2);
    }
}
