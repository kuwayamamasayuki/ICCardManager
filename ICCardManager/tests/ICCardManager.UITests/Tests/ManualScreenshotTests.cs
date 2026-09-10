using System;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2016: マニュアル用スクリーンショットを FlaUI で自動撮影する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通常のテストではなく撮影のための実行単位。環境変数 <c>ICCARDMANAGER_SCREENSHOT=1</c> が無ければ
    /// すべて Skip するため、CI（<c>Category!=UI</c> で除外）にも通常の UI テスト実行にも影響しない。
    /// 実行入口は <c>tools/take-screenshots-uitest.ps1</c>（Release ビルド → 本クラスだけを実行）。
    /// </para>
    /// <para>
    /// 撮影は <b>Release ビルド</b>で行う（<c>ICCARDMANAGER_UITEST_CONFIGURATION=Release</c>）。
    /// Debug ビルドはメイン画面下部に仮想タッチパネル（<c>App.IsDebugBuild</c>）が写り込むため。
    /// 職員証・交通系ICカードのタッチを要する画面（<c>staff_recognized.png</c> / <c>lend.png</c> / <c>return.png</c>）は
    /// 仮想タッチが Debug 限定のため本クラスの対象外（第 2 段階）。
    /// </para>
    /// <para>
    /// 画像は <c>docs/screenshots/auto/</c> へ出力し、マニュアルが参照する <c>docs/screenshots/</c> へは
    /// 人が見比べてから差し替える（<see cref="ScreenshotHelper"/>）。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    [Trait("Category", "Screenshot")]
    [Trait("Screenshot", "Release")]
    public class ManualScreenshotTests
    {
        public ManualScreenshotTests()
        {
            // 撮影範囲を物理座標へ固定する（Skip 判定より前でも副作用は無い）
            ScreenshotHelper.EnsureProcessDpiAware();
        }

        private const string SkipReason =
            "Issue #2016: マニュアル用スクリーンショットの撮影は ICCARDMANAGER_SCREENSHOT=1 を設定したときだけ実行する。" +
            "tools/take-screenshots-uitest.ps1 から起動してください。";

        [SkippableFact]
        public void main_メイン画面_待機状態()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            // 警告エリアの写らない投入データを使う（Issue #2011）。main_with_warnings.png と同じデータで撮ると
            // 2 枚が同じ画像になり、「警告がない場合、このエリアは表示されません」という本文と食い違う。
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedWithoutWarnings);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // カード一覧の読み込み完了（行の出現）を待ってから撮る
            page.CardListElement.Should().NotBeNull();
            WaitForCardRow(page, ScreenshotSeedData.NormalCardDisplayName);
            // 残額不足の警告が出ていないことを表明する（main_with_warnings_ の「出ている」と対）。
            // これが無いと、投入データが警告を消しきれていなくても 2 枚が同じ画像のまま緑になる
            // （実際、貸出中カードの残額を引き上げ忘れた初版はこの形だった。コードレビューで検出）
            RequireNoLowBalanceWarning(page);

            var path = ScreenshotHelper.Capture(fixture.MainWindow, "main.png");
            File.Exists(path).Should().BeTrue();
        }

        [SkippableFact]
        public void main_with_warnings_システム警告つきのメイン画面()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            WaitForCardRow(page, ScreenshotSeedData.NormalCardDisplayName);
            // 警告は残額チェック（WarningService）の非同期実行後に現れる。出現を待たずに撮ると
            // 「警告つき」と名乗る画像に警告が写らない
            WaitForWarningArea(page);

            File.Exists(ScreenshotHelper.Capture(fixture.MainWindow, "main_with_warnings.png")).Should().BeTrue();
        }

        [SkippableFact]
        public void card_list_カード一覧の状態表示()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            WaitForCardRow(page, ScreenshotSeedData.NormalCardDisplayName);

            // 画面全体では状態の色・アイコンが小さすぎて読み取れないため、カード一覧だけを撮る
            var path = ScreenshotHelper.CaptureElement(
                fixture.MainWindow, page.CardListElement!, "card_list_status_mixed.png");
            File.Exists(path).Should().BeTrue();
        }

        /// <summary>
        /// カードリーダー未接続のステータス表示。
        /// </summary>
        /// <remarks>
        /// この画像だけは投入データではなく<b>撮影機に PaSoRi が繋がっているか</b>で内容が決まる。
        /// 繋がっている環境で撮ると「接続済み」の画像が <c>error_no_reader.png</c> という名前で保存され、
        /// 見比べる人が気付かないままマニュアルへ載り得るので、切断でなければ<b>撮らずにスキップする</b>
        /// （失敗にしないのは、リーダーの有無は撮影者の環境であって不具合ではないため）。
        /// </remarks>
        [SkippableFact]
        public void error_no_reader_カードリーダー未接続のステータス表示()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedWithoutWarnings);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            // StatusBarItem は UIA ツリーに公開されないため、状態の文字列は内部の TextBlock で探す
            var statusText = Retry.WhileNull(
                () => page.FindByNameStartsWith(TestConstants.CardReaderStatusTextPrefix),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            statusText.Should().NotBeNull(
                $"ステータスバーに \"{TestConstants.CardReaderStatusTextPrefix}\" で始まる表示があること");

            Skip.If(!string.Equals(statusText!.Name, DisconnectedStatusText, StringComparison.Ordinal),
                "撮影機にカードリーダー（PaSoRi）が接続されているため、未接続の画面を撮れません。" +
                $"リーダーを外してから撮り直してください（現在の表示: {statusText.Name}）。");

            // 「再接続」ボタンは切断時だけ現れる。文字列と合わせて撮ると、マニュアル本文
            //（「赤色で表示されます。あわせて『再接続』ボタンが表示されます」）と画像が対応する
            var reconnect = page.FindByNameWithRetry(TestConstants.CardReaderReconnectButton);
            reconnect.Should().NotBeNull(
                $"切断時は \"{TestConstants.CardReaderReconnectButton}\" ボタンが現れること");

            // カードリーダーの接続は非同期に確立する。判定から撮影までの間に「接続中」へ変わると、
            // 画像の中身が名前（未接続）と食い違う。撮影の直前に読み直して食い違いを塞ぐ（コードレビューで検出）
            statusText.Name.Should().Be(DisconnectedStatusText,
                "撮影の直前まで未接続のままであること（途中で接続されたら、この名前の画像は撮らない）");

            File.Exists(ScreenshotHelper.CaptureElements(
                fixture.MainWindow, "error_no_reader.png", statusText, reconnect!)).Should().BeTrue();
        }

        [SkippableFact]
        public void history_merge_履歴一覧での統合対象の選択()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);

            // 統合の説明図なので、チェック済みの行が写っていることが要点。
            // 実際の統合（職員認証を伴う）は実行しない
            var checkBoxes = Retry.While(
                () => fixture.MainWindow.FindAllDescendants(
                    cf => cf.ByName(TestConstants.MergeTargetCheckBox).And(cf.ByControlType(ControlType.CheckBox))),
                found => found.Length < 2,
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            // Retry.While はタイムアウト時に最後の評価値（null になり得る）を返す
            (checkBoxes?.Length ?? 0).Should().BeGreaterOrEqualTo(2,
                $"履歴一覧に統合対象のチェックボックス（\"{TestConstants.MergeTargetCheckBox}\"）が 2 行以上あること");

            checkBoxes![0].AsCheckBox().IsChecked = true;
            checkBoxes[1].AsCheckBox().IsChecked = true;

            File.Exists(ScreenshotHelper.Capture(fixture.MainWindow, "history_merge.png")).Should().BeTrue();
        }

        [SkippableFact]
        public void admin_dashboard_管理者ダッシュボード()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenAdminDashboardButton, TestConstants.AdminDashboardDialogName);
            // 集計は非同期。DataGrid 自体は既定タブに常時あるので「見つかった」だけでは待ちにならない
            // （処理中オーバーレイが出たままの画像になる。コードレビューで検出）。行の出現まで待つ
            WaitForRow(dialog, TestConstants.AdminDashboardCardOperationList,
                "運用状況の集計が終わってカードの行が現れること");

            File.Exists(ScreenshotHelper.Capture(dialog.Window, "admin_dashboard.png")).Should().BeTrue();
        }

        [SkippableFact]
        public void report_preflight_帳票の事前チェック結果()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            // 未返却のまま月をまたぐカードを仕込む。警告が 0 件だと結果ダイアログが
            // 「チェック結果なし」になり、警告の読み方を説明する画像にならない
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedWithUnreturnedCard);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var report = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenReportButton, TestConstants.ReportDialogName);

            var selectAll = report.FindByNameWithRetry(TestConstants.ReportSelectAllCardsButton);
            selectAll.Should().NotBeNull($"帳票作成ダイアログに \"{TestConstants.ReportSelectAllCardsButton}\" があること");
            selectAll!.AsCheckBox().IsChecked = true;

            report.ClickButton(TestConstants.ReportPreflightButton);

            var preflight = DialogLocator.WaitForNestedDialog(
                fixture, report.Window, TestConstants.ReportPreflightDialogName);
            File.Exists(ScreenshotHelper.Capture(preflight, "report_preflight.png")).Should().BeTrue();
        }

        /// <param name="fileName">保存するファイル名。</param>
        /// <param name="buttonName">システム管理ダイアログで押すボタン。</param>
        /// <param name="dialogName">開くダイアログ。</param>
        /// <param name="readyListName">
        /// 表示が完了したことの目印になる一覧の AutomationProperties.Name。ダイアログを開いた直後は
        /// 一覧が空で、接続診断は <c>Window_Loaded</c> の非同期処理（DB・リーダー・空き容量の調査）が
        /// 終わって初めて項目が並ぶ。ダイアログの出現だけを待つと途中の状態が写る（コードレビューで検出）。
        /// </param>
        [SkippableTheory]
        [InlineData("operation_log.png", TestConstants.OpenOperationLogButton, TestConstants.OperationLogDialogName, TestConstants.OperationLogList)]
        [InlineData("connection_diagnostics.png", TestConstants.OpenConnectionDiagnosticsButton, TestConstants.ConnectionDiagnosticsDialogName, TestConstants.ConnectionDiagnosticsItemList)]
        public void system_システム管理から開く二段目のダイアログ(string fileName, string buttonName, string dialogName, string readyListName)
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var system = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            system.ClickButton(buttonName);

            var child = DialogLocator.WaitForNestedDialog(fixture, system.Window, dialogName);
            WaitForRow(new DialogPageBase(child), readyListName, $"{dialogName} の一覧に行が現れること");
            File.Exists(ScreenshotHelper.Capture(child, fileName)).Should().BeTrue();
        }

        [SkippableFact]
        public void transfer_station_groups_同一とみなす駅バス停の設定()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            // 一覧が空だと「何を登録する画面なのか」が読者に伝わらないので、グループを 2 組仕込む
            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.SeedWithTransferStationGroups);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var system = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            system.ClickButton(TestConstants.OpenTransferStationGroupButton);

            var child = DialogLocator.WaitForNestedDialog(
                fixture, system.Window, TestConstants.TransferStationGroupDialogName);
            File.Exists(ScreenshotHelper.Capture(child, "transfer_station_groups.png")).Should().BeTrue();
        }

        /// <summary>
        /// リストア用のバックアップ一覧（1 件選択した状態）。
        /// </summary>
        /// <remarks>
        /// 手動バックアップの作成は OS 標準の <c>SaveFileDialog</c> を経るため自動化の対象外
        /// （<c>restore_file_dialog.png</c> を撮り直し不要としたのと同じ判断）。一覧は起動時の
        /// 自動バックアップ（<c>StartupTaskRunner</c>）が作った世代で埋まるので、押すのは一覧の更新だけにする。
        /// </remarks>
        [SkippableFact]
        public void restore_list_リストア用バックアップ一覧()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var system = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName);
            {
                var item = WaitForRow(system, TestConstants.BackupFileList,
                    "バックアップ一覧に 1 件以上あること（起動時の自動バックアップが作られていること）");
                item.Patterns.SelectionItem.PatternOrDefault?.Select();

                File.Exists(ScreenshotHelper.Capture(system.Window, "restore_list.png")).Should().BeTrue();
            }
        }

        [SkippableFact]
        public void history_履歴照会画面()
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            try
            {
                page.OpenCardHistory(ScreenshotSeedData.NormalCardDisplayName);
            }
            catch (System.TimeoutException)
            {
                // 失敗時の画面を残す（履歴が開かない原因の切り分け用。docs/screenshots/auto/ は Git 管理外）
                ScreenshotHelper.Capture(fixture.MainWindow, "history_FAILED.png");
                throw;
            }

            var path = ScreenshotHelper.Capture(fixture.MainWindow, "history.png");
            File.Exists(path).Should().BeTrue();
        }

        [SkippableTheory]
        [InlineData("card.png", TestConstants.OpenCardManageButton, TestConstants.CardManageDialogName)]
        [InlineData("staff.png", TestConstants.OpenStaffManageButton, TestConstants.StaffManageDialogName)]
        [InlineData("report.png", TestConstants.OpenReportButton, TestConstants.ReportDialogName)]
        [InlineData("export.png", TestConstants.OpenDataExportImportButton, TestConstants.DataExportImportDialogName)]
        [InlineData("settings.png", TestConstants.OpenSettingsButton, TestConstants.SettingsDialogName)]
        [InlineData("system.png", TestConstants.OpenSystemManageButton, TestConstants.SystemManageDialogName)]
        public void dialog_ツールバーから開くダイアログ(string fileName, string buttonName, string dialogName)
        {
            Skip.If(ScreenshotHelper.ShouldSkip, SkipReason);

            using var fixture = AppFixture.LaunchWithSeed(ScreenshotSeedData.Seed);
            ScreenshotHelper.MoveToTopLeft(fixture.MainWindow);
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = page.ClickToolbarButtonAndWaitForDialog(buttonName, dialogName);
            try
            {
                var path = ScreenshotHelper.Capture(dialog.Window, fileName);
                File.Exists(path).Should().BeTrue();
            }
            finally
            {
                dialog.Close();
            }
        }

        /// <summary>カードリーダー切断時にステータスバーへ出る文字列。</summary>
        private const string DisconnectedStatusText = TestConstants.CardReaderDisconnectedText;

        /// <summary>
        /// システム警告エリアが現れるまで待つ。
        /// </summary>
        /// <remarks>
        /// 警告は起動直後ではなく <c>WarningService</c> の非同期チェックの後に現れる。待たずに撮ると
        /// 「警告つき」と名乗る画像に警告が写らない ―― もっともらしく見えて誤った画像になり、
        /// 見比べる人が気付かないまま <c>-Publish</c> でマニュアルへ載り得る。
        /// </remarks>
        private static void WaitForWarningArea(MainWindowPage page)
        {
            var header = Retry.WhileNull(
                () => page.FindByNameStartsWith(TestConstants.SystemWarningHeaderText),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            header.Should().NotBeNull(
                $"システム警告エリア（\"{TestConstants.SystemWarningHeaderText}\"）が現れること" +
                "（残額不足のカードを含む投入データで起動していること）");
        }


        /// <summary>
        /// 残額不足の警告が出ていないことを確かめる。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="WaitForWarningArea"/> の対。「出ていること」だけを表明すると、投入データが警告を
        /// 消しきれていない場合に <c>main.png</c> と <c>main_with_warnings.png</c> が同じ画像になっても緑になる。
        /// 呼ぶのはカード一覧の行が現れた後（＝ダッシュボードの読み込みと残額チェックが済んだ後）にすること。
        /// </para>
        /// <para>
        /// <b>「警告エリアが無いこと」では表明できない</b>（実測で判明）。警告エリアには
        /// 更新の案内（<c>NewVersionAvailable</c>）や journal_mode の低下（<c>DatabaseJournalModeDegraded</c>）など
        /// <b>投入データでは作れない環境由来の警告</b>も並ぶため、それを含めて禁止すると、そうした環境では
        /// <c>main.png</c> が原理的に撮れなくなる。投入データが支配する残額不足だけを見る。
        /// 環境由来の警告が写り込んでいないかは差し替え前の目視で確かめる（<c>docs/screenshots/README.md</c>）。
        /// </para>
        /// </remarks>
        private static void RequireNoLowBalanceWarning(MainWindowPage page)
        {
            var warnings = page.Window.FindAllDescendants()
                .Select(e => e.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && n.Contains(TestConstants.LowBalanceWarningMarker))
                .ToArray();

            warnings.Should().BeEmpty(
                "残額不足の警告が出ていないこと（しきい値以下の残額のカードが投入データに残っていないこと）。" +
                $"出ている警告: {string.Join(" / ", warnings)}");
        }

        /// <summary>
        /// 一覧に行が現れるまで待つ。
        /// </summary>
        /// <remarks>
        /// 一覧そのもの（DataGrid / ListView）は中身が空でも UIA ツリーに居るため、
        /// 「一覧が見つかった」ことを非同期の読み込み完了の代わりにできない。行の出現まで待つ。
        /// 行の ControlType は一覧の種類で変わる（DataGrid は <c>DataItem</c>、ListBox は <c>ListItem</c>）ので、
        /// 種類ではなく <b>選択できること</b>（SelectionItem パターンを持つこと）で行を見分ける。
        /// </remarks>
        private static AutomationElement WaitForRow(DialogPageBase page, string listName, string because)
        {
            var list = page.FindByNameWithRetry(listName);
            list.Should().NotBeNull($"\"{listName}\" が存在すること");

            var row = Retry.WhileNull(
                () => list!.FindAllDescendants()
                    .FirstOrDefault(e => e.Patterns.SelectionItem.IsSupported),
                TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds)).Result;
            row.Should().NotBeNull(because);
            return row!;
        }

        private static void WaitForCardRow(MainWindowPage page, string cardDisplayName)
        {
            var list = page.CardListElement!;
            var row = FlaUI.Core.Tools.Retry.WhileNull(
                () => list.FindFirstDescendant(cf => cf.ByName(cardDisplayName)),
                System.TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            row.Should().NotBeNull($"カード一覧に \"{cardDisplayName}\" の行が現れること（サンプルデータの投入とダッシュボードの読み込みが済んでいること）");
        }
    }
}
