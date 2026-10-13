using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2263: 職員の登録・更新・復元は職員証の認証を経て行い、操作ログに認証した職員を記録する。
/// 登録済みの職員が 0 人（初回セットアップ）のときだけ認証を免除する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は登録・更新・復元に認証が無く、操作ログの操作者は「GUI操作」だった。認証は登録済みの職員なら誰でも通るため、
/// 手持ちの任意の FeliCa カードを架空の氏名で登録すれば、認証付きの全操作を通過でき、誰が登録したかも残らなかった。
/// </para>
/// <para>
/// 認証の代役は本物の <see cref="StaffAuthService"/> と同じ結果（<c>Enter()</c> で操作者セッションを開始する認証結果）を
/// 返し、操作ログは本物の <see cref="OperationLogger"/> が本物の <see cref="CurrentOperatorContext"/> から操作者を解決する。
/// 「記録された操作者」は、書き込まれた操作ログの行で表明する（Issue #1729 の「操作者の帰属はテストで表明する」）。
/// </para>
/// </remarks>
public class StaffManageViewModelAuthenticationTests : IDisposable
{
    private const string TargetIdm = "FFFF000000000101";
    private const string AuthenticatedStaffIdm = "FFFF0000000000AA";
    private const string AuthenticatedStaffName = "認証 太郎";

    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<IValidationService> _validationServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepositoryMock = new();
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 10, 13, 9, 0, 0)));
    private readonly List<OperationLog> _operationLogs = new();
    private readonly DbContext _dbContext;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelAuthenticationTests()
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

        // 既定: 登録済みの職員がいる（認証を免除しない）
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        // 旧オーバーロード（トランザクション無し）も成功させる。呼ばれたら「認証なしで書かれた」ことを検出できるように
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Staff>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(r => r.RestoreAsync(It.IsAny<string>())).ReturnsAsync(true);

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
            new RecordingDispatcherService());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 認証の代役を、本物と同じ（<c>Enter()</c> で操作者セッションを開始する）認証結果で成功させる。
    /// 応答は非同期に完了させる（セッションを認証サービスの内側で開始する実装だと画面の流れに残らず赤になる）。
    /// </summary>
    private void ArrangeAuthenticationSucceeds()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .Returns(async () =>
            {
                await Task.Yield();
                return StaffAuthService.CreateAuthenticatedResult(
                    _operatorContext, AuthenticatedStaffIdm, AuthenticatedStaffName);
            });
    }

    /// <summary>認証のキャンセル・タイムアウト（認証サービスは null を返す）。</summary>
    private void ArrangeAuthenticationCancelled()
    {
        _staffAuthServiceMock
            .Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync((StaffAuthResult?)null);
    }

    private void ArrangeNoActiveStaff()
    {
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(false);
    }

    private void ArrangeUnregisteredTarget()
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(TargetIdm, true)).ReturnsAsync((Staff?)null);
    }

    private void ArrangeDeletedTarget()
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(TargetIdm, true)).ReturnsAsync(new Staff
        {
            StaffIdm = TargetIdm,
            Name = "削除 次郎",
            Number = "S-009",
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 4, 1)
        });
    }

    /// <summary>未登録の職員証をタッチして登録モードに入り、氏名を入力した状態にする。</summary>
    private async Task EnterNewStaffFormAsync()
    {
        ArrangeUnregisteredTarget();
        (await _viewModel.StartNewStaffWithIdmAsync(TargetIdm)).Should().BeFalse("登録モードに入る");
        _viewModel.EditName = "新人 花子";
        _viewModel.EditNumber = "S-100";
    }

    /// <summary>登録済みの職員を選んで編集を始め、氏名を書き換えた状態にする。</summary>
    private void EnterEditForm()
    {
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(TargetIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = TargetIdm,
            Name = "既存 一郎",
            Number = "S-001"
        });
        _viewModel.SelectedStaff = new StaffDto { StaffIdm = TargetIdm, Name = "既存 一郎", Number = "S-001" };
        _viewModel.StartEdit();
        _viewModel.EditName = "既存 一郎（改姓）";
    }

    private void VerifyNoStaffWrite()
    {
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Staff>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(It.IsAny<string>()), Times.Never);
        _operationLogs.Should().BeEmpty("何も書いていないので操作ログも残らない");
    }

    private OperationLog SingleStaffLog(string action)
    {
        return _operationLogs.Should().ContainSingle(l =>
            l.TargetTable == OperationLogger.Tables.Staff &&
            l.TargetId == TargetIdm &&
            l.Action == action).Subject;
    }

    #region 登録

    [Fact]
    public async Task 登録_職員がいれば認証してから登録し_操作ログに認証した職員を記録すること()
    {
        ArrangeAuthenticationSucceeds();
        await EnterNewStaffFormAsync();

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Once);
        _staffRepositoryMock.Verify(r => r.InsertAsync(
            It.Is<Staff>(s => s.StaffIdm == TargetIdm && s.Name == "新人 花子"), It.IsAny<SQLiteTransaction>()), Times.Once);
        var log = SingleStaffLog(OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm, "認証した職員の操作として記録する");
        log.OperatorName.Should().Be(AuthenticatedStaffName);
        _viewModel.StatusMessage.Should().Be("登録しました");
    }

    [Fact]
    public async Task 登録_認証が完了しなければ登録せず_入力内容を残して案内すること()
    {
        ArrangeAuthenticationCancelled();
        await EnterNewStaffFormAsync();

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の登録"), Times.Once);
        VerifyNoStaffWrite();
        _viewModel.IsEditing.Should().BeTrue("入力内容を消さない（Issue #1757）");
        _viewModel.IsNewStaff.Should().BeTrue();
        _viewModel.EditStaffIdm.Should().Be(TargetIdm);
        _viewModel.EditName.Should().Be("新人 花子");
        _viewModel.EditNumber.Should().Be("S-100");
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.RegisterAuthenticationCancelledMessage);
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// 対: 登録済みの職員が 0 人（初回セットアップ）なら、認証できる職員証がまだ無いので免除する。
    /// 免除した登録は「GUI操作」として記録される
    /// </summary>
    [Fact]
    public async Task 登録_職員が0人なら認証を求めずに登録し_GUI操作として記録すること()
    {
        ArrangeNoActiveStaff();
        ArrangeAuthenticationCancelled();
        await EnterNewStaffFormAsync();

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Once);
        var log = SingleStaffLog(OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        _viewModel.StatusMessage.Should().Be("登録しました");
    }

    /// <summary>
    /// 認証は入力の検証を通った後に求める（検証で弾かれる入力のために職員証をタッチさせない）
    /// </summary>
    [Fact]
    public async Task 登録_入力の検証で弾かれたときは認証を求めないこと()
    {
        ArrangeAuthenticationSucceeds();
        await EnterNewStaffFormAsync();
        _validationServiceMock.Setup(v => v.ValidateStaffName(It.IsAny<string>()))
            .Returns(ValidationResult.Failure("氏名が未入力です。氏名を入力してください。"));

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        VerifyNoStaffWrite();
    }

    #endregion

    #region 更新

    [Fact]
    public async Task 更新_認証してから更新し_操作ログに認証した職員を記録すること()
    {
        ArrangeAuthenticationSucceeds();
        EnterEditForm();

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員情報の変更"), Times.Once);
        _staffRepositoryMock.Verify(r => r.UpdateAsync(
            It.Is<Staff>(s => s.StaffIdm == TargetIdm && s.Name == "既存 一郎（改姓）"), It.IsAny<SQLiteTransaction>()), Times.Once);
        var log = SingleStaffLog(OperationLogger.Actions.Update);
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        log.OperatorName.Should().Be(AuthenticatedStaffName);
        _viewModel.StatusMessage.Should().Be("更新しました");
    }

    [Fact]
    public async Task 更新_認証が完了しなければ更新せず_入力内容を残して案内すること()
    {
        ArrangeAuthenticationCancelled();
        EnterEditForm();

        await _viewModel.SaveAsync();

        VerifyNoStaffWrite();
        _viewModel.IsEditing.Should().BeTrue("入力内容を消さない（Issue #1757）");
        _viewModel.EditStaffIdm.Should().Be(TargetIdm);
        _viewModel.EditName.Should().Be("既存 一郎（改姓）");
        _viewModel.StatusMessage.Should().Be(StaffManageViewModel.UpdateAuthenticationCancelledMessage);
        _viewModel.IsStatusError.Should().BeTrue();
    }

    #endregion

    #region 復元（3 つの入口）

    /// <summary>
    /// 入口 1: 新規登録の保存で、登録しようとした職員証が削除済みだった（確認ダイアログで「復元する」）
    /// </summary>
    [Fact]
    public async Task 復元_保存経路_認証してから復元し_操作ログに認証した職員を記録すること()
    {
        ArrangeAuthenticationSucceeds();
        await EnterNewStaffFormAsync();
        ArrangeDeletedTarget(); // 保存までの間に別の PC で同じ職員証の職員が削除された（形としては既存の Issue #284 経路）

        await _viewModel.SaveAsync();

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の復元"), Times.Once);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(TargetIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        var log = SingleStaffLog(OperationLogger.Actions.Restore);
        log.OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        _viewModel.StatusMessage.Should().Contain("を復元しました");
    }

    [Fact]
    public async Task 復元_保存経路_認証が完了しなければ復元せず_入力内容を残して案内すること()
    {
        ArrangeAuthenticationCancelled();
        await EnterNewStaffFormAsync();
        ArrangeDeletedTarget();

        await _viewModel.SaveAsync();

        VerifyNoStaffWrite();
        _viewModel.IsEditing.Should().BeTrue();
        _viewModel.EditName.Should().Be("新人 花子");
        _viewModel.StatusMessage.Should().Contain("復元していません");
        _viewModel.StatusMessage.Should().EndWith("してください（復元する職員証では認証できません）。");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// 入口 2: 「新規登録」ボタンの後、職員証タッチ待ちで削除済み職員の職員証をタッチした
    /// </summary>
    [Fact]
    public async Task 復元_タッチ経路_認証してから復元し_操作ログに認証した職員を記録すること()
    {
        ArrangeAuthenticationSucceeds();
        ArrangeDeletedTarget();
        _viewModel.StartNewStaff();

        await _viewModel.HandleCardReadAsync(TargetIdm);

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の復元"), Times.Once);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(TargetIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        SingleStaffLog(OperationLogger.Actions.Restore).OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        _viewModel.IsEditing.Should().BeFalse();
        _viewModel.StatusMessage.Should().Contain("を復元しました");
    }

    [Fact]
    public async Task 復元_タッチ経路_認証が完了しなければ復元せず_もう一度タッチできる状態へ戻すこと()
    {
        ArrangeAuthenticationCancelled();
        ArrangeDeletedTarget();
        _viewModel.StartNewStaff();

        await _viewModel.HandleCardReadAsync(TargetIdm);

        VerifyNoStaffWrite();
        _viewModel.IsEditing.Should().BeTrue("登録モードのまま");
        _viewModel.IsWaitingForCard.Should().BeTrue("もう一度タッチすれば復元をやり直せる");
        _viewModel.EditStaffIdm.Should().BeEmpty("確認の済んでいない IDm をフォームに残さない");
        _viewModel.StatusMessage.Should().Contain("復元していません");
        _viewModel.IsStatusError.Should().BeTrue();
    }

    /// <summary>
    /// 入口 3: メイン画面で未登録として扱われた職員証（削除済み職員）から職員登録へ進んだ
    /// </summary>
    [Fact]
    public async Task 復元_未登録カード経路_認証してから復元し_操作ログに認証した職員を記録すること()
    {
        ArrangeAuthenticationSucceeds();
        ArrangeDeletedTarget();

        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(TargetIdm);

        shouldClose.Should().BeTrue();
        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync("職員の復元"), Times.Once);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(TargetIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        SingleStaffLog(OperationLogger.Actions.Restore).OperatorIdm.Should().Be(AuthenticatedStaffIdm);
        _dialogServiceMock.Verify(d => d.ShowInformation(It.Is<string>(m => m.Contains("を復元しました")), "復元完了"), Times.Once);
    }

    [Fact]
    public async Task 復元_未登録カード経路_認証が完了しなければ復元せず案内すること()
    {
        ArrangeAuthenticationCancelled();
        ArrangeDeletedTarget();

        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(TargetIdm);

        shouldClose.Should().BeTrue();
        VerifyNoStaffWrite();
        _dialogServiceMock.Verify(d => d.ShowInformation(It.Is<string>(m => m.Contains("復元していません")), "ご案内"), Times.Once);
        _dialogServiceMock.Verify(d => d.ShowInformation(It.IsAny<string>(), "復元完了"), Times.Never);
    }

    /// <summary>
    /// 対: 登録済みの職員が 0 人なら復元も免除する（全員を削除した後に誰も復元できなくなるのを防ぐ）
    /// </summary>
    [Fact]
    public async Task 復元_職員が0人なら認証を求めずに復元すること()
    {
        ArrangeNoActiveStaff();
        ArrangeAuthenticationCancelled();
        ArrangeDeletedTarget();
        _viewModel.StartNewStaff();

        await _viewModel.HandleCardReadAsync(TargetIdm);

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
        _staffRepositoryMock.Verify(r => r.RestoreAsync(TargetIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        SingleStaffLog(OperationLogger.Actions.Restore).OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
    }

    #endregion

    #region 認証を待つ間に他の PC が先に書き込んだとき（競合）

    /// <summary>
    /// 認証を待つ間に他のパソコンが同じ職員証を登録した（<c>InsertAsync</c> は一過性でない SQLite の失敗をすべて
    /// false に畳むので、読み直して行があることで重複と分かる）。原因と確かめ方を案内し、案内どおり一覧を再読込し、入力内容は残す
    /// </summary>
    [Fact]
    public async Task 登録_認証の間に他のパソコンが先に登録していたら_一覧を再読込して競合として案内し入力を残すこと()
    {
        ArrangeAuthenticationSucceeds();
        await EnterNewStaffFormAsync();
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);
        _staffRepositoryMock.SetupSequence(r => r.GetByIdmAsync(TargetIdm, true))
            .ReturnsAsync((Staff?)null) // 保存前の重複チェック: まだ無い
            .ReturnsAsync(new Staff { StaffIdm = TargetIdm, Name = "他PC 登録" }); // 書き込みの後の読み直し: 他 PC が登録済み
        _staffRepositoryMock.Invocations.Clear();

        await _viewModel.SaveAsync();

        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once, "案内が「再読み込みしました」と述べるので、先に再読込する");
        _viewModel.StatusMessage.Should().Be(ConcurrencyConflictMessage.ForRegister("職員「新人 花子（S-100）」", "職員一覧"));
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeTrue("入力内容を消さない（Issue #1757）");
        _viewModel.EditName.Should().Be("新人 花子");
        _operationLogs.Should().BeEmpty();
    }

    /// <summary>
    /// 対: 書き込めなかったが読み直しても行が無い（ディスク満杯・読み取り専用・破損・I/O エラー等）。
    /// 「先に登録された可能性」「一覧を再読み込みしました」と誤誘導せず、原因を断定しない案内にする
    /// </summary>
    [Fact]
    public async Task 登録_書き込めず読み直しても行が無ければ_競合とは案内せず書き込めなかったことを案内すること()
    {
        ArrangeAuthenticationSucceeds();
        await EnterNewStaffFormAsync();
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);
        _staffRepositoryMock.Invocations.Clear();

        await _viewModel.SaveAsync();

        _staffRepositoryMock.Verify(r => r.GetByIdmAsync(TargetIdm, true), Times.Exactly(2), "書き込みの後に読み直して原因を分ける");
        _viewModel.StatusMessage.Should().Be(
            StaffManageViewModel.BuildRegisterWriteFailedMessage("職員「新人 花子（S-100）」"));
        _viewModel.StatusMessage.Should().NotContain("先に登録された");
        _viewModel.StatusMessage.Should().NotContain("再読み込みしました");
        _viewModel.IsStatusError.Should().BeTrue();
        _viewModel.IsEditing.Should().BeTrue("入力内容を消さない（Issue #1757）");
        _viewModel.EditName.Should().Be("新人 花子");
    }

    /// <summary>
    /// 未登録カード経路で、認証を待つ間に他のパソコンが先に復元した。保存経路・タッチ経路と同じ競合の案内にそろえ、
    /// 案内どおり一覧で確かめられるよう、一覧を再読込してダイアログを開いたままにする
    /// </summary>
    [Fact]
    public async Task 復元_未登録カード経路_他のパソコンが先に復元していたら_一覧を再読込して競合として案内すること()
    {
        ArrangeAuthenticationSucceeds();
        ArrangeDeletedTarget();
        _staffRepositoryMock.Setup(r => r.RestoreAsync(TargetIdm, It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);

        var shouldClose = await _viewModel.StartNewStaffWithIdmAsync(TargetIdm);

        shouldClose.Should().BeFalse("閉じると「一覧を再読み込みしました」が事実にならない");
        _staffRepositoryMock.Verify(r => r.GetAllAsync(), Times.Once);
        _viewModel.StatusMessage.Should().Be(ConcurrencyConflictMessage.ForRestore("職員「削除 次郎（S-009）」", "職員一覧"));
        _viewModel.IsStatusError.Should().BeTrue();
        _dialogServiceMock.Verify(d => d.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _dialogServiceMock.Verify(d => d.ShowInformation(It.IsAny<string>(), "復元完了"), Times.Never);
    }

    #endregion

    #region 案内で職員を名指しする表記

    /// <summary>
    /// 氏名・職員番号ともに空の削除済み職員（CSV 取り込み等で入り得る）でも、案内に空の名前を埋め込まないこと。
    /// 未登録カード経路は以前、氏名が空なら職員番号（null 可）をそのまま使っていた
    /// </summary>
    [Fact]
    public async Task 復元_未登録カード経路_氏名も職員番号も無い職員でも空の名前で案内しないこと()
    {
        ArrangeAuthenticationCancelled();
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(TargetIdm, true)).ReturnsAsync(new Staff
        {
            StaffIdm = TargetIdm,
            Name = string.Empty,
            Number = null,
            IsDeleted = true
        });
        var messages = new List<string>();
        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((m, _) => messages.Add(m)).Returns(true);
        _dialogServiceMock.Setup(d => d.ShowInformation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((m, _) => messages.Add(m));

        await _viewModel.StartNewStaffWithIdmAsync(TargetIdm);

        messages.Should().HaveCount(2, "復元の確認と、認証が完了しなかった案内");
        messages.Should().OnlyContain(m => m.Contains(StaffManageViewModel.UnnamedStaffLabel));
        messages.Should().NotContain(m => m.Contains("以前  として") || m.Contains("、 を復元"));
    }

    [Theory]
    [InlineData("田中 太郎", "S-001", "田中 太郎（S-001）")]
    [InlineData("田中 太郎", null, "田中 太郎")]
    [InlineData("", "S-001", "職員番号 S-001")]
    [InlineData(null, "  ", StaffManageViewModel.UnnamedStaffLabel)]
    public void 職員の表記_氏名が無ければ職員番号_どちらも無ければ代わりの表記にすること(
        string? name, string? number, string expected)
    {
        StaffManageViewModel.FormatStaffLabel(name, number).Should().Be(expected);
    }

    #endregion

    #region 免除の判定は処理中スコープの中で待つ

    /// <summary>
    /// 免除の判定（<c>HasActiveStaffAsync</c>）を止めておき、待っている間の状態を調べる。
    /// </summary>
    /// <remarks>
    /// 処理中の外で判定の DB を待つと（共有モードでは busy_timeout の 15 秒まで待ち得る）、その間 Esc が
    /// <c>CancelEdit()</c> として通り、取り消したつもりのフォームの内容が認証の後に書き込まれ得る。
    /// 待っている間は処理中（<c>IsBusy</c>）であり、Esc が何もしない（<see cref="EditFormEscapeAction.Ignore"/>）こと。
    /// </remarks>
    private async Task AssertBusyWhileJudgingExemptionAsync(Func<Task> start)
    {
        var judgement = new TaskCompletionSource<bool>();
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).Returns(judgement.Task);
        ArrangeAuthenticationSucceeds();

        var operation = start();

        _staffRepositoryMock.Verify(r => r.HasActiveStaffAsync(), Times.Once, "前提: 免除の判定を待っていること");
        _viewModel.IsBusy.Should().BeTrue("免除の判定は処理中スコープの中で待つ");
        EditFormKeyPolicy.ResolveEscapeAction(_viewModel.IsEditing, _viewModel.IsBusy)
            .Should().Be(EditFormEscapeAction.Ignore, "判定を待つ間に Esc で編集を取り消せないこと");

        judgement.SetResult(true);
        await operation;

        _staffAuthServiceMock.Verify(a => a.RequestAuthenticationAsync(It.IsAny<string>()), Times.Once);
        _viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task 登録_免除の判定を待つ間は処理中でEscが効かないこと()
    {
        await EnterNewStaffFormAsync();

        await AssertBusyWhileJudgingExemptionAsync(() => _viewModel.SaveAsync());
    }

    [Fact]
    public async Task 更新_免除の判定を待つ間は処理中でEscが効かないこと()
    {
        EnterEditForm();

        await AssertBusyWhileJudgingExemptionAsync(() => _viewModel.SaveAsync());
    }

    [Fact]
    public async Task 復元_保存経路_免除の判定を待つ間は処理中でEscが効かないこと()
    {
        await EnterNewStaffFormAsync();
        ArrangeDeletedTarget();

        await AssertBusyWhileJudgingExemptionAsync(() => _viewModel.SaveAsync());
    }

    [Fact]
    public async Task 復元_タッチ経路_免除の判定を待つ間は処理中でEscが効かないこと()
    {
        ArrangeDeletedTarget();
        _viewModel.StartNewStaff();

        await AssertBusyWhileJudgingExemptionAsync(() => _viewModel.HandleCardReadAsync(TargetIdm));
    }

    [Fact]
    public async Task 復元_未登録カード経路_免除の判定を待つ間は処理中でEscが効かないこと()
    {
        ArrangeDeletedTarget();

        await AssertBusyWhileJudgingExemptionAsync(() => _viewModel.StartNewStaffWithIdmAsync(TargetIdm));
    }

    #endregion

    #region 認証ダイアログが認証する職員

    /// <summary>
    /// 認証ダイアログは登録済み（論理削除されていない）職員だけを認証すること。
    /// これから登録する職員証（未登録）や削除済み職員の職員証で認証を通せると、登録・復元に認証を掛けた意味が無くなる。
    /// </summary>
    /// <remarks>
    /// <c>StaffAuthDialog</c> は Window のため単体テストから開けない。判定に使う呼び出しの形を静的に固定し、
    /// その呼び出しが削除済みを返さないことは <c>StaffRepositoryTests.GetByIdmAsync_DeletedStaff_ReturnsNull</c> が固定する。
    /// </remarks>
    [Fact]
    public void 認証ダイアログは削除済みを含めずに職員を引くこと()
    {
        var path = Directory
            .GetFiles(TestPaths.GetProductionSourceRoot(), "StaffAuthDialog.xaml.cs", SearchOption.AllDirectories)
            .Single();
        var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

        var lookups = Regex.Matches(code, @"\.GetByIdmAsync\((?<args>[^)]*)\)").Cast<System.Text.RegularExpressions.Match>().ToList();

        lookups.Should().NotBeEmpty("認証ダイアログはタッチされた職員証を職員として引く");
        lookups.Should().OnlyContain(m => !m.Groups["args"].Value.Contains("includeDeleted") &&
                                          !m.Groups["args"].Value.Contains("true"),
            "削除済みの職員を含めて引くと、削除済み職員の職員証で認証が通る");
    }

    #endregion
}
