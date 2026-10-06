#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// Issue #2251: バス停名入力の補助（候補の並び・既定値・往復の復路の補完）を決める純関数群。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 交通系固有（バス停名・往復）の判断なので <c>Services/</c> に置く（domain-boundaries.md の決定木③）。
    /// 永続化層（<c>LedgerRepository.GetBusStopUsageStatsAsync</c>）は集計だけを返し、並べ方はここで決める。
    /// </para>
    /// <para>
    /// 同一視グループ（Issue #1905）は使わない。同一視は摘要を組み立てるときの「同じ場所とみなしてよい」という判断で、
    /// 入力欄に入れる値は職員が実際に入力した文字列そのものである（「天神日銀前～下原」と「天神中央郵便局前～下原」は別の候補）。
    /// 往復の復路も文字列の前後を入れ替えるだけで、名前を代表させない（summary-generation.md「名前を代表させない」）。
    /// </para>
    /// </remarks>
    public static class BusStopInputAssistant
    {
        /// <summary>
        /// 既定値を入れるのに要る「同じ職員×同じ金額」の利用回数の下限。
        /// </summary>
        /// <remarks>
        /// 1 回だけの実績は「たまたま一度乗った」かもしれないので入れない。2 回以上かつ 1 位が単独のときだけ入れる
        /// （<see cref="SelectDefault"/>）。
        /// </remarks>
        public const int MinUsageCountForDefault = 2;

        /// <summary>
        /// 明細 1 件分の候補を並べる。① 同じ職員×同じ金額 → ② 同じ金額 → ③ 同じ職員 → ④ 全体（<paramref name="overall"/> の順）。
        /// </summary>
        /// <param name="stats">利用実績（<c>GetBusStopUsageStatsAsync</c> の結果。職員は取得時に指定した 1 人）</param>
        /// <param name="overall">従来の全体の並び（<c>GetBusStopSuggestionsAsync</c>。件数＋直近の加点の順）</param>
        /// <param name="amount">その明細の金額。null なら ①② は空になる</param>
        /// <returns>重複を除いた候補。先に現れた段の位置を残す</returns>
        /// <remarks>
        /// ①〜③ の段の中は、利用回数の多い順 → 最終利用日の新しい順 → 名前の順。未入力プレースホルダと空文字は
        /// 取得時点で除いてある（④ も同じ）。
        /// </remarks>
        public static List<string> Rank(
            IEnumerable<BusStopUsageStatRow> stats, IEnumerable<string> overall, int? amount)
        {
            var rows = stats.ToList();
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddRange(IEnumerable<string> values)
            {
                foreach (var value in values)
                {
                    if (seen.Add(value))
                    {
                        result.Add(value);
                    }
                }
            }

            AddRange(OrderByUsage(rows.Where(r => r.IsSameStaff && IsSameAmount(r, amount))).Select(t => t.BusStops));
            AddRange(OrderByUsage(rows.Where(r => IsSameAmount(r, amount))).Select(t => t.BusStops));
            AddRange(OrderByUsage(rows.Where(r => r.IsSameStaff)).Select(t => t.BusStops));
            AddRange(overall);
            return result;
        }

        /// <summary>
        /// 明細 1 件分の既定値を決める。「同じ職員×同じ金額」の 1 位が <see cref="MinUsageCountForDefault"/> 回以上で、
        /// かつ 2 位より回数が多い（単独の 1 位）ときだけ、その値を返す。それ以外は null（入れない）。
        /// </summary>
        /// <remarks>
        /// 同数なら入れない。往復（A～B と B～A）は同じ回数になりやすく、同数のまま片方を入れると向きが逆の値が入り得る。
        /// 同じ理由で、1 位の逆向きも 2 回以上あるときは回数に差があっても入れない。
        /// 往復は 1 行目を職員が入力すれば 2 行目は復路の補完（<see cref="IsRoundTripContinuation"/>）で埋まる。
        /// </remarks>
        public static string? SelectDefault(IEnumerable<BusStopUsageStatRow> stats, int? amount)
        {
            var ranked = OrderByUsage(stats.Where(r => r.IsSameStaff && IsSameAmount(r, amount))).ToList();
            if (ranked.Count == 0 || ranked[0].UsageCount < MinUsageCountForDefault)
            {
                return null;
            }

            var top = ranked[0];
            if (ranked.Count >= 2 && ranked[1].UsageCount == top.UsageCount)
            {
                return null;
            }

            // 逆向き（B～A）も同じ職員×同じ金額で 2 回以上使っているなら、その職員は両方向に乗っている。
            // 回数の差（帰りだけ一度鉄道にした等）で向きを決めると、往復の 1 行目に逆向きが入り、
            // 2 行目の復路も一緒に逆になる。向きが決まらないので入れない（1 行目を入力すれば 2 行目は復路で埋まる）
            var reversed = ReverseRoute(top.BusStops);
            if (reversed != null
                && ranked.Any(r => r.UsageCount >= MinUsageCountForDefault
                                   && string.Equals(r.BusStops, reversed, StringComparison.Ordinal)))
            {
                return null;
            }

            return top.BusStops;
        }

        /// <summary>
        /// 後の行が前の行の「往復の復路」の候補か（同じ金額・同じ利用日のバスが続いている）。
        /// </summary>
        /// <remarks>
        /// 金額・利用日のどちらかが不明なら false（補完しない側へ倒す）。日をまたぐと別々の移動である見込みが高い
        /// （1 日目 A→B・2 日目 C→D が同じ運賃のとき「B～A」を入れない）。
        /// </remarks>
        public static bool IsRoundTripContinuation(
            int? previousAmount, DateTime? previousUseDate, int? amount, DateTime? useDate)
            => previousAmount.HasValue
               && amount.HasValue
               && previousAmount.Value == amount.Value
               && previousUseDate.HasValue
               && useDate.HasValue
               && previousUseDate.Value.Date == useDate.Value.Date;

        /// <summary>
        /// 「A～B」の乗車と降車を入れ替えた「B～A」を返す（Issue #1570 の「↑往復」ボタンと自動補完で共有する）。
        /// 空欄・「～」で 2 つに分かれない・どちらかが空のときは null。
        /// </summary>
        public static string? ReverseRoute(string? route)
        {
            if (route is null || string.IsNullOrWhiteSpace(route))
            {
                return null;
            }

            var parts = route.Split('～');
            if (parts.Length != 2)
            {
                return null;
            }

            var from = parts[0].Trim();
            var to = parts[1].Trim();
            if (from.Length == 0 || to.Length == 0)
            {
                return null;
            }

            return $"{to}～{from}";
        }

        private static bool IsSameAmount(BusStopUsageStatRow row, int? amount)
            => amount.HasValue && row.Amount.HasValue && row.Amount.Value == amount.Value;

        /// <summary>
        /// バス停名ごとに回数を合算し、回数の多い順 → 最終利用日の新しい順 → 名前の順に並べる。
        /// </summary>
        private static IEnumerable<(string BusStops, int UsageCount)> OrderByUsage(IEnumerable<BusStopUsageStatRow> rows)
            => rows
                .GroupBy(r => r.BusStops, StringComparer.Ordinal)
                .Select(g => (
                    BusStops: g.Key,
                    UsageCount: g.Sum(r => r.UsageCount),
                    LastUsedDate: g.Max(r => r.LastUsedDate)))
                .OrderByDescending(t => t.UsageCount)
                .ThenByDescending(t => t.LastUsedDate)
                .ThenBy(t => t.BusStops, StringComparer.Ordinal)
                .Select(t => (t.BusStops, t.UsageCount));
    }
}
