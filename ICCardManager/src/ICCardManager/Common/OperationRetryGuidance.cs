namespace ICCardManager.Common
{
    /// <summary>
    /// 貸出・返却が台帳へ記録されずに終わったときの「やり直し方」の案内（Issue #2141）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>案内は「職員証のタッチから」でなければならない。</b>貸出・返却の処理は、成否にかかわらず
    /// <c>MainViewModel.ProcessLendAsync</c> / <c>ProcessReturnAsync</c> の <c>finally</c> で
    /// <c>ResetState()</c> を通り、操作者（職員証）を消して「職員証タッチ待ち」へ戻る。
    /// そこで交通系ICカードだけをタッチし直すと、職員証タッチ待ちの分岐
    /// （<c>HandleCardInStaffWaitingStateAsync</c>）で<b>履歴の表示</b>へ進む。
    /// 記録が無いので30秒ルールの逆処理も発動せず、職員は処理が済んだと思い込む。
    /// 旧文言「もう一度タッチしてください」はまさにその操作を促していた。
    /// </para>
    /// <para>
    /// 語彙は時間切れのトースト（「職員証のタッチからやり直してください」）に揃える。
    /// 同じ状態へ戻る 2 つの経路で案内が食い違わないようにするため
    /// （<c>.claude/rules/error-messages.md</c> #1817「近くにある既存文言の語彙に揃える」）。
    /// </para>
    /// <para>
    /// <b>この案内は「記録されていない」ときだけに使う。</b>記録済みのときに再試行を促すと、
    /// 30秒ルールの逆処理で逆の操作が新たに記録される（#1725 / #1805）。記録済みの案内は
    /// <c>MainViewModel.NotifyRecordedButIncomplete</c> が担う。
    /// </para>
    /// <para>
    /// 戻り値はトーストへ渡るため簡潔に保つ（文字サイズ「大」以上で末尾が切れる。#1273 / #1817）。
    /// </para>
    /// </remarks>
    internal static class OperationRetryGuidance
    {
        /// <summary>
        /// やり直し方の行動指示（句点付き）。
        /// </summary>
        public const string RestartFromStaffCard = "職員証のタッチからやり直してください。";

        /// <summary>
        /// 「{操作}処理に失敗しました。職員証のタッチからやり直してください。」を組み立てる。
        /// </summary>
        /// <param name="operationName">ユーザー視点の操作名（「貸出」「返却」）</param>
        public static string BuildFailureMessage(string operationName)
            => $"{operationName}処理に失敗しました。{RestartFromStaffCard}";
    }
}
