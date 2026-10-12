using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2260: 認証結果（<see cref="StaffAuthResult"/>）は、認証した 1 操作に限る操作者セッションのスコープである。
/// </summary>
/// <remarks>
/// 認証ダイアログ（モーダル）を伴わない部分 <see cref="StaffAuthService.CreateAuthenticatedResult"/> を
/// 本物の <see cref="CurrentOperatorContext"/> と <see cref="OperationLogger"/> に通して、
/// 操作ログの操作者が「認証した職員」から「GUI 操作」へ戻る境目を固定する。
/// </remarks>
public class StaffAuthServiceSessionTests
{
    private const string StaffIdm = "AAAA000000000001";
    private const string StaffName = "田中太郎";

    private readonly FixedSystemClock _clock = new FixedSystemClock(new DateTime(2026, 10, 13, 9, 0, 0));
    private readonly CurrentOperatorContext _operatorContext;
    private readonly OperationLogger _operationLogger;
    private readonly List<OperationLog> _logs = new List<OperationLog>();

    public StaffAuthServiceSessionTests()
    {
        _operatorContext = new CurrentOperatorContext(_clock);

        var repository = new Mock<IOperationLogRepository>();
        repository
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Callback<OperationLog>(log => _logs.Add(log))
            .ReturnsAsync(1);
        _operationLogger = new OperationLogger(repository.Object, _operatorContext);
    }

    private Task LogCardUpdateAsync()
    {
        var card = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "H-001" };
        return _operationLogger.LogCardUpdateAsync(card, card);
    }

    /// <summary>
    /// 対の表明: 認証した操作そのもの（認証結果を破棄する前の記録）は、認証した職員で記録される。
    /// </summary>
    [Fact]
    public async Task 認証した操作は認証した職員の操作として記録されること()
    {
        using (var auth = StaffAuthService.CreateAuthenticatedResult(_operatorContext, StaffIdm, StaffName).Enter())
        {
            auth.Idm.Should().Be(StaffIdm);
            auth.StaffName.Should().Be(StaffName);

            await LogCardUpdateAsync();
        }

        _logs.Should().ContainSingle()
            .Which.Should().Match<OperationLog>(l => l.OperatorIdm == StaffIdm && l.OperatorName == StaffName);
    }

    /// <summary>
    /// 認証した操作が旧実装の有効期間（5 分）を超えて続いても、その操作の記録は認証した職員のまま
    /// （「保存して次へ」で続ける履歴の変更・入力に時間のかかる貸出記録の作成など）。
    /// 失効はスコープの閉じ忘れに対する最後の防御であり、続いている操作を途中で「GUI操作」にしない。
    /// </summary>
    [Fact]
    public async Task 認証した操作が有効期間を超えて続いても認証した職員の操作として記録されること()
    {
        using (StaffAuthService.CreateAuthenticatedResult(_operatorContext, StaffIdm, StaffName).Enter())
        {
            // 旧実装の 5 分を超え、上限（30 分）の内側
            _clock.Now = _clock.Now + TimeSpan.FromMinutes(29);

            await LogCardUpdateAsync();
        }

        _logs.Should().ContainSingle()
            .Which.Should().Match<OperationLog>(l => l.OperatorIdm == StaffIdm && l.OperatorName == StaffName);
    }

    /// <summary>
    /// Issue #2260 の中核: 認証した操作を終えた（認証結果を破棄した）後の、認証の無い操作は GUI 操作として記録される。
    /// 旧実装は認証のセッションが 5 分間残り、直前に認証した職員の名前で記録されていた。
    /// </summary>
    [Fact]
    public async Task 認証した操作の後の認証の無い操作はGUI操作として記録されること()
    {
        using (StaffAuthService.CreateAuthenticatedResult(_operatorContext, StaffIdm, StaffName).Enter())
        {
        }

        await LogCardUpdateAsync();

        _logs.Should().ContainSingle()
            .Which.Should().Match<OperationLog>(l =>
                l.OperatorIdm == OperationLogger.GuiOperator.Idm && l.OperatorName == OperationLogger.GuiOperator.Name);
    }

    /// <summary>
    /// 操作が例外で終わっても、認証のセッションは残らない（<c>using</c> による破棄）。
    /// </summary>
    [Fact]
    public void 認証した操作が例外で終わってもセッションは残らないこと()
    {
        Action act = () =>
        {
            using var auth = StaffAuthService.CreateAuthenticatedResult(_operatorContext, StaffIdm, StaffName).Enter();
            throw new InvalidOperationException("書き込みに失敗");
        };

        act.Should().Throw<InvalidOperationException>();
        _operatorContext.HasSession.Should().BeFalse();
    }

    #region 非同期の流れごとのセッション（Issue #2260 レビュー 3 回目）

    private const string OtherStaffIdm = "BBBB000000000002";
    private const string OtherStaffName = "鈴木花子";

    private async Task<OperationLog> LogAndTakeAsync()
    {
        var before = _logs.Count;
        await LogCardUpdateAsync();
        _logs.Should().HaveCount(before + 1);
        return _logs[before];
    }

    /// <summary>
    /// 職員 A の認証した操作が DB を待っている間に、別の流れで職員 B が認証した操作を行っても、
    /// それぞれの記録はそれぞれの職員になり、A の待ち時間中に別の流れで始めた認証の無い操作は「GUI操作」になる。
    /// 旧実装（シングルトンのフィールド）では、B の認証が A の残りの記録を B の名前へ書き換え、
    /// 認証の無い操作も A または B の名前で記録された。
    /// </summary>
    [Fact]
    public async Task 認証が重なってもそれぞれの流れはそれぞれの職員で記録され_別の流れの認証の無い操作はGUI操作になること()
    {
        var aWaiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aResume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bWaiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bResume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(OperationLog First, OperationLog Second)> OperationAsync(
            string idm, string name, TaskCompletionSource<bool> waiting, TaskCompletionSource<bool> resume)
        {
            using var auth = StaffAuthService.CreateAuthenticatedResult(_operatorContext, idm, name).Enter();
            var first = await LogAndTakeAsync();
            waiting.SetResult(true);
            await resume.Task;                       // DB の待ちに相当
            var second = await LogAndTakeAsync();
            return (first, second);
        }

        var operationA = OperationAsync(StaffIdm, StaffName, aWaiting, aResume);
        await aWaiting.Task;

        // A の待ち時間中に、別の流れ（メイン画面の新しい入力に相当）で認証の無い操作をする
        var unauthenticatedDuringA = await LogAndTakeAsync();

        // A の待ち時間中に、別の流れで B が認証して操作する
        var operationB = OperationAsync(OtherStaffIdm, OtherStaffName, bWaiting, bResume);
        await bWaiting.Task;

        // B の待ち時間中に A を進める（A の残りの記録）
        aResume.SetResult(true);
        var a = await operationA;

        // A が終わった後に B を進める（B の残りの記録）
        bResume.SetResult(true);
        var b = await operationB;

        var afterBoth = await LogAndTakeAsync();

        a.First.OperatorIdm.Should().Be(StaffIdm);
        a.Second.OperatorIdm.Should().Be(StaffIdm, "B が重ねて認証しても、A の残りの記録は A のまま");
        b.First.OperatorIdm.Should().Be(OtherStaffIdm);
        b.Second.OperatorIdm.Should().Be(OtherStaffIdm, "A の操作が終わっても、B の残りの記録は B のまま");
        unauthenticatedDuringA.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm,
            "認証した操作の待ち時間中に別の流れで始めた認証の無い操作は、認証した職員に帰属させない");
        afterBoth.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
    }

    /// <summary>
    /// 認証サービスが本当に非同期に完了しても、呼び出し元が <c>Enter()</c> した流れでは認証した職員で記録される。
    /// 対として、<c>async</c> メソッドの内側で開始しただけのセッションは、呼び出し元の流れには残らない
    /// （ExecutionContext は async メソッドの境界で復元される）。認証サービスの内側でセッションを開始する
    /// 実装はこの前者で赤になる。
    /// </summary>
    [Fact]
    public async Task 非同期に完了した認証でも呼び出し元でEnterすれば認証した職員で記録され_asyncの内側で開始しただけでは残らないこと()
    {
        async Task<StaffAuthResult?> RequestAuthenticationAsync()
        {
            await Task.Yield();
            return StaffAuthService.CreateAuthenticatedResult(_operatorContext, StaffIdm, StaffName);
        }

        using (var auth = (await RequestAuthenticationAsync())?.Enter())
        {
            (await LogAndTakeAsync()).OperatorIdm.Should().Be(StaffIdm);
        }

        async Task BeginInsideAsync()
        {
            _operatorContext.BeginSession(OtherStaffIdm, OtherStaffName);
            await Task.Yield();
        }

        await BeginInsideAsync();

        _operatorContext.HasSession.Should().BeFalse(
            "async メソッドの内側で開始したセッションは呼び出し元へ戻らない。開始は呼び出し元の流れで同期的に行う");
        (await LogAndTakeAsync()).OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
    }

    /// <summary>
    /// 認証した流れから派生した処理（<c>Task.Run</c>）は同じ操作の続きとして認証した職員で記録され、
    /// 操作が終わった（認証結果を破棄した）後に走ったものは「GUI操作」になる。
    /// </summary>
    [Fact]
    public async Task 認証した流れから派生した処理は操作が終わるまで認証した職員で記録されること()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OperationLog> afterEnd;

        using (StaffAuthService.CreateAuthenticatedResult(_operatorContext, StaffIdm, StaffName).Enter())
        {
            var during = await Task.Run(LogAndTakeAsync);
            during.OperatorIdm.Should().Be(StaffIdm);

            afterEnd = Task.Run(async () =>
            {
                await release.Task;
                return await LogAndTakeAsync();
            });
        }

        release.SetResult(true);
        (await afterEnd).OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm,
            "操作が終わった後に走った派生処理は、終了済みのセッションを使わない");
    }

    #endregion
}
