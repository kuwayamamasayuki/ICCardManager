using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2494: 職員管理画面を開いている間は、職員の登録・変更・復元の職員証の認証を最初の 1 回だけにする。
/// </summary>
/// <remarks>
/// <para>
/// 最初に認証が要った操作で認証した職員を画面が閉じるまで保持し、2 回目以降は認証ダイアログを出さずに保持している職員の
/// 操作として操作ログへ記録する。保持は「認証を解除」・無操作 5 分・画面を閉じる・保持している職員が無効になった
/// （削除・職員証の差し替え。他のパソコンの操作を含む）で破棄する。削除と職員証の差し替えは保持を使わず、変えもしない。
/// </para>
/// <para>
/// 認証の代役は本物と同じ（<c>Enter()</c> で操作者セッションを開始する）認証結果を返し、操作ログは本物の
/// <see cref="OperationLogger"/> が本物の <see cref="CurrentOperatorContext"/> から操作者を解決する。
/// 「記録された操作者」は書き込まれた操作ログの行で表明する（Issue #1729）。
/// </para>
/// </remarks>
public class StaffManageViewModelAuthenticationHoldTests : IDisposable
{
    private const string FirstIdm = "FFFF000000000101";
    private const string SecondIdm = "FFFF000000000102";
    private const string ThirdIdm = "FFFF000000000103";
    private const string EditTargetIdm = "FFFF000000000201";
    private const string HolderIdm = "FFFF0000000000AA";
    private const string HolderName = "認証 太郎";
    private const string OtherAuthIdm = "FFFF0000000000BB";
    private const string OtherAuthName = "別人 次郎";

    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<IValidationService> _validationServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock = new();
    private static readonly DateTime Start = new(2026, 11, 16, 9, 0, 0);
    private static readonly TimeSpan IdleTimeout =
        TimeSpan.FromSeconds(StaffManageViewModel.HeldAuthenticationIdleTimeoutSeconds);

    /// <summary>無操作の時間を測る時計（テストで進める）。操作者セッションの絶対上限とは別の時計にする</summary>
    private readonly FixedSystemClock _clock = new(Start);

    /// <summary>
    /// 本物の <see cref="CurrentOperatorContext"/> を包み、開始したセッションのスコープが破棄されたかを記録する。
    /// </summary>
    /// <remarks>
    /// セッションは <c>AsyncLocal</c> に持つため、画面の async メソッドの中で開始した値はテストの流れへ戻らず、
    /// テストから <c>HasSession</c> を見ても常に false になる（スコープを閉じ忘れても緑のまま。レビュー 1 回目で検出）。
    /// そこでスコープの破棄そのものを表明する。
    /// </remarks>
    private readonly RecordingOperatorContext _operatorContext =
        new(new CurrentOperatorContext(new FixedSystemClock(Start)));
    private readonly List<OperationLog> _operationLogs = new();
    private readonly TestTimerFactory _timerFactory = new();
    private readonly RecordingLogger<StaffManageViewModel> _logger = new();
    private readonly DbContext _dbContext;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelAuthenticationHoldTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _validationServiceMock.Setup(v => v.ValidateStaffIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(v => v.ValidateStaffName(It.IsAny<string>())).Returns(ValidationResult.Success());
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        _operationLogRepositoryMock
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Callback<OperationLog, SQLiteTransaction>((log, _) => _operationLogs.Add(log))
            .ReturnsAsync(1);

        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(true);
        // 既定: 一覧には保持する職員（認証 太郎）が載っている（登録済みの職員がいる）
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>
        {
            new() { StaffIdm = HolderIdm, Name = HolderName, Number = "S-000" }
        });
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        // 旧オーバーロード（トランザクション無し）も成功させる（呼ばれたら「認証なしで書かれた」ことを検出できるように）
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.RestoreAsync(It.IsAny<string>())).ReturnsAsync(true);

        // 既定: 保持している職員は登録済み（削除されていない）
        ArrangeHolderRegistered(HolderName);

        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            Mock.Of<ICardReader>(),
            _validationServiceMock.Object,
            new StaffManagementService(
                _dbContext,
                _staffRepositoryMock.Object,
                new OperationLogger(_operationLogRepositoryMock.Object, _operatorContext),
                NullLogger<StaffManagementService>.Instance),
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            new WeakReferenceMessenger(),
            new RecordingDispatcherService(),
            _timerFactory,
            _clock,
            _logger);

        // 画面を開いたときの読み込み（一覧が空かどうかで「なし」の文言が変わる）
        _viewModel.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 準備

    /// <summary>認証の代役を、本物と同じ（<c>Enter()</c> で操作者セッションを開始する）認証結果で成功させる。</summary>
    private void ArrangeAuthenticationSucceeds(string operation, string idm, string name)
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(operation))
            .Returns(async () =>
            {
                await Task.Yield();
                return StaffAuthService.CreateAuthenticatedResult(_operatorContext, idm, name);
            });
    }

    /// <summary>登録・変更・復元の認証を、保持する職員（認証 太郎）で成功させる。</summary>
    private void ArrangeHolderAuthenticates()
    {
        foreach (var operation in new[] { "職員の登録", "職員情報の変更", "職員の復元" })
        {
            ArrangeAuthenticationSucceeds(operation, HolderIdm, HolderName);
        }
    }

    private void ArrangeAuthenticationCancelled(string operation)
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(operation))
            .ReturnsAsync((StaffAuthResult?)null);
    }

    /// <summary>保持している職員が、削除されていない職員として登録されている（氏名は <paramref name="currentName"/>）。</summary>
    private void ArrangeHolderRegistered(string currentName)
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = HolderIdm,
            Name = currentName,
            Number = "S-000"
        });
    }

    /// <summary>保持している職員が、削除された・職員証が差し替えられた（削除されていない職員として引けない）。</summary>
    private void ArrangeHolderNoLongerRegistered()
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false)).ReturnsAsync((Staff?)null);
    }

    /// <summary>未登録の職員証をタッチして登録モードに入り、氏名を入力した状態にする。</summary>
    private async Task EnterNewStaffFormAsync(string idm, string name)
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, true)).ReturnsAsync((Staff?)null);
        (await _viewModel.StartNewStaffWithIdmAsync(idm)).Should().BeFalse("登録モードに入る");
        _viewModel.EditName = name;
    }

    private async Task RegisterAsync(string idm, string name)
    {
        await EnterNewStaffFormAsync(idm, name);
        await _viewModel.SaveAsync();
        _viewModel.StatusMessage.Should().Be("登録しました");
    }

    /// <summary>登録済みの職員を選んで編集を始める（<paramref name="newName"/> を指定すれば氏名を書き換える）。</summary>
    private void EnterEditForm(string? newName)
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(EditTargetIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = EditTargetIdm,
            Name = "既存 一郎",
            Number = "S-001"
        });
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = EditTargetIdm, Name = "既存 一郎", Number = "S-001" };
        _viewModel.StartEdit();
        if (newName != null)
        {
            _viewModel.EditName = newName;
        }
    }

    private OperationLog SingleLogFor(string targetIdm, string action)
    {
        return _operationLogs.Should().ContainSingle(l =>
            l.TargetTable == OperationLogger.Tables.Staff &&
            l.TargetId == targetIdm &&
            l.Action == action).Subject;
    }

    private TestTimer HoldTimer => _timerFactory.LastCreatedTimer
        ?? throw new InvalidOperationException("認証を保持したときに無操作のタイマーが作られていない");

    /// <summary>1 人目の登録で保持する職員（認証 太郎）が認証し、保持した状態にする。</summary>
    private async Task HoldByFirstRegistrationAsync()
    {
        ArrangeHolderAuthenticates();
        await RegisterAsync(FirstIdm, "新人 一号");
        _viewModel.IsAuthenticationHeld.Should().BeTrue("認証した職員を保持する");
    }

    #endregion

    #region 2 回目以降は認証を求めない

    [Fact]
    public async Task 登録を続けても認証は最初の1回だけで_2回目以降も保持している職員を操作ログに記録すること()
    {
        await HoldByFirstRegistrationAsync();

        await RegisterAsync(SecondIdm, "新人 二号");
        await RegisterAsync(ThirdIdm, "新人 三号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once);
        foreach (var idm in new[] { FirstIdm, SecondIdm, ThirdIdm })
        {
            var log = SingleLogFor(idm, OperationLogger.Actions.Insert);
            log.OperatorIdm.Should().Be(HolderIdm, "保持している職員の操作として記録する");
            log.OperatorName.Should().Be(HolderName);
        }
    }

    [Fact]
    public async Task 登録で認証した後の変更と復元も認証を求めず_保持している職員を記録すること()
    {
        await HoldByFirstRegistrationAsync();

        EnterEditForm("既存 一郎（改姓）");
        await _viewModel.SaveAsync();
        _viewModel.StatusMessage.Should().Be("更新しました");

        // タッチ経路の復元（新規登録で削除済みの職員証をタッチ → 復元する）
        _viewModel.StartNewStaff();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(new Staff
        {
            StaffIdm = SecondIdm,
            Name = "削除 次郎",
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 4, 1)
        });
        await _viewModel.HandleCardReadAsync(SecondIdm);
        _viewModel.StatusMessage.Should().Contain("を復元しました");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once);
        SingleLogFor(EditTargetIdm, OperationLogger.Actions.Update).OperatorIdm.Should().Be(HolderIdm);
        SingleLogFor(SecondIdm, OperationLogger.Actions.Restore).OperatorIdm.Should().Be(HolderIdm);
    }

    /// <summary>
    /// 操作者セッションは操作ごとに開始する: 保持している職員の操作の後、そのセッションのスコープを閉じる（Issue #2260）
    /// </summary>
    [Fact]
    public async Task 保持した認証で操作した後に操作者セッションを残さないこと()
    {
        await HoldByFirstRegistrationAsync();

        await EnterNewStaffFormAsync(SecondIdm, "新人 二号");
        await _viewModel.SaveAsync();

        SingleLogFor(SecondIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(HolderIdm);
        _operatorContext.Sessions.Should().HaveCount(2, "最初の認証の操作と、保持した認証の操作で 1 回ずつ開始する");
        _operatorContext.Sessions.Should().OnlyContain(session => session.Idm == HolderIdm);
        _operatorContext.Sessions.Should().OnlyContain(session => session.Disposed,
            "保持するのは職員であってセッションではない。操作が終わったらスコープを閉じる");
    }

    /// <summary>
    /// 職員証が他のパソコンで差し替えられ、古い職員証が別の職員として登録し直された（同じ IDm・別の職員番号）なら、
    /// 保持のまま別の職員の名前で記録しない（レビュー 4 回目で検出）
    /// </summary>
    [Theory]
    [InlineData("S-777")]
    [InlineData("")]
    public async Task 同じIDmでも職員番号が保持したときと違えば保持を破棄して認証を求め直すこと(string otherNumber)
    {
        await HoldByFirstRegistrationAsync();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = HolderIdm,
            Name = "別人 三郎",
            Number = otherNumber
        });
        ArrangeAuthenticationSucceeds("職員の登録", OtherAuthIdm, OtherAuthName);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(OtherAuthIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = OtherAuthIdm,
            Name = OtherAuthName
        });

        await RegisterAsync(SecondIdm, "新人 二号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Exactly(2),
            "職員番号が違えば別の職員とみなす（片方だけ空のときも安全側で別の職員とみなす）");
        var log = SingleLogFor(SecondIdm, OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(OtherAuthIdm);
        log.OperatorName.Should().NotBe("別人 三郎");
        _logger.Entries.Should().Contain(e => e.Message.Contains("同じ職員証の職員の職員番号が保持したときと違う"));
    }

    [Fact]
    public async Task 保持している職員の氏名が変わっていたら_現在の氏名で記録し表示も追随すること()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeHolderRegistered("認証 太郎（改姓）");

        await RegisterAsync(SecondIdm, "新人 二号");

        SingleLogFor(SecondIdm, OperationLogger.Actions.Insert).OperatorName.Should().Be("認証 太郎（改姓）");
        _viewModel.HeldOperatorName.Should().Be("認証 太郎（改姓）");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
                && e.Message.Contains("氏名が変わっていたため") && e.Message.Contains(IdmMasker.Mask(HolderIdm)!),
            "同じ職員番号で氏名だけ変わったときは追随し、そのことをログに残す");
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region 保持しない・保持を使わない

    [Fact]
    public async Task 認証が完了しなければ保持しないこと()
    {
        ArrangeAuthenticationCancelled("職員の登録");
        await EnterNewStaffFormAsync(FirstIdm, "新人 一号");

        await _viewModel.SaveAsync();

        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.NoHeldAuthenticationText);
        _viewModel.ReleaseHeldAuthenticationCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// 導入直後: 1 人目は認証なしで登録（保持しない）、2 人目で 1 人目の職員証で認証して保持、3 人目からは認証を求めない
    /// </summary>
    [Fact]
    public async Task 職員が0人の免除では保持せず_2人目の認証から保持すること()
    {
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(false);
        ArrangeHolderAuthenticates();
        await RegisterAsync(FirstIdm, "最初 一人");
        _viewModel.IsAuthenticationHeld.Should().BeFalse("免除した登録には認証した職員がいない");
        SingleLogFor(FirstIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);

        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(true);
        await RegisterAsync(SecondIdm, "新人 二号");
        await RegisterAsync(ThirdIdm, "新人 三号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Once);
        SingleLogFor(SecondIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(HolderIdm);
        SingleLogFor(ThirdIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(HolderIdm);
    }

    [Fact]
    public async Task 削除は保持している認証を使わずに毎回認証を求め_保持も変えないこと()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeAuthenticationSucceeds("職員の削除", OtherAuthIdm, OtherAuthName);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(EditTargetIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = EditTargetIdm,
            Name = "既存 一郎",
            Number = "S-001"
        });
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = EditTargetIdm, Name = "既存 一郎", Number = "S-001" };

        await _viewModel.DeleteAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の削除"), Times.Once);
        SingleLogFor(EditTargetIdm, OperationLogger.Actions.Delete).OperatorIdm.Should().Be(OtherAuthIdm,
            "削除はその場で認証した職員の操作として記録する");
        _viewModel.HeldOperatorName.Should().Be(HolderName, "削除の認証で保持している職員を置き換えない");
    }

    [Fact]
    public async Task 職員証の差し替えは保持している認証を使わずに認証を求め_保持も変えないこと()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeAuthenticationCancelled("職員証の差し替え");
        EnterEditForm(newName: null);

        await _viewModel.ReplaceStaffCardAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員証の差し替え"), Times.Once);
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplaceAuthenticationCancelledMessage);
        _viewModel.HeldOperatorName.Should().Be(HolderName);
    }

    #endregion

    #region 保持している職員が使えなくなった

    [Fact]
    public async Task 保持している職員が削除されていたら_保持を破棄して認証を求め直すこと()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeHolderNoLongerRegistered();
        ArrangeAuthenticationSucceeds("職員の登録", OtherAuthIdm, OtherAuthName);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(OtherAuthIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = OtherAuthIdm,
            Name = OtherAuthName
        });

        await RegisterAsync(SecondIdm, "新人 二号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Exactly(2));
        SingleLogFor(SecondIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(OtherAuthIdm,
            "削除された職員の名前では記録しない");
        _viewModel.HeldOperatorName.Should().Be(OtherAuthName, "認証し直した職員を保持する");
    }

    [Fact]
    public async Task 保持している職員が使えず認証し直しも完了しなければ_登録せず保持もしないこと()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeHolderNoLongerRegistered();
        ArrangeAuthenticationCancelled("職員の登録");
        await EnterNewStaffFormAsync(SecondIdm, "新人 二号");

        await _viewModel.SaveAsync();

        _staffRepositoryMock.Verify(r => r.InsertAsync(
            It.Is<Staff>(s => s.StaffIdm == SecondIdm), It.IsAny<SQLiteTransaction>()), Times.Never);
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.RegisterAuthenticationCancelledMessage);
        _viewModel.IsAuthenticationHeld.Should().BeFalse();
    }

    #endregion

    #region 認証を解除・画面を閉じる

    [Fact]
    public async Task 認証を解除したら次の操作で認証を求めること()
    {
        await HoldByFirstRegistrationAsync();
        _viewModel.ReleaseHeldAuthenticationCommand.CanExecute(null).Should().BeTrue();

        _viewModel.ReleaseHeldAuthenticationCommand.Execute(null);

        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.NoHeldAuthenticationText);
        _viewModel.ReleaseHeldAuthenticationCommand.CanExecute(null).Should().BeFalse();

        await RegisterAsync(SecondIdm, "新人 二号");
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Exactly(2));
    }

    [Fact]
    public async Task 画面を閉じたら保持を破棄しタイマーを止めること()
    {
        await HoldByFirstRegistrationAsync();
        HoldTimer.IsRunning.Should().BeTrue();

        _viewModel.Cleanup();

        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        HoldTimer.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 保持している間は認証中の職員名を表示すること()
    {
        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.NoHeldAuthenticationText);

        await HoldByFirstRegistrationAsync();

        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.BuildHeldAuthenticationText(HolderName));
        _viewModel.HeldAuthenticationText.Should().StartWith($"認証中: {HolderName}");
        _viewModel.HeldAuthenticationText.Should().Contain("5分");
    }

    #endregion

    #region この画面での削除・差し替え（レビュー 2 回目）

    private void SelectStaff(string idm, string name)
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(idm, false)).ReturnsAsync(new Staff { StaffIdm = idm, Name = name });
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = idm, Name = name };
    }

    [Fact]
    public async Task 保持している職員をこの画面で削除したら保持を破棄すること()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeAuthenticationSucceeds("職員の削除", OtherAuthIdm, OtherAuthName);
        SelectStaff(HolderIdm, HolderName);

        await _viewModel.DeleteAsync();

        _staffRepositoryMock.Verify(r => r.DeleteAsync(HolderIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.IsAuthenticationHeld.Should().BeFalse("削除した職員の保持は使えない。表示を残さない");
        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.NoHeldAuthenticationText);
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information
            && e.Message.Contains("解除しました") && e.Message.Contains("保持している職員を削除した"));
    }

    /// <summary>
    /// 最後の 1 人（保持している職員）を削除したとき、表示の変更の通知は最終の文言の 1 回だけ（レビュー 4 回目で検出。
    /// 破棄を再読込の前にすると「なし（認証を求められたら…）」→「なし（職員がいない…）」と 2 回読み上げる）
    /// </summary>
    [Fact]
    public async Task 最後の1人の保持している職員を削除したら表示の通知は最終の文言の1回だけであること()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeAuthenticationSucceeds("職員の削除", OtherAuthIdm, OtherAuthName);
        SelectStaff(HolderIdm, HolderName);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        var changes = RecordHeldTextChanges();

        await _viewModel.DeleteAsync();

        changes.Should().Equal(new[] { StaffManageViewModel.NoHeldAuthenticationNoStaffText });
    }

    [Fact]
    public async Task 保持している職員の職員証を差し替えたら表示の通知は1回だけであること()
    {
        await HoldByFirstRegistrationAsync();
        var changes = RecordHeldTextChanges();

        await ReplaceStaffCardAsync(HolderIdm, HolderName);

        changes.Should().Equal(new[] { StaffManageViewModel.NoHeldAuthenticationText });
    }

    [Fact]
    public async Task 保持している職員の削除が競合で書かれなければ保持を変えないこと()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeAuthenticationSucceeds("職員の削除", OtherAuthIdm, OtherAuthName);
        SelectStaff(HolderIdm, HolderName);
        _staffRepositoryMock.Setup(r => r.DeleteAsync(HolderIdm, It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);

        await _viewModel.DeleteAsync();

        _viewModel.HeldOperatorName.Should().Be(HolderName, "削除が成功したときだけ破棄する（使えるかは次の操作の DB の確認が決める）");
    }

    private async Task ReplaceStaffCardAsync(string targetIdm, string targetName)
    {
        const string newIdm = "FFFF000000000999";
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync("職員証の差し替え"))
            .ReturnsAsync(() => StaffAuthService.CreateAuthenticatedResult(_operatorContext, OtherAuthIdm, OtherAuthName));
        _staffRepositoryMock.Setup(r => r.CheckStaffIdmReplacementAsync(targetIdm, newIdm))
            .ReturnsAsync(StaffCardReplacementResult.Available());
        _staffRepositoryMock.Setup(r => r.ReplaceStaffIdmAsync(targetIdm, newIdm, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(StaffCardReplacementResult.Replaced(
                new Staff { StaffIdm = targetIdm, Name = targetName },
                new Staff { StaffIdm = newIdm, Name = targetName },
                ledgerRows: 0, cardRows: 0, mergeHistoryRows: 0));
        SelectStaff(targetIdm, targetName);
        _viewModel.StartEdit();

        var task = _viewModel.ReplaceStaffCardAsync();
        _viewModel.IsWaitingForReplacementCard.Should().BeTrue("認証の後は新しい職員証のタッチを待つ");
        _viewModel.HandleReplacementCardRead(newIdm);
        await task;
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.BuildReplacementCompletedMessage(targetName));
    }

    [Fact]
    public async Task 保持している職員の職員証をこの画面で差し替えたら保持を破棄すること()
    {
        await HoldByFirstRegistrationAsync();

        await ReplaceStaffCardAsync(HolderIdm, HolderName);

        _viewModel.IsAuthenticationHeld.Should().BeFalse("古い職員証の IDm の保持は使えない");
        _logger.Entries.Should().Contain(e => e.Message.Contains("保持している職員の職員証を差し替えた"));
    }

    [Fact]
    public async Task 別の職員の職員証を差し替えても保持を変えないこと()
    {
        await HoldByFirstRegistrationAsync();

        await ReplaceStaffCardAsync(EditTargetIdm, "既存 一郎");

        _viewModel.HeldOperatorName.Should().Be(HolderName, "差し替えの認証でも、別の職員の差し替えでも保持は変えない");
    }

    #endregion

    #region 保持していないときの表示（レビュー 2 回目）

    [Fact]
    public async Task 登録済みの職員がいなければ認証を求めない旨を表示し_職員がいれば認証の案内に切り替わること()
    {
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        await _viewModel.LoadStaffAsync();
        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.NoHeldAuthenticationNoStaffText);

        var changes = new List<string>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StaffManageViewModel.HeldAuthenticationText))
            {
                changes.Add(_viewModel.HeldAuthenticationText);
            }
        };
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>
        {
            new() { StaffIdm = FirstIdm, Name = "最初 一人" }
        });
        await _viewModel.LoadStaffAsync();

        _viewModel.HeldAuthenticationText.Should().Be(StaffManageViewModel.NoHeldAuthenticationText);
        changes.Should().Equal(new[] { StaffManageViewModel.NoHeldAuthenticationText },
            "0 人から 1 人に変わったときに 1 回だけ通知する");

        changes.Clear();
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        await _viewModel.LoadStaffAsync();
        changes.Should().Equal(new[] { StaffManageViewModel.NoHeldAuthenticationNoStaffText },
            "1 人から 0 人に変わったときに 1 回だけ通知する");
    }

    private List<string> RecordHeldTextChanges()
    {
        var changes = new List<string>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StaffManageViewModel.HeldAuthenticationText))
            {
                changes.Add(_viewModel.HeldAuthenticationText);
            }
        };
        return changes;
    }

    private void ArrangeStaffList(int count)
    {
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(Enumerable.Range(1, count)
            .Select(i => new Staff { StaffIdm = $"FFFF0000000003{i:D2}", Name = $"職員 {i}" })
            .ToList());
    }

    /// <summary>
    /// 一覧の再読込（Clear と 1 件ずつの Add）では、文言が変わらないので通知しない（読み上げを繰り返さない。レビュー 3 回目で検出）
    /// </summary>
    [Fact]
    public async Task 保持していない間に一覧を再読込しても表示の変更を通知しないこと()
    {
        ArrangeStaffList(20);
        var changes = RecordHeldTextChanges();

        await _viewModel.LoadStaffAsync();
        await _viewModel.LoadStaffAsync();

        changes.Should().BeEmpty("職員が 1 人以上いる間は文言が変わらない");
    }

    [Fact]
    public async Task 保持している間に一覧を再読込しても表示の変更を通知しないこと()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeStaffList(20);
        var changes = RecordHeldTextChanges();

        await _viewModel.LoadStaffAsync();
        ArrangeStaffList(0);
        await _viewModel.LoadStaffAsync();

        changes.Should().BeEmpty("保持している間の文言は一覧の人数で変わらない");
    }

    [Fact]
    public async Task 保持している職員が自分の氏名を変更したら認証中の氏名も変更後にすること()
    {
        await HoldByFirstRegistrationAsync();
        var changes = RecordHeldTextChanges();
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = HolderIdm, Name = HolderName, Number = "S-000" };
        _viewModel.StartEdit();
        _viewModel.EditName = "認証 太郎（改姓）";

        await _viewModel.SaveAsync();

        _viewModel.StatusMessage.Should().Be("更新しました");
        _viewModel.HeldOperatorName.Should().Be("認証 太郎（改姓）", "次の操作まで旧氏名を表示し続けない");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
                && e.Message.Contains("氏名が変わっていたため") && e.Message.Contains(IdmMasker.Mask(HolderIdm)!),
            "この画面での変更に追随したときも、DB の確認で追随したときと同じく記録する（レビュー 5 回目）");
        changes.Should().Equal(new[] { StaffManageViewModel.BuildHeldAuthenticationText("認証 太郎（改姓）") },
            "表示の変更は 1 回だけ通知する");
    }

    [Fact]
    public async Task 別の職員の氏名を変更しても認証中の氏名は変えないこと()
    {
        await HoldByFirstRegistrationAsync();
        var changes = RecordHeldTextChanges();
        EnterEditForm("既存 一郎（改姓）");

        await _viewModel.SaveAsync();

        _viewModel.StatusMessage.Should().Be("更新しました");
        _viewModel.HeldOperatorName.Should().Be(HolderName);
        changes.Should().BeEmpty();
    }

    [Fact]
    public void 保持していないときの文言は認証の契機を保存に限らないこと()
    {
        StaffManageViewModel.NoHeldAuthenticationText.Should().NotContain("保存");
        StaffManageViewModel.NoHeldAuthenticationText.Should().Contain("登録済みの職員の職員証");
        StaffManageViewModel.NoHeldAuthenticationNoStaffText.Should().Contain("登録済みの職員がいない");
    }

    #endregion

    #region アプリのログ（レビュー 2 回目）

    [Fact]
    public async Task 保持の開始と保持した認証での操作をIDmをマスクしてログに残すこと()
    {
        await HoldByFirstRegistrationAsync();
        await RegisterAsync(SecondIdm, "新人 二号");

        var masked = IdmMasker.Mask(HolderIdm)!;
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information
            && e.Message.Contains("保持しました") && e.Message.Contains("職員の登録") && e.Message.Contains(masked));
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information
            && e.Message.Contains("保持している職員証の認証で操作を行います") && e.Message.Contains("職員の登録")
            && e.Message.Contains(masked), "2 回目の登録だけが保持した認証での操作");
        _logger.Entries.Should().NotContain(e => e.Message.Contains(HolderIdm), "IDm を生でログへ出さない（#1852）");
    }

    [Theory]
    [InlineData("button", "「認証を解除」ボタン")]
    [InlineData("idle", "無操作の期限")]
    [InlineData("invalid", "保持している職員が登録されていない")]
    [InlineData("closed", "画面を閉じた")]
    public async Task 保持の解除を理由とともにログに残すこと(string trigger, string reason)
    {
        await HoldByFirstRegistrationAsync();

        switch (trigger)
        {
            case "button":
                _viewModel.ReleaseHeldAuthenticationCommand.Execute(null);
                break;
            case "idle":
                _clock.Now += IdleTimeout;
                HoldTimer.SimulateTick();
                break;
            case "invalid":
                ArrangeHolderNoLongerRegistered();
                ArrangeAuthenticationCancelled("職員の登録");
                await EnterNewStaffFormAsync(SecondIdm, "新人 二号");
                await _viewModel.SaveAsync();
                break;
            case "closed":
                _viewModel.Cleanup();
                break;
        }

        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("解除しました"))
            .Which.Message.Should().Contain(reason).And.Contain(IdmMasker.Mask(HolderIdm)!);
    }

    [Fact]
    public void 保持していないときの解除はログに残さないこと()
    {
        _viewModel.Cleanup();

        _logger.Entries.Should().NotContain(e => e.Message.Contains("解除しました"));
    }

    #endregion

    #region 確認を待つ間に閉じた

    /// <summary>
    /// 保持している職員の確認（DB の読み取り）を待つ間に画面を閉じたら、確認から戻っても認証ダイアログへ進まず、
    /// 書き込まず、タイマーも動かし直さない（レビュー 1 回目で検出）
    /// </summary>
    [Fact]
    public async Task 保持している職員の確認を待つ間に画面を閉じたら認証を求めず保持もしないこと()
    {
        await HoldByFirstRegistrationAsync();
        var check = new TaskCompletionSource<Staff?>();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false)).Returns(check.Task);
        await EnterNewStaffFormAsync(SecondIdm, "新人 二号");

        var save = _viewModel.SaveAsync();
        _viewModel.Cleanup();
        check.SetResult(new Staff { StaffIdm = HolderIdm, Name = HolderName });
        await save;

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once,
            "閉じた画面の認証ダイアログを出さない（最初の 1 回だけ）");
        _staffRepositoryMock.Verify(r => r.InsertAsync(
            It.Is<Staff>(s => s.StaffIdm == SecondIdm), It.IsAny<SQLiteTransaction>()), Times.Never);
        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        HoldTimer.IsRunning.Should().BeFalse("閉じた画面のタイマーを動かし直さない");
    }

    /// <summary>
    /// 変更・復元（3 つの入口）でも、保持している職員の確認を待つ間に閉じたら、認証ダイアログへ進まず書き込まない
    /// （レビュー 4 回目。登録は上のテスト）
    /// </summary>
    [Theory]
    [InlineData("update")]
    [InlineData("restore-save")]
    [InlineData("restore-touch")]
    [InlineData("restore-unregistered-card")]
    public async Task 変更と復元でも保持している職員の確認を待つ間に画面を閉じたら認証を求めないこと(string path)
    {
        await HoldByFirstRegistrationAsync();
        var check = new TaskCompletionSource<Staff?>();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false)).Returns(check.Task);
        _dialogServiceMock.Invocations.Clear();
        var deleted = new Staff { StaffIdm = SecondIdm, Name = "削除 次郎", IsDeleted = true, DeletedAt = new DateTime(2026, 4, 1) };

        Task operation;
        switch (path)
        {
            case "update":
                EnterEditForm("既存 一郎（改姓）");
                operation = _viewModel.SaveAsync();
                break;
            case "restore-save":
                await EnterNewStaffFormAsync(SecondIdm, "新人 二号");
                _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(deleted);
                operation = _viewModel.SaveAsync();
                break;
            case "restore-touch":
                _viewModel.StartNewStaff();
                _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(deleted);
                operation = _viewModel.HandleCardReadAsync(SecondIdm);
                break;
            default:
                _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(deleted);
                operation = _viewModel.StartNewStaffWithIdmAsync(SecondIdm);
                break;
        }

        _viewModel.Cleanup();
        check.SetResult(new Staff { StaffIdm = HolderIdm, Name = HolderName, Number = "S-000" });
        await operation;

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once,
            "閉じた画面の認証ダイアログを出さない（最初の 1 回だけ）");
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _dialogServiceMock.Verify(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "閉じた後に「認証が完了しなかった」の案内を出さない");
        if (path == "restore-unregistered-card")
        {
            ((Task<bool>)operation).Result.Should().BeTrue("画面は閉じている（閉じる側として返す）");
        }
    }

    [Fact]
    public async Task 画面を閉じた後に認証が完了しても保持しないこと()
    {
        await EnterNewStaffFormAsync(FirstIdm, "新人 一号");
        var auth = new TaskCompletionSource<StaffAuthResult?>();
        _staffAuthServiceMock.Setup(a => a.RequestAuthenticationAsync("職員の登録")).Returns(auth.Task);

        var save = _viewModel.SaveAsync();
        _viewModel.Cleanup();
        auth.SetResult(StaffAuthService.CreateAuthenticatedResult(_operatorContext, HolderIdm, HolderName));
        await save;

        _viewModel.IsAuthenticationHeld.Should().BeFalse("閉じた画面では保持しない");
        _timerFactory.LastCreatedTimer.Should().BeNull("閉じた画面のタイマーを作らない・動かさない");
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never,
            "閉じた画面から書き込まない（レビュー 5 回目）");
        _operationLogs.Should().BeEmpty();
    }

    /// <summary>
    /// 認証の直後、保持する職員の読み取り（DB）を待つ間に閉じたら、閉じた画面から登録・変更・復元を書き込まない
    /// （レビュー 5 回目で検出。以前は保持だけをやめて書き込みは続けていた）
    /// </summary>
    [Theory]
    [InlineData("register")]
    [InlineData("update")]
    [InlineData("restore-save")]
    [InlineData("restore-touch")]
    [InlineData("restore-unregistered-card")]
    public async Task 認証の直後に保持する職員を読む間に画面を閉じたら書き込まないこと(string path)
    {
        var holdRead = new TaskCompletionSource<Staff?>();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false)).Returns(holdRead.Task);
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(() => StaffAuthService.CreateAuthenticatedResult(_operatorContext, HolderIdm, HolderName));
        var deleted = new Staff { StaffIdm = SecondIdm, Name = "削除 次郎", IsDeleted = true, DeletedAt = new DateTime(2026, 4, 1) };

        Task operation;
        switch (path)
        {
            case "register":
                await EnterNewStaffFormAsync(SecondIdm, "新人 二号");
                operation = _viewModel.SaveAsync();
                break;
            case "update":
                EnterEditForm("既存 一郎（改姓）");
                operation = _viewModel.SaveAsync();
                break;
            case "restore-save":
                await EnterNewStaffFormAsync(SecondIdm, "新人 二号");
                _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(deleted);
                operation = _viewModel.SaveAsync();
                break;
            case "restore-touch":
                _viewModel.StartNewStaff();
                _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(deleted);
                operation = _viewModel.HandleCardReadAsync(SecondIdm);
                break;
            default:
                _staffRepositoryMock.Setup(r => r.GetByIdmAsync(SecondIdm, true)).ReturnsAsync(deleted);
                operation = _viewModel.StartNewStaffWithIdmAsync(SecondIdm);
                break;
        }

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once,
            "前提: 認証が済み、保持する職員の読み取りを待っている");
        _viewModel.Cleanup();
        holdRead.SetResult(new Staff { StaffIdm = HolderIdm, Name = HolderName, Number = "S-000" });
        await operation;

        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _operationLogs.Should().BeEmpty("閉じた画面から操作ログを書かない");
        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        _dialogServiceMock.Verify(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "閉じた後に案内を出さない");
    }

    #endregion

    #region 無操作で破棄する

    [Fact]
    public async Task 無操作が5分に満たなければ保持し_5分で破棄すること()
    {
        await HoldByFirstRegistrationAsync();
        HoldTimer.Interval.Should().Be(TimeSpan.FromSeconds(1));
        StaffManageViewModel.HeldAuthenticationIdleTimeoutSeconds.Should().Be(300);
        var lastActivity = _clock.Now;

        _clock.Now = lastActivity + IdleTimeout - TimeSpan.FromSeconds(1);
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeTrue("最後の操作から 5 分の 1 秒手前では破棄しない");

        _clock.Now = lastActivity + IdleTimeout;
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeFalse("最後の操作から 5 分で破棄する");
        HoldTimer.IsRunning.Should().BeFalse("破棄したらタイマーを止める");
    }

    /// <summary>
    /// PC がスリープから復帰したとき、DispatcherTimer は取りこぼした Tick を再生しない。回数ではなく経過時間で判定するので、
    /// 復帰後の最初の Tick で破棄する（レビュー 1 回目で検出）
    /// </summary>
    [Fact]
    public async Task スリープで時計が飛んでもTick1回で破棄すること()
    {
        await HoldByFirstRegistrationAsync();

        _clock.Now += TimeSpan.FromHours(2);
        HoldTimer.SimulateTick();

        _viewModel.IsAuthenticationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task Tickが何回来ても時計が進まなければ破棄しないこと()
    {
        await HoldByFirstRegistrationAsync();

        HoldTimer.SimulateTicks(StaffManageViewModel.HeldAuthenticationIdleTimeoutSeconds * 2);

        _viewModel.IsAuthenticationHeld.Should().BeTrue("無操作は Tick の回数ではなく経過時間で判定する");
    }

    [Fact]
    public async Task 時計が戻ったら保持を破棄すること()
    {
        await HoldByFirstRegistrationAsync();

        _clock.Now -= TimeSpan.FromMinutes(1);
        HoldTimer.SimulateTick();

        _viewModel.IsAuthenticationHeld.Should().BeFalse("経過を確かめられないまま保持を延ばさない");
    }

    /// <summary>
    /// スリープ復帰の直後（Tick が来る前）の操作は、切れているはずの保持を延ばさない（自動のセキュリティレビューで検出。
    /// 延ばすと、その後の別の職員の操作が認証した職員の名前で記録される）
    /// </summary>
    [Fact]
    public async Task 期限が切れた後の画面への操作では保持を延ばさず破棄すること()
    {
        await HoldByFirstRegistrationAsync();
        _clock.Now += IdleTimeout;

        _viewModel.NotifyUserActivity();

        _viewModel.IsAuthenticationHeld.Should().BeFalse("Tick を待たずに、操作の時点で期限を確かめて破棄する");
        _clock.Now += TimeSpan.FromSeconds(1);
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeFalse("延長されていない");
    }

    [Fact]
    public async Task 期限が切れた後に保持を使う操作では認証を求めること()
    {
        await HoldByFirstRegistrationAsync();
        _clock.Now += IdleTimeout + TimeSpan.FromMinutes(30);
        ArrangeAuthenticationSucceeds("職員の登録", OtherAuthIdm, OtherAuthName);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(OtherAuthIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = OtherAuthIdm,
            Name = OtherAuthName
        });

        // Tick を起こさずに保持を使う操作をする（スリープ復帰の直後）
        await RegisterAsync(SecondIdm, "新人 二号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Exactly(2),
            "期限の切れた保持は使わず、認証ダイアログを出す");
        SingleLogFor(SecondIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(OtherAuthIdm,
            "期限の切れた保持の職員の名前では記録しない");
        _staffRepositoryMock.Verify(r => r.GetByIdmAsync(HolderIdm, false), Times.Once,
            "読むのは保持したときの 1 回だけ。期限は使う前の確認（DB の読み取り）より前に確かめる");
    }

    /// <summary>
    /// 保持している職員の確認（DB の読み取り）を待つ間に期限が来たら、確認から戻っても保持を使わない
    /// </summary>
    [Fact]
    public async Task 保持している職員の確認を待つ間に期限が来たら認証を求めること()
    {
        await HoldByFirstRegistrationAsync();
        ArrangeAuthenticationSucceeds("職員の登録", OtherAuthIdm, OtherAuthName);
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(OtherAuthIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = OtherAuthIdm,
            Name = OtherAuthName
        });
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(HolderIdm, false))
            .Callback(() => _clock.Now += IdleTimeout)
            .ReturnsAsync(new Staff { StaffIdm = HolderIdm, Name = HolderName });

        await RegisterAsync(SecondIdm, "新人 二号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Exactly(2));
        SingleLogFor(SecondIdm, OperationLogger.Actions.Insert).OperatorIdm.Should().Be(OtherAuthIdm);
    }

    [Fact]
    public async Task 期限の手前の画面への操作では保持を延ばすこと()
    {
        await HoldByFirstRegistrationAsync();
        _clock.Now += IdleTimeout - TimeSpan.FromSeconds(1);

        _viewModel.NotifyUserActivity();
        var activity = _clock.Now;

        _viewModel.IsAuthenticationHeld.Should().BeTrue();
        _clock.Now = activity + IdleTimeout - TimeSpan.FromSeconds(1);
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeTrue("期限の手前の操作で延長される");
    }

    [Fact]
    public async Task 画面への操作があれば無操作の時間を数え直すこと()
    {
        await HoldByFirstRegistrationAsync();
        _clock.Now += TimeSpan.FromSeconds(200);

        _viewModel.NotifyUserActivity();
        var activity = _clock.Now;

        _clock.Now = activity + IdleTimeout - TimeSpan.FromSeconds(1);
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeTrue("操作の時点から数え直す");
        _clock.Now = activity + IdleTimeout;
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task 保持した認証で操作したら無操作の時間を数え直すこと()
    {
        await HoldByFirstRegistrationAsync();
        _clock.Now += TimeSpan.FromSeconds(200);

        await RegisterAsync(SecondIdm, "新人 二号");
        var activity = _clock.Now;

        _clock.Now = activity + IdleTimeout - TimeSpan.FromSeconds(1);
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeTrue();
        _clock.Now = activity + IdleTimeout;
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeFalse();
    }

    /// <summary>
    /// 保持した認証の操作の最中（書き込みを待つ間）に時間がたっても、操作が終わった時点から数え直す
    /// （共有フォルダーの書き込みが遅いとき、操作の直後に保持を破棄しない）
    /// </summary>
    [Fact]
    public async Task 保持した認証の操作に時間がかかっても操作の終わりから数え直すこと()
    {
        await HoldByFirstRegistrationAsync();
        _staffRepositoryMock
            .Setup(r => r.InsertAsync(It.Is<Staff>(s => s.StaffIdm == SecondIdm), It.IsAny<SQLiteTransaction>()))
            .Callback(() => _clock.Now += TimeSpan.FromSeconds(200))
            .ReturnsAsync(true);
        var beforeOperation = _clock.Now;

        await RegisterAsync(SecondIdm, "新人 二号");

        var end = beforeOperation + TimeSpan.FromSeconds(200);
        _clock.Now = end + IdleTimeout - TimeSpan.FromSeconds(1);
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeTrue("操作が終わった時点から数え直す");
        _clock.Now = end + IdleTimeout;
        HoldTimer.SimulateTick();
        _viewModel.IsAuthenticationHeld.Should().BeFalse();
    }

    [Fact]
    public async Task 無操作で破棄した後の操作では認証を求めること()
    {
        await HoldByFirstRegistrationAsync();
        _clock.Now += IdleTimeout;
        HoldTimer.SimulateTick();

        await RegisterAsync(SecondIdm, "新人 二号");

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Exactly(2));
        _viewModel.IsAuthenticationHeld.Should().BeTrue("認証し直した職員を保持する");
        HoldTimer.IsRunning.Should().BeTrue("保持し直したらタイマーを動かし直す");
    }

    [Fact]
    public void 保持していない間の操作は無操作の時間に影響しないこと()
    {
        _viewModel.NotifyUserActivity();

        _viewModel.IsAuthenticationHeld.Should().BeFalse();
        _timerFactory.LastCreatedTimer.Should().BeNull("保持するまでタイマーを作らない");
    }

    /// <summary>
    /// 表示の変化（保持・解除・無操作での解除）で <c>HeldAuthenticationText</c> の変更を通知する
    /// （View はこの通知でスクリーンリーダーへ知らせる。#2142）
    /// </summary>
    [Fact]
    public async Task 保持と解除と無操作での解除で表示の変更を通知すること()
    {
        var changes = new List<string>();
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StaffManageViewModel.HeldAuthenticationText))
            {
                changes.Add(_viewModel.HeldAuthenticationText);
            }
        };

        await HoldByFirstRegistrationAsync();
        changes.Should().Equal(new[] { StaffManageViewModel.BuildHeldAuthenticationText(HolderName) },
            "保持したら 1 回だけ通知する（登録の後の一覧の再読込では通知しない）");

        changes.Clear();
        _clock.Now += IdleTimeout;
        HoldTimer.SimulateTick();
        changes.Should().Equal(new[] { StaffManageViewModel.NoHeldAuthenticationText }, "無操作で解除したら通知する");

        await RegisterAsync(SecondIdm, "新人 二号");
        changes.Clear();
        _viewModel.ReleaseHeldAuthenticationCommand.Execute(null);
        changes.Should().Equal(new[] { StaffManageViewModel.NoHeldAuthenticationText }, "「認証を解除」で通知する");
    }

    #endregion

    private sealed class RecordingOperatorContext : ICurrentOperatorContext
    {
        private readonly ICurrentOperatorContext _inner;

        public RecordingOperatorContext(ICurrentOperatorContext inner) => _inner = inner;

        public List<RecordedSession> Sessions { get; } = new();

        public string? CurrentIdm => _inner.CurrentIdm;

        public string? CurrentName => _inner.CurrentName;

        public bool HasSession => _inner.HasSession;

        public bool TryGetOperator(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? idm,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
            => _inner.TryGetOperator(out idm, out name);

        public IDisposable BeginSession(string idm, string name)
        {
            var session = new RecordedSession(idm, _inner.BeginSession(idm, name));
            Sessions.Add(session);
            return session;
        }
    }

    private sealed class RecordedSession : IDisposable
    {
        private readonly IDisposable _inner;

        public RecordedSession(string idm, IDisposable inner)
        {
            Idm = idm;
            _inner = inner;
        }

        public string Idm { get; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _inner.Dispose();
        }
    }
}
