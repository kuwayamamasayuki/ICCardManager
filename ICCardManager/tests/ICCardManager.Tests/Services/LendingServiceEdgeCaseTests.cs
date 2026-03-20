using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LendingServiceのエッジケーステスト
/// 既存テストで検出できない30秒ルール境界値・null引数・タイムアウト境界パターンを検証する。
/// </summary>
public class LendingServiceEdgeCaseTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly CardLockManager _lockManager;

    public LendingServiceEdgeCaseTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _summaryGenerator = new SummaryGenerator();
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        _lockManager?.Dispose();
    }

    private LendingService CreateService(int retouchWindowSeconds = 30)
    {
        return new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _summaryGenerator,
            _lockManager,
            Options.Create(new AppOptions { RetouchWindowSeconds = retouchWindowSeconds }),
            NullLogger<LendingService>.Instance);
    }

    /// <summary>
    /// テスト用にカードと職員のモックを設定するヘルパー
    /// </summary>
    private void SetupCardAndStaff(string cardIdm = "0102030405060708", string staffIdm = "STAFF00000000001")
    {
        var card = new IcCard { CardIdm = cardIdm, CardType = "はやかけん", IsLent = false };
        var staff = new Staff { StaffIdm = staffIdm, Name = "テスト職員" };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, false)).ReturnsAsync(card);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(staffIdm, false)).ReturnsAsync(staff);
        _cardRepositoryMock
            .Setup(r => r.UpdateLentStatusAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
    }

    #region IsRetouchWithinTimeout — 境界値テスト

    /// <summary>
    /// 初期状態（ClearHistory後）ではIsRetouchWithinTimeoutはfalseを返すこと。
    /// </summary>
    [Fact]
    public void IsRetouchWithinTimeout_AfterClearHistory_ReturnsFalse()
    {
        var service = CreateService();
        service.ClearHistory();

        var result = service.IsRetouchWithinTimeout("0102030405060708");

        result.Should().BeFalse("履歴がクリアされた状態なので常にfalse");
    }

    /// <summary>
    /// 異なるカードIDmの場合はfalseを返すこと。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_DifferentCardIdm_ReturnsFalse()
    {
        var service = CreateService();
        SetupCardAndStaff();

        await service.LendAsync("STAFF00000000001", "0102030405060708");

        // 別のカードIDmで確認
        var result = service.IsRetouchWithinTimeout("DIFFERENT_CARD_IDM");

        result.Should().BeFalse("異なるカードIDmなのでfalse");
    }

    /// <summary>
    /// null引数の場合はfalseを返すこと（例外を投げない）。
    /// </summary>
    [Fact]
    public void IsRetouchWithinTimeout_NullCardIdm_ReturnsFalse()
    {
        var service = CreateService();

        var result = service.IsRetouchWithinTimeout(null);

        result.Should().BeFalse();
    }

    #endregion

    #region ClearHistory — 状態リセット

    /// <summary>
    /// ClearHistoryが全てのフィールドをリセットすること。
    /// </summary>
    [Fact]
    public void ClearHistory_ResetsAllFields()
    {
        var service = CreateService();

        service.ClearHistory();

        service.LastProcessedCardIdm.Should().BeNull();
        service.LastProcessedTime.Should().BeNull();
        service.LastOperationType.Should().BeNull();
    }

    #endregion

    #region LendAsync — 未登録カード

    /// <summary>
    /// 未登録カード（GetByIdmAsyncがnull）の場合、エラーメッセージが返ること。
    /// </summary>
    [Fact]
    public async Task LendAsync_UnregisteredCard_ReturnsError()
    {
        var service = CreateService();
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(It.IsAny<string>(), false)).ReturnsAsync((IcCard?)null);

        var result = await service.LendAsync("STAFF00000000001", "UNREGISTERED_CARD");

        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// 論理削除されたカードの場合、エラーが返ること。
    /// </summary>
    [Fact]
    public async Task LendAsync_DeletedCard_ReturnsError()
    {
        var service = CreateService();
        var deletedCard = new IcCard { CardIdm = "0102030405060708", IsDeleted = true };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", It.IsAny<bool>())).ReturnsAsync(deletedCard);

        var result = await service.LendAsync("STAFF00000000001", "0102030405060708");

        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// すでに貸出中のカードを貸出しようとした場合、エラーが返ること。
    /// </summary>
    [Fact]
    public async Task LendAsync_AlreadyLentCard_ReturnsError()
    {
        var service = CreateService();
        var lentCard = new IcCard { CardIdm = "0102030405060708", IsLent = true, CardType = "はやかけん" };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", It.IsAny<bool>())).ReturnsAsync(lentCard);

        var result = await service.LendAsync("STAFF00000000001", "0102030405060708");

        result.Success.Should().BeFalse();
    }

    #endregion

    #region LendAsync — 正常系

    /// <summary>
    /// 正常な貸出フローが成功すること。
    /// </summary>
    [Fact]
    public async Task LendAsync_ValidCard_ReturnsSuccess()
    {
        var service = CreateService();
        SetupCardAndStaff();

        var result = await service.LendAsync("STAFF00000000001", "0102030405060708");

        result.Success.Should().BeTrue();
        result.OperationType.Should().Be(LendingOperationType.Lend);
    }

    #endregion

    #region RetouchWindowSeconds=0 のエッジケース

    /// <summary>
    /// RetouchWindowSecondsが0の場合でも例外が発生しないこと。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_ZeroWindowSeconds_DoesNotThrow()
    {
        var service = CreateService(retouchWindowSeconds: 0);
        SetupCardAndStaff();

        await service.LendAsync("STAFF00000000001", "0102030405060708");

        // RetouchWindowSeconds=0: elapsed <= 0 は直後ならtrue/falseどちらもありうる
        // 重要なのは例外が発生しないこと
        var act = () => service.IsRetouchWithinTimeout("0102030405060708");
        act.Should().NotThrow("RetouchWindowSeconds=0でも例外は発生しない");
    }

    #endregion

    #region LendAsync後の状態更新

    /// <summary>
    /// 貸出成功後にLastProcessedCardIdmが正しく設定されること。
    /// </summary>
    [Fact]
    public async Task LendAsync_Success_SetsLastProcessedCardIdm()
    {
        var service = CreateService();
        SetupCardAndStaff();

        await service.LendAsync("STAFF00000000001", "0102030405060708");

        service.LastProcessedCardIdm.Should().Be("0102030405060708");
        service.LastProcessedTime.Should().NotBeNull();
        service.LastOperationType.Should().Be(LendingOperationType.Lend);
    }

    /// <summary>
    /// 貸出成功直後はIsRetouchWithinTimeoutがtrueを返すこと。
    /// </summary>
    [Fact]
    public async Task LendAsync_Success_IsRetouchWithinTimeout_ReturnsTrue()
    {
        var service = CreateService();
        SetupCardAndStaff();

        await service.LendAsync("STAFF00000000001", "0102030405060708");

        var result = service.IsRetouchWithinTimeout("0102030405060708");

        result.Should().BeTrue("貸出直後なのでタイムアウト内");
    }

    #endregion
}
