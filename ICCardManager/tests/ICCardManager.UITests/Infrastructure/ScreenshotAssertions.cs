using System.Linq;
using FlaUI.Core.AutomationElements;
using FluentAssertions;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// 撮影した画像に写ってはならないものを、撮影の後に確かめる（撮影テストの共通処理）。
    /// </summary>
    internal static class ScreenshotAssertions
    {
        /// <summary>
        /// メイン画面の警告エリアに更新の案内（「新しいバージョン … が公開されています」）が出ていないことを確かめる（Issue #2394）。
        /// </summary>
        /// <param name="mainWindow">メイン画面のウィンドウ。</param>
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
        /// </remarks>
        internal static void RequireNoNewVersionNotice(Window mainWindow)
        {
            var notices = mainWindow.FindAllDescendants()
                .Select(e => e.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n) && n.Contains(TestConstants.NewVersionNoticeMarker))
                .ToArray();

            notices.Should().BeEmpty(
                "更新の案内が出ていないこと（UI テストの間は latest_version.txt を退避しているため）。" +
                $"出ている案内: {string.Join(" / ", notices)}");
        }
    }
}
