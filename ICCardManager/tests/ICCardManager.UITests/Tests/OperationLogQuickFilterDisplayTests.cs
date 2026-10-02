using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #1522: 操作ログダイアログのクイックフィルタボタン「今日/今月/先月」が
    /// 実 WPF 描画下で視覚的に隠れていない・操作種別 ComboBox と矩形衝突していないことを
    /// FlaUI 5.0 統合テストで機械検証する（Issue #1505 / PR #1521 のリグレッション検出）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// PR #1521 で追加した静的解析テスト <c>OperationLogDialogQuickFilterLayoutTests</c>（UT-058c）は
    /// XAML の不変条件（Grid.Row 分離、Width="Auto" 等）を固定するが、
    /// 「実描画下で BoundingRectangle が正の幅を持つ／親要素境界外に押し出されていない」
    /// までは検証できない。本クラスがその責務を担う。
    /// </para>
    /// <para>
    /// <b>WSL2 既知制約と回避策</b>: Issue #1522 では「WSL2 bash から
    /// <c>"/mnt/c/Program Files/dotnet/dotnet.exe" test</c> で実行した場合、二段モーダル
    /// (MainWindow → SystemManageDialog → OperationLogDialog) の UIA tree 取得が不安定で
    /// SystemManageDialog が早期消失する症状がある」と記載された。本実装では三段フォールバック
    /// (<c>systemManage.ModalWindows</c> → <c>MainWindow.ModalWindows</c> → <c>App.GetAllTopLevelWindows</c>)
    /// で取得し、現環境では再現しないことを確認済み。万一再発した場合に備え、環境変数
    /// <c>SKIP_QUICK_FILTER_UITEST=1</c> または <c>WSL_DISTRO_NAME</c> 設定時は
    /// <see cref="TestConstants.ShouldSkipQuickFilterFlaUiTest"/> 経由で
    /// <see cref="Skip"/> 自動除外する安全網を残している。
    /// </para>
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class OperationLogQuickFilterDisplayTests
    {
        [SkippableFact]
        public void クイックフィルタ3ボタンが画面内に正しく描画される()
        {
            Skip.If(TestConstants.ShouldSkipQuickFilterFlaUiTest,
                "Issue #1522: SKIP_QUICK_FILTER_UITEST=1 または WSL_DISTRO_NAME 設定により Skip。" +
                "Windows ローカル / CI で実行してください。");

            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            using var operationLog = OpenOperationLogDialog(page, fixture);

            var (today, thisMonth, lastMonth) = operationLog.RequireQuickFilterButtons();

            // 1. UIA tree から発見できる（RequireQuickFilterButtons が保証）
            // 2. BoundingRectangle が正の幅・高さを持つ（Grid 星共有列圧迫による幅 0 の検出）
            // 3. IsOffscreen=false（親境界外に押し出されていない）
            AssertButtonVisible(today, TestConstants.OperationLogQuickFilterToday);
            AssertButtonVisible(thisMonth, TestConstants.OperationLogQuickFilterThisMonth);
            AssertButtonVisible(lastMonth, TestConstants.OperationLogQuickFilterLastMonth);
        }

        [SkippableFact]
        public void クイックフィルタボタンが操作種別ComboBoxと矩形衝突しない()
        {
            Skip.If(TestConstants.ShouldSkipQuickFilterFlaUiTest,
                "Issue #1522: SKIP_QUICK_FILTER_UITEST=1 または WSL_DISTRO_NAME 設定により Skip。" +
                "Windows ローカル / CI で実行してください。");

            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            using var operationLog = OpenOperationLogDialog(page, fixture);

            var (today, thisMonth, lastMonth) = operationLog.RequireQuickFilterButtons();
            var actionType = operationLog.FindByNameWithRetry(
                TestConstants.OperationLogActionTypeComboBox,
                TimeSpan.FromSeconds(5));
            actionType.Should().NotBeNull(
                $"操作種別 ComboBox（{TestConstants.OperationLogActionTypeComboBox}）が見つからない");

            var actionTypeRect = actionType!.BoundingRectangle;

            // PR #1521 で Row 0/Row 1 の行分離を実装した。
            // 同じ Grid.Row に戻るリグレッションが起きると ComboBox と重なるはず。
            AssertNoIntersection(today, TestConstants.OperationLogQuickFilterToday, actionTypeRect);
            AssertNoIntersection(thisMonth, TestConstants.OperationLogQuickFilterThisMonth, actionTypeRect);
            AssertNoIntersection(lastMonth, TestConstants.OperationLogQuickFilterLastMonth, actionTypeRect);
        }

        /// <summary>
        /// ダイアログを最小幅（MinWidth=800）まで縮めても、終了日の DatePicker とクイックフィルタがダイアログの内側に収まること
        /// （Issue #2194。UT-058c の手動確認「ウィンドウ最小幅でも見切れない」を置き換える。Issue #1523）。
        /// </summary>
        /// <remarks>
        /// 既定の幅（1000）では収まっていても、最小幅では星共有列が縮んで右端の終了日が押し出される（#1523 の故障）。
        /// 最小幅より細く縮めるよう Win32 で指示すると、WPF は MinWidth で止める（UIA の Transform パターンの Resize は効かなかった）。
        /// 実際に縮んだこと（幅が既定より小さいこと）を前提として表明する — 縮んでいなければこのテストは何も検査しない。
        /// </remarks>
        [SkippableFact]
        public void 最小幅まで縮めても終了日とクイックフィルタがダイアログ内に収まる()
        {
            Skip.If(TestConstants.ShouldSkipQuickFilterFlaUiTest,
                "Issue #1522: SKIP_QUICK_FILTER_UITEST=1 または WSL_DISTRO_NAME 設定により Skip。" +
                "Windows ローカル / CI で実行してください。");

            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            using var operationLog = OpenOperationLogDialog(page, fixture);
            var window = operationLog.Page.Window;

            var before = window.BoundingRectangle;
            NativeWindows.Resize(NativeWindows.HandleOf(window), 400, before.Height);
            var shrunk = Retry.WhileFalse(
                () => window.BoundingRectangle.Width < before.Width,
                TimeSpan.FromSeconds(5)).Success;
            shrunk.Should().BeTrue($"前提: ダイアログが既定の幅（{before.Width}）より縮むこと");
            var bounds = window.BoundingRectangle;

            var toDate = operationLog.FindByNameWithRetry(TestConstants.OperationLogToDate, TimeSpan.FromSeconds(5));
            toDate.Should().NotBeNull($"終了日（{TestConstants.OperationLogToDate}）が見つかること");
            AssertInside(toDate!, TestConstants.OperationLogToDate, bounds);

            var (today, thisMonth, lastMonth) = operationLog.RequireQuickFilterButtons();
            AssertButtonVisible(today, TestConstants.OperationLogQuickFilterToday);
            AssertInside(today, TestConstants.OperationLogQuickFilterToday, bounds);
            AssertInside(thisMonth, TestConstants.OperationLogQuickFilterThisMonth, bounds);
            AssertInside(lastMonth, TestConstants.OperationLogQuickFilterLastMonth, bounds);
        }

        // ── 検証ヘルパー ─────────────────────────────────

        /// <summary>要素の矩形がダイアログの矩形の内側にある（右端・下端が押し出されていない）ことを表明する。</summary>
        private static void AssertInside(AutomationElement element, string name, System.Drawing.Rectangle bounds)
        {
            var rect = element.BoundingRectangle;
            rect.Width.Should().BeGreaterThan(0, $"「{name}」の描画幅が 0（見切れている）");
            bounds.Contains(rect).Should().BeTrue(
                $"「{name}」(rect={rect}) が最小幅のダイアログ (rect={bounds}) の内側に収まること（右端が押し出されていない）");
        }

        private static void AssertButtonVisible(AutomationElement button, string buttonName)
        {
            var rect = button.BoundingRectangle;
            rect.Width.Should().BeGreaterThan(0,
                $"クイックフィルタ「{buttonName}」の描画幅が 0。Grid 星共有列の幅不足によるクリップが疑われる");
            rect.Height.Should().BeGreaterThan(0,
                $"クイックフィルタ「{buttonName}」の描画高さが 0");
            button.IsOffscreen.Should().BeFalse(
                $"クイックフィルタ「{buttonName}」が親要素境界外に押し出されている（IsOffscreen=true）");
        }

        private static void AssertNoIntersection(
            AutomationElement button,
            string buttonName,
            System.Drawing.Rectangle other)
        {
            var rect = button.BoundingRectangle;
            rect.IntersectsWith(other).Should().BeFalse(
                $"クイックフィルタ「{buttonName}」(rect={rect}) が " +
                $"操作種別 ComboBox (rect={other}) と矩形衝突している。" +
                "PR #1521 の Row 0/Row 1 行分離が崩れている可能性。");
        }

        // ── 二段モーダル取得ヘルパー ────────────────────

        /// <summary>
        /// MainWindow → SystemManageDialog → OperationLogDialog の二段モーダル経路を辿って
        /// OperationLogDialog の PageObject を返す。SystemManageDialog は呼び出し側 using で閉じる。
        /// </summary>
        /// <remarks>
        /// 取得経路のフォールバック順:
        /// (1) <c>systemManage.Window.ModalWindows</c> から探す（NavigationService が SystemManage を Owner にした場合）
        /// (2) <c>fixture.MainWindow.ModalWindows</c> から探す（MainWindow を Owner にした場合）
        /// (3) <c>fixture.App.GetAllTopLevelWindows()</c> から探す（フォールバック）
        /// </remarks>
        private static OperationLogScope OpenOperationLogDialog(MainWindowPage page, AppFixture fixture)
        {
            // 1. システム管理ダイアログを開く
            var systemManage = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenSystemManageButton,
                TestConstants.SystemManageDialogName);

            // 2. システム管理内の「操作ログを表示」ボタンクリック
            systemManage.ClickButton(TestConstants.OpenOperationLogButton);

            // 3. 二段モーダル取得（複数経路でフォールバック）
            var timeout = TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds);
            var result = Retry.WhileNull(
                () => FindOperationLogWindow(systemManage.Window, fixture),
                timeout);

            if (result.Result == null)
            {
                throw new TimeoutException(
                    $"OperationLogDialog が {timeout.TotalSeconds} 秒以内に取得できませんでした。" +
                    "二段モーダル経路の UIA tree 観測に失敗。" +
                    "WSL2 経由実行の場合は Skip 条件を確認してください（Issue #1522）。");
            }

            return new OperationLogScope(systemManage.Window, new OperationLogDialogPage(result.Result));
        }

        private static Window? FindOperationLogWindow(Window systemManageWindow, AppFixture fixture)
        {
            // (1) SystemManageDialog 配下の ModalWindows
            var fromSystemManage = systemManageWindow.ModalWindows
                .FirstOrDefault(w => w.Name == TestConstants.OperationLogDialogName);
            if (fromSystemManage != null)
            {
                return fromSystemManage;
            }

            // (2) MainWindow 配下の ModalWindows
            var fromMainWindow = fixture.MainWindow.ModalWindows
                .FirstOrDefault(w => w.Name == TestConstants.OperationLogDialogName);
            if (fromMainWindow != null)
            {
                return fromMainWindow;
            }

            // (3) アプリ全体の Top-level Windows をフォールバックで走査
            var allWindows = fixture.App.GetAllTopLevelWindows(fixture.Automation);
            return allWindows.FirstOrDefault(w => w.Name == TestConstants.OperationLogDialogName);
        }

        /// <summary>
        /// OperationLogDialog と背後の SystemManageDialog をまとめて閉じる using スコープ。
        /// </summary>
        private sealed class OperationLogScope : IDisposable
        {
            private readonly Window _systemManageWindow;
            public OperationLogDialogPage Page { get; }

            public OperationLogScope(Window systemManageWindow, OperationLogDialogPage page)
            {
                _systemManageWindow = systemManageWindow;
                Page = page;
            }

            // 暗黙変換 (Page 経由のメンバアクセスを簡略化)
            public AutomationElement? FindByNameWithRetry(string name, TimeSpan? timeout = null)
                => Page.FindByNameWithRetry(name, timeout);

            public (AutomationElement Today, AutomationElement ThisMonth, AutomationElement LastMonth)
                RequireQuickFilterButtons(TimeSpan? retryTimeout = null)
                => Page.RequireQuickFilterButtons(retryTimeout);

            public void Dispose()
            {
                try { Page.Close(); } catch { /* ignore */ }
                try { _systemManageWindow.Close(); } catch { /* ignore */ }
            }
        }
    }
}
