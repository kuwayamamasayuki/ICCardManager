using ICCardManager.Models;

namespace DebugDataViewer
{
    /// <summary>
    /// 履歴明細の取引種別を表示用の文字列へ変換する（Issue #2012）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判定の優先順位は本体の <c>RouteDisplayFormatter</c> と揃える
    /// （チャージ → ポイント還元 → バス → 鉄道）。
    /// 以前は <c>IsCharge ? "チャージ" : (IsBus ? "バス" : "鉄道")</c> で
    /// ポイント還元（<c>ledger_detail.is_point_redemption</c>。Migration_002 / #942）を
    /// 見ておらず、還元行が「鉄道」と表示されていた。
    /// </para>
    /// <para>
    /// バスより先にポイント還元を判定するのは本体と同じ理由による。
    /// #1948 で <c>is_bus</c> と <c>is_point_redemption</c> が同時に立つ複合状態を
    /// 作らないよう是正したが、6 年保存の既存データには残り得るため、
    /// 消費側は優先順位を保つ必要がある。
    /// </para>
    /// </remarks>
    public static class HistoryTransactionType
    {
        /// <summary>チャージ</summary>
        public const string Charge = "チャージ";

        /// <summary>ポイント還元</summary>
        public const string PointRedemption = "ポイント還元";

        /// <summary>バス</summary>
        public const string Bus = "バス";

        /// <summary>鉄道</summary>
        public const string Railway = "鉄道";

        /// <summary>
        /// 履歴明細から取引種別の表示文字列を求める。
        /// </summary>
        public static string Classify(LedgerDetail detail)
        {
            if (detail == null)
            {
                return "-";
            }

            if (detail.IsCharge)
            {
                return Charge;
            }

            if (detail.IsPointRedemption)
            {
                return PointRedemption;
            }

            if (detail.IsBus)
            {
                return Bus;
            }

            return Railway;
        }
    }
}
