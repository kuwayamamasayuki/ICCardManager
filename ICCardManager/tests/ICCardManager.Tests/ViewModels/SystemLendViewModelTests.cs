using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1909: システム操作による貸出記録作成ダイアログの ViewModel テスト。
/// </summary>
/// <remarks>
/// 借用者（<c>ledger.LenderIdm</c>）と操作者（<c>operation_log.operator_idm</c>）が
/// 別々に記録されることを、実際に挿入された <see cref="OperationLog"/> の中身で表明する
/// （<c>OperationLogger</c> のメソッドは virtual ではないためモックできない。
/// <c>development-conventions.md</c>「ログが残ったかの検証は、ログ記録クラスのモックではできない」）。
/// </remarks>
public class SystemLendViewModelTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock = new();
    private readonly CardLockManager _lockManager;
    private readonly FakeClock _clock;
    private readonly CurrentOperatorContext _operatorContext;
    private readonly List<OperationLog> _recordedLogs = new();

    private const string CardIdm = "0102030405060708";
    private const string BorrowerIdm = "1112131415161718";
    private const string OperatorIdm = "2122232425262728";

    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0);

    private readonly IcCard _card = new()
    {
        CardIdm = CardIdm,
        CardType = "はやかけん",
        CardNumber = "C001",
        IsLent = false,
        IsDeleted = false
    };

    public SystemLendViewModelTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        _clock = new FakeClock(Now);
        _operatorContext = new CurrentOperatorContext(_clock);

        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false)).ReturnsAsync(_card);
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdm, It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(BorrowerIdm, false))
            .ReturnsAsync(new Staff { StaffIdm = BorrowerIdm, Name = "博多 花子", IsDeleted = false });
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new[]
        {
            new Staff { StaffIdm = BorrowerIdm, Name = "博多 花子", IsDeleted = false },
            new Staff { StaffIdm = OperatorIdm, Name = "天神 太郎", IsDeleted = false }
        });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(42);
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(CardIdm))
            .ReturnsAsync(new Ledger { CardIdm = CardIdm, Date = Now.AddDays(-5), Balance = 3000 });
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Callback<OperationLog>(log => _recordedLogs.Add(log))
            .ReturnsAsync(1);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private SystemLendViewModel CreateViewModel()
    {
        var lendingService = new LendingService(
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

        return new SystemLendViewModel(
            _staffRepositoryMock.Object,
            lendingService,
            new OperationLogger(_operationLogRepositoryMock.Object, _operatorContext),
            _clock,
            NullLogger<SystemLendViewModel>.Instance);
    }

    private async Task<SystemLendViewModel> CreateInitializedAsync()
    {
        var vm = CreateViewModel();
        await vm.InitializeAsync(_card);
        return vm;
    }

    // ============================================================
    // 初期化
    // ============================================================

    [Fact]
    public async Task InitializeAsync_在籍職員が借用者候補に並ぶ()
    {
        var vm = await CreateInitializedAsync();

        vm.StaffList.Should().HaveCount(2);
        vm.StaffList.Select(s => s.StaffIdm).Should().Contain(BorrowerIdm);
    }

    [Fact]
    public async Task InitializeAsync_貸出日時の既定は現在時刻になる()
    {
        var vm = await CreateInitializedAsync();

        vm.LentDate.Should().Be(Now.Date);
        vm.LentTimeText.Should().Be(Now.ToString("HH:mm"));
    }

    [Fact]
    public async Task InitializeAsync_対象カードの表示名を保持する()
    {
        var vm = await CreateInitializedAsync();

        vm.CardDisplayName.Should().Contain("はやかけん");
        vm.CardDisplayName.Should().Contain("C001");
    }

    // ============================================================
    // 入力の検証
    // ============================================================

    [Fact]
    public async Task SaveAsync_借用者未選択なら記録せず案内する()
    {
        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = null;

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().EndWith("選択してください。");
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9")]
    [InlineData("25:00")]
    [InlineData("あさ")]
    public async Task SaveAsync_時刻が読み取れないなら記録せず案内する(string timeText)
    {
        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);
        vm.LentTimeText = timeText;

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().EndWith("入力してください。");
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// `DatePicker.SelectedDate` は `DateTime?` のため、日付欄を空にすると null が入る。
    /// 非 Null 許容で受けるとバインドが黙って失敗し、画面は空なのに直前の日付で記録される。
    /// </summary>
    [Fact]
    public async Task SaveAsync_貸出日が空なら記録せず案内する()
    {
        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);
        vm.LentDate = null;

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Contain("貸出日");
        vm.StatusMessage.Should().EndWith("選択してください。");
        // 時刻欄のエラーと取り違えないこと（原因が違えば「どうすれば」も違う）
        vm.StatusMessage.Should().NotContain("貸出時刻");
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// サービス側の日時検証（未来・直近履歴より前）の文言がそのまま画面に出ること。
    /// 「保存できません」と丸めると原因へ到達できない（error-messages.md「なぜ」）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_サービスの検証エラーをそのまま案内しダイアログを閉じない()
    {
        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);
        vm.LentDate = Now.AddDays(-10).Date;

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Contain("直近の履歴");
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    // ============================================================
    // 記録
    // ============================================================

    [Fact]
    public async Task SaveAsync_指定した借用者と日時で貸出中レコードを作る()
    {
        Ledger captured = null;
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => captured = l)
            .ReturnsAsync(42);

        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);
        vm.LentDate = Now.AddDays(-1).Date;
        vm.LentTimeText = "09:30";

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeTrue();
        captured.Should().NotBeNull();
        captured.LenderIdm.Should().Be(BorrowerIdm);
        captured.StaffName.Should().Be("博多 花子");
        captured.IsLentRecord.Should().BeTrue();
        captured.Date.Should().Be(Now.AddDays(-1).Date.AddHours(9).AddMinutes(30));
    }

    /// <summary>
    /// 監査ログの操作者は認証した庶務担当者であり、借用者ではない。
    /// 台帳側（借用者）と操作ログ側（操作者）が別人として残ることを対で表明する。
    /// </summary>
    [Fact]
    public async Task SaveAsync_操作ログには認証した操作者が借用者と別に記録される()
    {
        _operatorContext.BeginSession(OperatorIdm, "天神 太郎");

        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);

        await vm.SaveAsync();

        _recordedLogs.Should().HaveCount(1);
        var log = _recordedLogs[0];
        log.OperatorIdm.Should().Be(OperatorIdm);
        log.OperatorName.Should().Be("天神 太郎");
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        log.TargetId.Should().Be("42");
        log.AfterData.Should().Contain(BorrowerIdm);
    }

    /// <summary>
    /// 記録済みの操作を、コミット確定後の後処理（操作ログ記録）の失敗で取り消さない
    /// （development-conventions.md「コミット確定後の後処理を、成否の判定に巻き込まない」）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_操作ログの記録に失敗しても貸出記録は確定扱いにする()
    {
        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .ThrowsAsync(new InvalidOperationException("操作ログの書き込みに失敗"));

        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeTrue();
        vm.ResultMessage.Should().Contain("記録しました");
        vm.ResultMessage.Should().Contain("操作ログの記録には失敗");
        // 「記録できた」ことと「付帯情報まで揃った」ことを別の値で伝える（#1727 / #1805）
        vm.HasPostCommitFailure.Should().BeTrue();
    }

    /// <summary>
    /// 対の表明。片側だけだと「常に後処理失敗を立てる」実装でも緑になる。
    /// </summary>
    [Fact]
    public async Task SaveAsync_操作ログまで成功したら後処理失敗を立てない()
    {
        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeTrue();
        vm.HasPostCommitFailure.Should().BeFalse();
        vm.ResultMessage.Should().NotContain("失敗");
    }

    /// <summary>
    /// 既に貸出中のカードはサービス側の事前検証で弾かれる（共有モードで他 PC が先に貸し出した場合）。
    /// </summary>
    [Fact]
    public async Task SaveAsync_既に貸出中なら記録せず案内する()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false))
            .ReturnsAsync(new IcCard { CardIdm = CardIdm, CardType = "はやかけん", CardNumber = "C001", IsLent = true });

        var vm = await CreateInitializedAsync();
        vm.SelectedStaff = vm.StaffList.First(s => s.StaffIdm == BorrowerIdm);

        await vm.SaveAsync();

        vm.IsCompleted.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>固定時計。</summary>
    private sealed class FakeClock : ISystemClock
    {
        public FakeClock(DateTime now) => Now = now;

        public DateTime Now { get; set; }
    }
}
