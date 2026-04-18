using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// DashboardServiceの単体テスト
/// データ結合・ソート・残高警告判定を検証する。
/// </summary>
public class DashboardServiceTests
{
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly DashboardService _service;

    public DashboardServiceTests()
    {
        _cardRepositoryMock = new Mock<ICardRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();

        _service = new DashboardService(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _settingsRepositoryMock.Object);
    }

    private void SetupRepositories(
        IEnumerable<IcCard> cards,
        Dictionary<string, (int Balance, DateTime? LastUsageDate)>? balances = null,
        IEnumerable<Staff>? staff = null,
        int warningBalance = 1000)
    {
        _settingsRepositoryMock
            .Setup(s => s.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { WarningBalance = warningBalance });
        _cardRepositoryMock.Setup(c => c.GetAllAsync()).ReturnsAsync(cards.ToList());
        _ledgerRepositoryMock
            .Setup(l => l.GetAllLatestBalancesAsync())
            .ReturnsAsync(balances ?? new Dictionary<string, (int, DateTime?)>());
        _staffRepositoryMock
            .Setup(s => s.GetAllAsync())
            .ReturnsAsync((staff ?? Enumerable.Empty<Staff>()).ToList());
    }

    #region BuildDashboardAsync — データ結合

    [Fact]
    public async Task BuildDashboardAsync_カードと残高と職員名を結合すること()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "H-001", IsLent = true, LastLentStaff = "STAFF00000000001" }
        };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["0102030405060708"] = (5000, new DateTime(2026, 3, 15))
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "山田太郎" } };

        SetupRepositories(cards, balances, staff, warningBalance: 1000);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items.Should().HaveCount(1);
        var item = result.Items[0];
        item.CardIdm.Should().Be("0102030405060708");
        item.CardType.Should().Be("はやかけん");
        item.CardNumber.Should().Be("H-001");
        item.CurrentBalance.Should().Be(5000);
        item.LastUsageDate.Should().Be(new DateTime(2026, 3, 15));
        item.IsLent.Should().BeTrue();
        item.LentStaffName.Should().Be("山田太郎", "貸出中の場合は職員名が解決される");
        result.WarningBalance.Should().Be(1000);
    }

    [Fact]
    public async Task BuildDashboardAsync_balancesに該当キーがない場合は残高0最終利用日nullになること()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "MISSING_BALANCE_KEY", CardType = "nimoca", CardNumber = "N-001" }
        };
        SetupRepositories(cards); // balances は空

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].CurrentBalance.Should().Be(0, "balances辞書にキーがない場合は0にフォールバック");
        result.Items[0].LastUsageDate.Should().BeNull("balances辞書にキーがない場合はnullにフォールバック");
    }

    [Fact]
    public async Task BuildDashboardAsync_貸出中だがLastLentStaffがnullの場合はLentStaffNameもnull()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "0102030405060708", CardType = "SUGOCA", CardNumber = "S-001", IsLent = true, LastLentStaff = null }
        };
        SetupRepositories(cards);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].LentStaffName.Should().BeNull();
    }

    [Fact]
    public async Task BuildDashboardAsync_LastLentStaffに該当する職員が存在しない場合はLentStaffNameもnull()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "0102030405060708", IsLent = true, LastLentStaff = "MISSING_STAFF" }
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "別人" } };
        SetupRepositories(cards, staff: staff);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].LentStaffName.Should().BeNull("職員辞書に存在しない場合はnull");
    }

    [Fact]
    public async Task BuildDashboardAsync_未貸出の場合はLentStaffNameは常にnull()
    {
        // Arrange
        var cards = new[]
        {
            // IsLent=false でも LastLentStaff に値がある場合（履歴的にあり得る）
            new IcCard { CardIdm = "0102030405060708", IsLent = false, LastLentStaff = "STAFF00000000001" }
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "山田太郎" } };
        SetupRepositories(cards, staff: staff);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].LentStaffName.Should().BeNull("未貸出時は名前を表示しない");
    }

    #endregion

    #region BuildDashboardAsync — 残高警告境界値

    [Theory]
    [InlineData(999, 1000, true, "残高 < 閾値 → 警告（境界値-1）")]
    [InlineData(1000, 1000, true, "残高 == 閾値 → 警告（境界値）")]
    [InlineData(1001, 1000, false, "残高 > 閾値 → 警告なし（境界値+1）")]
    [InlineData(0, 1000, true, "残高ゼロは警告対象")]
    public async Task BuildDashboardAsync_残高警告判定_境界値(int balance, int warningThreshold, bool expectedWarning, string reason)
    {
        // Arrange
        var cards = new[] { new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "H-001" } };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["0102030405060708"] = (balance, null)
        };
        SetupRepositories(cards, balances, warningBalance: warningThreshold);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items[0].IsBalanceWarning.Should().Be(expectedWarning, reason);
    }

    #endregion

    #region SortItems — ソート順

    private static List<CardBalanceDashboardItem> CreateUnsortedItems()
    {
        return new List<CardBalanceDashboardItem>
        {
            new() { CardType = "nimoca",   CardNumber = "N-002", CurrentBalance = 500,  LastUsageDate = new DateTime(2026, 1, 5) },
            new() { CardType = "はやかけん", CardNumber = "H-001", CurrentBalance = 3000, LastUsageDate = new DateTime(2026, 3, 1) },
            new() { CardType = "SUGOCA",   CardNumber = "S-001", CurrentBalance = 1500, LastUsageDate = null },
            new() { CardType = "はやかけん", CardNumber = "H-002", CurrentBalance = 3000, LastUsageDate = new DateTime(2026, 2, 1) },
        };
    }

    [Fact]
    public void SortItems_BalanceAscending_残高昇順でソートされ同額はカード種別順()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.BalanceAscending);

        sorted.Select(s => s.CurrentBalance).Should().BeInAscendingOrder();
        // 同額(3000)のうち、はやかけんH-001 → H-002の順
        var threes = sorted.Where(s => s.CurrentBalance == 3000).ToList();
        threes.Should().HaveCount(2);
        threes[0].CardNumber.Should().Be("H-001");
        threes[1].CardNumber.Should().Be("H-002");
    }

    [Fact]
    public void SortItems_BalanceDescending_残高降順でソートされること()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.BalanceDescending);

        sorted.Select(s => s.CurrentBalance).Should().BeInDescendingOrder();
    }

    [Fact]
    public void SortItems_LastUsageDate_最新利用日順でソートされnullは末尾()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.LastUsageDate);

        sorted[0].LastUsageDate.Should().Be(new DateTime(2026, 3, 1));
        sorted[1].LastUsageDate.Should().Be(new DateTime(2026, 2, 1));
        sorted[2].LastUsageDate.Should().Be(new DateTime(2026, 1, 5));
        sorted[3].LastUsageDate.Should().BeNull("nullは最古として末尾に配置される");
    }

    [Fact]
    public void SortItems_CardName_カード種別と番号順でソートされること()
    {
        var items = CreateUnsortedItems();

        var sorted = _service.SortItems(items, DashboardSortOrder.CardName);

        // 各カード種別内で番号順になっていればOK（種別ごとの相対順は CardSortExtensions に依存）
        var hayakaken = sorted.Where(s => s.CardType == "はやかけん").ToList();
        hayakaken[0].CardNumber.Should().Be("H-001");
        hayakaken[1].CardNumber.Should().Be("H-002");
    }

    [Fact]
    public void SortItems_空リストは空リストを返すこと()
    {
        var sorted = _service.SortItems(new List<CardBalanceDashboardItem>(), DashboardSortOrder.CardName);

        sorted.Should().BeEmpty();
    }

    #endregion

    #region Issue #1261: 集計ロジック拡充テスト

    /// <summary>
    /// Issue #1261: 貸出中と在庫カードが混在する場合、各カードの IsLent / LentStaffName が
    /// 個別に正しく設定されること。
    /// </summary>
    [Fact]
    public async Task BuildDashboardAsync_貸出中と在庫の混在_各カードの状態が個別に反映されること()
    {
        // Arrange: 3枚貸出中 + 2枚在庫 の混合
        var cards = new[]
        {
            new IcCard { CardIdm = "AAAA000000000001", CardType = "はやかけん", CardNumber = "H-001", IsLent = true,  LastLentStaff = "STAFF00000000001" },
            new IcCard { CardIdm = "AAAA000000000002", CardType = "はやかけん", CardNumber = "H-002", IsLent = false, LastLentStaff = null },
            new IcCard { CardIdm = "AAAA000000000003", CardType = "nimoca",   CardNumber = "N-001", IsLent = true,  LastLentStaff = "STAFF00000000002" },
            new IcCard { CardIdm = "AAAA000000000004", CardType = "nimoca",   CardNumber = "N-002", IsLent = false, LastLentStaff = "STAFF00000000001" /* 履歴値のみ */ },
            new IcCard { CardIdm = "AAAA000000000005", CardType = "SUGOCA",   CardNumber = "S-001", IsLent = true,  LastLentStaff = "STAFF00000000003" },
        };
        var staff = new[]
        {
            new Staff { StaffIdm = "STAFF00000000001", Name = "田中一郎" },
            new Staff { StaffIdm = "STAFF00000000002", Name = "佐藤花子" },
            new Staff { StaffIdm = "STAFF00000000003", Name = "鈴木次郎" },
        };
        SetupRepositories(cards, staff: staff);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert: 5件すべて返る
        result.Items.Should().HaveCount(5);

        // 貸出中カード: IsLent=true + LentStaffName が解決される
        var lent = result.Items.Where(i => i.IsLent).ToList();
        lent.Should().HaveCount(3);
        lent.Select(i => i.LentStaffName).Should().BeEquivalentTo(
            new[] { "田中一郎", "佐藤花子", "鈴木次郎" });

        // 在庫カード: IsLent=false かつ LentStaffName=null（履歴値があっても表示しない）
        var available = result.Items.Where(i => !i.IsLent).ToList();
        available.Should().HaveCount(2);
        available.Should().OnlyContain(i => i.LentStaffName == null,
            "在庫中は LastLentStaff に値があっても LentStaffName は表示しない");
    }

    /// <summary>
    /// Issue #1261: 複数カードが警告残高を超過・未超過の混合状態のとき、
    /// 各カードで IsBalanceWarning が個別に判定されること。
    /// </summary>
    [Fact]
    public async Task BuildDashboardAsync_警告残高判定_複数カード混在でカードごとに独立判定()
    {
        // Arrange: 警告閾値=1000、残高500/1000/1500/100/2000 の5枚
        var cards = new[]
        {
            new IcCard { CardIdm = "BBBB000000000001", CardType = "はやかけん", CardNumber = "H-001" },
            new IcCard { CardIdm = "BBBB000000000002", CardType = "はやかけん", CardNumber = "H-002" },
            new IcCard { CardIdm = "BBBB000000000003", CardType = "はやかけん", CardNumber = "H-003" },
            new IcCard { CardIdm = "BBBB000000000004", CardType = "はやかけん", CardNumber = "H-004" },
            new IcCard { CardIdm = "BBBB000000000005", CardType = "はやかけん", CardNumber = "H-005" },
        };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["BBBB000000000001"] = (500,  null),
            ["BBBB000000000002"] = (1000, null),
            ["BBBB000000000003"] = (1500, null),
            ["BBBB000000000004"] = (100,  null),
            ["BBBB000000000005"] = (2000, null),
        };
        SetupRepositories(cards, balances, warningBalance: 1000);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert: balance ≤ 1000 のカードのみ警告、それ以外は警告なし
        var warnings = result.Items.Where(i => i.IsBalanceWarning).ToList();
        warnings.Should().HaveCount(3, "500/1000/100 の3枚が警告対象");
        warnings.Select(i => i.CardNumber).Should().BeEquivalentTo(new[] { "H-001", "H-002", "H-004" });

        var safe = result.Items.Where(i => !i.IsBalanceWarning).ToList();
        safe.Should().HaveCount(2, "1500/2000 の2枚は警告対象外");
        safe.Select(i => i.CardNumber).Should().BeEquivalentTo(new[] { "H-003", "H-005" });
    }

    /// <summary>
    /// Issue #1261: DashboardService は論理削除除外を Repository 層 (GetAllAsync) に委譲し、
    /// 自身で GetAllIncludingDeletedAsync を呼ばないこと。
    /// </summary>
    /// <remarks>
    /// CardRepository.GetAllAsync は SQL で <c>WHERE is_deleted = 0</c> を適用する。
    /// DashboardService がこの契約を崩して GetAllIncludingDeletedAsync を呼ぶと、
    /// 論理削除済みカードがダッシュボードに表示されてしまう。
    /// </remarks>
    [Fact]
    public async Task BuildDashboardAsync_論理削除除外はRepositoryに委譲する契約()
    {
        // Arrange
        SetupRepositories(Array.Empty<IcCard>());

        // Act
        await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once,
            "DashboardService は必ず GetAllAsync を呼ぶ（論理削除フィルタ適用済）");
        _cardRepositoryMock.Verify(r => r.GetAllIncludingDeletedAsync(), Times.Never,
            "DashboardService は GetAllIncludingDeletedAsync を呼ばない");
    }

    /// <summary>
    /// Issue #1261: 残高取得は GetAllLatestBalancesAsync への一本化で行い、
    /// 生の Ledger クエリ（GetByDateRangeAsync/GetByMonthAsync 等）は呼ばないこと。
    /// </summary>
    /// <remarks>
    /// GetAllLatestBalancesAsync は SQL 側で各カードの最新 Ledger のみを取得するため、
    /// IsLentRecord=true の貸出中レコードが紛れていても最新の balance/date として
    /// 扱われる。DashboardService はこの契約を前提にしており、自身で Ledger を
    /// フィルタリングする責務を持たない。
    /// </remarks>
    [Fact]
    public async Task BuildDashboardAsync_残高取得は集計APIに一本化しLedgerを直接取得しない()
    {
        // Arrange
        SetupRepositories(Array.Empty<IcCard>());

        // Act
        await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        _ledgerRepositoryMock.Verify(r => r.GetAllLatestBalancesAsync(), Times.Once,
            "最新残高は集計API経由で取得");
        _ledgerRepositoryMock.Verify(
            r => r.GetByDateRangeAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()),
            Times.Never,
            "DashboardService は Ledger を直接日付範囲で取得しない（IsLentRecord フィルタは Repository の責務）");
        _ledgerRepositoryMock.Verify(
            r => r.GetByMonthAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()),
            Times.Never,
            "DashboardService は月次Ledger取得もしない");
    }

    /// <summary>
    /// Issue #1261: LastUsageDate はカードごとに独立して balances 辞書から引き継がれること。
    /// 他カードの日付が混入したり、最新一枚に集約されたりしない。
    /// </summary>
    [Fact]
    public async Task BuildDashboardAsync_LastUsageDate_カードごとに独立して反映される()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "CCCC000000000001", CardType = "はやかけん", CardNumber = "H-001" },
            new IcCard { CardIdm = "CCCC000000000002", CardType = "はやかけん", CardNumber = "H-002" },
            new IcCard { CardIdm = "CCCC000000000003", CardType = "はやかけん", CardNumber = "H-003" },
        };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["CCCC000000000001"] = (1000, new DateTime(2026, 1, 15)),
            ["CCCC000000000002"] = (2000, new DateTime(2026, 4, 1)),
            ["CCCC000000000003"] = (3000, null), // 履歴なし
        };
        SetupRepositories(cards, balances);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert: 各カードの LastUsageDate が個別に保持される
        var byCard = result.Items.ToDictionary(i => i.CardIdm);
        byCard["CCCC000000000001"].LastUsageDate.Should().Be(new DateTime(2026, 1, 15));
        byCard["CCCC000000000002"].LastUsageDate.Should().Be(new DateTime(2026, 4, 1));
        byCard["CCCC000000000003"].LastUsageDate.Should().BeNull("履歴なしのカードは null");
    }

    /// <summary>
    /// Issue #1261: balances 辞書が完全に空の場合、全カードが FallbackBalance=0 + null で
    /// 構築され、0 は警告対象（<c>balance ≤ warningBalance</c>）となること。
    /// </summary>
    [Fact]
    public async Task BuildDashboardAsync_空balances辞書_全カードが0警告対象になる()
    {
        // Arrange: 3枚のカードがあるが balances 辞書は空
        var cards = new[]
        {
            new IcCard { CardIdm = "DDDD000000000001", CardType = "はやかけん", CardNumber = "H-001" },
            new IcCard { CardIdm = "DDDD000000000002", CardType = "はやかけん", CardNumber = "H-002" },
            new IcCard { CardIdm = "DDDD000000000003", CardType = "はやかけん", CardNumber = "H-003" },
        };
        SetupRepositories(cards, balances: new Dictionary<string, (int, DateTime?)>(), warningBalance: 1000);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items.Should().HaveCount(3);
        result.Items.Should().OnlyContain(i => i.CurrentBalance == 0);
        result.Items.Should().OnlyContain(i => i.LastUsageDate == null);
        result.Items.Should().OnlyContain(i => i.IsBalanceWarning,
            "残高0は警告閾値1000以下のため全件警告対象");
    }

    /// <summary>
    /// Issue #1261: 警告残高=0 の境界動作。残高0 のみ警告となり、残高1以上は警告されない。
    /// （`balance &lt;= 0` の判定で 0 のみマッチ）
    /// </summary>
    [Fact]
    public async Task BuildDashboardAsync_警告残高ゼロ境界_残高0のみ警告対象()
    {
        // Arrange
        var cards = new[]
        {
            new IcCard { CardIdm = "EEEE000000000001", CardType = "はやかけん", CardNumber = "H-001" },
            new IcCard { CardIdm = "EEEE000000000002", CardType = "はやかけん", CardNumber = "H-002" },
            new IcCard { CardIdm = "EEEE000000000003", CardType = "はやかけん", CardNumber = "H-003" },
        };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["EEEE000000000001"] = (0, null),
            ["EEEE000000000002"] = (1, null),
            ["EEEE000000000003"] = (1000, null),
        };
        SetupRepositories(cards, balances, warningBalance: 0);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        var byCard = result.Items.ToDictionary(i => i.CardIdm);
        byCard["EEEE000000000001"].IsBalanceWarning.Should().BeTrue(
            "0 ≤ 0 なので警告対象");
        byCard["EEEE000000000002"].IsBalanceWarning.Should().BeFalse(
            "1 > 0 なので警告対象外");
        byCard["EEEE000000000003"].IsBalanceWarning.Should().BeFalse(
            "1000 > 0 なので警告対象外");
    }

    /// <summary>
    /// Issue #1261: 長期貸出中カード（返却期限超過の実運用ケース）も、
    /// IsLent=true + LentStaffName + LastUsageDate が正しく表示されて可視化されること。
    /// </summary>
    /// <remarks>
    /// DashboardService は明示的な「返却期限超過フラグ」を持たないが、
    /// 貸出中カードはダッシュボード上で IsLent=true かつ LentStaffName 付きで
    /// 常に表示され、ユーザーが長期貸出を目視確認できる設計になっている。
    /// 本テストは「長期貸出でもカードが消えない／LentStaffName が復元される」ことを保証する。
    /// </remarks>
    [Fact]
    public async Task BuildDashboardAsync_長期貸出中カードもLentStaffName付きで可視化される()
    {
        // Arrange: 60日前から貸出中のカード（返却期限超過相当）
        var cards = new[]
        {
            new IcCard
            {
                CardIdm = "FFFF000000000001",
                CardType = "はやかけん",
                CardNumber = "H-001",
                IsLent = true,
                LastLentAt = DateTime.Now.AddDays(-60),
                LastLentStaff = "STAFF00000000001"
            }
        };
        var balances = new Dictionary<string, (int, DateTime?)>
        {
            ["FFFF000000000001"] = (1500, DateTime.Now.AddDays(-60))
        };
        var staff = new[] { new Staff { StaffIdm = "STAFF00000000001", Name = "長期利用者" } };
        SetupRepositories(cards, balances, staff);

        // Act
        var result = await _service.BuildDashboardAsync(DashboardSortOrder.CardName);

        // Assert
        result.Items.Should().HaveCount(1, "長期貸出でもカードは消えず表示される");
        var item = result.Items[0];
        item.IsLent.Should().BeTrue();
        item.LentStaffName.Should().Be("長期利用者",
            "長期貸出中でも職員名が解決されて表示される（返却期限超過の可視化）");
        item.LastUsageDate.Should().NotBeNull("最終利用日が表示される");
    }

    #endregion
}
