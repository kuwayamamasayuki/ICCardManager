#nullable enable
namespace ICCardManager.Dtos
{
    /// <summary>
    /// 残高ダッシュボード（<see cref="CardBalanceDashboardItem"/> の一覧）の並び順。
    /// </summary>
    /// <remarks>
    /// Issue #2332: 以前は <c>ViewModels/MainViewModel.cs</c> に置かれており、並べ替えを担う
    /// <c>Services/DashboardService</c> の公開 API がこれを引数に取るため、サービス層が ViewModel 層を
    /// 参照する逆向きの依存になっていた（05_クラス設計書 §2 のレイヤー構成は ViewModels → Services の一方向）。
    /// 並べ替えの対象である DTO と同じ層へ置く。並べ替えの観点（カード名・残高・最終利用日）は
    /// 交通系ICカードでなくても意味が通るため、交通系固有の層には置かない（domain-boundaries.md の決定木 1）。
    /// </remarks>
    public enum DashboardSortOrder
    {
        /// <summary>
        /// カード種別・番号順（デフォルト）
        /// </summary>
        CardName,

        /// <summary>
        /// 残高昇順（少ない順）
        /// </summary>
        BalanceAscending,

        /// <summary>
        /// 残高降順（多い順）
        /// </summary>
        BalanceDescending,

        /// <summary>
        /// 最終利用日順（新しい順）
        /// </summary>
        LastUsageDate
    }
}
