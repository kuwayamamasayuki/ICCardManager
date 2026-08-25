using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Common.Charting;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 管理者ダッシュボード（Issue #1692）のデータを構築するサービス
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>警告エリアには載せない設計判断。</b> 長期未返却・帳票未出力はメイン画面右の
    /// 警告エリアには出さず、本画面に留める。理由は 3 点:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// 長期未返却は督促が完了するまで数日〜数週間出続ける。常設の警告エリアに居座らせると
    /// 残額不足・DB 接続断など即応が必要な警告を埋もれさせる（警告疲れ）。
    /// </description></item>
    /// <item><description>
    /// 帳票未出力は月次締めの作業であり、月中に未出力なのは正常な状態。常時警告すると誤報になる。
    /// 帳票作成時のチェックは Issue #1691 のプリフライトが担っており責務も重複する。
    /// </description></item>
    /// <item><description>
    /// 警告エリアはメイン画面の縦幅を消費する。窓口の貸出・返却操作を妨げない設計原則
    /// （画面設計書 §3.1）に反する。
    /// </description></item>
    /// </list>
    /// <para>
    /// 将来 <c>WarningService</c> から再利用できるよう、本サービスは UI に依存せず
    /// しきい値を引数で受け取る形にしてある。
    /// </para>
    /// </remarks>
    public class AdminDashboardService : IAdminDashboardService
    {
        private readonly ICardRepository _cardRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IReportExportStatusService _reportExportStatusService;

        /// <summary>職員名を特定できない台帳行の表示名</summary>
        internal const string UnknownStaffName = "（職員名なし）";

        public AdminDashboardService(
            ICardRepository cardRepository,
            ILedgerRepository ledgerRepository,
            IStaffRepository staffRepository,
            ISettingsRepository settingsRepository,
            IReportExportStatusService reportExportStatusService)
        {
            _cardRepository = cardRepository;
            _ledgerRepository = ledgerRepository;
            _staffRepository = staffRepository;
            _settingsRepository = settingsRepository;
            _reportExportStatusService = reportExportStatusService;
        }

        /// <inheritdoc/>
        public async Task<AdminDashboardOperationStatus> GetOperationStatusAsync(DateTime asOf, int longTermUnreturnedDays)
        {
            // Issue #1452: 同一の SQLiteConnection 上で SQLiteCommand が並列実行されると
            // SQLITE_MISUSE 不定動作の原因となるため、リポジトリ呼び出しは直列化する。
            var settings = await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false);
            var cards = FilterActiveCards(await _cardRepository.GetAllAsync().ConfigureAwait(false));
            var lentRecords = await _ledgerRepository.GetAllLentRecordsAsync().ConfigureAwait(false);
            var balances = await _ledgerRepository.GetAllLatestBalancesAsync().ConfigureAwait(false);
            var lastUsageDates = await _ledgerRepository.GetAllLastUsageDatesAsync().ConfigureAwait(false);
            var staffNames = await BuildStaffNameMapAsync().ConfigureAwait(false);

            // Issue #1691: 帳票の出力状況は出力先フォルダのファイル走査（同期処理）で判定するため、
            // UI スレッドを塞がないよう Task.Run にオフロードする。
            var targets = cards
                .Select(c => new ReportExportTarget { CardIdm = c.CardIdm, CardType = c.CardType, CardNumber = c.CardNumber })
                .ToList();
            var reportStatuses = await Task.Run(
                () => _reportExportStatusService.GetStatuses(targets, settings.ReportOutputFolder, asOf.Year, asOf.Month))
                .ConfigureAwait(false);
            var reportStateByCard = reportStatuses.ToDictionary(s => s.CardIdm, s => s.State);

            var latestLentByCard = BuildLatestLentRecordMap(lentRecords);

            var items = new List<AdminDashboardCardStatus>(cards.Count);
            foreach (var card in cards)
            {
                var balance = balances.TryGetValue(card.CardIdm, out var balanceInfo) ? balanceInfo.Balance : 0;

                // Issue #1747: 最終利用日は残高とは別のクエリから取る。GetAllLatestBalancesAsync の
                // LastUsageDate は貸出中・新規購入・繰越を含む「最新レコード日」で、登録しただけの
                // カードが「使われている」ように見え、稼働状況タブ（利用実績のみ集計）と矛盾する。
                // 利用実績が無いカードは辞書に載らないため null（画面では空欄）になる。
                var lastUsageDate = lastUsageDates.TryGetValue(card.CardIdm, out var usageDate)
                    ? usageDate
                    : (DateTime?)null;

                // 貸出中でないカードは、貸出中レコードが残っていても返却済みとして扱う。
                // ic_card.is_lent = 0 なのに貸出中レコードが残る不整合は、共有モードで他 PC の
                // 返却が反映される前などに一時的に生じる（起動時に LendingService が修復する）。
                // ここで督促対象に数えると「貸出中 0 枚なのに長期未返却 1 枚」という自己矛盾になり、
                // しかも貸出職員名を解決できないため督促に使えない行が出る。
                var lentRecord = card.IsLent && latestLentByCard.TryGetValue(card.CardIdm, out var record)
                    ? record
                    : null;
                var lentAt = lentRecord?.LentAt;
                var elapsedLentDays = lentAt.HasValue
                    ? CardUtilizationCalculator.CalculateElapsedDays(lentAt.Value, asOf)
                    : (int?)null;

                items.Add(new AdminDashboardCardStatus
                {
                    CardIdm = card.CardIdm,
                    CardType = card.CardType,
                    CardNumber = card.CardNumber,
                    DisplayName = card.DisplayName,
                    IsLent = card.IsLent,
                    LentStaffName = ResolveLentStaffName(card, lentRecord, staffNames),
                    LentAt = lentAt,
                    ElapsedLentDays = elapsedLentDays,
                    IsLongTermUnreturned = lentAt.HasValue
                        && CardUtilizationCalculator.IsLongTermUnreturned(lentAt.Value, asOf, longTermUnreturnedDays),
                    CurrentBalance = balance,
                    // 既存の残額不足警告（WarningService）と同じく「以下」で判定する
                    IsBalanceWarning = balance <= settings.WarningBalance,
                    ReportState = reportStateByCard.TryGetValue(card.CardIdm, out var state)
                        ? state
                        : ReportExportState.Unknown,
                    LastUsageDate = lastUsageDate
                });
            }

            return new AdminDashboardOperationStatus
            {
                AsOf = asOf,
                LongTermUnreturnedThresholdDays = longTermUnreturnedDays,
                WarningBalance = settings.WarningBalance,
                ReportYear = asOf.Year,
                ReportMonth = asOf.Month,
                TotalCardCount = items.Count,
                LentCardCount = items.Count(i => i.IsLent),
                LongTermUnreturnedCount = items.Count(i => i.IsLongTermUnreturned),
                LowBalanceCount = items.Count(i => i.IsBalanceWarning),
                ReportNotExportedCount = items.Count(i => i.ReportState == ReportExportState.NotExported),
                ReportStatusUnknownCount = items.Count(i => i.ReportState == ReportExportState.Unknown),
                Cards = items
            };
        }

        /// <inheritdoc/>
        public async Task<AdminDashboardAnalytics> GetAnalyticsAsync(DateTime fromDate, DateTime toDate, DateTime asOf)
        {
            var cards = FilterActiveCards(await _cardRepository.GetAllAsync().ConfigureAwait(false));
            var usageStats = await _ledgerRepository.GetUsageStatsByCardAsync(fromDate, toDate).ConfigureAwait(false);
            var monthlyUsage = await _ledgerRepository.GetMonthlyUsageByLenderAsync(fromDate, toDate).ConfigureAwait(false);
            var monthEndBalances = await _ledgerRepository.GetMonthEndBalancesByCardAsync(fromDate, toDate).ConfigureAwait(false);
            var balancesBeforePeriod = await _ledgerRepository.GetBalancesBeforeAsync(fromDate).ConfigureAwait(false);
            var staffNames = await BuildStaffNameMapAsync().ConfigureAwait(false);

            var months = EnumerateMonthKeys(fromDate, toDate);
            var periodDayCount = CardUtilizationCalculator.CalculatePeriodDayCount(fromDate, toDate);

            return new AdminDashboardAnalytics
            {
                FromDate = fromDate,
                ToDate = toDate,
                PeriodDayCount = periodDayCount,
                MonthLabels = months.Select(FormatMonthLabel).ToList(),
                Utilizations = BuildUtilizations(cards, usageStats, periodDayCount, asOf),
                UsageSeries = BuildUsageSeries(monthlyUsage, months, staffNames),
                BalanceSeries = BuildBalanceSeries(cards, monthEndBalances, balancesBeforePeriod, months)
            };
        }

        #region 集計の組み立て

        /// <summary>
        /// 集計対象のカードを絞り込む。
        /// </summary>
        /// <remarks>
        /// 削除済み・払戻済みのカードは既に運用から外れており、稼働率の分母に含めると
        /// 「遊んでいるカードが増え続ける」ように見えてしまうため除外する。
        /// </remarks>
        private static List<IcCard> FilterActiveCards(IEnumerable<IcCard> cards)
            => (cards ?? Enumerable.Empty<IcCard>())
                .Where(c => !c.IsDeleted && !c.IsRefunded)
                .OrderByCardDefault(c => c.CardType, c => c.CardNumber)
                .ToList();

        private async Task<Dictionary<string, string>> BuildStaffNameMapAsync()
        {
            var staff = await _staffRepository.GetAllAsync().ConfigureAwait(false);
            var map = new Dictionary<string, string>();
            foreach (var s in staff ?? Enumerable.Empty<Staff>())
            {
                map[s.StaffIdm] = s.Name;
            }

            return map;
        }

        /// <summary>
        /// カードごとの「最新の貸出中レコード」を求める。
        /// </summary>
        /// <remarks>
        /// 共有モードでは同一カードに複数の貸出中レコードが残ることがある（Issue #1196）。
        /// 経過日数を過大に見せないよう、最も新しい貸出日時のレコードを採用する。
        /// </remarks>
        private static Dictionary<string, Ledger> BuildLatestLentRecordMap(IEnumerable<Ledger> lentRecords)
        {
            var map = new Dictionary<string, Ledger>();
            foreach (var record in lentRecords ?? Enumerable.Empty<Ledger>())
            {
                if (!map.TryGetValue(record.CardIdm, out var existing)
                    || (record.LentAt ?? DateTime.MinValue) > (existing.LentAt ?? DateTime.MinValue))
                {
                    map[record.CardIdm] = record;
                }
            }

            return map;
        }

        /// <summary>
        /// 貸出職員名を解決する（職員マスタ優先、無ければ台帳に記録された氏名）。
        /// </summary>
        private static string ResolveLentStaffName(IcCard card, Ledger lentRecord, Dictionary<string, string> staffNames)
        {
            if (!card.IsLent)
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(card.LastLentStaff) && staffNames.TryGetValue(card.LastLentStaff, out var byCard))
            {
                return byCard;
            }

            if (lentRecord != null)
            {
                if (!string.IsNullOrEmpty(lentRecord.LenderIdm) && staffNames.TryGetValue(lentRecord.LenderIdm, out var byLedger))
                {
                    return byLedger;
                }

                if (!string.IsNullOrEmpty(lentRecord.StaffName))
                {
                    return lentRecord.StaffName;
                }
            }

            return string.Empty;
        }

        private static IReadOnlyList<CardUtilizationItem> BuildUtilizations(
            IReadOnlyList<IcCard> cards, IReadOnlyList<CardUsageStatsRow> usageStats, int periodDayCount, DateTime asOf)
        {
            var statsByCard = new Dictionary<string, CardUsageStatsRow>();
            foreach (var row in usageStats ?? new CardUsageStatsRow[0])
            {
                statsByCard[row.CardIdm] = row;
            }

            var items = new List<CardUtilizationItem>(cards.Count);
            foreach (var card in cards)
            {
                statsByCard.TryGetValue(card.CardIdm, out var stats);
                var usedDayCount = stats?.UsedDayCount ?? 0;

                items.Add(new CardUtilizationItem
                {
                    CardIdm = card.CardIdm,
                    DisplayName = card.DisplayName,
                    UtilizationRate = CardUtilizationCalculator.CalculateUtilizationRate(usedDayCount, periodDayCount),
                    UsedDayCount = usedDayCount,
                    UsageCount = stats?.UsageCount ?? 0,
                    TotalExpense = stats?.TotalExpense ?? 0,
                    LastUsageDate = stats?.LastUsageDate,
                    UnusedDays = CardUtilizationCalculator.CalculateUnusedDays(stats?.LastUsageDate, asOf)
                });
            }

            // 「遊んでいるカードの発見」が目的なので稼働率の低い順に並べる
            return items
                .OrderBy(i => i.UtilizationRate)
                .ThenBy(i => i.UsageCount)
                .ThenBy(i => i.DisplayName, StringComparer.Ordinal)
                .ToList();
        }

        private static IReadOnlyList<MonthlyUsageSeries> BuildUsageSeries(
            IReadOnlyList<MonthlyUsageRow> monthlyUsage, IReadOnlyList<string> months, Dictionary<string, string> staffNames)
        {
            var monthIndex = new Dictionary<string, int>();
            for (var i = 0; i < months.Count; i++)
            {
                monthIndex[months[i]] = i;
            }

            // 職員の同一性は lender_idm を優先する。過去のインポートデータには lender_idm が
            // 無い行があり、その場合のみ台帳の氏名を識別子として使う。
            var buckets = new Dictionary<string, int[]>();
            var displayNames = new Dictionary<string, string>();

            foreach (var row in monthlyUsage ?? new MonthlyUsageRow[0])
            {
                if (!monthIndex.TryGetValue(row.YearMonth, out var index))
                {
                    continue;
                }

                var key = !string.IsNullOrEmpty(row.LenderIdm) ? "idm:" + row.LenderIdm : "name:" + row.StaffName;
                if (!buckets.TryGetValue(key, out var values))
                {
                    values = new int[months.Count];
                    buckets[key] = values;
                    displayNames[key] = ResolveSeriesName(row, staffNames);
                }

                values[index] += row.TotalExpense;
            }

            var series = buckets
                .Select(kv => new MonthlyUsageSeries
                {
                    // IsOther は AggregatedSeriesCount からの導出になったため設定しない（Issue #1883）。
                    // 集約していない系列は既定（件数 0）のまま IsOther = false になる。
                    Name = displayNames[kv.Key],
                    MonthlyExpenses = kv.Value,
                    TotalExpense = kv.Value.Sum()
                })
                .OrderByDescending(s => s.TotalExpense)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToList();

            if (series.Count <= AppConstants.AdminDashboardMaxSeries)
            {
                return series;
            }

            // 色相差を確保できる本数を超えたら「その他」へ集約する。
            // 凡例が読み取れなくなるうえ、色覚多様性への配慮も破綻するため。
            var top = series.Take(AppConstants.AdminDashboardMaxSeries).ToList();
            var rest = series.Skip(AppConstants.AdminDashboardMaxSeries).ToList();

            var otherValues = new int[months.Count];
            foreach (var s in rest)
            {
                for (var i = 0; i < months.Count; i++)
                {
                    otherValues[i] += s.MonthlyExpenses[i];
                }
            }

            // 名前に人数を添えるのは、氏名が「その他」の職員（職員マスタに無い staff_name を
            // そのまま系列名に使う経路がある）と凡例上で同一表記になるのを避けるため（Issue #1858）。
            // 組み立てを消費側（凡例・代替一覧・Excel）へ配らず、DTO 1 か所で確定させる。
            // Issue #1883: 件数・集約フラグ・表示名を別々の文で書くと片方だけ変わる日が来るため、
            // MarkAsAggregated が 3 つをまとめて確定させる（Name と IsOther は件数からの導出）。
            var otherSeries = new MonthlyUsageSeries
            {
                MonthlyExpenses = otherValues,
                TotalExpense = otherValues.Sum()
            };
            otherSeries.MarkAsAggregated(rest.Count);
            top.Add(otherSeries);

            return top;
        }

        private static string ResolveSeriesName(MonthlyUsageRow row, Dictionary<string, string> staffNames)
        {
            if (!string.IsNullOrEmpty(row.LenderIdm) && staffNames.TryGetValue(row.LenderIdm, out var name))
            {
                return name;
            }

            return !string.IsNullOrEmpty(row.StaffName) ? row.StaffName : UnknownStaffName;
        }

        private static IReadOnlyList<MonthlyBalanceSeries> BuildBalanceSeries(
            IReadOnlyList<IcCard> cards,
            IReadOnlyList<MonthEndBalanceRow> monthEndBalances,
            Dictionary<string, int> balancesBeforePeriod,
            IReadOnlyList<string> months)
        {
            var monthIndex = new Dictionary<string, int>();
            for (var i = 0; i < months.Count; i++)
            {
                monthIndex[months[i]] = i;
            }

            var byCard = new Dictionary<string, double?[]>();
            foreach (var row in monthEndBalances ?? new MonthEndBalanceRow[0])
            {
                if (!monthIndex.TryGetValue(row.YearMonth, out var index))
                {
                    continue;
                }

                if (!byCard.TryGetValue(row.CardIdm, out var values))
                {
                    values = new double?[months.Count];
                    byCard[row.CardIdm] = values;
                }

                values[index] = row.Balance;
            }

            var series = new List<MonthlyBalanceSeries>(cards.Count);
            foreach (var card in cards)
            {
                var hasInitial = balancesBeforePeriod != null
                    && balancesBeforePeriod.TryGetValue(card.CardIdm, out var initial);
                var initialValue = hasInitial ? (double?)balancesBeforePeriod[card.CardIdm] : null;

                if (!byCard.TryGetValue(card.CardIdm, out var values))
                {
                    // 期間内に一度も取引が無くても、期間前に残高があるカードは水平線として描く。
                    // 系列ごと落とすと「残高が無い」のか「使われていない」のか区別できない。
                    if (!hasInitial)
                    {
                        continue;
                    }

                    values = new double?[months.Count];
                }

                series.Add(new MonthlyBalanceSeries
                {
                    CardIdm = card.CardIdm,
                    DisplayName = card.DisplayName,
                    // 取引の無い月は前月の残高のまま。欠測として線を切ると残高不明と誤読される。
                    // 期間の先頭に取引が無いだけのカードは期間前の残高を起点にする
                    // （引き継がないと「途中から使い始めたカード」に見える）
                    MonthlyBalances = CardUtilizationCalculator.CarryForward(values, initialValue)
                });
            }

            return series;
        }

        #endregion

        #region 年月の列挙

        /// <summary>
        /// 集計期間に含まれる年月キー（"yyyy-MM"）を昇順で列挙する。
        /// </summary>
        /// <remarks>
        /// 取引の無い月も系列に含める必要があるため、DB の結果からではなく期間から作る。
        /// </remarks>
        internal static IReadOnlyList<string> EnumerateMonthKeys(DateTime fromDate, DateTime toDate)
        {
            var months = new List<string>();
            if (toDate.Date < fromDate.Date)
            {
                return months;
            }

            var cursor = new DateTime(fromDate.Year, fromDate.Month, 1);
            var last = new DateTime(toDate.Year, toDate.Month, 1);

            while (cursor <= last)
            {
                months.Add(cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                cursor = cursor.AddMonths(1);
            }

            return months;
        }

        /// <summary>
        /// 年月キー（"yyyy-MM"）を表示用ラベル（"yyyy/MM"）へ変換する。
        /// </summary>
        internal static string FormatMonthLabel(string monthKey)
            => string.IsNullOrEmpty(monthKey) ? string.Empty : monthKey.Replace('-', '/');

        #endregion
    }
}
