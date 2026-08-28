using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using ICCardManager.Views.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #1909: カード管理画面（F3）の「貸出記録の作成」コマンドのテスト。
/// </summary>
/// <remarks>
/// ダイアログの ViewModel を呼び出し元が生成する（<c>Func&lt;SystemLendViewModel&gt;</c>）ため、
/// <c>Window</c> を実体化せずに成功経路まで検証できる。
/// </remarks>
public class CardManageViewModelSystemLendTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock = new();
    private readonly Mock<ICardReader> _cardReaderMock = new();
    private readonly Mock<IValidationService> _validationServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<INavigationService> _navigationServiceMock = new();
    private readonly CardLockManager _lockManager;
    private readonly LendingService _lendingService;
    private readonly CardManageViewModel _viewModel;
    private readonly FakeClock _clock;

    /// <summary>ファクトリが生成した最新の ViewModel（テストから操作するため保持する）</summary>
    private SystemLendViewModel? _createdSystemLendViewModel;

    private const string CardIdm = "0102030405060708";
    private const string BorrowerIdm = "1112131415161718";

    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0);

    public CardManageViewModelSystemLendTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);
        _clock = new FakeClock(Now);

        _lendingService = new LendingService(
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

        _cardRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<IcCard>());
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false)).ReturnsAsync(CreateCard(isLent: false));
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                CardIdm, It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new[]
        {
            new Staff { StaffIdm = BorrowerIdm, Name = "博多 花子", IsDeleted = false }
        });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(BorrowerIdm, false))
            .ReturnsAsync(new Staff { StaffIdm = BorrowerIdm, Name = "博多 花子", IsDeleted = false });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(42);
        _ledgerRepositoryMock.Setup(r => r.GetLatestLedgerAsync(CardIdm))
            .ReturnsAsync(new Ledger { CardIdm = CardIdm, Date = Now.AddDays(-5), Balance = 3000 });
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>())).ReturnsAsync(1);

        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "2122232425262728", StaffName = "天神 太郎" });

        _viewModel = new CardManageViewModel(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _cardReaderMock.Object,
            _validationServiceMock.Object,
            new OperationLogger(_operationLogRepositoryMock.Object, new CurrentOperatorContext(_clock)),
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _lendingService,
            new WeakReferenceMessenger(),
            new ICCardManager.Tests.Infrastructure.Timing.RecordingDispatcherService(),
            _navigationServiceMock.Object,
            CreateSystemLendViewModel);

        _viewModel.SelectedCard = new CardDto
        {
            CardIdm = CardIdm,
            CardType = "はやかけん",
            CardNumber = "C001",
            IsLent = false,
            IsRefunded = false
        };
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private IcCard CreateCard(bool isLent) => new()
    {
        CardIdm = CardIdm,
        CardType = "はやかけん",
        CardNumber = "C001",
        IsLent = isLent,
        IsDeleted = false
    };

    private SystemLendViewModel CreateSystemLendViewModel()
    {
        _createdSystemLendViewModel = new SystemLendViewModel(
            _staffRepositoryMock.Object,
            _lendingService,
            new OperationLogger(_operationLogRepositoryMock.Object, new CurrentOperatorContext(_clock)),
            _clock,
            NullLogger<SystemLendViewModel>.Instance);
        return _createdSystemLendViewModel;
    }

    /// <summary>
    /// ダイアログの表示を模して、生成された ViewModel で実際に作成を行わせる。
    /// </summary>
    private void ArrangeDialogSaves()
    {
        _navigationServiceMock
            .Setup(n => n.ShowDialog(It.IsAny<Action<SystemLendDialog>>()))
            .Returns(() =>
            {
                var vm = _createdSystemLendViewModel
                    ?? throw new InvalidOperationException(
                        "ダイアログ表示時点で ViewModel が生成されていない（呼び出し元がファクトリを通っていない）");
                vm.SelectedStaff = vm.StaffList[0];
                vm.SaveAsync().GetAwaiter().GetResult();
                return vm.IsCompleted ? true : (bool?)false;
            });
    }

    // ============================================================
    // 実行可否
    // ============================================================

    [Fact]
    public void CanExecute_未払戻のカードが選択されていれば実行できる()
    {
        _viewModel.CreateLendRecordCommand.CanExecute(null).Should().BeTrue();

        _viewModel.SelectedCard = new CardDto { CardIdm = CardIdm, IsRefunded = true };
        _viewModel.CreateLendRecordCommand.CanExecute(null).Should().BeFalse();

        _viewModel.SelectedCard = null;
        _viewModel.CreateLendRecordCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Issue #1109: 貸出中は <c>CanExecute</c> で無効化しない。共有モードのヘルスチェックが
    /// <c>SelectedCard.IsLent</c> を書き換えると、ボタンが無言で押せなくなり職員に何も伝わらない。
    /// `CanDelete` / `CanRefund` と同じ扱いに揃える。
    /// </summary>
    [Fact]
    public void CanExecute_貸出中でもボタンを無効化しない()
    {
        _viewModel.SelectedCard = new CardDto { CardIdm = CardIdm, IsLent = true };

        _viewModel.CreateLendRecordCommand.CanExecute(null).Should().BeTrue();
    }

    /// <summary>
    /// 貸出中であることは、認証を求める前にダイアログで伝える（対の表明）。
    /// これが無いと、上のテストは「押せるが何も起きない」実装でも緑になる。
    /// </summary>
    [Fact]
    public async Task CreateLendRecordAsync_貸出中なら認証を求めずダイアログで伝える()
    {
        _viewModel.SelectedCard = new CardDto { CardIdm = CardIdm, IsLent = true };

        await _viewModel.CreateLendRecordAsync();

        _dialogServiceMock.Verify(
            d => d.ShowError(It.Is<string>(m => m.Contains("既に貸出中")), It.IsAny<string>()),
            Times.Once);
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    // ============================================================
    // 認証・競合
    // ============================================================

    [Fact]
    public async Task CreateLendRecordAsync_認証をキャンセルしたら何も書かない()
    {
        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult)null);

        await _viewModel.CreateLendRecordAsync();

        _navigationServiceMock.Verify(
            n => n.ShowDialog(It.IsAny<Action<SystemLendDialog>>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
    }

    /// <summary>
    /// 認証の待機中に他 PC が貸し出した場合、ダイアログを開かずに案内する（Issue #1760）。
    /// </summary>
    [Fact]
    public async Task CreateLendRecordAsync_認証中に他PCが貸し出していたら開かずに案内する()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false)).ReturnsAsync(CreateCard(isLent: true));

        await _viewModel.CreateLendRecordAsync();

        _navigationServiceMock.Verify(
            n => n.ShowDialog(It.IsAny<Action<SystemLendDialog>>()), Times.Never);
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("既に貸出中");
        _viewModel.StatusMessage.Should().EndWith("確認してください。");
    }

    /// <summary>
    /// 認証の待機中に他 PC が払い戻した場合も、ダイアログを開かずに案内する。
    /// `LendingService.ValidateLendPreconditionsAsync` は `is_refunded` を見ないため、
    /// ここで弾かないと払戻済カード（Issue #530 で貸出対象外）が貸出中になる。
    /// </summary>
    [Fact]
    public async Task CreateLendRecordAsync_認証中に他PCが払い戻していたら開かずに案内する()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false))
            .ReturnsAsync(new IcCard
            {
                CardIdm = CardIdm,
                CardType = "はやかけん",
                CardNumber = "C001",
                IsLent = false,
                IsRefunded = true,
                IsDeleted = false
            });

        await _viewModel.CreateLendRecordAsync();

        _navigationServiceMock.Verify(
            n => n.ShowDialog(It.IsAny<Action<SystemLendDialog>>()), Times.Never);
        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain("既に払戻済");
        _viewModel.StatusMessage.Should().NotContain("既に貸出中");
    }

    /// <summary>
    /// Issue #1759 / #1760: 「再読み込みしました」と案内する以上、先にキャッシュを破棄する。
    /// 破棄しないと `GetAllAsync` のキャッシュ（既定 TTL 60 秒／共有モード 15 秒）から
    /// 貸出中になる前の一覧が返り、案内が事実にならない。
    /// </summary>
    [Fact]
    public async Task CreateLendRecordAsync_競合を検出したらキャッシュを破棄してから再読込する()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false)).ReturnsAsync(CreateCard(isLent: true));

        await _viewModel.CreateLendRecordAsync();

        _cardRepositoryMock.Verify(r => r.InvalidateCache(), Times.Once);
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateLendRecordAsync_対象カードが削除されていたら開かずに案内する()
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(CardIdm, false)).ReturnsAsync((IcCard)null);

        await _viewModel.CreateLendRecordAsync();

        _navigationServiceMock.Verify(
            n => n.ShowDialog(It.IsAny<Action<SystemLendDialog>>()), Times.Never);
        _viewModel.IsStatusError.Should().BeTrue();
        _cardRepositoryMock.Verify(r => r.InvalidateCache(), Times.Once);
    }

    // ============================================================
    // 成功経路
    // ============================================================

    [Fact]
    public async Task CreateLendRecordAsync_作成に成功したら一覧を再読込して完了を伝える()
    {
        ArrangeDialogSaves();

        await _viewModel.CreateLendRecordAsync();

        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Once);
        // 一覧の再読込（初期化時は呼ばれないため、この 1 回はコマンド由来）
        _cardRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.StatusMessage.Should().Contain("博多 花子");
        _viewModel.StatusMessage.Should().Contain("記録しました");
    }

    /// <summary>
    /// 完了メッセージは <c>CancelEdit()</c> のあとに設定する（Issue #1727 / #1764）。
    /// 先に設定すると一度も表示されない。
    /// </summary>
    [Fact]
    public async Task CreateLendRecordAsync_編集中に実行しても完了メッセージが残る()
    {
        ArrangeDialogSaves();
        _viewModel.StartEdit();

        await _viewModel.CreateLendRecordAsync();

        _viewModel.IsEditing.Should().BeFalse();
        _viewModel.StatusMessage.Should().NotBeEmpty();
        _viewModel.StatusMessage.Should().Contain("記録しました");
    }

    /// <summary>
    /// 監査ログが残らなかった場合は、通常の完了と同じ見た目にしない。
    /// 記録そのものは確定しているため `IsCompleted` は落とさず、ステータスだけ警告色にする。
    /// </summary>
    [Fact]
    public async Task CreateLendRecordAsync_操作ログが残らなかったら完了扱いだが通常の完了とは区別する()
    {
        _operationLogRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .ThrowsAsync(new InvalidOperationException("操作ログの書き込みに失敗"));
        ArrangeDialogSaves();

        await _viewModel.CreateLendRecordAsync();

        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Once);
        _viewModel.StatusMessage.Should().Contain("記録しました");
        _viewModel.StatusMessage.Should().Contain("操作ログの記録には失敗");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    [Fact]
    public async Task CreateLendRecordAsync_ダイアログをキャンセルしたら書き込まない()
    {
        _navigationServiceMock
            .Setup(n => n.ShowDialog(It.IsAny<Action<SystemLendDialog>>()))
            .Returns((bool?)null);

        await _viewModel.CreateLendRecordAsync();

        _ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        _viewModel.StatusMessage.Should().BeEmpty();
    }

    /// <summary>固定時計。</summary>
    private sealed class FakeClock : ISystemClock
    {
        public FakeClock(DateTime now) => Now = now;

        public DateTime Now { get; set; }
    }
}
