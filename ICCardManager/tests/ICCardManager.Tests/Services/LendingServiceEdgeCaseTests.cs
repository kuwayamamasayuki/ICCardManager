using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
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
    private readonly FixedSystemClock _clock = new FixedSystemClock(new DateTime(2026, 3, 10, 9, 0, 0));

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
            NullLogger<LendingService>.Instance,
            _clock);
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
    /// <remarks>
    /// Issue #2105: 以前は生成直後のインスタンス（全フィールドが最初から null）に対して呼んでいたため、
    /// <c>ClearHistory</c> の本体を空にしても緑だった。貸出で 30 秒ルールを武装してから消す。
    /// </remarks>
    [Fact]
    public async Task ClearHistory_ResetsAllFields()
    {
        var service = CreateService();
        SetupCardAndStaff();
        var lend = await service.LendAsync("STAFF00000000001", "0102030405060708");
        lend.Success.Should().BeTrue(lend.ErrorMessage);
        // 前提: 貸出で 3 つとも値を持っていること（持っていなければ「消した」ことを観測できない）
        service.LastProcessedCardIdm.Should().Be("0102030405060708");
        service.LastProcessedTime.Should().Be(_clock.Now);
        service.LastOperationType.Should().Be(LendingOperationType.Lend);
        service.IsRetouchWithinTimeout("0102030405060708").Should().BeTrue();

        service.ClearHistory();

        service.LastProcessedCardIdm.Should().BeNull();
        service.LastProcessedTime.Should().BeNull();
        service.LastOperationType.Should().BeNull();
        service.IsRetouchWithinTimeout("0102030405060708").Should().BeFalse(
            "履歴を消した後の再タッチは逆処理にならないこと");
    }

    /// <summary>
    /// 論理削除されたカードの場合、エラーが返ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2105: 以前は職員のセットアップを呼んでおらず、「職員証が登録されていません」で
    /// 失敗しているだけだった（表明は <c>Success == false</c> のみ）。
    /// 削除済みカードを弾く実体は、リポジトリの <c>includeDeleted: false</c>
    /// （<c>WHERE is_deleted = 0</c>。<c>CardRepositoryTests.GetByIdmAsync_DeletedCard_ReturnsNull</c> が固定）で、
    /// <see cref="LendingService"/> 側に <c>IsDeleted</c> の判定は無い。
    /// </para>
    /// <para>
    /// そこでモックにリポジトリの契約（削除済みは <c>includeDeleted: true</c> のときだけ返る）を再現させ、
    /// 職員は登録済みにしておく。<c>includeDeleted: true</c> で引く実装へ変わると、
    /// 削除済みカードが貸し出されて赤になる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LendAsync_DeletedCard_ReturnsError()
    {
        var service = CreateService();
        SetupCardAndStaff();
        var deletedCard = new IcCard { CardIdm = "0102030405060708", CardType = "はやかけん", IsDeleted = true };
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", false)).ReturnsAsync((IcCard)null);
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync("0102030405060708", true)).ReturnsAsync(deletedCard);

        var result = await service.LendAsync("STAFF00000000001", "0102030405060708");

        result.Success.Should().BeFalse();
        // 利用者向け文言は将来「交通系ICカード」表記へ直り得るため、完全一致ではなく
        // 「理由の取り違えが無いこと」（カードの未登録であって職員証ではない）を表明する
        result.ErrorMessage.Should().Contain("カード").And.Contain("登録されていません");
        result.ErrorMessage.Should().NotContain("職員証",
            "失敗の理由が職員証の未登録ではなく、カードが見つからない（削除済みを除外した）ことであること");
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync("0102030405060708", false), Times.Once);
        _cardRepositoryMock.Verify(r => r.GetByIdmAsync(It.IsAny<string>(), true), Times.Never,
            "削除済みを含めて引くと、削除したカードを貸し出せてしまう");
        _cardRepositoryMock.Verify(r => r.UpdateLentStatusAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// 上のテストの対: 同じ準備で、カードが未削除なら貸出が成功すること
    /// （準備が「何をしても失敗する」形になっていないことの表明）。
    /// </summary>
    [Fact]
    public async Task LendAsync_ActiveCardWithSameSetup_Succeeds()
    {
        var service = CreateService();
        SetupCardAndStaff();

        var result = await service.LendAsync("STAFF00000000001", "0102030405060708");

        result.Success.Should().BeTrue(result.ErrorMessage);
    }

    /// <summary>
    /// RetouchWindowSecondsが0の場合、同じ瞬間の再タッチだけが逆処理の対象になり、
    /// 1 秒でも経過すれば対象外になること。
    /// </summary>
    /// <remarks>
    /// Issue #2105: 以前は壁時計のまま <c>NotThrow</c> だけを見ていた（「直後なら true/false どちらもありうる」）。
    /// 固定時計を注入すれば境界は決定論的に決まるので、値で表明する。
    /// </remarks>
    [Fact]
    public async Task IsRetouchWithinTimeout_ZeroWindowSeconds_OnlySameInstantIsWithin()
    {
        var service = CreateService(retouchWindowSeconds: 0);
        SetupCardAndStaff();

        var lend = await service.LendAsync("STAFF00000000001", "0102030405060708");
        lend.Success.Should().BeTrue(lend.ErrorMessage);

        service.IsRetouchWithinTimeout("0102030405060708").Should().BeTrue(
            "経過 0 秒は「0 秒以内」に含まれる（判定は elapsed <= しきい値）");

        _clock.Now = _clock.Now.AddSeconds(1);
        service.IsRetouchWithinTimeout("0102030405060708").Should().BeFalse(
            "しきい値 0 秒では 1 秒後の再タッチは逆処理にならない");
    }
}
