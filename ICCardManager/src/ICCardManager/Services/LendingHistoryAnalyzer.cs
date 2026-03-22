using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// ICカード利用履歴の静的解析ユーティリティ。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LendingService"/> から抽出された、利用履歴データの分析・変換を行う
    /// 純粋関数群を提供します。副作用やDB依存がなく、入力データのみで結果が決まります。
    /// </para>
    /// <para>
    /// 主な機能:
    /// </para>
    /// <list type="bullet">
    /// <item><description>残高不足パターンの検出（<see cref="DetectInsufficientBalancePattern"/>）</description></item>
    /// <item><description>チャージ境界での利用グループ分割（<see cref="SplitAtChargeBoundaries"/>）</description></item>
    /// <item><description>残高チェーンに基づく時系列ソート（<see cref="SortChronologically"/>）</description></item>
    /// <item><description>カード内履歴の完全性チェック（<see cref="CheckHistoryCompleteness"/>）</description></item>
    /// </list>
    /// </remarks>
    internal static class LendingHistoryAnalyzer
    {
        /// <summary>
        /// 同一日の時系列セグメント（利用グループまたは単一チャージ）。
        /// チャージ境界で利用を分割するために使用する。
        /// </summary>
        internal class DailySegment
        {
            /// <summary>チャージセグメントかどうか</summary>
            public bool IsCharge { get; init; }

            /// <summary>ポイント還元セグメントかどうか（Issue #942）</summary>
            public bool IsPointRedemption { get; init; }

            /// <summary>セグメント内の詳細リスト（利用グループの場合は複数、チャージ/ポイント還元の場合は1件）</summary>
            public List<LedgerDetail> Details { get; init; } = new();
        }

        /// <summary>
        /// 残高不足パターン検出時に許容するチャージ超過額の閾値（円）。
        /// 精算機でのチャージは不足額ちょうどか10円単位の端数切り上げのため、
        /// 利用後残高（= チャージ額 - 不足額）がこの値未満であれば残高不足パターンとみなす。
        /// </summary>
        internal const int InsufficientBalanceExcessThreshold = 100;

        /// <summary>
        /// 残高不足パターンを検出します。
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #380対応: 残高が不足して不足分を現金でチャージした場合のパターンを検出。
        /// </para>
        /// <para>
        /// Issue #978対応: 精算機が10円単位等でしかチャージできない場合、不足額より
        /// 多めにチャージされることがある（例: 不足134円に対し140円チャージ）。
        /// このケースでも検出できるよう条件を緩和。
        /// </para>
        /// <para>
        /// パターン:
        /// 1. チャージ前の残高が運賃未満（残高不足）
        /// 2. チャージ後の残高で運賃を支払っている（チャージ→利用の連続性）
        ///    つまり: チャージ後残高 = 利用額 + 利用後残高
        /// </para>
        /// <para>
        /// 例1（ぴったりチャージ）: 残高200円、運賃210円
        /// - チャージ: 10円（残高 → 210円）
        /// - 利用: 210円（残高 → 0円）
        /// </para>
        /// <para>
        /// 例2（端数あり）: 残高76円、運賃210円
        /// - チャージ: 140円（残高 → 216円）※不足134円だが10円単位で140円チャージ
        /// - 利用: 210円（残高 → 6円）
        /// </para>
        /// </remarks>
        /// <param name="dailyDetails">日付グループ内の履歴詳細リスト</param>
        /// <returns>検出されたペアのリスト</returns>
        internal static List<(LedgerDetail Charge, LedgerDetail Usage)> DetectInsufficientBalancePattern(
            List<LedgerDetail> dailyDetails)
        {
            var result = new List<(LedgerDetail Charge, LedgerDetail Usage)>();
            var processedIndices = new HashSet<int>();

            for (int i = 0; i < dailyDetails.Count; i++)
            {
                if (processedIndices.Contains(i)) continue;

                var current = dailyDetails[i];

                // チャージレコードを探す
                if (!current.IsCharge) continue;
                if (!current.Balance.HasValue || !current.Amount.HasValue) continue;

                var chargeAfterBalance = current.Balance.Value;
                var chargeAmount = current.Amount.Value;
                var originalBalance = chargeAfterBalance - chargeAmount;

                // 対応する利用レコードを探す
                for (int j = 0; j < dailyDetails.Count; j++)
                {
                    if (i == j || processedIndices.Contains(j)) continue;

                    var candidate = dailyDetails[j];

                    // 利用レコード（チャージでもポイント還元でもない）
                    if (candidate.IsCharge || candidate.IsPointRedemption) continue;
                    if (!candidate.Balance.HasValue || !candidate.Amount.HasValue) continue;

                    var usageAmount = candidate.Amount.Value;
                    var usageAfterBalance = candidate.Balance.Value;

                    // パターン検出条件:
                    // 1. チャージ前の残高が運賃未満（残高不足だった）
                    // 2. チャージ後残高 = 利用額 + 利用後残高（チャージ→利用の連続性）
                    //    ※隣接取引では常にTRUEだが、間に別取引がある場合の除外に有効
                    // 3. チャージ額が運賃以下（不足分を補うためのチャージであること）
                    //    通常の大額チャージ（1000円等）を除外する
                    // 4. 利用後の残高が少額（チャージ額 ≈ 不足額であること）
                    //    精算機でのチャージは不足額ちょうどか10円単位の端数切り上げのため、
                    //    利用後の残高（= チャージ額 - 不足額）は小さい値になる
                    //    Issue #1001: 通常チャージ（500円等）が運賃以下でも誤検出されるのを防止
                    if (originalBalance < usageAmount &&
                        chargeAfterBalance == usageAmount + usageAfterBalance &&
                        chargeAmount <= usageAmount &&
                        usageAfterBalance < InsufficientBalanceExcessThreshold)
                    {
                        // パターン検出！
                        result.Add((current, candidate));
                        processedIndices.Add(i);
                        processedIndices.Add(j);
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 同一日の履歴を時系列順に並べ、チャージの位置で利用グループを分割します。
        /// </summary>
        /// <remarks>
        /// <para>
        /// ICカードの利用履歴を残高チェーンで時系列順（古い順）に並べ替え、
        /// チャージが出現する位置で利用グループを区切る。
        /// これにより、チャージが利用の間に挟まるケースで残高チェーンが正しく維持される。
        /// </para>
        /// <para>
        /// 例: [trip1, charge, trip2] → [UsageGroup(trip1), Charge, UsageGroup(trip2)]<br/>
        /// 例: [trip1, trip2, charge] → [UsageGroup(trip1+trip2), Charge]<br/>
        /// 例: [trip1, trip2] → [UsageGroup(trip1+trip2)]
        /// </para>
        /// </remarks>
        /// <param name="dailyDetails">同一日内の全詳細（残高不足パターン処理済み）</param>
        /// <returns>時系列順のセグメントリスト</returns>
        internal static List<DailySegment> SplitAtChargeBoundaries(List<LedgerDetail> dailyDetails)
        {
            if (dailyDetails.Count == 0)
                return new List<DailySegment>();

            // 時系列順（古い順）に並べ替え
            var chronological = SortChronologically(dailyDetails);

            var segments = new List<DailySegment>();
            var currentUsageGroup = new List<LedgerDetail>();

            foreach (var detail in chronological)
            {
                if (detail.IsCharge)
                {
                    // 溜まった利用グループを先に出力
                    if (currentUsageGroup.Count > 0)
                    {
                        segments.Add(new DailySegment
                        {
                            IsCharge = false,
                            Details = new List<LedgerDetail>(currentUsageGroup)
                        });
                        currentUsageGroup.Clear();
                    }

                    // チャージを出力
                    segments.Add(new DailySegment
                    {
                        IsCharge = true,
                        Details = new List<LedgerDetail> { detail }
                    });
                }
                else if (detail.IsPointRedemption || SummaryGenerator.IsImplicitPointRedemption(detail))
                {
                    // Issue #942: ポイント還元（明示的・暗黙的）も個別セグメントとして分離
                    if (currentUsageGroup.Count > 0)
                    {
                        segments.Add(new DailySegment
                        {
                            IsCharge = false,
                            Details = new List<LedgerDetail>(currentUsageGroup)
                        });
                        currentUsageGroup.Clear();
                    }

                    segments.Add(new DailySegment
                    {
                        IsPointRedemption = true,
                        Details = new List<LedgerDetail> { detail }
                    });
                }
                else
                {
                    currentUsageGroup.Add(detail);
                }
            }

            // 残りの利用グループを出力
            if (currentUsageGroup.Count > 0)
            {
                segments.Add(new DailySegment
                {
                    IsCharge = false,
                    Details = new List<LedgerDetail>(currentUsageGroup)
                });
            }

            return segments;
        }

        /// <summary>
        /// 残高チェーンに基づいて詳細を時系列順（古い順）に並べ替えます。
        /// </summary>
        /// <remarks>
        /// LedgerDetailChronologicalSorterに委譲。
        /// FeliCa入力（新しい→古い）を想定し、チェーン構築失敗時はリスト逆順にフォールバック。
        /// </remarks>
        /// <param name="details">並べ替え対象の履歴詳細リスト</param>
        /// <returns>時系列順（古い順）に並べ替えられたリスト</returns>
        internal static List<LedgerDetail> SortChronologically(List<LedgerDetail> details)
        {
            return Common.LedgerDetailChronologicalSorter.Sort(details, preserveOrderOnFailure: false);
        }

        /// <summary>
        /// カードから読み取った履歴の完全性をチェックします。
        /// </summary>
        /// <remarks>
        /// Issue #596対応: カード内の履歴20件がすべて今月以降の場合、
        /// 今月初日以降の古い履歴がカードから押し出されている可能性がある。
        /// </remarks>
        /// <param name="rawDetails">カードから読み取った生の履歴（最大20件）</param>
        /// <param name="currentMonthStart">今月1日</param>
        /// <returns>今月の履歴が不完全な可能性がある場合true</returns>
        internal static bool CheckHistoryCompleteness(IList<LedgerDetail> rawDetails, DateTime currentMonthStart)
        {
            // 20件未満の場合はカード内の全履歴を取得済み
            if (rawDetails.Count < 20)
            {
                return false;
            }

            // 日付のある履歴のうち、今月より前のものがあれば今月分は全件カバー
            var hasPreCurrentMonth = rawDetails
                .Where(d => d.UseDate.HasValue)
                .Any(d => d.UseDate.Value.Date < currentMonthStart);

            // 先月以前の履歴がなければ → 今月分が押し出されている可能性あり
            return !hasPreCurrentMonth;
        }
    }
}
