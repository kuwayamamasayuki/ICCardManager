using System.Globalization;

namespace ICCardManager.Common
{
    /// <summary>
    /// 同行者数入力の自動クローズ秒数として許される値の判定と案内文言（Issue #2009）。
    /// </summary>
    /// <remarks>
    /// 許容範囲は <b>0（自動的に閉じない）または
    /// <see cref="AppConstants.MinCompanionCountInputTimeoutSeconds"/>〜
    /// <see cref="AppConstants.MaxCompanionCountInputTimeoutSeconds"/> 秒</b>という
    /// <b>連続していない</b>範囲である。
    ///
    /// 判定と文言をここ 1 か所へ置くのは、消費側が 2 つあるため（#1763）:
    /// <list type="bullet">
    /// <item>保存時の検証（<c>ValidationService.ValidateCompanionCountInputTimeout</c>）</item>
    /// <item>入力時の即時フィードバック（<c>Common.Validation.CompanionCountTimeoutValidationRule</c>。
    /// 赤枠表示。<see cref="Validation.NumericRangeValidationRule"/> は連続範囲しか表現できず、
    /// 1〜4 秒を「妥当」として通してしまう）</item>
    /// </list>
    /// 手段が 2 通りあると、次に範囲を変える人が片方を取りこぼし、
    /// 「入力中は妥当に見えるのに保存時だけ弾かれる」食い違いが再発する。
    /// </remarks>
    internal static class CompanionCountTimeoutRange
    {
        /// <summary>
        /// 「自動的に閉じない」を表す値
        /// </summary>
        public const int NoAutoClose = 0;

        /// <summary>
        /// 値が許容範囲内かどうか
        /// </summary>
        public static bool IsValid(int seconds) => Describe(seconds) == null;

        /// <summary>
        /// 範囲外の理由を「何が／なぜ／どうすれば」の 3 要素で返す。範囲内なら <c>null</c>
        /// </summary>
        /// <remarks>
        /// 文言は数値のみを埋め込むため、書式は <see cref="CultureInfo.CurrentCulture"/> でよい
        /// （日付ではないので `db-write-conventions.md` の InvariantCulture 規約の対象外）。
        /// </remarks>
        public static string Describe(int seconds)
        {
            if (seconds == NoAutoClose)
            {
                // 0 =「自動的に閉じない」。必ず尋ねたい部署のための設定値
                return null;
            }

            if (seconds < AppConstants.MinCompanionCountInputTimeoutSeconds)
            {
                return $"同行者数入力の自動クローズ秒数が{seconds}秒で短すぎます。" +
                       $"{AppConstants.MinCompanionCountInputTimeoutSeconds}秒以上を入力するか、" +
                       "自動的に閉じない場合は 0 を入力してください。";
            }

            if (seconds > AppConstants.MaxCompanionCountInputTimeoutSeconds)
            {
                return $"同行者数入力の自動クローズ秒数が{seconds}秒で上限を超えています。" +
                       $"{AppConstants.MaxCompanionCountInputTimeoutSeconds}秒以下を入力するか、" +
                       "自動的に閉じない場合は 0 を入力してください。";
            }

            return null;
        }

        /// <summary>
        /// 数値として読めない入力に対する案内（入力時の即時フィードバック用）
        /// </summary>
        public static string DescribeNonNumeric(string text)
        {
            return $"同行者数入力の自動クローズ秒数「{text}」は数値として認識できません。" +
                   "半角数字で入力してください。";
        }
    }
}
