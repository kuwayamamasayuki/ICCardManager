using ICCardManager.Models;

namespace ICCardManager.Services;

/// <summary>
/// 摘要文字列を生成するサービス
/// </summary>
public class SummaryGenerator
{
    /// <summary>
    /// 利用履歴詳細から摘要文字列を生成
    /// </summary>
    /// <param name="details">利用履歴詳細のリスト</param>
    /// <returns>摘要文字列</returns>
    public string Generate(IEnumerable<LedgerDetail> details)
    {
        var detailList = details.ToList();

        if (detailList.Count == 0)
        {
            return string.Empty;
        }

        // チャージのみの場合
        if (detailList.All(d => d.IsCharge))
        {
            return "役務費によりチャージ";
        }

        var railwayTrips = detailList.Where(d => !d.IsCharge && !d.IsBus).ToList();
        var busTrips = detailList.Where(d => !d.IsCharge && d.IsBus).ToList();

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
    /// 鉄道利用の摘要を生成
    /// </summary>
    private string GenerateRailwaySummary(List<LedgerDetail> trips)
    {
        if (trips.Count == 0)
        {
            return string.Empty;
        }

        // 駅→駅のペアを抽出
        var routes = trips
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
    /// </summary>
    private List<(string Start, string End)> ConsolidateRoutes(List<(string Entry, string Exit)> routes)
    {
        if (routes.Count == 0)
        {
            return new List<(string Start, string End)>();
        }

        var consolidated = new List<(string Start, string End)>();
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
                consolidated.Add((currentStart, currentEnd));
                currentStart = routes[i].Entry;
                currentEnd = routes[i].Exit;
            }
        }

        consolidated.Add((currentStart, currentEnd));
        return consolidated;
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
