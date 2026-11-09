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
