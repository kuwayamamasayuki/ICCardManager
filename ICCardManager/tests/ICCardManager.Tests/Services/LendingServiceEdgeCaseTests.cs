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
/// LendingServiceTestsでカバーされない真のエッジケース
/// (null安全性、削除済みカード、ゼロ秒ウィンドウ、ClearHistory) のみを扱う。
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
}
