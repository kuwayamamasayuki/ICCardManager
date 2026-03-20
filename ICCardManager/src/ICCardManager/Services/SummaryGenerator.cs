using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        private readonly DepartmentType _departmentType;

        /// <summary>
        /// 組織固有設定（Issue #974）
        /// </summary>
        private static OrganizationOptions _options = new();

        /// <summary>
        /// TransferStationGroups のHashSet版キャッシュ
        /// </summary>
        private static List<HashSet<string>> _transferStationGroups = BuildTransferStationGroups(new OrganizationOptions());

        /// <summary>
        /// 組織固有設定を注入（起動時に1回だけ呼ぶ）
        /// </summary>
        public static void Configure(OrganizationOptions options)
        {
            _options = options ?? new OrganizationOptions();
            _transferStationGroups = BuildTransferStationGroups(_options);
        }

        /// <summary>
        /// 設定をデフォルトにリセット（テスト用）
        /// </summary>
        internal static void ResetToDefaults()
        {
            _options = new OrganizationOptions();
            _transferStationGroups = BuildTransferStationGroups(_options);
        }

        /// <summary>
        /// TransferStationGroups を List&lt;List&lt;string&gt;&gt; から List&lt;HashSet&lt;string&gt;&gt; に変換
        /// </summary>
        private static List<HashSet<string>> BuildTransferStationGroups(OrganizationOptions options)
        {
            return options.SummaryRules.TransferStationGroups
                .Select(g => new HashSet<string>(g))
                .ToList();
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="departmentType">部署種別（チャージ摘要の切替に使用）</param>
        public SummaryGenerator(DepartmentType departmentType = DepartmentType.MayorOffice)
        {
            _departmentType = departmentType;
        }

        /// <summary>
        /// DI用コンストラクタ。組織固有設定と部署種別をコンストラクタで注入します。
        /// </summary>
        /// <param name="departmentType">部署種別（チャージ摘要の切替に使用）</param>
        /// <param name="options">組織固有設定</param>
        public SummaryGenerator(DepartmentType departmentType, OrganizationOptions options)
            : this(departmentType)
        {
            // DI経由で生成された場合、静的フィールドも設定する
            // （静的メソッドが参照するため、DI経由の初期化でも静的状態を更新）
            Configure(options);
        }

        /// <summary>
        /// 金額が負でチャージでもポイント還元フラグでもないレコードを暗黙のポイント還元として判定
        /// </summary>
        /// <remarks>
        /// Issue #942: ICカードの生データでは、ポイント還元が乗車駅ありの負金額レコードとして
        /// 記録されることがある（IsPointRedemption=falseのまま）。
        /// 金額が負＝カードに入金されている＝チャージまたはポイント還元であるため、
        /// IsCharge=falseかつIsPointRedemption=falseで金額が負のレコードはポイント還元とみなす。
        /// </remarks>
        internal static bool IsImplicitPointRedemption(LedgerDetail detail)
        {
            return detail.Amount.HasValue
                && detail.Amount.Value < 0
                && !detail.IsCharge
                && !detail.IsPointRedemption;
        }

        /// <summary>
        /// 2つの駅が乗り継ぎ駅として同一かどうかを判定
        /// </summary>
        /// <param name="station1">駅名1</param>
        /// <param name="station2">駅名2</param>
        /// <returns>同一（完全一致または同一グループ内）の場合true</returns>
        private static bool AreTransferStations(string station1, string station2)
        {
            // 完全一致
            if (station1 == station2)
            {
                return true;
            }

            // 同一グループ内かチェック
            foreach (var group in _transferStationGroups)
            {
                if (group.Contains(station1) && group.Contains(station2))
                {
                    return true;
                }
            }

            return false;
        }

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
        /// var generator = new SummaryGenerator(DepartmentType.MayorOffice);
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

                // ポイント還元を先に分離（ポイント還元は個別DailySummaryだがチャージ境界にはしない）
                // Issue #942: 明示的フラグ + 暗黙のポイント還元（金額が負でチャージでもない）を両方分離
                var pointRedemptionItems = dayItems
                    .Where(x => x.Detail.IsPointRedemption || IsImplicitPointRedemption(x.Detail)).ToList();

                // 残りの項目（利用+チャージ）を時系列順（古い順＝インデックス降順）にソート
                var usageAndChargeItems = dayItems
                    .Where(x => !x.Detail.IsPointRedemption && !IsImplicitPointRedemption(x.Detail))
                    .OrderByDescending(x => x.Index)
                    .ToList();

                // 出力候補を作成（最古のインデックスと共に）
                var summariesToAdd = new List<(int OldestIndex, DailySummary Summary)>();

                // チャージ境界で利用グループを分割しながら摘要を生成
                var currentUsageGroup = new List<(LedgerDetail Detail, int Index)>();

                foreach (var item in usageAndChargeItems)
                {
                    if (item.Detail.IsCharge)
                    {
                        // 溜まった利用グループを先に出力
                        if (currentUsageGroup.Count > 0)
                        {
                            var usageDetails = currentUsageGroup.Select(x => x.Detail).ToList();
                            var usageSummary = GenerateUsageSummary(usageDetails);
                            if (!string.IsNullOrEmpty(usageSummary))
                            {
                                var oldestIndex = currentUsageGroup.Max(x => x.Index);
                                summariesToAdd.Add((oldestIndex, new DailySummary
                                {
                                    Date = date,
                                    Summary = usageSummary,
                                    IsCharge = false,
                                    IsPointRedemption = false
                                }));
                            }
                            currentUsageGroup.Clear();
                        }

                        // チャージを出力
                        summariesToAdd.Add((item.Index, new DailySummary
                        {
                            Date = date,
                            Summary = GetChargeSummary(_departmentType),
                            IsCharge = true,
                            IsPointRedemption = false
                        }));
                    }
                    else
                    {
                        // 利用: グループに追加
                        currentUsageGroup.Add(item);
                    }
                }

                // 残りの利用グループを出力
                if (currentUsageGroup.Count > 0)
                {
                    var usageDetails = currentUsageGroup.Select(x => x.Detail).ToList();
                    var usageSummary = GenerateUsageSummary(usageDetails);
                    if (!string.IsNullOrEmpty(usageSummary))
                    {
                        var oldestIndex = currentUsageGroup.Max(x => x.Index);
                        summariesToAdd.Add((oldestIndex, new DailySummary
                        {
                            Date = date,
                            Summary = usageSummary,
                            IsCharge = false,
                            IsPointRedemption = false
                        }));
                    }
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
                    summaryParts.Add($"{_options.SummaryText.RailwayLabel}（{railwaySummary}）");
                }
            }

            // バス利用がある場合
            if (busTrips.Count > 0)
            {
                var busSummary = GenerateBusSummary(busTrips);
                summaryParts.Add($"{_options.SummaryText.BusLabel}（{busSummary}）");
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
                return GetChargeSummary(_departmentType);
            }

            // ポイント還元のみの場合
            // Issue #942: 暗黙のポイント還元（金額が負でチャージでもない）も含めて判定
            if (detailList.All(d => d.IsPointRedemption || IsImplicitPointRedemption(d)))
            {
                return _options.SummaryText.PointRedemption;
            }

            var railwayTrips = detailList.Where(d => !d.IsCharge && !d.IsPointRedemption && !IsImplicitPointRedemption(d) && !d.IsBus).ToList();
            var busTrips = detailList.Where(d => !d.IsCharge && !d.IsPointRedemption && !IsImplicitPointRedemption(d) && d.IsBus).ToList();

            var summaryParts = new List<string>();

            // 鉄道利用がある場合
            if (railwayTrips.Count > 0)
            {
                var railwaySummary = GenerateRailwaySummary(railwayTrips);
                if (!string.IsNullOrEmpty(railwaySummary))
                {
                    summaryParts.Add($"{_options.SummaryText.RailwayLabel}（{railwaySummary}）");
                }
            }

            // バス利用がある場合
            if (busTrips.Count > 0)
            {
                var busSummary = GenerateBusSummary(busTrips);
                summaryParts.Add($"{_options.SummaryText.BusLabel}（{busSummary}）");
            }

            return string.Join("、", summaryParts);
        }

        /// <summary>
        /// 利用履歴をSequenceNumber/UseDate/Balanceで時系列順（古い順）にソート
        /// </summary>
        /// <remarks>
        /// Issue #548, #880: FeliCa互換でrowid（=SequenceNumber）が小さいほど新しい（後に利用した）。
        /// DESCで大きいrowid（古い）を先にして時系列順に。
        /// SequenceNumberが0（未設定）の場合はBalance降順を使用。
        /// </remarks>
        private static List<LedgerDetail> SortChronologically(List<LedgerDetail> trips)
        {
            return trips
                .OrderByDescending(t => t.SequenceNumber > 0 ? t.SequenceNumber : int.MinValue)
                .ThenBy(t => t.UseDate ?? DateTime.MaxValue)
                .ThenByDescending(t => t.Balance ?? 0)
                .ToList();
        }

        /// <summary>
        /// 経路リストに対して乗り継ぎ統合→往復検出→文字列整形の共通パイプラインを実行
        /// </summary>
        /// <param name="routes">経路の(Entry, Exit)タプルリスト（時系列順）</param>
        /// <returns>「A～B、C～D 往復」形式の摘要文字列。空リストの場合はstring.Empty</returns>
        private string BuildRouteSummary(List<(string Entry, string Exit)> routes)
        {
            if (routes.Count == 0)
            {
                return string.Empty;
            }

            // Issue #878: 乗り継ぎ統合を往復判定より先に行う
            // Issue #974: EnableTransferConsolidation で ON/OFF 可能
            var consolidatedAsPairs = routes;
            List<(string Start, string End)> consolidatedRoutes;
            if (_options.SummaryRules.EnableTransferConsolidation)
            {
                consolidatedRoutes = ConsolidateRoutes(routes);
                consolidatedAsPairs = consolidatedRoutes
                    .Select(r => (Entry: r.Start, Exit: r.End)).ToList();
            }
            else
            {
                consolidatedRoutes = routes.Select(r => (Start: r.Entry, End: r.Exit)).ToList();
            }

            // 往復判定（統合後の経路で判定）
            // Issue #974: EnableRoundTripDetection で ON/OFF 可能
            if (_options.SummaryRules.EnableRoundTripDetection && consolidatedAsPairs.Count >= 2)
            {
                var roundTrips = DetectRoundTrips(consolidatedAsPairs);
                if (roundTrips.Count > 0)
                {
                    var roundTripStrings = roundTrips.Select(rt => $"{rt.Start}～{rt.End}{_options.SummaryText.RoundTripSuffix}");
                    var remainingRoutes = GetRemainingRoutes(consolidatedAsPairs, roundTrips);

                    var allRoutes = roundTripStrings.Concat(
                        remainingRoutes.Select(r => $"{r.Entry}～{r.Exit}"));

                    return string.Join("、", allRoutes);
                }
            }

            // 往復なしの場合は統合済みの経路を表示
            return string.Join("、", consolidatedRoutes.Select(r => $"{r.Start}～{r.End}"));
        }

        /// <summary>
        /// 鉄道利用の摘要文字列を生成します。
        /// </summary>
        /// <param name="trips">鉄道利用の履歴詳細リスト</param>
        /// <returns>「A駅～B駅」形式の摘要文字列。往復の場合は「A駅～B駅 往復」形式</returns>
        /// <remarks>
        /// <para>アルゴリズム：</para>
        /// <list type="number">
        /// <item><description>GroupIdが設定されている場合、同じGroupIdの経路を1つの乗り継ぎとして統合</description></item>
        /// <item><description>GroupIdが未設定の場合、往復パターン（A→B、B→A）を検出して「A駅～B駅 往復」として統合</description></item>
        /// <item><description>GroupIdが未設定の場合、乗継パターン（降車駅=次の乗車駅）を検出して「始発駅～終着駅」として統合</description></item>
        /// <item><description>循環移動（始点=終点）の場合は統合せず個別表示</description></item>
        /// </list>
        /// </remarks>
        private string GenerateRailwaySummary(List<LedgerDetail> trips)
        {
            if (trips.Count == 0)
            {
                return string.Empty;
            }

            var sortedTrips = SortChronologically(trips);

            // Issue #484: GroupIdが設定されている場合はそのグループ化を優先
            var hasGroupId = sortedTrips.Any(t => t.GroupId.HasValue);
            if (hasGroupId)
            {
                return GenerateRailwaySummaryWithGroupId(sortedTrips);
            }

            // GroupIdが設定されていない場合は従来の自動判定
            return GenerateRailwaySummaryAutomatic(sortedTrips);
        }

        /// <summary>
        /// GroupIdに基づいて鉄道利用の摘要を生成（Issue #484）
        /// </summary>
        private string GenerateRailwaySummaryWithGroupId(List<LedgerDetail> sortedTrips)
        {
            var result = new List<string>();

            // GroupIdでグループ化（NULLは個別のグループとして扱う）
            // まず、GroupIdがある経路とない経路を分離
            var groupedTrips = sortedTrips
                .Where(t => t.GroupId.HasValue && !string.IsNullOrEmpty(t.EntryStation) && !string.IsNullOrEmpty(t.ExitStation))
                .GroupBy(t => t.GroupId!.Value)
                .OrderBy(g => g.Min(t => t.UseDate ?? DateTime.MaxValue));

            var ungroupedTrips = sortedTrips
                .Where(t => !t.GroupId.HasValue && !string.IsNullOrEmpty(t.EntryStation) && !string.IsNullOrEmpty(t.ExitStation))
                .ToList();

            // グループ化された経路を処理
            foreach (var group in groupedTrips)
            {
                var groupTrips = SortChronologically(group.ToList());
                if (groupTrips.Count == 1)
                {
                    result.Add($"{groupTrips[0].EntryStation}～{groupTrips[0].ExitStation}");
                }
                else
                {
                    // Issue #548: グループ内でも往復・乗継を自動判定
                    // 単純にfirst/lastを使うと往復（A→B, B→A）で「A～A」になるバグがあった
                    var groupSummary = GenerateRailwaySummaryAutomatic(groupTrips);
                    if (!string.IsNullOrEmpty(groupSummary))
                    {
                        result.Add(groupSummary);
                    }
                }
            }

            // グループ化されていない経路は自動判定
            if (ungroupedTrips.Count > 0)
            {
                var autoSummary = GenerateRailwaySummaryAutomatic(ungroupedTrips);
                if (!string.IsNullOrEmpty(autoSummary))
                {
                    result.Add(autoSummary);
                }
            }

            return string.Join("、", result);
        }

        /// <summary>
        /// 自動判定で鉄道利用の摘要を生成（従来のロジック）
        /// </summary>
        private string GenerateRailwaySummaryAutomatic(List<LedgerDetail> sortedTrips)
        {
            var routes = sortedTrips
                .Where(t => !string.IsNullOrEmpty(t.EntryStation) && !string.IsNullOrEmpty(t.ExitStation))
                .Select(t => (Entry: t.EntryStation!, Exit: t.ExitStation!))
                .ToList();

            return BuildRouteSummary(routes);
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
                // 降車駅と次の乗車駅が同じ（または乗り継ぎ駅として同一視できる）場合は乗継として統合
                if (AreTransferStations(currentEnd, routes[i].Entry))
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
            // 起点と終点が同じ場合（循環移動）
            // Issue #878: 乗り継ぎ駅も考慮して循環判定
            if (AreTransferStations(consolidatedStart, consolidatedEnd) && chainEnd > chainStart)
            {
                var chainLength = chainEnd - chainStart + 1;

                // Issue #878: 偶数長の循環チェーンは往復の可能性が高い
                // 中間点で分割して各半分を再統合し、往復判定に渡す
                if (chainLength % 2 == 0 && chainLength >= 4)
                {
                    int mid = chainStart + chainLength / 2 - 1;

                    var firstHalf = new List<(string Entry, string Exit)>();
                    for (int i = chainStart; i <= mid; i++)
                    {
                        firstHalf.Add(routes[i]);
                    }

                    var secondHalf = new List<(string Entry, string Exit)>();
                    for (int i = mid + 1; i <= chainEnd; i++)
                    {
                        secondHalf.Add(routes[i]);
                    }

                    result.AddRange(ConsolidateRoutes(firstHalf));
                    result.AddRange(ConsolidateRoutes(secondHalf));
                }
                else
                {
                    // 奇数長または2経路の循環は個別の経路として追加
                    for (int i = chainStart; i <= chainEnd; i++)
                    {
                        result.Add((routes[i].Entry, routes[i].Exit));
                    }
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
            var sortedTrips = SortChronologically(trips);

            // GroupIdが設定されている場合はグループ化を優先（鉄道と同様）
            var hasGroupId = sortedTrips.Any(t => t.GroupId.HasValue);
            if (hasGroupId)
            {
                return GenerateBusSummaryWithGroupId(sortedTrips);
            }

            return GenerateBusSummaryAutomatic(sortedTrips);
        }

        /// <summary>
        /// GroupIdに基づいてバス利用の摘要を生成
        /// </summary>
        private string GenerateBusSummaryWithGroupId(List<LedgerDetail> sortedTrips)
        {
            var result = new List<string>();

            // GroupIdでグループ化（NULLは個別のグループとして扱う）
            var groupedTrips = sortedTrips
                .Where(t => t.GroupId.HasValue)
                .GroupBy(t => t.GroupId!.Value)
                .OrderBy(g => g.Min(t => t.UseDate ?? DateTime.MaxValue));

            var ungroupedTrips = sortedTrips
                .Where(t => !t.GroupId.HasValue)
                .ToList();

            // グループ化された経路を処理
            foreach (var group in groupedTrips)
            {
                var groupTrips = SortChronologically(group.ToList());
                var groupSummary = GenerateBusSummaryAutomatic(groupTrips);
                if (!string.IsNullOrEmpty(groupSummary))
                {
                    result.Add(groupSummary);
                }
            }

            // グループ化されていない経路は自動判定
            if (ungroupedTrips.Count > 0)
            {
                var autoSummary = GenerateBusSummaryAutomatic(ungroupedTrips);
                if (!string.IsNullOrEmpty(autoSummary))
                {
                    result.Add(autoSummary);
                }
            }

            return string.Join("、", result);
        }

        /// <summary>
        /// 自動判定でバス利用の摘要を生成
        /// </summary>
        private string GenerateBusSummaryAutomatic(List<LedgerDetail> sortedTrips)
        {
            // バス停名が入力されているものを時系列順（古い→新しい）で取得
            var allBusStops = sortedTrips
                .Where(t => !string.IsNullOrEmpty(t.BusStops))
                .Select(t => t.BusStops!)
                .ToList();

            if (allBusStops.Count == 0)
            {
                // 未入力の場合はプレースホルダ
                return _options.SummaryText.BusPlaceholder;
            }

            // Issue #985: 「A～B」形式のバス停名から乗り継ぎ統合・往復検出を行う
            var parsedRoutes = allBusStops
                .Select(ParseBusRoute)
                .Where(r => r.HasValue)
                .Select(r => r!.Value)
                .ToList();

            // 解析できなかったバス停名（「A～B」形式でないもの）
            var unparsed = allBusStops
                .Where(bs => !ParseBusRoute(bs).HasValue)
                .Distinct()
                .ToList();

            if (parsedRoutes.Count >= 2)
            {
                // 共通パイプラインで統合・往復検出・整形
                var routeSummary = BuildRouteSummary(parsedRoutes);

                if (unparsed.Count > 0)
                {
                    return string.Join("、", new[] { routeSummary }.Concat(unparsed));
                }
                return routeSummary;
            }

            // 経路が1件以下の場合: 重複除去して連結
            return string.Join("、", allBusStops.Distinct());
        }

        /// <summary>
        /// バス停名を「A～B」形式として解析（Issue #985）
        /// </summary>
        /// <returns>解析成功時は(Entry, Exit)のタプル、失敗時はnull</returns>
        private static (string Entry, string Exit)? ParseBusRoute(string busStops)
        {
            var parts = busStops.Split('～');
            if (parts.Length == 2 &&
                !string.IsNullOrWhiteSpace(parts[0]) &&
                !string.IsNullOrWhiteSpace(parts[1]))
            {
                return (parts[0], parts[1]);
            }
            return null;
        }

        /// <summary>
        /// 貸出中を示す摘要を生成
        /// </summary>
        public static string GetLendingSummary()
        {
            return _options.SummaryText.LendingSummary;
        }

        /// <summary>
        /// チャージの摘要を生成（市長事務部局用デフォルト）
        /// </summary>
        public static string GetChargeSummary()
        {
            return GetChargeSummary(DepartmentType.MayorOffice);
        }

        /// <summary>
        /// チャージの摘要を部署種別に応じて生成
        /// </summary>
        /// <param name="departmentType">部署種別</param>
        /// <returns>市長事務部局:「役務費によりチャージ」、企業会計部局:「旅費によりチャージ」</returns>
        public static string GetChargeSummary(DepartmentType departmentType)
        {
            return departmentType == DepartmentType.EnterpriseAccount
                ? _options.SummaryText.ChargeSummaryEnterprise
                : _options.SummaryText.ChargeSummaryMayorOffice;
        }

        /// <summary>
        /// ポイント還元の摘要を生成
        /// </summary>
        public static string GetPointRedemptionSummary()
        {
            return _options.SummaryText.PointRedemption;
        }

        /// <summary>
        /// 払い戻しの摘要を生成
        /// </summary>
        public static string GetRefundSummary()
        {
            return _options.SummaryText.RefundSummary;
        }

        /// <summary>
        /// 残高不足時の備考テキストを生成
        /// </summary>
        /// <remarks>
        /// Issue #380対応: 残高不足で不足分を現金でチャージした場合の備考テキスト。
        /// 例: 運賃210円に対し残高200円の場合、不足額10円を現金で支払い。
        /// </remarks>
        /// <param name="totalFare">支払総額（運賃）</param>
        /// <param name="shortfall">不足額（現金支払額）</param>
        /// <returns>備考テキスト</returns>
        public static string GetInsufficientBalanceNote(int totalFare, int shortfall)
        {
            return string.Format(_options.SummaryText.InsufficientBalanceNoteFormat, totalFare, shortfall);
        }

        /// <summary>
        /// 前年度繰越の摘要を生成
        /// </summary>
        public static string GetCarryoverFromPreviousYearSummary()
        {
            return _options.SummaryText.CarryoverFromPreviousYear;
        }

        /// <summary>
        /// 前月繰越の摘要を生成
        /// </summary>
        /// <param name="previousMonth">前月の月番号（1-12）</param>
        public static string GetCarryoverFromPreviousMonthSummary(int previousMonth)
        {
            return string.Format(_options.SummaryText.CarryoverFromMonthFormat, previousMonth);
        }

        /// <summary>
        /// 次年度繰越の摘要を生成
        /// </summary>
        public static string GetCarryoverToNextYearSummary()
        {
            return _options.SummaryText.CarryoverToNextYear;
        }

        /// <summary>
        /// 年度途中導入時の繰越摘要を生成（Issue #510）
        /// </summary>
        /// <param name="carryoverMonth">繰越元の月（1-12）</param>
        /// <returns>「○月から繰越」形式の摘要文字列</returns>
        /// <remarks>
        /// 年度途中から本アプリを導入する場合に使用。
        /// 例: 5月まで紙の出納簿を使用し、6月からアプリを使う場合は「5月から繰越」を生成。
        /// </remarks>
        public static string GetMidYearCarryoverSummary(int carryoverMonth)
        {
            return string.Format(_options.SummaryText.MidYearCarryoverFormat, carryoverMonth);
        }

        /// <summary>
        /// 年度途中導入の繰越レコード日付を計算（Issue #599）
        /// </summary>
        /// <param name="carryoverMonth">繰越元の月（1-12）</param>
        /// <param name="registrationDate">登録日</param>
        /// <returns>繰越月の翌月1日</returns>
        /// <remarks>
        /// 繰越レコードの日付は「繰越月の翌月1日」とする。
        /// 例: 2月9日に「1月から繰越」→ 2月1日、1月15日に「12月から繰越」→ 1月1日。
        /// 翌月が登録月より後の場合は前年のデータとみなす。
        /// 例: 2月15日に「11月から繰越」→ 前年12月1日。
        /// </remarks>
        public static DateTime GetMidYearCarryoverDate(int carryoverMonth, DateTime registrationDate)
        {
            var nextMonth = (carryoverMonth % 12) + 1;
            var recordYear = registrationDate.Year;

            // 翌月が登録月より後の場合、前年のデータ
            if (nextMonth > registrationDate.Month)
            {
                recordYear--;
            }

            return new DateTime(recordYear, nextMonth, 1);
        }

        /// <summary>
        /// 摘要が年度途中導入の繰越かどうかを判定（Issue #510）
        /// </summary>
        /// <param name="summary">摘要文字列</param>
        /// <returns>「○月から繰越」形式の場合true</returns>
        public static bool IsMidYearCarryoverSummary(string? summary)
        {
            if (string.IsNullOrEmpty(summary))
            {
                return false;
            }

            try
            {
                return Regex.IsMatch(summary, _options.SummaryText.MidYearCarryoverPattern);
            }
            catch (ArgumentException)
            {
                // 不正な正規表現の場合はデフォルトパターンにフォールバック
                return Regex.IsMatch(summary, @"^(1[0-2]|[1-9])月から繰越$");
            }
        }

        /// <summary>
        /// 月計の摘要を生成
        /// </summary>
        public static string GetMonthlySummary(int month)
        {
            return string.Format(_options.SummaryText.MonthlySummaryFormat, month);
        }

        /// <summary>
        /// 累計の摘要を生成
        /// </summary>
        public static string GetCumulativeSummary()
        {
            return _options.SummaryText.CumulativeSummary;
        }
    }
}
