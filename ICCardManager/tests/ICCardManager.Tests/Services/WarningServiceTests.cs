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
        // Issue #2106: Contain("3") は「13件」「3,000件」にも一致する。区切り（「が」「件」）を含む完全な文言で比べる
        warning.DisplayText.Should().Be("⚠️ バス停名が未入力の履歴が3件あります", "★を含む3件がカウントされる");
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
        // Issue #2106: Contain("1") は「11件」「21件」にも一致するため、完全な文言で比べる
        warning!.DisplayText.Should().Be("⚠️ バス停名が未入力の履歴が1件あります", "カウントは1件");
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

        // Act: 本体は DateTime.Now を直接読むため、呼び出しの前後の時刻で挟んで起点を確定する
        var before = DateTime.Now;
        await _service.CheckIncompleteBusStopsAsync();
        var after = DateTime.Now;

        // Assert: 期間が「現在から1年前 〜 現在」になっている。
        // Issue #2106: 旧版は幅（約 1 年）しか見ておらず、期間がまるごと過去へずれても
        // （例: 2 年前〜1 年前）緑だった。起点と終点をそれぞれ呼び出し時刻の前後で挟む。
        capturedFrom.Should().NotBeNull();
        capturedTo.Should().NotBeNull();
        capturedTo!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after, "終点は呼び出した時点の現在時刻");
        capturedFrom!.Value.Should().BeOnOrAfter(before.AddYears(-1)).And.BeOnOrBefore(
            after.AddYears(-1), "起点は呼び出した時点のちょうど 1 年前（日付単位へ丸めない）");
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

    #region CheckUpdateNotificationWarning — 更新通知（Issue #1687）

    [Fact]
    public void CheckUpdateNotificationWarning_新バージョンがある場合は両バージョンを含む通知を返す()
    {
        var updateServiceMock = new Mock<IUpdateNotificationService>();
        updateServiceMock.Setup(x => x.CheckForNewerVersion()).Returns(new UpdateCheckResult
        {
            LatestVersion = "2.11.0",
            CurrentVersion = "2.10.0",
        });
        var service = new WarningService(
            _ledgerRepositoryMock.Object, _databaseInfoMock.Object, updateServiceMock.Object);

        var warning = service.CheckUpdateNotificationWarning();

        warning.Should().NotBeNull();
        warning!.Type.Should().Be(WarningType.NewVersionAvailable);
        warning.DisplayText.Should().Contain("2.11.0", "新しいバージョンを明示する");
        warning.DisplayText.Should().Contain("2.10.0", "このPCの現在バージョンも併記する");
        warning.DisplayText.Should().Contain("ご確認ください", "行動指示で終わる");
    }

    [Fact]
    public void CheckUpdateNotificationWarning_更新がない場合はnullを返す()
    {
        var updateServiceMock = new Mock<IUpdateNotificationService>();
        updateServiceMock.Setup(x => x.CheckForNewerVersion()).Returns((UpdateCheckResult)null);
        var service = new WarningService(
            _ledgerRepositoryMock.Object, _databaseInfoMock.Object, updateServiceMock.Object);

        service.CheckUpdateNotificationWarning().Should().BeNull();
    }

    [Fact]
    public void CheckUpdateNotificationWarning_サービス未注入の場合はnullを返す()
    {
        // 既存の2引数構築（テスト互換経路）では更新通知は常に無効
        _service.CheckUpdateNotificationWarning().Should().BeNull();
    }

    #endregion
}
