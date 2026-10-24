#nullable enable
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 摘要のバス停名を明細（<c>ledger_detail.bus_stops</c>）へ書き戻せるかの判定結果（Issue #2306）
    /// </summary>
    internal enum BusStopSyncOutcome
    {
        /// <summary>書き戻す対象が無い（バス明細が無い／摘要にバスのブロックが無い）</summary>
        NothingToSync,

        /// <summary>摘要のバス停名を明細へ 1 対 1 で対応付けられた</summary>
        Mapped,

        /// <summary>
        /// 摘要にバス停名はあるが、明細へ対応付けられない形（区間数とバス利用の件数が合わない等）。
        /// 書き戻さない。書き戻せないまま摘要だけを保存すると、統合時に明細から摘要が作り直され、
        /// 編集が失われる
        /// </summary>
        Unmappable,
    }

    /// <summary>
    /// 対応付けられない理由（Issue #2306 のコードレビューで追加）。利用者への案内を理由ごとに出し分けるため
    /// </summary>
    internal enum BusStopSyncFailure
    {
        /// <summary>対応付けられた・対象が無い</summary>
        None,

        /// <summary>区間の数（往復は 2 件と数える）がバス利用の件数と合わない</summary>
        CountMismatch,

        /// <summary>バス利用が 1 件なのに往復の表記がある</summary>
        RoundTripOnSingleUsage,

        /// <summary>
        /// 往復の往路と復路が隣り合う明細と読めない（あいだに別の利用がある等）。
        /// 件数は合っていても、位置で書き戻すと別の利用へ名前が入る
        /// </summary>
        RoundTripNotAdjacent,

        /// <summary>往復の接尾辞で終わるのに、その前が「乗車～降車」の形になっていない（「A～B～C 往復」「天神 往復」）</summary>
        MalformedRoundTrip,

        /// <summary>
        /// 編集前の摘要にはバスのブロックがあったのに、編集後は 1 つも読み取れない（半角括弧「バス(…)」・ラベルの削除等）。
        /// 計画（<see cref="SummaryGenerator.PlanBusStopSync"/>）は返さず、行編集が編集前後を比べて判定する
        /// </summary>
        BusBlockMissing,

        /// <summary>
        /// バス停名の並びだけを変えた編集（値は同じ）。明細の利用の順と違うため書き戻さない。
        /// 計画は <see cref="BusStopSyncPlan.SuppressedReorder"/> で伝え、行編集がこの理由で警告する（コードレビュー 5 回目）
        /// </summary>
        ReorderOnly,
    }

    /// <summary>
    /// 明細 1 件へ書き戻すバス停名（Issue #2306）
    /// </summary>
    internal sealed class BusStopAssignment
    {
        public BusStopAssignment(LedgerDetail detail, string busStops, string currentBusStops)
        {
            Detail = detail;
            BusStops = busStops;
            CurrentBusStops = currentBusStops;
        }

        /// <summary>書き戻し先の明細</summary>
        public LedgerDetail Detail { get; }

        /// <summary>書き戻すバス停名</summary>
        public string BusStops { get; }

        /// <summary>
        /// 計画を立てた時点の明細の値。未入力（null・空白）はプレースホルダ（★）へ寄せてある
        /// （摘要側の未入力も★として読まれるため。コードレビュー 5 回目）
        /// </summary>
        public string CurrentBusStops { get; }

        /// <summary>明細の現在の値と異なる（実際に書き換えが要る）か。未入力と★は同じとみなす</summary>
        public bool IsChange => !string.Equals(CurrentBusStops, BusStops, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// 摘要からバス停名を明細へ書き戻す計画（Issue #2306）
    /// </summary>
    /// <remarks>
    /// 統合（<c>LedgerMergeService.SyncBusStopsFromSummary</c>）と行編集
    /// （<c>LedgerRowEditViewModel.SyncBusStopsFromSummaryAsync</c>）の両方が
    /// <see cref="SummaryGenerator.PlanBusStopSync"/> ただ 1 つから計画を受け取る。
    /// 対応付けの規則（往復の展開・件数の照合）を消費側ごとに書くと、
    /// 片方だけが往復を解釈する日が来る（#1763「同じ判断を配らない」）。
    /// </remarks>
    internal sealed class BusStopSyncPlan
    {
        private BusStopSyncPlan(
            BusStopSyncOutcome outcome,
            IReadOnlyList<BusStopAssignment> assignments,
            BusStopSyncFailure failure,
            bool suppressedReorder = false)
        {
            Outcome = outcome;
            Assignments = assignments;
            Failure = failure;
            SuppressedReorder = suppressedReorder;
        }

        /// <summary>
        /// 対応付けた値が現在の値の並べ替えにすぎないため書き戻さなかったか（Outcome は NothingToSync）
        /// </summary>
        public bool SuppressedReorder { get; }

        /// <summary>書き戻す対象が無い</summary>
        public static BusStopSyncPlan NothingToSync { get; } =
            new BusStopSyncPlan(BusStopSyncOutcome.NothingToSync, new List<BusStopAssignment>(), BusStopSyncFailure.None);

        /// <summary>
        /// 書き戻さない（対応付けた値が現在の値の並べ替えにすぎない）。<see cref="BusStopSyncOutcome.NothingToSync"/> の一種で、
        /// 行編集が「並べ替えただけの編集」を警告するために <see cref="SuppressedReorder"/> を立てる（コードレビュー 5 回目）
        /// </summary>
        public static BusStopSyncPlan SuppressedAsReorder { get; } =
            new BusStopSyncPlan(BusStopSyncOutcome.NothingToSync, new List<BusStopAssignment>(), BusStopSyncFailure.None, suppressedReorder: true);

        /// <summary>対応付けられない形（理由付き）</summary>
        public static BusStopSyncPlan Unmappable(BusStopSyncFailure failure)
            => new BusStopSyncPlan(BusStopSyncOutcome.Unmappable, new List<BusStopAssignment>(), failure);

        /// <summary>対応付けられた計画を作る</summary>
        public static BusStopSyncPlan Mapped(IReadOnlyList<BusStopAssignment> assignments)
            => new BusStopSyncPlan(BusStopSyncOutcome.Mapped, assignments, BusStopSyncFailure.None);

        /// <summary>判定結果</summary>
        public BusStopSyncOutcome Outcome { get; }

        /// <summary>対応付けられない理由（<see cref="BusStopSyncOutcome.Unmappable"/> 以外では None）</summary>
        public BusStopSyncFailure Failure { get; }

        /// <summary>対応付けた明細とバス停名（<see cref="BusStopSyncOutcome.Mapped"/> のときだけ要素を持つ）</summary>
        public IReadOnlyList<BusStopAssignment> Assignments { get; }

        /// <summary>実際に値が変わる対応付けだけ</summary>
        public IEnumerable<BusStopAssignment> Changes => Assignments.Where(a => a.IsChange);
    }
}
