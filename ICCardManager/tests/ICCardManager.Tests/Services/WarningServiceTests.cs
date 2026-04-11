using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// WarningServiceの単体テスト
/// 残額警告・バス停未入力検出・ジャーナルモード警告の判定ロジックを検証する。
/// </summary>
public class WarningServiceTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IDatabaseInfo> _databaseInfoMock;
    private readonly WarningService _service;

    public WarningServiceTests()
    {
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _databaseInfoMock = new Mock<IDatabaseInfo>();
        _service = new WarningService(_ledgerRepositoryMock.Object, _databaseInfoMock.Object);
    }

    #region CheckLowBalanceWarnings — 残額警告境界値

    [Fact]
    public void CheckLowBalanceWarnings_残高が閾値以下の場合に警告対象となる()
    {
        // Arrange — 閾値1000で残高999/1000/1001/0を準備
        // DashboardServiceのIsBalanceWarning判定(<=)と一貫させるため、境界値ちょうども警告対象
        var items = new[]
        {
            new CardBalanceDashboardItem { CardIdm = "A", CardType = "はやかけん", CardNumber = "H-001", CurrentBalance = 999 },  // 警告
            new CardBalanceDashboardItem { CardIdm = "B", CardType = "nimoca",   CardNumber = "N-001", CurrentBalance = 1000 }, // 境界値: 警告対象（<=）
            new CardBalanceDashboardItem { CardIdm = "C", CardType = "SUGOCA",   CardNumber = "S-001", CurrentBalance = 1001 }, // 警告対象外
            new CardBalanceDashboardItem { CardIdm = "D", CardType = "PASMO",    CardNumber = "P-001", CurrentBalance = 0 },    // 警告
        };

        // Act
        var warnings = _service.CheckLowBalanceWarnings(items, warningBalance: 1000);

        // Assert
        warnings.Should().HaveCount(3, "残高999/1000/0が警告対象（閾値ちょうども含む）");
        warnings.Should().Contain(w => w.CardIdm == "A");
        warnings.Should().Contain(w => w.CardIdm == "B", "閾値ちょうどは<=なので警告対象");
        warnings.Should().Contain(w => w.CardIdm == "D");
        warnings.Should().NotContain(w => w.CardIdm == "C", "閾値超過は警告対象外");
    }

    [Fact]
    public void CheckLowBalanceWarnings_警告アイテムにカード種別と番号と残額が含まれること()
    {
        // Arrange
        var items = new[]
        {
            new CardBalanceDashboardItem { CardIdm = "0102030405060708", CardType = "はやかけん", CardNumber = "H-001", CurrentBalance = 500 }
        };

        // Act
        var warnings = _service.CheckLowBalanceWarnings(items, warningBalance: 1000);

        // Assert
        warnings.Should().HaveCount(1);
        var warning = warnings[0];
        warning.Type.Should().Be(WarningType.LowBalance);
        warning.CardIdm.Should().Be("0102030405060708");
        warning.DisplayText.Should().Contain("はやかけん");
        warning.DisplayText.Should().Contain("H-001");
        warning.DisplayText.Should().Contain("500");
        warning.DisplayText.Should().Contain("1,000", "しきい値も表示される");
    }

    [Fact]
    public void CheckLowBalanceWarnings_空リストでも例外なく空のリストを返す()
    {
        var warnings = _service.CheckLowBalanceWarnings(Array.Empty<CardBalanceDashboardItem>(), 1000);

        warnings.Should().BeEmpty();
    }

    [Fact]
    public void CheckLowBalanceWarnings_全カードが閾値以上なら警告ゼロ件()
    {
        var items = new[]
        {
            new CardBalanceDashboardItem { CardIdm = "A", CurrentBalance = 5000 },
            new CardBalanceDashboardItem { CardIdm = "B", CurrentBalance = 10000 }
        };

        var warnings = _service.CheckLowBalanceWarnings(items, 1000);

        warnings.Should().BeEmpty();
    }

    #endregion

    #region CheckIncompleteBusStopsAsync — バス停未入力検出

    [Fact]
    public async Task CheckIncompleteBusStopsAsync_星マークを含む摘要をカウントすること()
    {
        // Arrange — 過去1年分の履歴のうち3件に「★」を含む摘要が含まれる
        var ledgers = new List<Ledger>
        {
            new() { Summary = "鉄道（博多～天神）" },
            new() { Summary = "バス（★）" },
            new() { Summary = "バス（★）、鉄道（博多～天神）" },
            new() { Summary = "鉄道（博多～天神 往復）" },
            new() { Summary = "バス（★）" }
        };
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        var warning = await _service.CheckIncompleteBusStopsAsync();

        // Assert
        warning.Should().NotBeNull();
        warning!.Type.Should().Be(WarningType.IncompleteBusStop);
        warning.DisplayText.Should().Contain("3", "★を含む3件がカウントされる");
    }

    [Fact]
    public async Task CheckIncompleteBusStopsAsync_該当0件の場合はnullを返すこと()
    {
        // Arrange
        var ledgers = new List<Ledger>
        {
            new() { Summary = "鉄道（博多～天神）" },
            new() { Summary = "鉄道（博多～天神 往復）" }
        };
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act
        var warning = await _service.CheckIncompleteBusStopsAsync();

        // Assert
        warning.Should().BeNull("★を含む摘要がない場合はnull");
    }

    [Fact]
    public async Task CheckIncompleteBusStopsAsync_Summaryがnullのレコードを安全に扱うこと()
    {
        // Arrange: null/空/有効な★の混在パターン
        var ledgers = new List<Ledger>
        {
            new() { Summary = null },
            new() { Summary = "" },
            new() { Summary = "バス（★）" }
        };
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);

        // Act: null混在でも例外は発生せず、★を含む1件のみカウントされる
        var warning = await _service.CheckIncompleteBusStopsAsync();

        // Assert
        warning.Should().NotBeNull("★を含む1件は検出される（null/空はnull安全に無視）");
        warning!.DisplayText.Should().Contain("1", "カウントは1件");
    }

    [Fact]
    public async Task CheckIncompleteBusStopsAsync_過去1年間の範囲で問い合わせること()
    {
        // Arrange
        DateTime? capturedFrom = null;
        DateTime? capturedTo = null;
        _ledgerRepositoryMock
            .Setup(r => r.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .Callback<string, DateTime, DateTime>((_, from, to) =>
            {
                capturedFrom = from;
                capturedTo = to;
            })
            .ReturnsAsync(new List<Ledger>());

        // Act
        await _service.CheckIncompleteBusStopsAsync();

        // Assert: 期間が「現在から1年前 〜 現在」になっている
        capturedFrom.Should().NotBeNull();
        capturedTo.Should().NotBeNull();
        var elapsedYears = (capturedTo!.Value - capturedFrom!.Value).TotalDays / 365.0;
        elapsedYears.Should().BeApproximately(1.0, 0.05, "おおよそ1年間の範囲で取得すること");
    }

    #endregion

    #region CheckJournalModeWarning

    [Fact]
    public void CheckJournalModeWarning_劣化していない場合はnullを返す()
    {
        _databaseInfoMock.SetupGet(d => d.IsJournalModeDegraded).Returns(false);

        var warning = _service.CheckJournalModeWarning();

        warning.Should().BeNull("劣化していない場合は警告なし");
    }

    [Fact]
    public void CheckJournalModeWarning_劣化している場合はジャーナルモード名を含む警告を返す()
    {
        _databaseInfoMock.SetupGet(d => d.IsJournalModeDegraded).Returns(true);
        _databaseInfoMock.SetupGet(d => d.CurrentJournalMode).Returns("memory");

        var warning = _service.CheckJournalModeWarning();

        warning.Should().NotBeNull();
        warning!.Type.Should().Be(WarningType.DatabaseJournalModeDegraded);
        warning.DisplayText.Should().Contain("memory", "現在のジャーナルモードがメッセージに含まれる");
        warning.DisplayText.Should().Contain("クラッシュ耐性");
    }

    #endregion
}
