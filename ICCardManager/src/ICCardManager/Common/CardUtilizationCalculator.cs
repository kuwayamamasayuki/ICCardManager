using System;
using System.Collections.Generic;

namespace ICCardManager.Common
{
    /// <summary>
    /// 管理者ダッシュボードの稼働状況指標を算出する純粋関数群（Issue #1692）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>稼働率は「利用実績のあった日数 ÷ 期間日数」で定義する。</b>
    /// 「延べ貸出日数 ÷ 期間日数」の方が本来の稼働を表すが、返却時に
    /// <c>LendingService</c> が貸出中レコードを物理削除し（履歴に「（貸出中）」を残さないため）、
    /// <c>ic_card.last_lent_at</c> も null に戻すため、<b>過去に何日借りていたかは DB のどこにも残らない</b>。
    /// 貸出／返却は <c>operation_log</c> にも記録されていない。
    /// そのため利用実績（台帳の利用日）から稼働を推定する。
    /// </para>
    /// <para>
    /// この定義は「貸し出されたが使われなかった日」を稼働に数えない。目的が
    /// 「遊んでいるカードの発見（＝カード枚数の最適化）」であるため、
    /// 使われていない事実を拾えるこの定義で目的を満たす。ただし単独では誤読を招くので、
    /// 呼び出し側は利用回数・利用総額・最終利用日からの経過日数を必ず併記すること。
    /// </para>
    /// </remarks>
    internal static class CardUtilizationCalculator
    {
        /// <summary>
        /// 集計期間の日数を返す（両端を含む）。
        /// </summary>
        internal static int CalculatePeriodDayCount(DateTime from, DateTime to)
        {
            var days = (to.Date - from.Date).Days + 1;
            return days > 0 ? days : 0;
        }

        /// <summary>
        /// 稼働率（0.0〜1.0）を返す。
        /// </summary>
        /// <param name="usedDayCount">期間内に利用実績があった日数（同日複数回は 1 日と数える）</param>
        /// <param name="periodDayCount">期間の日数</param>
        /// <remarks>
        /// 期間日数が 0 の場合は 0 を返す（0 除算を避ける）。台帳の日付が期間外に
        /// はみ出している等で 1.0 を超える場合は 1.0 に丸める。
        /// .NET Framework 4.8 には <c>Math.Clamp</c> が無いため Min/Max で表現する。
        /// </remarks>
        internal static double CalculateUtilizationRate(int usedDayCount, int periodDayCount)
        {
            if (periodDayCount <= 0 || usedDayCount <= 0)
            {
                return 0.0;
            }

            return Math.Min(1.0, Math.Max(0.0, usedDayCount / (double)periodDayCount));
        }

        /// <summary>
        /// 基準日時までに経過した満日数を返す（負にはならない）。
        /// </summary>
        /// <remarks>
        /// 貸出からの経過日数・最終利用日からの経過日数の双方で使う。
        /// 共有モードでは PC 間の時計ずれで基準日時が過去になり得るため、負値は 0 に丸める。
        /// </remarks>
        internal static int CalculateElapsedDays(DateTime since, DateTime asOf)
        {
            var elapsed = (asOf - since).TotalDays;
            if (elapsed <= 0.0 || double.IsNaN(elapsed))
            {
                return 0;
            }

            return (int)Math.Floor(elapsed);
        }

        /// <summary>
        /// 貸出から <paramref name="thresholdDays"/> 日以上返却されていないか（督促対象か）を判定する。
        /// </summary>
        internal static bool IsLongTermUnreturned(DateTime lentAt, DateTime asOf, int thresholdDays)
        {
            if (thresholdDays <= 0)
            {
                return false;
            }

            return CalculateElapsedDays(lentAt, asOf) >= thresholdDays;
        }

        /// <summary>
        /// 最終利用日からの経過日数を返す。利用実績が無い場合は null。
        /// </summary>
        internal static int? CalculateUnusedDays(DateTime? lastUsageDate, DateTime asOf)
        {
            if (!lastUsageDate.HasValue)
            {
                return null;
            }

            return CalculateElapsedDays(lastUsageDate.Value, asOf);
        }

        /// <summary>
        /// 残高推移の欠測月を直前の月の値で補完する。
        /// </summary>
        /// <remarks>
        /// 残高は「その月に取引が無ければ前月末のまま」であり、欠測を線の切断として描くと
        /// 「残高が不明になった」と誤読される。一方、先頭側の欠測（カード登録前・取引開始前）は
        /// 補完する根拠が無いため null のまま残し、折れ線を描き始めない。
        /// </remarks>
        internal static IReadOnlyList<double?> CarryForward(IReadOnlyList<double?> monthlyValues)
        {
            if (monthlyValues == null || monthlyValues.Count == 0)
            {
                return new double?[0];
            }

            var result = new double?[monthlyValues.Count];
            double? previous = null;

            for (var i = 0; i < monthlyValues.Count; i++)
            {
                if (monthlyValues[i].HasValue)
                {
                    previous = monthlyValues[i];
                }

                result[i] = previous;
            }

            return result;
        }
    }
}
