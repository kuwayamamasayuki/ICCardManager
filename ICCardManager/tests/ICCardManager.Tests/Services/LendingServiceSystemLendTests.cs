using System;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1909: システム操作による貸出記録作成のためにLendAsyncへ追加した
/// <c>lentAt</c>（貸出日時の任意指定）と <c>armRetouchWindow</c>（30秒ルールの武装可否）の単体テスト。
/// </summary>
/// <remarks>
/// 日時の検証は純関数 <c>LendingService.ValidateSystemLendDateTime</c> に切り出してあるため、
/// 境界値はDBもモックも介さずに固定できる。
/// </remarks>
public class LendingServiceSystemLendTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly CardLockManager _lockManager;
    private readonly FakeClock _clock;

    private const string CardIdm = "0102030405060708";
    private const string StaffIdm = "1112131415161718";

    /// <summary>任意の固定基準時刻（月境界・曜日の影響を避けるため月中の正午）</summary>
    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0);

    public LendingServiceSystemLendTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        _clock = new FakeClock(Now);

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false))
            .ReturnsAsync(new IcCard
            {
                CardIdm = CardIdm,
                CardType = "はやかけん",
                CardNumber = "C001",
                IsLent = false,
                IsDeleted = false
            });
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdm, It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(StaffIdm, false))
            .ReturnsAsync(new Staff { StaffIdm = StaffIdm, Name = "博多 花子", IsDeleted = false });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private LendingService CreateService()
    {
        return new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new SummaryGenerator(),
            _lockManager,
            Options.Create(new AppOptions()),
            NullLogger<LendingService>.Instance,
            _clock);
    }

    private void SetupLatestLedger(DateTime? date)
    {
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(CardIdm))
            .ReturnsAsync(date.HasValue
                ? new Ledger { CardIdm = CardIdm, Date = date.Value, Balance = 3000 }
                : null);
    }

    // ============================================================
    // ValidateSystemLendDateTime（純関数・境界値）
    // ============================================================

    [Fact]
    public void ValidateSystemLendDateTime_現在時刻ちょうどは許容する()
    {
        var error = LendingService.ValidateSystemLendDateTime(Now, Now, null);

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateSystemLendDateTime_未来の日時はエラーになる()
    {
        var error = LendingService.ValidateSystemLendDateTime(Now.AddSeconds(1), Now, null);

        error.Should().NotBeNull();
        error.Should().Contain("貸出日時");
        error.Should().EndWith("入力してください。");
    }

    [Fact]
    public void ValidateSystemLendDateTime_直近履歴と同じ日時は許容する()
    {
        var latest = Now.AddDays(-3);

        var error = LendingService.ValidateSystemLendDateTime(latest, Now, latest);

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateSystemLendDateTime_直近履歴より前の日時はエラーになる()
    {
        var latest = Now.AddDays(-3);

        var error = LendingService.ValidateSystemLendDateTime(latest.AddSeconds(-1), Now, latest);

        error.Should().NotBeNull();
        error.Should().EndWith("入力してください。");
    }

    [Fact]
    public void ValidateSystemLendDateTime_履歴が無いカードは過去日時でも許容する()
    {
        var error = LendingService.ValidateSystemLendDateTime(Now.AddYears(-1), Now, null);

        error.Should().BeNull();
    }

    /// <summary>
    /// 未来と「直近履歴より前」は原因が異なるため、互いの文言を含まないこと
    /// （error-messages.md「取り違えを検出する」）。
    /// </summary>
    [Fact]
    public void ValidateSystemLendDateTime_未来と過去で異なる理由を述べる()
    {
        var latest = Now.AddDays(-3);
        var futureError = LendingService.ValidateSystemLendDateTime(Now.AddDays(1), Now, latest);
        var tooOldError = LendingService.ValidateSystemLendDateTime(latest.AddDays(-1), Now, latest);

        futureError.Should().NotBe(tooOldError);
        futureError.Should().Contain("未来");
        tooOldError.Should().NotContain("未来");
    }

    // ============================================================
    // LendAsync（lentAt の反映）
    // ============================================================

    [Fact]
    public async Task LendAsync_lentAt指定時は台帳の日付と貸出日時に反映される()
    {
        var lentAt = Now.AddDays(-2).Date.AddHours(9);
        SetupLatestLedger(Now.AddDays(-5));
        Ledger captured = null;
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => captured = l)
            .ReturnsAsync(1);

        var result = await CreateService().LendAsync(StaffIdm, CardIdm, 3000, lentAt);

        result.Success.Should().BeTrue();
        captured.Should().NotBeNull();
        captured.Date.Should().Be(lentAt);
        captured.LentAt.Should().Be(lentAt);
        captured.IsLentRecord.Should().BeTrue();
        captured.LenderIdm.Should().Be(StaffIdm);
        captured.StaffName.Should().Be("博多 花子");
    }

    [Fact]
    public async Task LendAsync_lentAt指定時はカードの最終貸出日時にも反映される()
    {
        var lentAt = Now.AddDays(-2).Date.AddHours(9);
        SetupLatestLedger(Now.AddDays(-5));

        await CreateService().LendAsync(StaffIdm, CardIdm, 3000, lentAt);

        _cardRepositoryMock.Verify(
            r => r.UpdateLentStatusAsync(CardIdm, true, lentAt, StaffIdm), Times.Once);
    }

    [Fact]
    public async Task LendAsync_lentAt省略時は現在時刻が使われる()
    {
        Ledger captured = null;
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => captured = l)
            .ReturnsAsync(1);

        var result = await CreateService().LendAsync(StaffIdm, CardIdm, 3000);

        result.Success.Should().BeTrue();
        captured.Date.Should().Be(Now);
    }

    [Fact]
    public async Task LendAsync_lentAtが不正なら記録せずエラーを返す()
    {
        SetupLatestLedger(Now.AddDays(-1));

        var result = await CreateService().LendAsync(StaffIdm, CardIdm, 3000, Now.AddDays(-5));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().EndWith("入力してください。");
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        _cardRepositoryMock.Verify(
            r => r.UpdateLentStatusAsync(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<DateTime?>(), It.IsAny<string?>()), Times.Never);
    }

    // ============================================================
    // LendAsync（30秒ルールの武装）
    // ============================================================

    /// <summary>
    /// 既定（物理タッチ経路）では従来どおり武装する。
    /// armRetouchWindow=false 側だけを固定すると、武装そのものを消した実装でも緑になるため対で表明する。
    /// </summary>
    [Fact]
    public async Task LendAsync_既定では30秒ルールを武装する()
    {
        var service = CreateService();

        await service.LendAsync(StaffIdm, CardIdm, 3000);

        service.LastProcessedCardIdm.Should().Be(CardIdm);
        service.LastOperationType.Should().Be(LendingOperationType.Lend);
    }

    /// <summary>
    /// システム操作では物理タッチが1度も起きていないため、再タッチ窓を開かない。
    /// 武装すると、借用者が30秒以内に戻ってタッチしたとき「返却」ではなく
    /// 「貸出の逆処理（取り消し）」が走ってしまう。
    /// </summary>
    [Fact]
    public async Task LendAsync_armRetouchWindowがfalseなら30秒ルールを武装しない()
    {
        var service = CreateService();
        SetupLatestLedger(Now.AddDays(-5));

        var result = await service.LendAsync(
            StaffIdm, CardIdm, 3000, Now.AddHours(-2), armRetouchWindow: false);

        result.Success.Should().BeTrue();
        service.LastProcessedCardIdm.Should().BeNull();
        service.LastProcessedTime.Should().BeNull();
    }

    /// <summary>
    /// 武装しないことが、直後の物理タッチを逆処理にしないことを表明する
    /// （状態値ではなく、次の操作の判定結果で固定する）。
    /// </summary>
    [Fact]
    public async Task LendAsync_システム操作の直後の再タッチは逆処理と判定されない()
    {
        var service = CreateService();
        SetupLatestLedger(Now.AddDays(-5));

        await service.LendAsync(StaffIdm, CardIdm, 3000, Now.AddHours(-2), armRetouchWindow: false);

        service.IsRetouchWithinTimeout(CardIdm).Should().BeFalse();
    }

    /// <summary>固定時計。境界値を wall-clock のジッタに依存させないため（Issue #1626 と同じ方針）。</summary>
    private sealed class FakeClock : ISystemClock
    {
        public FakeClock(DateTime now) => Now = now;

        public DateTime Now { get; set; }
    }
}
