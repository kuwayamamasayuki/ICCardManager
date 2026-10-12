using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2260（レビュー 3 回目）: 操作者セッションは WPF の実際の流れでも、認証した操作に閉じること。
/// </summary>
/// <remarks>
/// <para>
/// セッションは <c>AsyncLocal</c> に持つ。本物の Dispatcher（STA スレッドのメッセージループ）の上で、
/// 認証した操作が DB を待っている間に届いた<b>新しい入力イベント</b>は、操作の流れを引き継がず「GUI操作」になること、
/// 操作の <c>await</c> の続き（<c>DispatcherSynchronizationContext</c> 経由）と、操作の流れから投げた
/// <c>Dispatcher.InvokeAsync</c>、操作が開いたモーダルダイアログ（<c>ShowDialog</c> の入れ子のメッセージループ）の
/// 中の入力は、同じ操作の続きとして認証した職員になることを固定する（対として、操作の外の最上位の入力は「GUI操作」）。
/// </para>
/// <para>
/// 入力イベントは、投稿元の ExecutionContext を持ち込まない形（<c>ExecutionContext.SuppressFlow</c> の下での
/// <c>BeginInvoke</c>）で投げる。Win32 の入力メッセージと同じく、メッセージループがその時点で持つ流れの上で走る。
/// </para>
/// </remarks>
[Collection(StaThreadCollection.Name)]
public class OperatorSessionDispatcherFlowTests
{
    private const string StaffIdm = "AAAA000000000001";
    private const string StaffName = "田中太郎";

    [Fact]
    public void Dispatcherの新しい入力イベントは認証した操作の流れを引き継がないこと()
    {
        var recorded = new Dictionary<string, string>();

        StaTestRunner.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            var context = new CurrentOperatorContext(new FixedSystemClock(new DateTime(2026, 10, 13, 9, 0, 0)));
            var logs = new List<OperationLog>();
            var repository = new Mock<IOperationLogRepository>();
            repository
                .Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
                .Callback<OperationLog>(logs.Add)
                .ReturnsAsync(1);
            var logger = new OperationLogger(repository.Object, context);
            var card = new IcCard { CardIdm = "0123456789ABCDEF", CardType = "はやかけん", CardNumber = "H-001" };

            string LogNow()
            {
                // 記録用リポジトリは完了済みの Task を返すので、記録は同期的に終わる
                logger.LogCardUpdateAsync(card, card).GetAwaiter().GetResult();
                return logs[logs.Count - 1].OperatorName;
            }

            var frame = new DispatcherFrame();
            var dbWait = new TaskCompletionSource<bool>();

            void PostInputEvent(Action action)
            {
                using (ExecutionContext.SuppressFlow())
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Input, action);
                }
            }

            async Task AuthenticatedOperationAsync()
            {
                using var auth = StaffAuthService.CreateAuthenticatedResult(context, StaffIdm, StaffName).Enter();
                recorded["操作の開始"] = LogNow();

                // 操作が開いたモーダルダイアログ（ShowDialog の入れ子のメッセージループ）の中の入力。
                // 入力は投稿元の流れを持ち込まず、入れ子のループがその時点で持つ流れ（＝操作の流れ）の上で走る
                var modal = new DispatcherFrame();
                PostInputEvent(() =>
                {
                    recorded["モーダルダイアログ内の入力"] = LogNow();
                    modal.Continue = false;
                });
                Dispatcher.PushFrame(modal);

                // 操作が DB を待つ間にメイン画面へ届く入力（メッセージループの最上位の流れで走る）
                PostInputEvent(() => recorded["待ち時間中の入力イベント"] = LogNow());
                PostInputEvent(() => dbWait.SetResult(true));
                await dbWait.Task;                                              // DB の待ちに相当
                recorded["await の続き"] = LogNow();
                recorded["操作から投げた InvokeAsync"] = await dispatcher.InvokeAsync(LogNow);
            }

            PostInputEvent(() =>
            {
                var operation = AuthenticatedOperationAsync();
                operation.ContinueWith(
                    t => PostInputEvent(() =>
                    {
                        if (t.IsFaulted)
                        {
                            recorded["例外"] = t.Exception!.GetBaseException().Message;
                        }

                        recorded["操作の後の入力イベント"] = LogNow();
                        frame.Continue = false;
                    }),
                    TaskScheduler.Default);
            });
            Dispatcher.PushFrame(frame);
        });

        recorded.Should().NotContainKey("例外");
        recorded["操作の開始"].Should().Be(StaffName);
        recorded["モーダルダイアログ内の入力"].Should().Be(StaffName,
            "認証した操作が開いたモーダルダイアログの中の入力（保存ボタン等）は、その操作の続き");
        recorded["待ち時間中の入力イベント"].Should().Be(OperationLogger.GuiOperator.Name,
            "認証した操作の待ち時間中にメッセージループへ届いた新しい入力イベントは、別の操作である");
        recorded["await の続き"].Should().Be(StaffName);
        recorded["操作から投げた InvokeAsync"].Should().Be(StaffName,
            "操作の流れから投げた Dispatcher の処理は、同じ操作の続き");
        recorded["操作の後の入力イベント"].Should().Be(OperationLogger.GuiOperator.Name);
    }
}
