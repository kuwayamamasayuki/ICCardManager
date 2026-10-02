using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.PageObjects;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 職員証・交通系ICカードのタッチを DEBUG ビルドの仮想タッチで行い、その結果（トースト・ダイアログ）を待つ操作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #2019 の撮影テスト（<c>TouchScreenshotTests</c>）に private で書かれていたものを、
    /// 回帰テスト（Issue #2190。<c>LendReturnFlowTests</c> / <c>MessageBoxOwnerTests</c>）と共有するため切り出した。
    /// 同じ待ち方を 2 か所に書くと、片方だけ直される（#1763）。
    /// </para>
    /// <para>
    /// 仮想タッチは DEBUG ビルドにしか無いので、呼び出し側は Debug 構成で起動していること
    /// （<see cref="AppFixture.LaunchConfiguration"/>）。
    /// </para>
    /// </remarks>
    internal static class TouchOperations
    {
        /// <summary>トーストは 3 秒で消える（ToastNotificationWindow.DefaultDisplayDurationMs）ので、出現待ちは短く切る。</summary>
        private static readonly TimeSpan ToastTimeout = TimeSpan.FromSeconds(10);

        /// <summary>DEBUG パネルのボタンを押す。Content 由来の Name は他の要素と重なり得るので Button に限定する。</summary>
        public static void InvokeDebugPanelButton(MainWindowPage page, string name)
        {
            var button = Retry.WhileNull(
                () => page.Window.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Button))),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            button.Should().NotBeNull($"DEBUG パネルのボタン \"{name}\" が存在すること（Debug ビルドで起動していること）");
            button!.AsButton().Invoke();
        }

        /// <summary>
        /// 仮想タッチダイアログを開き、履歴を 1 件追加して「タッチ実行」する。
        /// カード・職員はダイアログの既定選択（先頭）をそのまま使う。未貸出なら貸出→返却、貸出中なら返却が走る。
        /// </summary>
        /// <remarks>
        /// 「タッチ実行」は <c>MainViewModel.ProcessVirtualTouchAsync</c> が <c>LendingService</c> を直接呼ぶ。
        /// 本物のカード読み取りイベントからの状態遷移（<c>HandleCardInStaffWaitingStateAsync</c> 等）は通らないが、
        /// 返却後の処理（<c>HandleReturnSuccessAsync</c>。トースト・バス停名入力・同行者数入力）は通常の返却と同じものを呼ぶ（Issue #1577）。
        /// </remarks>
        /// <param name="page">メイン画面。</param>
        /// <param name="entryStation">乗車駅。null なら既定のまま（駅名なし＝バス利用扱い）。</param>
        /// <param name="exitStation">降車駅。同上。</param>
        public static void ExecuteVirtualTouchWithOneEntry(MainWindowPage page, string? entryStation = null, string? exitStation = null)
        {
            InvokeDebugPanelButton(page, TestConstants.DebugPanelVirtualTouchButton);
            var dialog = page.WaitForDialog(TestConstants.VirtualCardDialogName);
            var dialogPage = new DialogPageBase(dialog);

            dialogPage.ClickButton(TestConstants.VirtualCardAddEntryButton);

            if (entryStation != null || exitStation != null)
            {
                // WPF の DataGridTextColumn のセルは UIA の ValuePattern（DataGridCellItemAutomationPeer）で値を設定できる。
                // 列順は XAML どおり: 0=日付, 1=乗車駅, 2=降車駅, 3=金額, 4=チャージ
                var grid = dialog.FindFirstDescendant(cf => cf.ByName(TestConstants.VirtualCardHistoryGrid))?.AsGrid();
                grid.Should().NotBeNull($"仮想タッチダイアログに履歴一覧（\"{TestConstants.VirtualCardHistoryGrid}\"）が存在すること");
                var row = Retry.WhileNull(() => grid!.Rows.FirstOrDefault(), TimeSpan.FromSeconds(5)).Result;
                row.Should().NotBeNull("「履歴追加」で行が 1 件できること");
                if (entryStation != null)
                {
                    row!.Cells[1].Patterns.Value.Pattern.SetValue(entryStation);
                }

                if (exitStation != null)
                {
                    row!.Cells[2].Patterns.Value.Pattern.SetValue(exitStation);
                }
            }

            dialogPage.ClickButton(TestConstants.VirtualCardExecuteButton);
        }

        /// <summary>
        /// 職員証認証ダイアログを仮想タッチで通す。
        /// </summary>
        /// <remarks>
        /// 認証ダイアログは実カードリーダーへのタッチを待つため、DEBUG ビルドの
        /// 「職員証仮想タッチ（デバッグ用）」ボタン（#688）でしか通せない。これが
        /// 履歴の追加・貸出記録の作成を Release パスで撮れない理由。
        /// </remarks>
        public static void PassStaffAuthentication(AppFixture fixture, Window owner)
        {
            var auth = DialogLocator.WaitForNestedDialog(fixture, owner, TestConstants.StaffAuthDialogName);
            new DialogPageBase(auth).ClickButton(TestConstants.DebugVirtualTouchButtonName);
        }

        /// <summary>交通系ICカード管理ダイアログのカード一覧から、管理番号で行を選ぶ。</summary>
        public static void SelectCardRow(DialogPageBase cardManage, string cardNumber)
        {
            var grid = cardManage.FindByNameWithRetry(TestConstants.CardList)?.AsGrid();
            grid.Should().NotBeNull($"カード管理ダイアログに \"{TestConstants.CardList}\" があること");

            var row = Retry.WhileNull(
                () => grid!.Rows.FirstOrDefault(
                    r => r.Cells.Any(c => string.Equals(c.Name, cardNumber, StringComparison.Ordinal))),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            row.Should().NotBeNull($"カード一覧に管理番号 \"{cardNumber}\" の行があること");
            row!.Select();
        }

        /// <summary>トーストが現れるのを待つ。</summary>
        public static Window WaitForToast(AppFixture fixture)
        {
            var toast = Retry.WhileNull(
                () => FindToast(fixture),
                ToastTimeout).Result;
            toast.Should().NotBeNull(
                $"トースト通知（\"{TestConstants.ToastWindowHelpText}\"）が {ToastTimeout.TotalSeconds} 秒以内に表示されること。" +
                DescribeBlockingModal(fixture));
            return toast!;
        }

        /// <summary>
        /// 指定したタイトルのトーストが現れるのを待つ（Issue #2190）。
        /// </summary>
        /// <remarks>
        /// 回帰テストは「どのトーストが出たか」を表明したいので、最初に見つかったトーストではなく
        /// タイトルが一致するものを待つ。一致しないまま時間切れになったら、そのとき出ていたトーストのタイトルを名指しする
        /// （「貸出トーストが出ない」と「エラートーストが出た」を失敗メッセージで区別するため）。
        /// </remarks>
        public static Window WaitForToastWithTitle(AppFixture fixture, string expectedTitle)
        {
            string? lastSeenTitle = null;
            var toast = Retry.WhileNull(
                () =>
                {
                    var current = FindToast(fixture);
                    if (current == null)
                    {
                        return null;
                    }

                    lastSeenTitle = ReadToastText(current, TestConstants.ToastTitleHelpText);
                    return lastSeenTitle == expectedTitle ? current : null;
                },
                ToastTimeout).Result;
            toast.Should().NotBeNull(
                $"タイトル「{expectedTitle}」のトーストが {ToastTimeout.TotalSeconds} 秒以内に表示されること" +
                (lastSeenTitle == null ? "（トーストが 1 つも出なかった）。" : $"（出ていたのは「{lastSeenTitle}」）。") +
                DescribeBlockingModal(fixture));
            return toast!;
        }

        /// <summary>
        /// トーストの文字列（タイトル・本文）を読む。要素は HelpText（<see cref="TestConstants.ToastTitleHelpText"/> 等）で識別する。
        /// </summary>
        /// <returns>見つからなければ null。</returns>
        public static string? ReadToastText(Window toast, string helpText)
        {
            try
            {
                return toast.FindFirstDescendant(cf => cf.ByHelpText(helpText))?.Name;
            }
            catch
            {
                // トーストは 3 秒で消えるので、読む途中で要素が無効になり得る
                return null;
            }
        }

        /// <summary>
        /// トーストが消えるのを待つ。<b>消えたことを表明する</b>のが要点。
        /// </summary>
        /// <remarks>
        /// トーストはすべて同じ UIA HelpText（<see cref="TestConstants.ToastWindowHelpText"/>）を持つため、
        /// 前のトーストが残ったまま次の操作へ進むと <see cref="WaitForToast"/> が<b>古いトーストを掴む</b>。
        /// 結果、貸出の撮影に「職員証を認識しました」が写った、もっともらしく見えて誤った画像ができ、
        /// 見比べる人が気付かないまま <c>-Publish</c> で 6 年参照されるマニュアルへ載り得る（コードレビューで検出）。
        /// </remarks>
        public static void WaitForToastGone(AppFixture fixture)
        {
            // Success は「時間内に条件が false になった＝トーストが消えた」ことを表す
            // （Result は最後に評価した値なので、ここでは Success を見る）。
            var gone = Retry.WhileTrue(
                () => FindToast(fixture) != null,
                ToastTimeout).Success;
            gone.Should().BeTrue(
                $"直前のトーストが {ToastTimeout.TotalSeconds} 秒以内に消えること" +
                "（消える前に次を撮ると、同じ UIA HelpText の古いトーストを掴んで誤った画像になる）。" +
                DescribeBlockingModal(fixture));
        }

        /// <summary>
        /// 待機に失敗した原因になり得るモーダルを名指しする補助文。
        /// </summary>
        /// <remarks>
        /// 仮想タッチダイアログのカード・職員は既定選択（先頭）を使うが、その中身は本体の
        /// <c>DebugDataService.TestCardList</c> / <c>TestStaffList</c> という<b>ハードコードされた一覧</b>で、
        /// 撮影モードでは <c>RegisterTestDataAsync</c> を止めるため DB に居るのは先頭の IDm だけ。
        /// 一覧の順序が変わると「カードがデータベースに登録されていません」のモーダルが出て、
        /// トースト待ちが原因不明のタイムアウトになる。せめて何が出ているかを名指しする
        /// （順序そのものは <c>Views/ScreenshotModeTests</c> の静的検査が固定する。コードレビューで検出）。
        /// </remarks>
        public static string DescribeBlockingModal(AppFixture fixture)
        {
            try
            {
                var modal = fixture.MainWindow.ModalWindows.FirstOrDefault();
                return modal == null
                    ? string.Empty
                    : $" 前面にモーダル「{modal.Name}」が出ています。";
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>返却の後処理（一覧再読込・警告確認）を挟むため、通常より長く待つ。</summary>
        public static Window WaitForDialogLong(MainWindowPage page, string dialogName)
        {
            var result = Retry.WhileNull(
                () => page.Window.ModalWindows.FirstOrDefault(w => w.Name == dialogName),
                TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds)).Result;
            result.Should().NotBeNull($"ダイアログ \"{dialogName}\" が開くこと");
            return result!;
        }

        private static Window? FindToast(AppFixture fixture)
        {
            try
            {
                return fixture.App.GetAllTopLevelWindows(fixture.Automation)
                    .FirstOrDefault(w => w.HelpText == TestConstants.ToastWindowHelpText);
            }
            catch
            {
                return null;
            }
        }
    }
}
