using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 撮影した画像に写ってはならないものを、撮影の後に確かめる（撮影テストの共通処理）。
    /// </summary>
    internal static class ScreenshotAssertions
    {
        /// <summary>
        /// メイン画面の警告一覧に更新の案内（「新しいバージョン … が公開されています」）が出ていないことを確かめる（Issue #2394）。
        /// 出ていれば、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする。
        /// </summary>
        /// <param name="mainWindow">メイン画面のウィンドウ。</param>
        /// <param name="capturedPaths">このメイン画面を写した画像（<see cref="ScreenshotHelper.Capture"/> 等が返したパス）。</param>
        /// <remarks>
        /// <para>
        /// 更新の案内は DB と同じフォルダーの <c>latest_version.txt</c> から作られる。開発機に残った 99.0.0 の
        /// ファイルが、マニュアル用の画像に実在しない版への更新案内を写していた。UI テストの間は
        /// <see cref="UiTestDatabaseGuard"/> がこのファイルを退避して取り除くので、出ていればガードの退行である。
        /// </para>
        /// <para>
        /// <b>撮った後に呼ぶこと</b>。更新のチェック（<c>MainViewModel.CheckUpdateNotificationAsync</c>）は、カード一覧の読み込みの
        /// <b>後に</b> fire-and-forget で始まり、完了を待てる合図が画面に無い。撮る前に確かめても、まだ追加されていないだけで緑になり得る。
        /// 更新の案内は一度出たらその起動の間は消えない（<c>WarningMessages</c> から取り除く経路は種別を絞っており、
        /// <c>NewVersionAvailable</c> を含まない）ので、撮った後に無ければ、撮った時点にも無かった。
        /// ただし、撮った後にもまだチェックが終わっていない可能性は残る（ファイルの読み取り 1 回なので、撮影の待ちの間に終わる想定）。
        /// ガードの退避・復元そのものは <c>UiTestDatabaseGuardTests</c>（CI で実行）が固定している。
        /// </para>
        /// <para>
        /// 失敗した画像を正規の名前のまま残さない。撮影スクリプトのマニフェストはファイルの有無で「撮れた」と数えるため、
        /// 残すと案内の写った画像が <c>-Publish</c> で公開され得る（<see cref="ScreenshotHelper"/> の未描画の扱いと同じ規約）。
        /// 探す範囲は警告一覧（<see cref="TestConstants.SystemWarningListName"/>）の子に限る。ウィンドウ全体を走査すると時間が掛かり、
        /// 3 秒で消えるトーストの撮影の前に呼ぶと撮り逃し得る（トーストの撮影の後に呼ぶこと）。
        /// </para>
        /// </remarks>
        internal static void RequireNoNewVersionNotice(Window mainWindow, params string[] capturedPaths)
        {
            var list = mainWindow.FindFirstDescendant(cf => cf.ByName(TestConstants.SystemWarningListName));

            // 警告が 1 件も無いときは一覧ごと表示されない（案内も無い）
            var notices = list == null
                ? Array.Empty<string>()
                : list.FindAllDescendants()
                    .Select(e => e.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.Contains(TestConstants.NewVersionNoticeMarker))
                    .Distinct()
                    .ToArray();

            RejectCapturesIfNoticeShown(notices, capturedPaths);
        }

        /// <summary>
        /// 更新の案内が出ていたら、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする（GUI に依存しない部分）。
        /// </summary>
        /// <param name="notices">警告一覧に出ていた更新の案内の文言。</param>
        /// <param name="capturedPaths">撮った画像のパス。</param>
        internal static void RejectCapturesIfNoticeShown(IReadOnlyCollection<string> notices, IEnumerable<string> capturedPaths)
        {
            if (notices.Count == 0)
            {
                return;
            }

            var failedNames = RenameToFailed(capturedPaths);

            throw new InvalidOperationException(
                $"撮影した画像の警告欄に更新の案内が写っています（{string.Join(" / ", notices)}）。" +
                "UI テストの間は latest_version.txt を退避して取り除くはずなので、UiTestDatabaseGuard の退避が効いていません。" +
                $"撮れた画像は {string.Join("、", failedNames)} に残しています。" +
                "C:\\ProgramData\\ICCardManager の latest_version.txt と退避（.uitest-backup / .uitest-absent）の状態を確認してから撮り直してください。");
        }

        /// <summary>
        /// 撮影用の DB の台帳で、残高チェーン（前行の残額 ＋ 受入 − 払出 ＝ 当行の残額）が切れていないことを確かめる（Issue #2425）。
        /// 切れていれば、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする。
        /// </summary>
        /// <param name="capturedPaths">この DB の内容を写した画像。</param>
        /// <remarks>
        /// <para>
        /// マニュアルの返却完了の画像が「鉄道 払出 200 残高 20,000」（直前の残高も 20,000）を写していた。
        /// 仮想タッチのダイアログの「現在残高」を DB の最新残高のまま返却し、カードが申告した残高がそのまま台帳に入ったため。
        /// 物品出納簿の「受入 − 払出 ＝ 残額」を前提に読む庶務担当者には、システムの計算の誤りに見える。
        /// シードの残高チェーンは単体テスト（<c>ScreenshotSeedDataBalanceChainTests</c>）が固定しているが、
        /// 撮影の操作が作る行はそこでは見られないので、撮った後に DB を読み返す。
        /// </para>
        /// <para>
        /// 規則は本体の <c>LedgerConsistencyChecker</c> の台帳の行の検査を写したもの（<see cref="LedgerBalanceChain"/>）。
        /// 明細（<c>ledger_detail</c>）のチェーンは見ず、同じ日の行は id の順に並べる点が本体と異なる。
        /// アプリの起動中に読み取り専用で開き、すぐ閉じる（<see cref="DatabaseProbe"/> と同じ）。
        /// 返却の記録はトーストより前にコミットされるので、トースト・返却後のダイアログを撮った後に呼べばよい。
        /// </para>
        /// </remarks>
        internal static void RequireConsistentLedgerChain(params string[] capturedPaths)
        {
            IReadOnlyList<string> breaks;
            using (var conn = new SQLiteConnection($"Data Source={AppFixture.DatabasePath};Version=3;Read Only=True"))
            {
                conn.Open();
                breaks = LedgerBalanceChain.FindBreaks(conn);
            }

            RejectCapturesIfLedgerChainBroken(breaks, capturedPaths);
        }

        /// <summary>
        /// 残高チェーンが切れていたら、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする（GUI に依存しない部分）。
        /// </summary>
        /// <param name="breaks">切れている箇所の説明（<see cref="LedgerBalanceChain.FindBreaks(SQLiteConnection)"/> の結果）。</param>
        /// <param name="capturedPaths">撮った画像のパス。</param>
        internal static void RejectCapturesIfLedgerChainBroken(IReadOnlyCollection<string> breaks, IEnumerable<string> capturedPaths)
        {
            if (breaks.Count == 0)
            {
                return;
            }

            var failedNames = RenameToFailed(capturedPaths);

            throw new InvalidOperationException(
                $"撮影用の DB の台帳で残高チェーン（前行の残額 ＋ 受入 − 払出 ＝ 残額）が切れています（{string.Join(" / ", breaks)}）。" +
                "仮想タッチの「現在残高」が、追加した利用の後の残高（台帳の最新残高 − 利用の合計）になっていない可能性があります。" +
                $"撮れた画像は {string.Join("、", failedNames)} に残しています。" +
                "ScreenshotSeedData の残高の定数と、ExecuteVirtualTouchWithOneEntry へ渡した現在残高を確認してから撮り直してください。");
        }

        /// <summary>
        /// 撮ったウィンドウの文字に、UI テスト専用のフォルダー（<c>%ProgramData%\ICCardManager.UITests</c>）のパスが出ていないことを確かめる（Issue #2435）。
        /// 出ていれば、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする。
        /// </summary>
        /// <param name="window">撮ったウィンドウ（ダイアログ）。</param>
        /// <param name="capturedPaths">このウィンドウを写した画像。</param>
        /// <remarks>
        /// <para>
        /// UI テストのアプリはバックアップを UI テスト専用のフォルダーへ作る（#2423）。システム管理画面・接続診断は使っている保存先をそのまま表示するので、
        /// 撮影はアプリの置き場所ごと撮影用のフォルダーへ移し、本体に本番の置き場所の配下として表示させる（<see cref="AppFixture.LaunchWithSeedInScreenshotDataRoot"/>）。
        /// 読み替えを通さない表示（新しく足した欄・メッセージ）が 1 つでもあれば、撮影機にしか無いパスがマニュアルの画像に載る。
        /// </para>
        /// <para>
        /// 見るのはウィンドウの全要素の Name と、値を持つ要素（入力欄）の Value。スクロールの外の要素も UIA ツリーにあるので含まれる
        /// （画像に写らない欄でも、画面に出せば次の撮り直しで写り得る）。撮った後に呼ぶ — 保存先は非同期の読み込みの後に表示される。
        /// </para>
        /// </remarks>
        internal static void RequireNoUiTestPathShown(AutomationElement window, params string[] capturedPaths) =>
            RequireNoUiTestPathShown(window, onScreenOnly: false, capturedPaths);

        /// <summary>
        /// <see cref="RequireNoUiTestPathShown(AutomationElement, string[])"/> の、見る範囲を選べる形。
        /// </summary>
        /// <param name="window">撮ったウィンドウ（ダイアログ）。</param>
        /// <param name="onScreenOnly">
        /// true なら画面内の要素（UIA の IsOffscreen が false）だけを見る。撮影用の置き場所で動かさない撮影（Release パス）用。
        /// そのアプリは UI テスト専用のバックアップ先を使うので、設定画面のバックアップ先の入力欄（スクロールの外）には UI テスト専用のパスが入っている。
        /// 写る範囲に入ったら撮影を失敗にする（Issue #2435 のレビューで検出）。
        /// </param>
        /// <param name="capturedPaths">このウィンドウを写した画像。</param>
        internal static void RequireNoUiTestPathShown(AutomationElement window, bool onScreenOnly, params string[] capturedPaths)
        {
            // 画面内かどうかは、文字が一致した要素だけについて調べる（祖先をたどるので全要素に掛けると遅い）
            var texts = new[] { window }.Concat(window.FindAllDescendants())
                .SelectMany(e => TextsOf(e)
                    .Where(t => t.IndexOf(UiTestDatabaseGuard.UiTestBackupRootFolderName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(t => (Element: e, Text: t)))
                .Where(x => !onScreenOnly || IsVisibleInWindow(x.Element, window))
                .Select(x => x.Text)
                .Distinct()
                .ToArray();

            RejectCapturesIfUiTestPathShown(texts, capturedPaths);
        }

        /// <summary>
        /// UI テスト専用のフォルダーのパスが出ていたら、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする（GUI に依存しない部分）。
        /// </summary>
        /// <param name="texts">ウィンドウに出ていた、UI テスト専用のフォルダー名を含む文字。</param>
        /// <param name="capturedPaths">撮った画像のパス。</param>
        internal static void RejectCapturesIfUiTestPathShown(IReadOnlyCollection<string> texts, IEnumerable<string> capturedPaths)
        {
            if (texts.Count == 0)
            {
                return;
            }

            var failedNames = RenameToFailed(capturedPaths);

            throw new InvalidOperationException(
                $"撮影した画面に UI テスト専用のフォルダー（{UiTestDatabaseGuard.UiTestBackupRootFolderName}）のパスが出ています（{string.Join(" / ", texts)}）。" +
                "マニュアルの読み手には本番に無いパスに見え、本文の説明（既定は C:\\ProgramData\\ICCardManager\\backup）と食い違います。" +
                $"撮れた画像は {string.Join("、", failedNames)} に残しています。" +
                "撮影を AppFixture.LaunchWithSeedInScreenshotDataRoot で起動しているか、その欄が AppDataPaths.ToDisplayPath を通しているかを確認してから撮り直してください。");
        }

        /// <summary>
        /// システム管理画面の「保存先」が本番の既定のバックアップ先（<c>C:\ProgramData\ICCardManager\backup</c>）を表示していることを確かめる（Issue #2435）。
        /// 違えば、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする。
        /// </summary>
        /// <param name="shownText">「保存先: 」で始まる表示の文字（<see cref="TestConstants.BackupFolderTextPrefix"/>）。</param>
        /// <param name="capturedPaths">撮った画像のパス。</param>
        /// <remarks>
        /// <see cref="RequireNoUiTestPathShown"/> の対。そちらは「出てはならないもの」の不在しか見ないので、読み替えが別のパス
        /// （撮影用の置き場所のまま・別の名前）を出していても緑になる。マニュアルの説明は既定のパスなので、その一致を見る。
        /// </remarks>
        internal static void RequireBackupFolderShownAsDefault(string shownText, params string[] capturedPaths)
        {
            var expected = TestConstants.BackupFolderTextPrefix + DefaultBackupDirectory;
            if (string.Equals(shownText, expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var failedNames = RenameToFailed(capturedPaths);

            throw new InvalidOperationException(
                $"システム管理画面のバックアップの保存先が「{shownText}」と表示されていて、マニュアルが説明する既定のパス（{expected}）と違います。" +
                $"撮れた画像は {string.Join("、", failedNames)} に残しています。" +
                "撮影を AppFixture.LaunchWithSeedInScreenshotDataRoot で起動しているか、Debug 構成でビルドし直したかを確認してから撮り直してください。");
        }

        /// <summary>
        /// 接続診断の「データベース到達性」の詳細が、本番の既定の DB（<c>C:\ProgramData\ICCardManager\iccard.db</c>）を示していることを確かめる（Issue #2435）。
        /// 違えば（空・項目を選ぶ案内・別のパス）、撮った画像を <c>_FAILED.png</c> へ改名してから例外にする。
        /// </summary>
        /// <param name="detailText">詳細欄の文字（<see cref="TestConstants.ConnectionDiagnosticsDetailHelpText"/> の要素の Name）。</param>
        /// <param name="capturedPaths">撮った画像のパス。</param>
        /// <remarks>
        /// <see cref="RequireNoUiTestPathShown(AutomationElement, string[])"/> の対。不在の確認だけだと、詳細欄を UIA で読めない・空のままでも緑になる。
        /// </remarks>
        internal static void RequireDatabaseShownAsDefault(string? detailText, params string[] capturedPaths)
        {
            var expected = $"データベース（{DefaultDatabasePath}）";
            if (detailText != null && detailText.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            var failedNames = RenameToFailed(capturedPaths);

            throw new InvalidOperationException(
                $"接続診断の詳細欄が「{detailText ?? "（読み取れず）"}」で、本番の既定の DB（{expected}）を示していません。" +
                $"撮れた画像は {string.Join("、", failedNames)} に残しています。" +
                "「データベース到達性」の項目を選んでから撮っているか、撮影を AppFixture.LaunchWithSeedInScreenshotDataRoot で起動しているかを確認してから撮り直してください。");
        }

        /// <summary>本番の既定の DB（<c>%ProgramData%\ICCardManager\iccard.db</c>）。</summary>
        internal static string DefaultDatabasePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ICCardManager",
            UiTestDatabaseGuard.DatabaseFileName);

        /// <summary>本番の既定のバックアップ先（<c>%ProgramData%\ICCardManager\backup</c>）。マニュアルの本文が説明するパス。</summary>
        internal static string DefaultBackupDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ICCardManager",
            UiTestDatabaseGuard.DefaultBackupFolderName);

        /// <summary>
        /// 要素がウィンドウの中で見えている（祖先のすべての矩形と重なる）か。
        /// </summary>
        /// <remarks>
        /// WPF の UIA の IsOffscreen は、ScrollViewer のスクロールの外にある要素にも false を返す（設定画面のバックアップ先の入力欄で実測）。
        /// スクロールの外の要素の矩形は、ScrollViewer（とその内側の表示領域）の矩形の外にあるので、祖先の矩形との重なりで判定する。
        /// 読み取りに失敗した要素は見えているものとして扱う（見逃すより撮影を止める側に倒す）。
        /// </remarks>
        private static bool IsVisibleInWindow(AutomationElement element, AutomationElement window)
        {
            try
            {
                var visible = element.BoundingRectangle;
                var walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
                var current = walker.GetParent(element);
                while (visible.Width > 0 && visible.Height > 0 && current != null)
                {
                    visible = System.Drawing.Rectangle.Intersect(visible, current.BoundingRectangle);
                    if (current.Equals(window))
                    {
                        break;
                    }

                    current = walker.GetParent(current);
                }

                return visible.Width > 0 && visible.Height > 0;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is FlaUI.Core.Exceptions.PropertyNotSupportedException)
            {
                return true;
            }
        }

        private static IEnumerable<string> TextsOf(AutomationElement element)
        {
            var name = SafeRead(() => element.Name);
            if (!string.IsNullOrEmpty(name))
            {
                yield return name!;
            }

            var value = SafeRead(() => element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);
            if (!string.IsNullOrEmpty(value))
            {
                yield return value!;
            }
        }

        /// <summary>要素が読み取りの途中で消えた（UIA の例外）ときは、その要素の文字を無いものとして扱う。</summary>
        private static string? SafeRead(Func<string?> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException || ex is FlaUI.Core.Exceptions.PropertyNotSupportedException)
            {
                return null;
            }
        }

        /// <summary>撮れた画像を <c>_FAILED.png</c> へ改名し、改名後の名前（改名できなければその旨）を返す。</summary>
        private static List<string> RenameToFailed(IEnumerable<string> capturedPaths)
        {
            var failedNames = new List<string>();
            foreach (var path in capturedPaths)
            {
                var failedPath = FailedPathOf(path);
                try
                {
                    File.Copy(path, failedPath, overwrite: true);
                    File.Delete(path);
                    failedNames.Add(Path.GetFileName(failedPath));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 診断用の改名に失敗しても、本来の原因は伝える
                    failedNames.Add($"{Path.GetFileName(path)}（改名できず。公開しないこと）");
                }
            }

            return failedNames;
        }

        /// <summary>撮れた画像の診断用の名前（<c>name.png</c> → <c>name_FAILED.png</c>。<see cref="ScreenshotHelper"/> と同じ形）。</summary>
        internal static string FailedPathOf(string capturedPath) =>
            Path.Combine(
                Path.GetDirectoryName(capturedPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(capturedPath) + "_FAILED.png");
    }
}
