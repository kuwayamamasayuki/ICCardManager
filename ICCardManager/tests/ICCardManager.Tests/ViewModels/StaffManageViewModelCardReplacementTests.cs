using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Common.Messages;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2461: 職員管理画面の「職員証を差し替え」。
/// 職員証の認証 → 新しい職員証のタッチ（この画面で待つ）→ 確認 → 保存。キャンセル・時間切れの出口を持つ。
/// </summary>
/// <remarks>
/// <para>
/// 認証の代役は本物と同じ認証結果（<c>Enter()</c> で操作者セッションを開始する）を返し、操作ログは本物の
/// <see cref="OperationLogger"/> が本物の <see cref="CurrentOperatorContext"/> から操作者を解決する。
/// 差し替えの書き込みそのもの（台帳・カードの付け替え・巻き戻り）は <c>StaffCardReplacementTests</c> が実 DB で検証する。
/// </para>
/// <para>
/// タッチ待ちの時間切れはタイマーの代役（<see cref="TestTimerFactory"/>）で、実時間を待たずに境界を固定する。
/// </para>
/// </remarks>
public class StaffManageViewModelCardReplacementTests : IDisposable
{
    private const string TargetIdm = "0123456789ABCDEF";
    private const string NewIdm = "FEDCBA9876543210";
    private const string OtherIdm = "1111222233334444";
    private const string TargetName = "博多 花子";
    private const string TargetNumber = "S-001";
    private const string TargetLabel = "博多 花子（S-001）";
    private const string AuthenticatedStaffIdm = "AAAA0000BBBB1111";
    private const string AuthenticatedStaffName = "認証 太郎";

    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ICardReader> _cardReaderMock = new();
    private readonly Mock<IValidationService> _validationServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock = new();
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 11, 13, 9, 0, 0)));
    private readonly List<OperationLog> _operationLogs = new();
    private readonly List<(bool Suppressed, CardReadingSource Source)> _suppressionMessages = new();
    private readonly List<StaffCardReplacedMessage> _replacedMessages = new();
    private readonly TestTimerFactory _timerFactory = new();
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly object _recipient = new();
    private readonly DbContext _dbContext;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelCardReplacementTests()
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
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        _staffRepositoryMock.Setup(r => r.CheckStaffIdmReplacementAsync(TargetIdm, NewIdm))
            .ReturnsAsync(StaffCardReplacementResult.Available());
        _staffRepositoryMock.Setup(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(StaffCardReplacementResult.Replaced(
                new Staff { StaffIdm = TargetIdm, Name = TargetName, Number = TargetNumber },
                new Staff { StaffIdm = NewIdm, Name = TargetName, Number = TargetNumber },
                ledgerRows: 3, cardRows: 1, mergeHistoryRows: 0));

        _messenger.Register<CardReadingSuppressedMessage>(_recipient,
            (_, m) => _suppressionMessages.Add((m.Value, m.Source)));
        _messenger.Register<StaffCardReplacedMessage>(_recipient, (_, m) => _replacedMessages.Add(m));

        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            _cardReaderMock.Object,
            _validationServiceMock.Object,
            new StaffManagementService(
                _dbContext,
                _staffRepositoryMock.Object,
                new OperationLogger(_operationLogRepositoryMock.Object, _operatorContext),
                NullLogger<StaffManagementService>.Instance),
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            _messenger,
            new RecordingDispatcherService(),
            _timerFactory);
    }

    public void Dispose()
    {
        _viewModel.Cleanup();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 準備

    /// <summary>認証を本物と同じ結果で成功させる（同期に完了させ、タッチ待ちに入った状態をテストから観測できるようにする）</summary>
    private void ArrangeAuthenticationSucceeds()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(() => StaffAuthService.CreateAuthenticatedResult(
                _operatorContext, AuthenticatedStaffIdm, AuthenticatedStaffName));
    }

    private void ArrangeAuthenticationCancelled()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);
    }

    /// <summary>登録済みの職員を選んで編集を始める</summary>
    private void EnterEditForm()
    {
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = TargetIdm, Name = TargetName, Number = TargetNumber };
        _viewModel.StartEdit();
    }

    /// <summary>認証を通してタッチ待ちに入った状態にする（戻り値は差し替えの操作全体のタスク）</summary>
    private Task StartReplacementAndWaitForTouch()
    {
        ArrangeAuthenticationSucceeds();
        EnterEditForm();
        var task = _viewModel.ReplaceStaffCardAsync();
        _viewModel.IsWaitingForReplacementCard.Should().BeTrue("認証の後は新しい職員証のタッチを待つ");
        return task;
    }

    private TestTimer Timer => _timerFactory.LastCreatedTimer
        ?? throw new InvalidOperationException("タッチ待ちのタイマーが作られていない");

    private void VerifyNoReplacementWrite()
    {
        _replacedMessages.Should().BeEmpty("差し替えていないのでメイン画面へ知らせない");
        _staffRepositoryMock.Verify(
            r => r.ReplaceStaffIdmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _operationLogs.Should().BeEmpty("何も書いていないので操作ログも残らない");
    }

    private void VerifySuppressionReleased()
    {
        _suppressionMessages.Where(m => m.Source == CardReadingSource.StaffCardReplacement)
            .Select(m => m.Suppressed).Should().Equal(new[] { true, false },
                "メイン画面のカード読み取りの抑制は、操作の始めに取って終わりに解く");
    }

    #endregion

    #region 認証

    [Fact]
    public async Task 認証が完了しなければタッチを待たず何も書かないこと()
    {
        ArrangeAuthenticationCancelled();
        EnterEditForm();

        await _viewModel.ReplaceStaffCardAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員証の差し替え"), Times.Once);
        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplaceAuthenticationCancelledMessage);
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeTrue("編集中の入力内容は消さない");
        _suppressionMessages.Should().NotContain(m => m.Source == CardReadingSource.StaffCardReplacement,
            "タッチを待たないので抑制も取らない");
        VerifyNoReplacementWrite();
    }

    public static IEnumerable<object[]> UnsavedEditCases()
    {
        yield return new object[] { "氏名", (Action<StaffManageViewModel>)(vm => vm.EditName = "博多 花子（改姓）") };
        yield return new object[] { "職員番号", (Action<StaffManageViewModel>)(vm => vm.EditNumber = "S-099") };
        yield return new object[] { "備考", (Action<StaffManageViewModel>)(vm => vm.EditNote = "総務課") };
    }

    [Theory]
    [MemberData(nameof(UnsavedEditCases))]
    public async Task 保存していない変更があれば差し替えを始めず先に保存か取り消しを案内すること(
        string field, Action<StaffManageViewModel> edit)
    {
        // 差し替えが済むと編集を終える（CancelEdit）ので、未保存の変更を黙って捨てないこと
        ArrangeAuthenticationSucceeds();
        EnterEditForm();
        edit(_viewModel);

        var task = _viewModel.ReplaceStaffCardAsync();
        task.IsCompleted.Should().BeTrue("認証もタッチ待ちも始めずに終わる");
        await task;

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never, field);
        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplacementBlockedByUnsavedEditMessage);
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeTrue("入力内容は消さない");
        VerifyNoReplacementWrite();
    }

    [Fact]
    public async Task 入力が編集開始時のままなら差し替えを始めること()
    {
        // 上の対: 前後の空白だけの違いは保存と同じサニタイズで変更と見なさない
        ArrangeAuthenticationSucceeds();
        EnterEditForm();
        _viewModel.EditName = "  " + TargetName + " ";

        var task = _viewModel.ReplaceStaffCardAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員証の差し替え"), Times.Once);
        _viewModel.IsWaitingForReplacementCard.Should().BeTrue();
        _viewModel.CancelReplaceStaffCard();
        await task;
    }

    [Fact]
    public async Task 新規登録中は差し替えを始めないこと()
    {
        ArrangeAuthenticationSucceeds();
        _viewModel.StartNewStaff();

        await _viewModel.ReplaceStaffCardAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
    }

    [Fact]
    public async Task 認証の後はタッチ待ちに入り_メイン画面のカード読み取りを抑制すること()
    {
        var task = StartReplacementAndWaitForTouch();

        _viewModel.ReplacementCardRemainingSeconds.Should().Be(StaffManageViewModel.ReplacementCardTimeoutSeconds);
        _viewModel.IsStaffCardReplacementIdle.Should().BeFalse("待っている間は一覧・入力欄を操作させない");
        Timer.IsRunning.Should().BeTrue();
        _suppressionMessages.Should().Contain((true, CardReadingSource.StaffCardReplacement));

        _viewModel.CancelReplaceStaffCard();
        await task;
    }

    #endregion

    #region 成功

    [Fact]
    public async Task 新しい職員証をタッチして確認すると差し替え_操作ログに認証した職員を記録すること()
    {
        var task = StartReplacementAndWaitForTouch();

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _dialogServiceMock.Verify(d => d.ShowConfirmation(
            StaffManageViewModel.BuildReplacementConfirmationMessage(TargetLabel),
            StaffManageViewModel.ReplacementConfirmationTitle), Times.Once);
        _staffRepositoryMock.Verify(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()), Times.Once);

        var log = _operationLogs.Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.TargetTable.Should().Be(OperationLogger.Tables.Staff);
        log.TargetId.Should().Be(NewIdm);
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm, "認証した職員の操作として記録する");
        log.OperatorName.Should().Be(AuthenticatedStaffName);
        log.BeforeData.Should().Contain(TargetIdm);
        log.AfterData.Should().Contain(NewIdm);

        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.BuildReplacementCompletedMessage(TargetLabel));
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsEditing.Should().BeFalse("差し替えたら編集を終える");
        _viewModel.NewlyRegisteredIdm.Should().Be(NewIdm, "差し替えた職員を一覧で強調する");
        _replacedMessages.Should().ContainSingle("メイン画面の 30 秒ルールの前回操作者を置き換えさせる")
            .Which.Should().Match<StaffCardReplacedMessage>(m => m.OldIdm == TargetIdm && m.NewIdm == NewIdm);
        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        _staffRepositoryMock.Verify(r => r.InvalidateCachesAffectedByStaffIdmReplacement(), Times.AtLeastOnce());
        VerifySuppressionReleased();
    }

    [Fact]
    public async Task タッチ待ちの間のカード読み取りは差し替えへ渡り_新規登録のIDmにならないこと()
    {
        var task = StartReplacementAndWaitForTouch();

        _cardReaderMock.Raise(r => r.CardRead += null, new CardReadEventArgs { Idm = NewIdm });
        await task;

        _staffRepositoryMock.Verify(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.IsNewStaff.Should().BeFalse();
    }

    [Fact]
    public async Task 差し替えの最中は保存しないこと()
    {
        var task = StartReplacementAndWaitForTouch();
        _viewModel.EditName = "書き換え中";

        await _viewModel.SaveAsync();

        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);

        _viewModel.CancelReplaceStaffCard();
        await task;
    }

    #endregion

    #region やめる・時間切れ

    [Fact]
    public async Task 差し替えをやめるとタッチ待ちを終えて何も書かないこと()
    {
        var task = StartReplacementAndWaitForTouch();

        _viewModel.CancelReplaceStaffCard();
        task.IsCompleted.Should().BeTrue("やめたら差し替えの操作は終わる");
        await task;

        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplacementCancelledMessage);
        _viewModel.IsStatusError.Should().BeFalse();
        _viewModel.IsEditing.Should().BeTrue("編集は続けられる");
        Timer.IsRunning.Should().BeFalse();
        VerifyNoReplacementWrite();
        VerifySuppressionReleased();
    }

    [Fact]
    public async Task 編集を取り消すと差し替えもやめること()
    {
        var task = StartReplacementAndWaitForTouch();

        _viewModel.CancelEdit();
        task.IsCompleted.Should().BeTrue("編集を取り消したら差し替えの操作も終わる");
        await task;

        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        _viewModel.IsEditing.Should().BeFalse();
        VerifyNoReplacementWrite();
        VerifySuppressionReleased();
    }

    [Fact]
    public async Task 画面を閉じると差し替えもやめること()
    {
        var task = StartReplacementAndWaitForTouch();

        _viewModel.Cleanup();
        task.IsCompleted.Should().BeTrue("画面を閉じたら差し替えの操作も終わる");
        await task;

        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        VerifyNoReplacementWrite();
    }

    [Fact]
    public async Task 時間切れの1秒前まではタッチを待ち続けること()
    {
        var task = StartReplacementAndWaitForTouch();

        Timer.SimulateTicks(StaffManageViewModel.ReplacementCardTimeoutSeconds - 1);

        _viewModel.IsWaitingForReplacementCard.Should().BeTrue();
        _viewModel.ReplacementCardRemainingSeconds.Should().Be(1);
        task.IsCompleted.Should().BeFalse();

        // 残り 1 秒でタッチすれば差し替えられる
        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;
        _staffRepositoryMock.Verify(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    [Fact]
    public async Task 時間切れになるとタッチ待ちを終えて何も書かないこと()
    {
        var task = StartReplacementAndWaitForTouch();

        Timer.SimulateTicks(StaffManageViewModel.ReplacementCardTimeoutSeconds);
        task.IsCompleted.Should().BeTrue("時間切れで差し替えの操作は終わる");
        await task;

        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplacementTimedOutMessage);
        _viewModel.IsStatusError.Should().BeTrue();
        Timer.IsRunning.Should().BeFalse();
        VerifyNoReplacementWrite();
        VerifySuppressionReleased();

        // 時間切れの後のタッチは差し替えに使わない
        _viewModel.HandleReplacementCardRead(NewIdm);
        VerifyNoReplacementWrite();
    }

    [Fact]
    public async Task 確認でいいえを選ぶと何も書かないこと()
    {
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        var task = StartReplacementAndWaitForTouch();

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplacementNotConfirmedMessage);
        VerifyNoReplacementWrite();
        VerifySuppressionReleased();
    }

    #endregion

    #region 拒否

    public static IEnumerable<object[]> RejectionCases()
    {
        yield return new object[] { StaffCardReplacementStatus.SameCard, null!, TargetLabel };
        yield return new object[]
        {
            StaffCardReplacementStatus.RegisteredStaff, new Staff { StaffIdm = OtherIdm, Name = "天神 次郎", Number = "S-002" },
            "職員「天神 次郎（S-002）」"
        };
        yield return new object[]
        {
            StaffCardReplacementStatus.DeletedStaff,
            new Staff { StaffIdm = OtherIdm, Name = "退職 三郎", IsDeleted = true }, "削除済みの職員「退職 三郎」"
        };
        yield return new object[] { StaffCardReplacementStatus.RegisteredCard, null!, "交通系ICカードとして登録" };
    }

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public async Task 使えない職員証をタッチしたら理由を案内してタッチを待ち続けること(
        StaffCardReplacementStatus status, Staff? conflicting, string expectedFragment)
    {
        _staffRepositoryMock.Setup(r => r.CheckStaffIdmReplacementAsync(TargetIdm, OtherIdm))
            .ReturnsAsync(StaffCardReplacementResult.Rejected(status, conflicting));
        var task = StartReplacementAndWaitForTouch();

        _viewModel.HandleReplacementCardRead(OtherIdm);

        _viewModel.IsWaitingForReplacementCard.Should().BeTrue("別の職員証をタッチし直せる");
        _viewModel.ReplacementCardRemainingSeconds.Should().Be(StaffManageViewModel.ReplacementCardTimeoutSeconds,
            "タッチし直す時間を取り直す");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.StatusMessage.Should().Contain(expectedFragment, "何が（タッチした職員証）と、なぜ（誰として登録済みか）");
        _viewModel.StatusMessage.Should().EndWith("てください）。", "どうすれば（新しい職員証をタッチする・やめる）");
        _dialogServiceMock.Verify(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        VerifyNoReplacementWrite();

        // 続けて正しい職員証をタッチすれば差し替えられる
        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;
        _staffRepositoryMock.Verify(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    [Fact]
    public async Task 確認の後で他のパソコンが同じ職員証を登録していたら差し替えず案内すること()
    {
        var other = new Staff { StaffIdm = NewIdm, Name = "天神 次郎", Number = "S-002" };
        _staffRepositoryMock.Setup(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.RegisteredStaff, other));
        var task = StartReplacementAndWaitForTouch();

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.BuildReplacementRejectedAtSaveMessage(
            StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.RegisteredStaff, other), TargetLabel));
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeTrue();
        _operationLogs.Should().BeEmpty();
        VerifySuppressionReleased();
    }

    [Fact]
    public async Task 対象の職員が見つからなければ一覧を再読込して競合を案内すること()
    {
        _staffRepositoryMock.Setup(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.TargetNotFound));
        var task = StartReplacementAndWaitForTouch();
        _staffRepositoryMock.Invocations.Clear();

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _viewModel.StatusMessage.Should().Be(
            ConcurrencyConflictMessage.ForReplaceStaffCard($"職員「{TargetLabel}」", "職員一覧"));
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeTrue("入力内容は消さない");
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.AtLeastOnce(), "案内のとおり一覧を再読込する");
        _operationLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task タッチの確認で対象の職員が見つからなければ確認を出さずに競合を案内すること()
    {
        _staffRepositoryMock.Setup(r => r.CheckStaffIdmReplacementAsync(TargetIdm, NewIdm))
            .ReturnsAsync(StaffCardReplacementResult.Rejected(StaffCardReplacementStatus.TargetNotFound));
        var task = StartReplacementAndWaitForTouch();

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _viewModel.IsWaitingForReplacementCard.Should().BeFalse("対象がいないのでタッチを待ち続けない");
        _viewModel.StatusMessage.Should().Be(
            ConcurrencyConflictMessage.ForReplaceStaffCard($"職員「{TargetLabel}」", "職員一覧"));
        _dialogServiceMock.Verify(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        VerifyNoReplacementWrite();
        VerifySuppressionReleased();
    }

    #endregion

    #region 後処理の失敗

    [Fact]
    public async Task 差し替えた後の一覧の再読込に失敗しても_差し替えの失敗とは案内しないこと()
    {
        var task = StartReplacementAndWaitForTouch();
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ThrowsAsync(new SQLiteException("disk I/O error"));

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _operationLogs.Should().ContainSingle("差し替えは確定している");
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.ReplacementCommittedButRefreshFailedMessage);
        _viewModel.IsEditing.Should().BeFalse();
        VerifySuppressionReleased();
    }

    [Fact]
    public async Task 差し替えの書き込みが例外になったら確定していないと案内すること()
    {
        _staffRepositoryMock.Setup(r => r.ReplaceStaffIdmAsync(TargetIdm, NewIdm, It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new SQLiteException("disk I/O error"));
        var task = StartReplacementAndWaitForTouch();

        _viewModel.HandleReplacementCardRead(NewIdm);
        await task;

        _viewModel.StatusMessage.Should().Be(ExceptionMessageFormatter.ToUserMessage(
            new SQLiteException("disk I/O error"), "職員証の差し替え"));
        _viewModel.StatusMessage.Should().NotContain("disk I/O error", "生の例外メッセージは画面に出さない");
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.IsWaitingForReplacementCard.Should().BeFalse();
        VerifySuppressionReleased();
    }

    #endregion

    #region 文言

    public static IEnumerable<object[]> AllMessages()
    {
        yield return new object[] { StaffManageViewModel.ReplaceAuthenticationCancelledMessage };
        yield return new object[] { StaffManageViewModel.ReplacementTimedOutMessage };
        yield return new object[] { StaffManageViewModel.ReplacementNotConfirmedMessage };
        yield return new object[] { StaffManageViewModel.ReplacementCommittedButRefreshFailedMessage };
        yield return new object[] { StaffManageViewModel.ReplacementBlockedByUnsavedEditMessage };
        foreach (var status in new[]
                 {
                     StaffCardReplacementStatus.SameCard, StaffCardReplacementStatus.RegisteredStaff,
                     StaffCardReplacementStatus.DeletedStaff, StaffCardReplacementStatus.RegisteredCard
                 })
        {
            var check = StaffCardReplacementResult.Rejected(status, new Staff { StaffIdm = OtherIdm, Name = "天神 次郎" });
            yield return new object[] { StaffManageViewModel.BuildReplacementRejectedWhileWaitingMessage(check, TargetLabel) };
            yield return new object[] { StaffManageViewModel.BuildReplacementRejectedAtSaveMessage(check, TargetLabel) };
        }
    }

    [Theory]
    [MemberData(nameof(AllMessages))]
    public void エラーの案内は行動指示で終わり_交通系ICカードを単にICカードと書かないこと(string message)
    {
        message.Length.Should().BeGreaterOrEqualTo(20);
        message.Should().MatchRegex("(してください|押してください|選択してください)（?[^。]*）?。$");
        message.Replace("交通系ICカード", string.Empty).Should().NotContain("ICカード");
        message.Should().NotContain(TargetIdm).And.NotContain(OtherIdm, "IDm は利用者が読み取れる識別子ではないので文言に出さない");
    }

    #endregion
}
