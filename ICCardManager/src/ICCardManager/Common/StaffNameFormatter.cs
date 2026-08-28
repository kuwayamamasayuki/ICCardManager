using System;

namespace ICCardManager.Common
{
    /// <summary>
    /// 台帳の氏名欄の表示名を組み立てる純関数
    /// </summary>
    /// <remarks>
    /// Issue #1906: 複数名が同一経路を 1 枚の交通系ICカードで利用した場合、
    /// 物品会計事務の手引きの例（「博多 花子 外１名」）にならい、氏名に「外N名」を付けて表示する。
    /// 同行者数（本人を含まない人数）は <c>ledger.companion_count</c> に保存し、
    /// 「外N名」は表示・帳票・CSV の各消費側がこのメソッドで導出する。
    /// <c>staff_name</c> 列そのものへ「外N名」を書き込まない（表示名の組み立ては 1 か所に置く、Issue #1858）。
    /// 数字は他の数値表記と揃えて半角にする（手引きの全角例は転記上の違い）。
    /// </remarks>
    public static class StaffNameFormatter
    {
        /// <summary>同行者数の上限（履歴編集・CSV 取込の入力検証に使う）</summary>
        public const int MaxCompanionCount = 99;

        /// <summary>
        /// 氏名と同行者数から表示名を組み立てる
        /// </summary>
        /// <param name="staffName">利用者氏名（null / 空可）</param>
        /// <param name="companionCount">同行者数（本人を含まない。0 以上）</param>
        /// <returns>同行者数が 0 なら氏名そのもの（null は空文字）、1 以上なら「氏名 外N名」。氏名が空なら「外N名」</returns>
        /// <exception cref="ArgumentOutOfRangeException">同行者数が負の場合</exception>
        public static string Format(string staffName, int companionCount)
        {
            if (companionCount < 0)
            {
                // 定義域外は黙って丸めない（Issue #1812）
                throw new ArgumentOutOfRangeException(nameof(companionCount), companionCount, "同行者数は0以上で指定してください。");
            }

            var name = string.IsNullOrWhiteSpace(staffName) ? string.Empty : staffName;
            if (companionCount == 0)
            {
                return name;
            }

            var suffix = $"外{companionCount}名";
            return name.Length == 0 ? suffix : $"{name} {suffix}";
        }
    }
}
