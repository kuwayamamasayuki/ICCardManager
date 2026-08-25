using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Charting;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// AdminDashboardService の単体テスト（Issue #1692）
/// </summary>
/// <remarks>
/// 管理者ダッシュボードの集計。しきい値の境界・貸出中レコードの重複・帳票状況の
/// 判定不能の扱い・職員名のフォールバックを固定する。
/// SQLite は同一接続での並列クエリを許さない（Issue #1452）ため、リポジトリ呼び出しが
/// 直列であることも検証する。
/// </remarks>
public class AdminDashboardServiceTests
{
    private const string CardA = "AAAA000000000001";
    private const string CardB = "BBBB000000000002";
    private const string StaffA = "STAFF00000000001";
    private const string StaffB = "STAFF00000000002";

    private readonly Mock<ICardRepository> _cardRepository = new();
    private readonly Mock<ILedgerRepository> _ledgerRepository = new();
    private readonly Mock<IStaffRepository> _staffRepository = new();
    private readonly Mock<ISettingsRepository> _settingsRepository = new();
    private readonly Mock<IReportExportStatusService> _reportExportStatusService = new();

    private static readonly DateTime AsOf = new DateTime(2026, 8, 3, 9, 0, 0);

    private AdminDashboardService CreateService() => new AdminDashboardService(
        _cardRepository.Object,
        _ledgerRepository.Object,
        _staffRepository.Object,
        _settingsRepository.Object,
        _reportExportStatusService.Object);

    #region セットアップ用ヘルパー

    private void SetupDefaults(
        IEnumerable<IcCard> cards = null,
        IEnumerable<Ledger> lentRecords = null,
        Dictionary<string, (int Balance, DateTime? LastUsageDate)> balances = null,
        Dictionary<string, DateTime> lastUsageDates = null,
        IEnumerable<Staff> staff = null,
        AppSettings settings = null,
        IEnumerable<ReportExportStatus> reportStatuses = null)
    {
        _settingsRepository.Setup(r => r.GetAppSettingsAsync())
            .ReturnsAsync(settings ?? new AppSettings { WarningBalance = 10000, ReportOutputFolder = @"C:\reports" });
        _cardRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards ?? new List<IcCard>());
        _ledgerRepository.Setup(r => r.GetAllLentRecordsAsync())
            .ReturnsAsync((lentRecords ?? Enumerable.Empty<Ledger>()).ToList());
        _ledgerRepository.Setup(r => r.GetAllLatestBalancesAsync())
            .ReturnsAsync(balances ?? new Dictionary<string, (int, DateTime?)>());
        _ledgerRepository.Setup(r => r.GetAllLastUsageDatesAsync())
            .ReturnsAsync(lastUsageDates ?? new Dictionary<string, DateTime>());
        _staffRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(staff ?? new List<Staff>());
        _reportExportStatusService
            .Setup(s => s.GetStatuses(It.IsAny<IEnumerable<ReportExportTarget>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((reportStatuses ?? Enumerable.Empty<ReportExportStatus>()).ToList());
    }

    private void SetupAnalyticsDefaults(
        IEnumerable<IcCard> cards = null,
        IEnumerable<CardUsageStatsRow> usageStats = null,
        IEnumerable<MonthlyUsageRow> monthlyUsage = null,
        IEnumerable<MonthEndBalanceRow> monthEndBalances = null,
        IEnumerable<Staff> staff = null,
        Dictionary<string, int> balancesBeforePeriod = null)
    {
        _ledgerRepository.Setup(r => r.GetBalancesBeforeAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(balancesBeforePeriod ?? new Dictionary<string, int>());
        _cardRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(cards ?? new List<IcCard>());
        _ledgerRepository.Setup(r => r.GetUsageStatsByCardAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync((usageStats ?? Enumerable.Empty<CardUsageStatsRow>()).ToList());
        _ledgerRepository.Setup(r => r.GetMonthlyUsageByLenderAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync((monthlyUsage ?? Enumerable.Empty<MonthlyUsageRow>()).ToList());
        _ledgerRepository.Setup(r => r.GetMonthEndBalancesByCardAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync((monthEndBalances ?? Enumerable.Empty<MonthEndBalanceRow>()).ToList());
        _staffRepository.Setup(r => r.GetAllAsync())
            .ReturnsAsync(staff ?? new List<Staff>());
    }

    private static IcCard Card(string idm, string type = "はやかけん", string number = "001",
        bool isLent = false, string lastLentStaff = null, bool isDeleted = false, bool isRefunded = false)
        => new IcCard
        {
            CardIdm = idm,
            CardType = type,
            CardNumber = number,
            IsLent = isLent,
            LastLentStaff = lastLentStaff,
            IsDeleted = isDeleted,
            IsRefunded = isRefunded
        };

    private static Ledger LentRecord(string cardIdm, DateTime lentAt, string lenderIdm = StaffA, string staffName = "福岡 太郎")
        => new Ledger
        {
            CardIdm = cardIdm,
            LenderIdm = lenderIdm,
            StaffName = staffName,
            Date = lentAt,
            LentAt = lentAt,
            Summary = "（貸出中）",
            IsLentRecord = true
        };

    #endregion

    #region GetOperationStatusAsync — 基本

    [Fact]
    public async Task GetOperationStatusAsync_WithNoCards_ReturnsZeroCounts()
    {
        SetupDefaults();

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.TotalCardCount.Should().Be(0);
        result.LentCardCount.Should().Be(0);
        result.Cards.Should().BeEmpty();
    }

    [Fact]
    public async Task GetOperationStatusAsync_EchoesBackTheThresholdsUsed()
    {
        SetupDefaults(settings: new AppSettings { WarningBalance = 3000, ReportOutputFolder = @"C:\reports" });

        var result = await CreateService().GetOperationStatusAsync(AsOf, 30);

        result.AsOf.Should().Be(AsOf);
        result.LongTermUnreturnedThresholdDays.Should().Be(30, "画面に「何日以上を督促対象としたか」を明示するため");
        result.WarningBalance.Should().Be(3000);
        result.ReportYear.Should().Be(2026);
        result.ReportMonth.Should().Be(8);
    }

    [Fact]
    public async Task GetOperationStatusAsync_ExcludesDeletedAndRefundedCards()
    {
        SetupDefaults(cards: new[]
        {
            Card(CardA),
            Card("CCCC000000000003", number: "003", isDeleted: true),
            Card("DDDD000000000004", number: "004", isRefunded: true)
        });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.TotalCardCount.Should().Be(1, "既に運用から外れたカードは統制対象ではない");
        result.Cards.Single().CardIdm.Should().Be(CardA);
    }

    [Fact]
    public async Task GetOperationStatusAsync_CountsLentCards()
    {
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: true), Card(CardB, number: "002") },
            lentRecords: new[] { LentRecord(CardA, AsOf.AddDays(-1)) });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.LentCardCount.Should().Be(1);
    }

    #endregion

    #region GetOperationStatusAsync — 長期未返却

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    public async Task GetOperationStatusAsync_FlagsLongTermUnreturnedAtThreshold(int elapsedDays, bool expected)
    {
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: true) },
            lentRecords: new[] { LentRecord(CardA, AsOf.AddDays(-elapsedDays)) });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.Cards.Single().IsLongTermUnreturned.Should().Be(expected);
        result.LongTermUnreturnedCount.Should().Be(expected ? 1 : 0);
    }

    [Fact]
    public async Task GetOperationStatusAsync_ReportsElapsedLentDays()
    {
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: true) },
            lentRecords: new[] { LentRecord(CardA, AsOf.AddDays(-20)) });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.ElapsedLentDays.Should().Be(20);
        card.LentAt.Should().Be(AsOf.AddDays(-20));
    }

    [Fact]
    public async Task GetOperationStatusAsync_WithDuplicateLentRecords_UsesTheNewestOne()
    {
        // Issue #1196: 共有モードでは同一カードに複数の貸出中レコードが残ることがある
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: true) },
            lentRecords: new[]
            {
                LentRecord(CardA, AsOf.AddDays(-60)),
                LentRecord(CardA, AsOf.AddDays(-2))
            });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.ElapsedLentDays.Should().Be(2, "古いレコードを採ると経過日数を過大に見せてしまう");
        card.IsLongTermUnreturned.Should().BeFalse();
    }

    [Fact]
    public async Task GetOperationStatusAsync_WithStaleLentRecordButCardNotLent_DoesNotCountAsUnreturned()
    {
        // ic_card.is_lent = 0 なのに貸出中レコードが残る不整合は、共有モードで他 PC の
        // 返却が反映される前などに一時的に起こる（起動時に LendingService が修復する）。
        // ここで督促対象に数えると「貸出中 0 枚なのに長期未返却 1 枚」という自己矛盾になり、
        // しかも貸出職員名は解決できないため督促に使えない行が出る。
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: false) },
            lentRecords: new[] { LentRecord(CardA, AsOf.AddDays(-60)) });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.LongTermUnreturnedCount.Should().Be(0);
        result.Cards.Single().IsLongTermUnreturned.Should().BeFalse();
        result.Cards.Single().ElapsedLentDays.Should().BeNull();
        result.Cards.Single().LentAt.Should().BeNull("貸出中でないカードに貸出日時を出すと返却済みか判断できない");
    }

    [Fact]
    public async Task GetOperationStatusAsync_LongTermUnreturnedNeverExceedsLentCount()
    {
        // サマリータイルの数値どうしが矛盾しないことを不変条件として表明する
        SetupDefaults(
            cards: new[]
            {
                Card(CardA, isLent: true),
                Card(CardB, number: "002", isLent: false),
                Card("CCCC000000000003", number: "003", isLent: false)
            },
            lentRecords: new[]
            {
                LentRecord(CardA, AsOf.AddDays(-60)),
                LentRecord(CardB, AsOf.AddDays(-60)),
                LentRecord("CCCC000000000003", AsOf.AddDays(-60))
            });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.LongTermUnreturnedCount.Should().BeLessOrEqualTo(result.LentCardCount);
        result.LongTermUnreturnedCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOperationStatusAsync_WithNoLentRecord_LeavesElapsedDaysNull()
    {
        SetupDefaults(cards: new[] { Card(CardA) });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.ElapsedLentDays.Should().BeNull();
        card.IsLongTermUnreturned.Should().BeFalse();
    }

    #endregion

    #region GetOperationStatusAsync — 残額

    [Theory]
    [InlineData(9999, true)]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    public async Task GetOperationStatusAsync_FlagsLowBalanceInclusiveOfThreshold(int balance, bool expected)
    {
        // 既存の残額不足警告（WarningService）と同じく「以下」で判定する
        SetupDefaults(
            cards: new[] { Card(CardA) },
            balances: new Dictionary<string, (int, DateTime?)> { [CardA] = (balance, AsOf.AddDays(-1)) });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.Cards.Single().IsBalanceWarning.Should().Be(expected);
        result.LowBalanceCount.Should().Be(expected ? 1 : 0);
    }

    [Fact]
    public async Task GetOperationStatusAsync_WithNoLedgerHistory_TreatsBalanceAsZero()
    {
        SetupDefaults(cards: new[] { Card(CardA) });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.CurrentBalance.Should().Be(0);
        card.LastUsageDate.Should().BeNull();
    }

    [Fact]
    public async Task GetOperationStatusAsync_ReportsLastUsageDate()
    {
        // Issue #1747: 最終利用日は残高クエリの「最新レコード日」ではなく、
        // 利用実績（貸出中・繰越を除外した）クエリから取る
        var lastUsage = AsOf.AddDays(-5);
        SetupDefaults(
            cards: new[] { Card(CardA) },
            balances: new Dictionary<string, (int, DateTime?)> { [CardA] = (12000, null) },
            lastUsageDates: new Dictionary<string, DateTime> { [CardA] = lastUsage });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.LastUsageDate.Should().Be(lastUsage);
    }

    [Fact]
    public async Task GetOperationStatusAsync_DoesNotUseLatestRecordDateAsLastUsageDate()
    {
        // Issue #1747 故障シナリオ(1): 登録しただけのカードでは残高クエリの最新レコード日
        //（＝新規購入日）が返るが、利用実績が無い以上、最終利用日は空欄にすべき。
        // ここで残高側の日付へフォールバックすると同じバグが再発する。
        var purchaseDate = AsOf.AddDays(-30);
        SetupDefaults(
            cards: new[] { Card(CardA) },
            balances: new Dictionary<string, (int, DateTime?)> { [CardA] = (5000, purchaseDate) },
            lastUsageDates: new Dictionary<string, DateTime>());

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.LastUsageDate.Should().BeNull("利用実績の無いカードに最終利用日を表示してはいけない");
        card.CurrentBalance.Should().Be(5000, "残高は繰越レコードも含む全レコードから取る（Issue #1747 で変えない側）");
    }

    #endregion

    #region GetOperationStatusAsync — 帳票出力状況

    [Fact]
    public async Task GetOperationStatusAsync_CountsNotExportedAndUnknownSeparately()
    {
        SetupDefaults(
            cards: new[] { Card(CardA), Card(CardB, number: "002"), Card("CCCC000000000003", number: "003") },
            reportStatuses: new[]
            {
                new ReportExportStatus { CardIdm = CardA, State = ReportExportState.Exported },
                new ReportExportStatus { CardIdm = CardB, State = ReportExportState.NotExported },
                new ReportExportStatus { CardIdm = "CCCC000000000003", State = ReportExportState.Unknown }
            });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        // 合算すると「出していないのか、確認できないだけなのか」が区別できなくなる
        result.ReportNotExportedCount.Should().Be(1);
        result.ReportStatusUnknownCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOperationStatusAsync_WithMissingReportStatus_FallsBackToUnknown()
    {
        SetupDefaults(cards: new[] { Card(CardA) });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.Cards.Single().ReportState.Should().Be(ReportExportState.Unknown);
        result.ReportNotExportedCount.Should().Be(0, "判定できなかったカードを未出力として督促してはいけない");
    }

    [Fact]
    public async Task GetOperationStatusAsync_QueriesReportStatusForTheAsOfMonth()
    {
        SetupDefaults(cards: new[] { Card(CardA) });

        await CreateService().GetOperationStatusAsync(new DateTime(2026, 3, 15), AppConstants.LongTermUnreturnedDays);

        _reportExportStatusService.Verify(
            s => s.GetStatuses(It.IsAny<IEnumerable<ReportExportTarget>>(), @"C:\reports", 2026, 3), Times.Once);
    }

    [Fact]
    public async Task GetOperationStatusAsync_PassesCardTypeAndNumberToReportStatusService()
    {
        // 年度ファイル名はカード種別と管理番号から決まるため、IDm だけでは判定できない
        SetupDefaults(cards: new[] { Card(CardA, type: "nimoca", number: "N-7") });
        IEnumerable<ReportExportTarget> captured = null;
        _reportExportStatusService
            .Setup(s => s.GetStatuses(It.IsAny<IEnumerable<ReportExportTarget>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Callback<IEnumerable<ReportExportTarget>, string, int, int>((t, _, __, ___) => captured = t)
            .Returns(new List<ReportExportStatus>());

        await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        captured.Should().ContainSingle();
        captured.Single().CardType.Should().Be("nimoca");
        captured.Single().CardNumber.Should().Be("N-7");
    }

    #endregion

    #region GetOperationStatusAsync — 職員名と注意フラグ

    [Fact]
    public async Task GetOperationStatusAsync_ResolvesLentStaffNameFromStaffMaster()
    {
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: true, lastLentStaff: StaffA) },
            lentRecords: new[] { LentRecord(CardA, AsOf.AddDays(-1), staffName: "台帳の氏名") },
            staff: new[] { new Staff { StaffIdm = StaffA, Name = "職員マスタの氏名" } });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.LentStaffName.Should().Be("職員マスタの氏名", "改姓時に最新の氏名で督促できるようマスタを優先する");
    }

    [Fact]
    public async Task GetOperationStatusAsync_FallsBackToLedgerStaffNameWhenMasterMissing()
    {
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: true) },
            lentRecords: new[] { LentRecord(CardA, AsOf.AddDays(-1), lenderIdm: null, staffName: "台帳の氏名") });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.LentStaffName.Should().Be("台帳の氏名");
    }

    [Fact]
    public async Task GetOperationStatusAsync_LeavesStaffNameEmptyForCardsNotLent()
    {
        SetupDefaults(
            cards: new[] { Card(CardA, isLent: false, lastLentStaff: StaffA) },
            staff: new[] { new Staff { StaffIdm = StaffA, Name = "福岡 太郎" } });

        var card = (await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays)).Cards.Single();

        card.LentStaffName.Should().BeEmpty("返却済みのカードに貸出者名を残すと誤解を招く");
    }

    [Fact]
    public async Task GetOperationStatusAsync_MarksAttentionForAnyProblem()
    {
        SetupDefaults(
            cards: new[] { Card(CardA), Card(CardB, number: "002") },
            balances: new Dictionary<string, (int, DateTime?)>
            {
                [CardA] = (500, AsOf.AddDays(-1)),
                [CardB] = (50000, AsOf.AddDays(-1))
            },
            reportStatuses: new[]
            {
                new ReportExportStatus { CardIdm = CardA, State = ReportExportState.Exported },
                new ReportExportStatus { CardIdm = CardB, State = ReportExportState.Exported }
            });

        var result = await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        result.Cards.Single(c => c.CardIdm == CardA).HasAttention.Should().BeTrue();
        result.Cards.Single(c => c.CardIdm == CardB).HasAttention.Should().BeFalse();
    }

    #endregion

    #region GetOperationStatusAsync — SQLite の直列アクセス

    /// <summary>
    /// リポジトリ呼び出しの保持区間が重ならないことを数えるための計測器。
    /// </summary>
    /// <remarks>
    /// 呼び出し「順序」だけを見る Moq の <c>MockSequence</c> では並列化のリグレッションを
    /// 検出できない（<c>Task.WhenAll</c> へ書き換えても開始順序は変わらないため）。
    /// 実際の同時実行数を数える方式は `DashboardServiceTests` の Issue #1452 回帰テストと同じ。
    /// </remarks>
    private sealed class ConcurrencyProbe
    {
        private readonly object _lock = new object();
        private int _activeCalls;

        public int MaxConcurrentCalls { get; private set; }

        public async Task<T> TrackAsync<T>(T value)
        {
            lock (_lock)
            {
                _activeCalls++;
                if (_activeCalls > MaxConcurrentCalls)
                {
                    MaxConcurrentCalls = _activeCalls;
                }
            }

            // 並列があれば検出されるよう少し滞留させる
            await Task.Delay(20).ConfigureAwait(false);

            lock (_lock)
            {
                _activeCalls--;
            }

            return value;
        }
    }

    [Fact]
    public async Task GetOperationStatusAsync_DoesNotOverlapRepositoryCalls()
    {
        // Issue #1452: 同一の SQLiteConnection 上でコマンドを並列実行すると SQLITE_MISUSE になる
        var probe = new ConcurrencyProbe();
        _settingsRepository.Setup(r => r.GetAppSettingsAsync())
            .Returns(() => probe.TrackAsync(new AppSettings { WarningBalance = 10000, ReportOutputFolder = @"C:\reports" }));
        _cardRepository.Setup(r => r.GetAllAsync())
            .Returns(() => probe.TrackAsync<IEnumerable<IcCard>>(new List<IcCard> { Card(CardA) }));
        _ledgerRepository.Setup(r => r.GetAllLentRecordsAsync())
            .Returns(() => probe.TrackAsync(new List<Ledger>()));
        _ledgerRepository.Setup(r => r.GetAllLatestBalancesAsync())
            .Returns(() => probe.TrackAsync(new Dictionary<string, (int Balance, DateTime? LastUsageDate)>()));
        _ledgerRepository.Setup(r => r.GetAllLastUsageDatesAsync())
            .Returns(() => probe.TrackAsync(new Dictionary<string, DateTime>()));
        _staffRepository.Setup(r => r.GetAllAsync())
            .Returns(() => probe.TrackAsync<IEnumerable<Staff>>(new List<Staff>()));
        _reportExportStatusService
            .Setup(s => s.GetStatuses(It.IsAny<IEnumerable<ReportExportTarget>>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new List<ReportExportStatus>());

        await CreateService().GetOperationStatusAsync(AsOf, AppConstants.LongTermUnreturnedDays);

        probe.MaxConcurrentCalls.Should().Be(1,
            "同一 SQLiteConnection 上の SQLITE_MISUSE を防ぐためリポジトリ呼び出しを直列化する（Issue #1452）");
    }

    [Fact]
    public async Task GetAnalyticsAsync_DoesNotOverlapRepositoryCalls()
    {
        var probe = new ConcurrencyProbe();
        _cardRepository.Setup(r => r.GetAllAsync())
            .Returns(() => probe.TrackAsync<IEnumerable<IcCard>>(new List<IcCard> { Card(CardA) }));
        _ledgerRepository.Setup(r => r.GetUsageStatsByCardAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(() => probe.TrackAsync<IReadOnlyList<CardUsageStatsRow>>(new List<CardUsageStatsRow>()));
        _ledgerRepository.Setup(r => r.GetMonthlyUsageByLenderAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(() => probe.TrackAsync<IReadOnlyList<MonthlyUsageRow>>(new List<MonthlyUsageRow>()));
        _ledgerRepository.Setup(r => r.GetMonthEndBalancesByCardAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Returns(() => probe.TrackAsync<IReadOnlyList<MonthEndBalanceRow>>(new List<MonthEndBalanceRow>()));
        _ledgerRepository.Setup(r => r.GetBalancesBeforeAsync(It.IsAny<DateTime>()))
            .Returns(() => probe.TrackAsync(new Dictionary<string, int>()));
        _staffRepository.Setup(r => r.GetAllAsync())
            .Returns(() => probe.TrackAsync<IEnumerable<Staff>>(new List<Staff>()));

        await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31), AsOf);

        probe.MaxConcurrentCalls.Should().Be(1,
            "利用分析も同じ接続を使うため直列化が必要（Issue #1452）");
    }

    #endregion

    #region GetAnalyticsAsync — 稼働状況

    [Fact]
    public async Task GetAnalyticsAsync_WithNoData_ReturnsEmptySeries()
    {
        SetupAnalyticsDefaults();

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), AsOf);

        result.Utilizations.Should().BeEmpty();
        result.UsageSeries.Should().BeEmpty();
        result.BalanceSeries.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAnalyticsAsync_CalculatesUtilizationRateOverPeriodDays()
    {
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA) },
            usageStats: new[] { new CardUsageStatsRow { CardIdm = CardA, UsedDayCount = 15, UsageCount = 40, TotalExpense = 8400 } });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), AsOf);

        result.PeriodDayCount.Should().Be(30);
        result.Utilizations.Single().UtilizationRate.Should().Be(0.5);
        result.Utilizations.Single().UsageCount.Should().Be(40);
        result.Utilizations.Single().TotalExpense.Should().Be(8400);
    }

    [Fact]
    public async Task GetAnalyticsAsync_IncludesCardsWithoutAnyUsageAsZeroPercent()
    {
        SetupAnalyticsDefaults(cards: new[] { Card(CardA) });

        var item = (await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), AsOf)).Utilizations.Single();

        item.UtilizationRate.Should().Be(0.0, "使われていないカードこそ発見したい対象なので一覧から消さない");
        item.UsedDayCount.Should().Be(0);
        item.UnusedDays.Should().BeNull();
    }

    [Fact]
    public async Task GetAnalyticsAsync_OrdersUtilizationsAscending()
    {
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA, number: "001"), Card(CardB, number: "002") },
            usageStats: new[]
            {
                new CardUsageStatsRow { CardIdm = CardA, UsedDayCount = 20 },
                new CardUsageStatsRow { CardIdm = CardB, UsedDayCount = 2 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), AsOf);

        result.Utilizations.First().CardIdm.Should().Be(CardB, "遊んでいるカードを先頭に出す");
    }

    [Fact]
    public async Task GetAnalyticsAsync_ReportsUnusedDaysFromLastUsage()
    {
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA) },
            usageStats: new[]
            {
                new CardUsageStatsRow { CardIdm = CardA, UsedDayCount = 1, LastUsageDate = AsOf.AddDays(-90) }
            });

        var item = (await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), AsOf)).Utilizations.Single();

        item.UnusedDays.Should().Be(90);
    }

    [Fact]
    public async Task GetAnalyticsAsync_ExcludesDeletedCardsFromUtilization()
    {
        SetupAnalyticsDefaults(cards: new[] { Card(CardA), Card(CardB, number: "002", isDeleted: true) });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 30), AsOf);

        result.Utilizations.Should().ContainSingle();
    }

    #endregion

    #region GetAnalyticsAsync — 月ラベル

    [Fact]
    public async Task GetAnalyticsAsync_EnumeratesEveryMonthInThePeriod()
    {
        SetupAnalyticsDefaults();

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 10), new DateTime(2026, 8, 3), AsOf);

        result.MonthLabels.Should().Equal(new[] { "2026/05", "2026/06", "2026/07", "2026/08" });
    }

    [Fact]
    public async Task GetAnalyticsAsync_IncludesMonthsWithoutAnyTransaction()
    {
        SetupAnalyticsDefaults();

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), AsOf);

        result.MonthLabels.Should().HaveCount(12, "取引の無い月も横軸に並べないと推移が読めない");
    }

    [Fact]
    public void EnumerateMonthKeys_WithReversedRange_ReturnsEmpty()
    {
        AdminDashboardService.EnumerateMonthKeys(new DateTime(2026, 8, 1), new DateTime(2026, 5, 1))
            .Should().BeEmpty();
    }

    [Fact]
    public void EnumerateMonthKeys_SpansYearBoundary()
    {
        AdminDashboardService.EnumerateMonthKeys(new DateTime(2025, 11, 15), new DateTime(2026, 2, 1))
            .Should().Equal(new[] { "2025-11", "2025-12", "2026-01", "2026-02" });
    }

    #endregion

    #region GetAnalyticsAsync — 職員別利用額

    [Fact]
    public async Task GetAnalyticsAsync_BuildsUsageSeriesPerStaffAlignedToMonthLabels()
    {
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 1000 },
                new MonthlyUsageRow { YearMonth = "2026-07", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 3000 }
            },
            staff: new[] { new Staff { StaffIdm = StaffA, Name = "福岡 太郎" } });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31), AsOf);

        var series = result.UsageSeries.Single();
        series.Name.Should().Be("福岡 太郎");
        series.MonthlyExpenses.Should().Equal(new[] { 1000, 0, 3000 });
        series.TotalExpense.Should().Be(4000);
    }

    [Fact]
    public async Task GetAnalyticsAsync_MergesRowsOfTheSameLenderAcrossRecordedNames()
    {
        // 改姓等で台帳の staff_name が割れても lender_idm が同じなら 1 人として扱う
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "旧姓 太郎", TotalExpense = 1000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "新姓 太郎", TotalExpense = 500 }
            },
            staff: new[] { new Staff { StaffIdm = StaffA, Name = "新姓 太郎" } });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Should().ContainSingle();
        result.UsageSeries.Single().TotalExpense.Should().Be(1500);
        result.UsageSeries.Single().Name.Should().Be("新姓 太郎");
    }

    [Fact]
    public async Task GetAnalyticsAsync_KeepsRowsWithoutLenderIdmSeparateByRecordedName()
    {
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = "", StaffName = "旧職員 A", TotalExpense = 1000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = "", StaffName = "旧職員 B", TotalExpense = 500 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAnalyticsAsync_WithNoIdentifiableStaff_UsesPlaceholderName()
    {
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = "", StaffName = "", TotalExpense = 1000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Single().Name.Should().Be(AdminDashboardService.UnknownStaffName);
    }

    [Fact]
    public async Task GetAnalyticsAsync_同姓同名の職員を職員番号で判別できること()
    {
        // Issue #1886: 凡例・代替一覧・Excel が表示するのは名前の文字列だけで、
        // 系列を内部で区別しているバケットキー（IDm）は利用者に届かない。
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 3000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffB, StaffName = "福岡 太郎", TotalExpense = 1000 }
            },
            staff: new[]
            {
                new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "A001" },
                new Staff { StaffIdm = StaffB, Name = "福岡 太郎", Number = "A002" }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Select(s => s.Name)
            .Should().Equal(new[] { "福岡 太郎（職員番号 A001）", "福岡 太郎（職員番号 A002）" });
    }

    [Fact]
    public async Task GetAnalyticsAsync_同名でなければ職員番号を添えないこと()
    {
        // 対の表明。常に職員番号を添える実装でも上のテストは緑になるため、
        // 「必要なときだけ修飾する」ことを併せて固定する。
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 3000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffB, StaffName = "博多 花子", TotalExpense = 1000 }
            },
            staff: new[]
            {
                new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "A001" },
                new Staff { StaffIdm = StaffB, Name = "博多 花子", Number = "A002" }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Select(s => s.Name).Should().Equal(new[] { "福岡 太郎", "博多 花子" });
    }

    [Fact]
    public async Task GetAnalyticsAsync_職員名なしが複数あっても判別できること()
    {
        // lender_idm を持つが職員マスタに無く氏名も空の行は、すべて同じプレースホルダへ潰れる。
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "", TotalExpense = 3000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffB, StaffName = "", TotalExpense = 1000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Should().HaveCount(2);
        result.UsageSeries.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        result.UsageSeries.Select(s => s.Name)
            .Should().OnlyContain(n => n.StartsWith(AdminDashboardService.UnknownStaffName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAnalyticsAsync_職員番号を持たない同名は通し番号で判別できること()
    {
        // lender_idm を持たない過去のインポート行と、職員マスタに職員番号が無い職員。
        // どちらも職員番号を引けないため、通し番号でしか一意にできない。
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 3000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = "", StaffName = "福岡 太郎", TotalExpense = 1000 }
            },
            staff: new[] { new Staff { StaffIdm = StaffA, Name = "福岡 太郎", Number = "" } });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Select(s => s.Name)
            .Should().Equal(new[] { "福岡 太郎（1 人目）", "福岡 太郎（2 人目）" });
    }

    [Fact]
    public async Task GetAnalyticsAsync_同名同額でもラベルの並びが決定的であること()
    {
        // 通し番号は表示順に乗るため、同名・同額の系列の並びが実行のたびに変わると
        // ラベルまで入れ替わる。バケットキーで並びを固定していることを表明する。
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffB, StaffName = "福岡 太郎", TotalExpense = 1000 },
                new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 1000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        // StaffA < StaffB（序数比較）なので、金額が同じなら常に StaffA が先。
        result.UsageSeries.Select(s => s.MonthlyExpenses[0]).Should().Equal(new[] { 1000, 1000 });
        result.UsageSeries.Select(s => s.Name)
            .Should().Equal(new[] { "福岡 太郎（1 人目）", "福岡 太郎（2 人目）" });
    }

    [Fact]
    public async Task GetAnalyticsAsync_AggregatesLowRankedStaffIntoOtherSeries()
    {
        var rows = Enumerable.Range(1, AppConstants.AdminDashboardMaxSeries + 3)
            .Select(i => new MonthlyUsageRow
            {
                YearMonth = "2026-05",
                LenderIdm = "STAFF" + i.ToString("D11"),
                StaffName = "職員" + i,
                TotalExpense = i * 1000
            })
            .ToList();
        SetupAnalyticsDefaults(monthlyUsage: rows);

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Should().HaveCount(AppConstants.AdminDashboardMaxSeries + 1);
        result.UsageSeries.Last().IsOther.Should().BeTrue();
        // 集約系列の名前には人数が入る（氏名「その他」の職員との同一表記を避ける。Issue #1858）
        result.UsageSeries.Last().Name.Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(3));
        result.UsageSeries.Last().AggregatedSeriesCount.Should().Be(3);
        // 上位 5 名は 8000/7000/6000/5000/4000 円、残りは 3000+2000+1000 = 6000 円
        result.UsageSeries.Last().TotalExpense.Should().Be(6000);
    }

    [Fact]
    public async Task GetAnalyticsAsync_氏名がその他の職員がいても集約系列と同一表記にならないこと()
    {
        // Issue #1858 の故障シナリオ:
        // 氏名「その他」の職員（職員マスタに無い staff_name をそのまま系列名に使う経路）が
        // 上位 5 名に入り、かつ職員が 6 人以上いると、凡例に「その他」が 2 行並んで
        // どちらが集約分か判別できなくなる。
        var rows = new List<MonthlyUsageRow>
        {
            new MonthlyUsageRow { YearMonth = "2026-05", LenderIdm = "", StaffName = "その他", TotalExpense = 9000 }
        };
        rows.AddRange(Enumerable.Range(1, AppConstants.AdminDashboardMaxSeries + 2)
            .Select(i => new MonthlyUsageRow
            {
                YearMonth = "2026-05",
                LenderIdm = "STAFF" + i.ToString("D11"),
                StaffName = "職員" + i,
                TotalExpense = i * 1000
            }));
        SetupAnalyticsDefaults(monthlyUsage: rows);

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        // 氏名「その他」の職員は上位 5 名に入り、名前はそのまま
        result.UsageSeries.Should().ContainSingle(s => !s.IsOther && s.Name == "その他");
        // 集約系列は人数付きなので、表示名が衝突しない
        result.UsageSeries.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        var other = result.UsageSeries.Single(s => s.IsOther);
        other.Name.Should().Be(ChartSeriesNameFormatter.BuildOtherSeriesName(other.AggregatedSeriesCount));
        other.Name.Should().NotBe("その他");
    }

    [Fact]
    public async Task GetAnalyticsAsync_集約系列の人数が実際に集約した系列数と一致すること()
    {
        // 人数はラベルの飾りではなく集計の事実なので、集約された系列の数と一致させる
        const int extra = 4;
        var rows = Enumerable.Range(1, AppConstants.AdminDashboardMaxSeries + extra)
            .Select(i => new MonthlyUsageRow
            {
                YearMonth = "2026-05",
                LenderIdm = "STAFF" + i.ToString("D11"),
                StaffName = "職員" + i,
                TotalExpense = i * 1000
            })
            .ToList();
        SetupAnalyticsDefaults(monthlyUsage: rows);

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        var other = result.UsageSeries.Single(s => s.IsOther);
        other.AggregatedSeriesCount.Should().Be(extra);

        // Issue #1885: 期待値は書式込みのリテラルで書く。`extra.ToString()` は
        // 現在カルチャと桁数に静かに依存し（4 桁以上で桁区切りが入る）、
        // 「件数がラベルに載っているか」を表明したいのに書式の退行を素通りさせる。
        other.Name.Should().Be("その他（4 名）");
    }

    [Fact]
    public async Task GetAnalyticsAsync_WithExactlyMaxSeries_DoesNotCreateOtherSeries()
    {
        var rows = Enumerable.Range(1, AppConstants.AdminDashboardMaxSeries)
            .Select(i => new MonthlyUsageRow
            {
                YearMonth = "2026-05",
                LenderIdm = "STAFF" + i.ToString("D11"),
                StaffName = "職員" + i,
                TotalExpense = i * 1000
            })
            .ToList();
        SetupAnalyticsDefaults(monthlyUsage: rows);

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Should().HaveCount(AppConstants.AdminDashboardMaxSeries);
        result.UsageSeries.Should().NotContain(s => s.IsOther);
    }

    [Fact]
    public async Task GetAnalyticsAsync_IgnoresUsageRowsOutsideTheMonthLabels()
    {
        SetupAnalyticsDefaults(
            monthlyUsage: new[]
            {
                new MonthlyUsageRow { YearMonth = "2025-01", LenderIdm = StaffA, StaffName = "福岡 太郎", TotalExpense = 9999 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.UsageSeries.Should().BeEmpty();
    }

    #endregion

    #region GetAnalyticsAsync — 残高推移

    [Fact]
    public async Task GetAnalyticsAsync_CarriesBalanceForwardIntoMonthsWithoutTransactions()
    {
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA) },
            monthEndBalances: new[]
            {
                new MonthEndBalanceRow { CardIdm = CardA, YearMonth = "2026-05", Balance = 5000 },
                new MonthEndBalanceRow { CardIdm = CardA, YearMonth = "2026-07", Balance = 3000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31), AsOf);

        result.BalanceSeries.Single().MonthlyBalances
            .Should().Equal(new double?[] { 5000.0, 5000.0, 3000.0 });
    }

    [Fact]
    public async Task GetAnalyticsAsync_SeedsBalanceFromBeforeThePeriod()
    {
        // 期間の先頭に取引が無いだけのカードは、期間前の残高を引き継いで線を描き始める。
        // 引き継がないと「4か月目から使い始めたカード」に見えるが、実際には残高を持っていた。
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA) },
            balancesBeforePeriod: new Dictionary<string, int> { [CardA] = 8000 },
            monthEndBalances: new[]
            {
                new MonthEndBalanceRow { CardIdm = CardA, YearMonth = "2026-07", Balance = 3000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31), AsOf);

        result.BalanceSeries.Single().MonthlyBalances
            .Should().Equal(new double?[] { 8000.0, 8000.0, 3000.0 });
    }

    [Fact]
    public async Task GetAnalyticsAsync_LeavesMonthsBeforeFirstTransactionAsNull()
    {
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA) },
            monthEndBalances: new[]
            {
                new MonthEndBalanceRow { CardIdm = CardA, YearMonth = "2026-07", Balance = 3000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 7, 31), AsOf);

        result.BalanceSeries.Single().MonthlyBalances
            .Should().Equal(new double?[] { null, null, 3000.0 });
    }

    [Fact]
    public async Task GetAnalyticsAsync_OmitsCardsWithoutAnyBalanceRow()
    {
        SetupAnalyticsDefaults(cards: new[] { Card(CardA), Card(CardB, number: "002") },
            monthEndBalances: new[]
            {
                new MonthEndBalanceRow { CardIdm = CardA, YearMonth = "2026-05", Balance = 5000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.BalanceSeries.Should().ContainSingle();
        result.BalanceSeries.Single().CardIdm.Should().Be(CardA);
    }

    [Fact]
    public async Task GetAnalyticsAsync_CarriesCardDisplayNameIntoBalanceSeries()
    {
        SetupAnalyticsDefaults(
            cards: new[] { Card(CardA, type: "SUGOCA", number: "S-1") },
            monthEndBalances: new[]
            {
                new MonthEndBalanceRow { CardIdm = CardA, YearMonth = "2026-05", Balance = 5000 }
            });

        var result = await CreateService().GetAnalyticsAsync(
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), AsOf);

        result.BalanceSeries.Single().DisplayName.Should().Be("SUGOCA S-1");
    }

    #endregion
}
