using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// 日別摘要の結果
    /// </summary>
    public class DailySummary
    {
        /// <summary>
        /// 利用日
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 摘要文字列
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// チャージかどうか
        /// </summary>
        public bool IsCharge { get; set; }

        /// <summary>
        /// ポイント還元かどうか
        /// </summary>
        public bool IsPointRedemption { get; set; }
    }

    /// <summary>
    /// 交通系ICカードの利用履歴から摘要文字列を生成するサービスです。
    /// </summary>
    /// <remarks>
    /// <para>
    /// このクラスは物品出納簿の「摘要」列に表示する文字列を生成します。
    /// 以下のパターンの摘要を生成できます：
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>パターン</term>
    /// <description>出力例</description>
    /// </listheader>
    /// <item>
    /// <term>単純片道</term>
    /// <description>鉄道（A駅～B駅）</description>
    /// </item>
    /// <item>
    /// <term>往復</term>
    /// <description>鉄道（A駅～B駅 往復）</description>
    /// </item>
    /// <item>
    /// <term>乗継</term>
    /// <description>鉄道（A駅～C駅）※途中駅は省略</description>
    /// </item>
    /// <item>
    /// <term>複数区間</term>
    /// <description>鉄道（A駅～B駅、C駅～D駅）</description>
    /// </item>
    /// <item>
    /// <term>バス混在</term>
    /// <description>鉄道（A駅～B駅）、バス（★）</description>
    /// </item>
    /// <item>
    /// <term>チャージ</term>
    /// <description>役務費によりチャージ</description>
    /// </item>
    /// </list>
    /// <para>
    /// バス利用時は「★」マークが表示され、後からバス停名を入力できます。
    /// </para>
    /// </remarks>
    public class SummaryGenerator
    {
        /// <summary>
        /// 利用履歴詳細から日付ごとの摘要リストを生成します。
        /// </summary>
        /// <param name="details">利用履歴詳細のリスト（ICカードから取得した新しい順）</param>
        /// <returns>日別摘要のリスト（古い順）</returns>
        /// <remarks>
        /// <para>このメソッドは以下の処理を行います：</para>
        /// <list type="bullet">
        /// <item><description>日付ごとにグループ化</description></item>
        /// <item><description>利用（鉄道・バス）とチャージを別行として分離</description></item>
        /// <item><description>古い順（時系列順）にソート</description></item>
        /// </list>
        /// <para>
        /// ICカードの履歴は新しい順で格納されているため、
        /// インデックスが大きいほど古いデータとして処理します。
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var generator = new SummaryGenerator();
        /// var summaries = generator.GenerateByDate(usageDetails);
        /// foreach (var summary in summaries)
        /// {
        ///     Console.WriteLine($"{summary.Date:yyyy/MM/dd}: {summary.Summary}");
        /// }
        /// </code>
        /// </example>
        /// <seealso cref="Generate"/>
        public List<DailySummary> GenerateByDate(IEnumerable<LedgerDetail> details)
        {
            var detailList = details.ToList();

            if (detailList.Count == 0)
            {
                return new List<DailySummary>();
            }

            var results = new List<DailySummary>();

            // 入力順にインデックスを付与（ICカード履歴は新しい順なので、インデックスが大きいほど古い）
            var indexedDetails = detailList
                .Select((d, index) => (Detail: d, Index: index))
                .Where(x => x.Detail.UseDate.HasValue)
                .ToList();

            // 日付でグループ化（古い順にソート）
            var groupedByDate = indexedDetails
                .GroupBy(x => x.Detail.UseDate!.Value.Date)
                .OrderBy(g => g.Key);

            foreach (var dateGroup in groupedByDate)
            {
                var date = dateGroup.Key;
                var dayItems = dateGroup.ToList();

                // 利用（鉄道・バス）、チャージ、ポイント還元を分離
                var usageItems = dayItems.Where(x => !x.Detail.IsCharge && !x.Detail.IsPointRedemption).ToList();
                var chargeItems = dayItems.Where(x => x.Detail.IsCharge).ToList();
                var pointRedemptionItems = dayItems.Where(x => x.Detail.IsPointRedemption).ToList();

                // 出力候補を作成（最古のインデックスと共に）
                var summariesToAdd = new List<(int OldestIndex, DailySummary Summary)>();

                // 利用がある場合は利用摘要を追加
                if (usageItems.Count > 0)
                {
                    // 古い順（インデックス降順）にソートして摘要生成
                    var usageDetails = usageItems
                        .OrderByDescending(x => x.Index)
                        .Select(x => x.Detail)
                        .ToList();
                    var usageSummary = GenerateUsageSummary(usageDetails);
                    if (!string.IsNullOrEmpty(usageSummary))
                    {
                        var oldestIndex = usageItems.Max(x => x.Index);
                        summariesToAdd.Add((oldestIndex, new DailySummary
                        {
                            Date = date,
                            Summary = usageSummary,
                            IsCharge = false,
                            IsPointRedemption = false
                        }));
                    }
                }

                // チャージがある場合はチャージ摘要を追加
                if (chargeItems.Count > 0)
                {
                    var oldestIndex = chargeItems.Max(x => x.Index);
                    summariesToAdd.Add((oldestIndex, new DailySummary
                    {
                        Date = date,
                        Summary = GetChargeSummary(),
                        IsCharge = true,
                        IsPointRedemption = false
                    }));
                }

                // ポイント還元がある場合はポイント還元摘要を追加
                if (pointRedemptionItems.Count > 0)
                {
                    var oldestIndex = pointRedemptionItems.Max(x => x.Index);
                    summariesToAdd.Add((oldestIndex, new DailySummary
                    {
                        Date = date,
                        Summary = GetPointRedemptionSummary(),
                        IsCharge = false,
                        IsPointRedemption = true
                    }));
                }

                // 古い順（インデックス降順）にソートして追加
                foreach (var item in summariesToAdd.OrderByDescending(x => x.OldestIndex))
                {
                    results.Add(item.Summary);
                }
            }

            return results;
        }

        /// <summary>
        /// 利用（鉄道・バス）の摘要を生成
        /// </summary>
        private string GenerateUsageSummary(List<LedgerDetail> usageDetails)
        {
            var railwayTrips = usageDetails.Where(d => !d.IsBus).ToList();
            var busTrips = usageDetails.Where(d => d.IsBus).ToList();

            var summaryParts = new List<string>();

            // 鉄道利用がある場合
            if (railwayTrips.Count > 0)
            {
                var railwaySummary = GenerateRailwaySummary(railwayTrips);
                if (!string.IsNullOrEmpty(railwaySummary))
                {
                    summaryParts.Add($"鉄道（{railwaySummary}）");
                }
            }

            // バス利用がある場合
            if (busTrips.Count > 0)
            {
                var busSummary = GenerateBusSummary(busTrips);
                summaryParts.Add($"バス（{busSummary}）");
            }

            return string.Join("、", summaryParts);
        }

        /// <summary>
        /// 利用履歴詳細から摘要文字列を生成（従来メソッド・互換性のため維持）
        /// </summary>
        /// <param name="details">利用履歴詳細のリスト（ICカードから取得した新しい順）</param>
        /// <returns>摘要文字列</returns>
        /// <remarks>
        /// <para>
        /// ICカード履歴は新しい順で格納されているため、内部で古い順（時系列順）に
        /// 変換してから処理します。これにより、往復検出時に出発点が正しく
        /// 摘要の先頭に表示されます。
        /// </para>
        /// <para>
        /// 例：薬院→博多→薬院の往復移動は「薬院～博多 往復」と表示されます。
        /// </para>
        /// </remarks>
        /// <seealso cref="GenerateByDate"/>
        public virtual string Generate(IEnumerable<LedgerDetail> details)
        {
            // ICカード履歴は新しい順で格納されているため、
            // 逆順にして古い順（時系列順）に変換する (Issue #336)
            var detailList = details.Reverse().ToList();

            if (detailList.Count == 0)
            {
                return string.Empty;
            }

            // チャージのみの場合
            if (detailList.All(d => d.IsCharge))
            {
                return "役務費によりチャージ";
            }

            // ポイント還元のみの場合
            if (detailList.All(d => d.IsPointRedemption))
            {
                return "ポイント還元";
            }

            var railwayTrips = detailList.Where(d => !d.IsCharge && !d.IsPointRedemption && !d.IsBus).ToList();
            var busTrips = detailList.Where(d => !d.IsCharge && !d.IsPointRedemption && d.IsBus).ToList();

            var summaryParts = new List<string>();

            // 鉄道利用がある場合
            if (railwayTrips.Count > 0)
            {
                var railwaySummary = GenerateRailwaySummary(railwayTrips);
                if (!string.IsNullOrEmpty(railwaySummary))
                {
                    summaryParts.Add($"鉄道（{railwaySummary}）");
                }
            }

            // バス利用がある場合
            if (busTrips.Count > 0)
            {
                var busSummary = GenerateBusSummary(busTrips);
                summaryParts.Add($"バス（{busSummary}）");
            }

            return string.Join("、", summaryParts);
        }

        /// <summary>
        /// 鉄道利用の摘要文字列を生成します。
        /// </summary>
        /// <param name="trips">鉄道利用の履歴詳細リスト</param>
        /// <returns>「A駅～B駅」形式の摘要文字列。往復の場合は「A駅～B駅 往復」形式</returns>
        /// <remarks>
        /// <para>アルゴリズム：</para>
        /// <list type="number">
        /// <item><description>往復パターン（A→B、B→A）を検出して「A駅～B駅 往復」として統合</description></item>
        /// <item><description>乗継パターン（降車駅=次の乗車駅）を検出して「始発駅～終着駅」として統合</description></item>
        /// <item><description>循環移動（始点=終点）の場合は統合せず個別表示</description></item>
        /// </list>
        /// </remarks>
        private string GenerateRailwaySummary(List<LedgerDetail> trips)
        {
            if (trips.Count == 0)
            {
                return string.Empty;
            }

            // 残高ベースの入力順序バリデーション：
            // ICカードの履歴は新しい順で格納されるが、摘要生成には古い順が必要。
            // UseDate昇順（古い日付を先に）+ Balance降順（同日内では残高が高い方が先＝先に利用した）
            // でソートすることで、呼び出し元の入力順序に依存せず正しく処理できる。
            var sortedTrips = trips
                .OrderBy(t => t.UseDate ?? DateTime.MaxValue)
                .ThenByDescending(t => t.Balance ?? 0)
                .ToList();

            // 駅→駅のペアを抽出
            var routes = sortedTrips
                .Where(t => !string.IsNullOrEmpty(t.EntryStation) && !string.IsNullOrEmpty(t.ExitStation))
                .Select(t => (Entry: t.EntryStation!, Exit: t.ExitStation!))
                .ToList();

            if (routes.Count == 0)
            {
                return string.Empty;
            }

            // 往復判定
            if (routes.Count >= 2)
            {
                var roundTrips = DetectRoundTrips(routes);
                if (roundTrips.Count > 0)
                {
                    var roundTripStrings = roundTrips.Select(rt => $"{rt.Start}～{rt.End} 往復");
                    var remainingRoutes = GetRemainingRoutes(routes, roundTrips);

                    var allRoutes = roundTripStrings.Concat(
                        remainingRoutes.Select(r => $"{r.Entry}～{r.Exit}"));

                    return string.Join("、", allRoutes);
                }
            }

            // 乗継判定（連続する駅の場合は始発～終着をまとめる）
            var consolidatedRoutes = ConsolidateRoutes(routes);

            return string.Join("、", consolidatedRoutes.Select(r => $"{r.Start}～{r.End}"));
        }

        /// <summary>
        /// 往復を検出
        /// </summary>
        /// <param name="routes">経路リスト（時系列順：古い順であること）</param>
        /// <returns>往復経路のリスト。Startは出発点（往路の乗車駅）、Endは折り返し点（往路の降車駅）</returns>
        /// <remarks>
        /// <para>
        /// 入力リストは必ず時系列順（古い順）であること。
        /// 往復検出時は最初にマッチした経路（routes[i]）の方向を採用するため、
        /// 順序が逆だと「帰りの経路」が先に来てしまい、摘要の駅順が逆転する。
        /// </para>
        /// <para>
        /// 例：薬院→博多→薬院の移動
        /// - 正しい順序: [(薬院,博多), (博多,薬院)] → "薬院～博多 往復"
        /// - 逆順の場合: [(博多,薬院), (薬院,博多)] → "博多～薬院 往復" (不正)
        /// </para>
        /// </remarks>
        private List<(string Start, string End)> DetectRoundTrips(List<(string Entry, string Exit)> routes)
        {
            var roundTrips = new List<(string Start, string End)>();
            var usedIndices = new HashSet<int>();

            for (int i = 0; i < routes.Count; i++)
            {
                if (usedIndices.Contains(i))
                {
                    continue;
                }

                // 逆方向の経路を探す
                for (int j = i + 1; j < routes.Count; j++)
                {
                    if (usedIndices.Contains(j))
                    {
                        continue;
                    }

                    // A→B と B→A のパターン
                    if (routes[i].Entry == routes[j].Exit && routes[i].Exit == routes[j].Entry)
                    {
                        roundTrips.Add((routes[i].Entry, routes[i].Exit));
                        usedIndices.Add(i);
                        usedIndices.Add(j);
                        break;
                    }
                }
            }

            return roundTrips;
        }

        /// <summary>
        /// 往復で使われなかった経路を取得
        /// </summary>
        private List<(string Entry, string Exit)> GetRemainingRoutes(
            List<(string Entry, string Exit)> allRoutes,
            List<(string Start, string End)> roundTrips)
        {
            var remaining = new List<(string Entry, string Exit)>();
            var roundTripSet = new HashSet<(string, string)>();

            foreach (var rt in roundTrips)
            {
                roundTripSet.Add((rt.Start, rt.End));
                roundTripSet.Add((rt.End, rt.Start));
            }

            var usedCount = new Dictionary<(string, string), int>();
            foreach (var route in allRoutes)
            {
                var key = (route.Entry, route.Exit);
                var reverseKey = (route.Exit, route.Entry);

                if (roundTripSet.Contains(key) || roundTripSet.Contains(reverseKey))
                {
                    if (!usedCount.ContainsKey(key))
                    {
                        usedCount[key] = 0;
                    }

                    usedCount[key]++;

                    // 2回目以降は残りとして追加
                    if (usedCount[key] > 1)
                    {
                        remaining.Add(route);
                    }
                }
                else
                {
                    remaining.Add(route);
                }
            }

            return remaining;
        }

        /// <summary>
        /// 連続する経路を統合（乗継判定）
        /// 注：起点と終点が同じになる循環移動の場合は統合せず、個別の経路を表示
        /// </summary>
        private List<(string Start, string End)> ConsolidateRoutes(List<(string Entry, string Exit)> routes)
        {
            if (routes.Count == 0)
            {
                return new List<(string Start, string End)>();
            }

            var result = new List<(string Start, string End)>();
            var chainStartIndex = 0;
            var currentStart = routes[0].Entry;
            var currentEnd = routes[0].Exit;

            for (int i = 1; i < routes.Count; i++)
            {
                // 降車駅と次の乗車駅が同じ場合は乗継として統合
                if (currentEnd == routes[i].Entry)
                {
                    currentEnd = routes[i].Exit;
                }
                else
                {
                    // チェーンを結果に追加
                    AddConsolidatedChain(result, routes, chainStartIndex, i - 1, currentStart, currentEnd);

                    chainStartIndex = i;
                    currentStart = routes[i].Entry;
                    currentEnd = routes[i].Exit;
                }
            }

            // 最後のチェーンを追加
            AddConsolidatedChain(result, routes, chainStartIndex, routes.Count - 1, currentStart, currentEnd);

            return result;
        }

        /// <summary>
        /// 統合されたチェーンを結果に追加
        /// 起点と終点が同じ（循環）の場合は個別の経路を追加
        /// </summary>
        private void AddConsolidatedChain(
            List<(string Start, string End)> result,
            List<(string Entry, string Exit)> routes,
            int chainStart,
            int chainEnd,
            string consolidatedStart,
            string consolidatedEnd)
        {
            // 起点と終点が同じ場合（循環移動）は、統合せずに個別の経路を追加
            if (consolidatedStart == consolidatedEnd && chainEnd > chainStart)
            {
                for (int i = chainStart; i <= chainEnd; i++)
                {
                    result.Add((routes[i].Entry, routes[i].Exit));
                }
            }
            else
            {
                result.Add((consolidatedStart, consolidatedEnd));
            }
        }

        /// <summary>
        /// バス利用の摘要を生成
        /// </summary>
        private string GenerateBusSummary(List<LedgerDetail> trips)
        {
            // バス停名が入力されている場合はそれを使用
            var busStops = trips
                .Where(t => !string.IsNullOrEmpty(t.BusStops))
                .Select(t => t.BusStops!)
                .Distinct()
                .ToList();

            if (busStops.Count > 0)
            {
                return string.Join("、", busStops);
            }

            // 未入力の場合は★マーク
            return "★";
        }

        /// <summary>
        /// 貸出中を示す摘要を生成
        /// </summary>
        public static string GetLendingSummary()
        {
            return "（貸出中）";
        }

        /// <summary>
        /// チャージの摘要を生成
        /// </summary>
        public static string GetChargeSummary()
        {
            return "役務費によりチャージ";
        }

        /// <summary>
        /// ポイント還元の摘要を生成
        /// </summary>
        public static string GetPointRedemptionSummary()
        {
            return "ポイント還元";
        }

        /// <summary>
        /// 払い戻しの摘要を生成
        /// </summary>
        public static string GetRefundSummary()
        {
            return "払い戻し";
        }

        /// <summary>
        /// 前年度繰越の摘要を生成
        /// </summary>
        public static string GetCarryoverFromPreviousYearSummary()
        {
            return "前年度より繰越";
        }

        /// <summary>
        /// 次年度繰越の摘要を生成
        /// </summary>
        public static string GetCarryoverToNextYearSummary()
        {
            return "次年度へ繰越";
        }

        /// <summary>
        /// 月計の摘要を生成
        /// </summary>
        public static string GetMonthlySummary(int month)
        {
            return $"{month}月計";
        }

        /// <summary>
        /// 累計の摘要を生成
        /// </summary>
        public static string GetCumulativeSummary()
        {
            return "累計";
        }
    }
}
