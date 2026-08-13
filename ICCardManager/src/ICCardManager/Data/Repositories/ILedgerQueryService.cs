using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Dtos;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// 利用履歴の読み取り専用クエリインターフェース
    /// </summary>
    /// <remarks>
    /// ILedgerRepositoryから読み取り専用メソッドを分離。
    /// 読み取りのみが必要なサービス（DashboardService, ReportDataBuilder等）は
    /// このインターフェースに依存することで、不要な書き込み操作への依存を避けられる。
    /// </remarks>
    public interface ILedgerQueryService
    {
        /// <summary>
        /// 指定期間の利用履歴を取得
        /// </summary>
        Task<IEnumerable<Ledger>> GetByDateRangeAsync(string cardIdm, DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定月の利用履歴を取得（帳票用）
        /// </summary>
        Task<IEnumerable<Ledger>> GetByMonthAsync(string cardIdm, int year, int month);

        /// <summary>
        /// IDで利用履歴を取得（詳細含む）
        /// </summary>
        Task<Ledger> GetByIdAsync(int id);

        /// <summary>
        /// 指定日以前の利用履歴を取得（残額計算用）
        /// </summary>
        /// <remarks>
        /// Issue #1731: 同一日に複数レコードがある場合は id 順ではなく残高チェーン
        /// （Issue #784 の <c>LedgerOrderHelper.ReorderByBalanceChain</c>）で時系列順を
        /// 確定した最終レコードを返す。貸出中レコード（is_lent_record = 1）も対象に含む
        /// （返却処理の残高起点として使われるため）。
        /// </remarks>
        Task<Ledger> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate);

        /// <summary>
        /// 年度繰越残高を取得
        /// </summary>
        /// <remarks>
        /// Issue #1731: 年度末最終日に複数レコードがある場合は残高チェーン順の最終残高を返す
        /// （<see cref="GetLatestBeforeDateAsync"/> と同じ規則）。
        /// </remarks>
        Task<int?> GetCarryoverBalanceAsync(string cardIdm, int fiscalYear);

        /// <summary>
        /// 指定カードの最新利用履歴を取得
        /// </summary>
        /// <remarks>
        /// Issue #1731: 同一日に複数レコードがある場合は残高チェーン順の最終レコードを返す
        /// （<see cref="GetLatestBeforeDateAsync"/> と同じ規則）。
        /// </remarks>
        Task<Ledger> GetLatestLedgerAsync(string cardIdm);

        /// <summary>
        /// 全カードの最新残高情報を一括取得（ダッシュボード用）
        /// </summary>
        /// <remarks>
        /// Issue #1731: 最新日に複数レコードがあるカードは残高チェーン順の最終残高を返す
        /// （<see cref="GetLatestBeforeDateAsync"/> と同じ規則）。最終利用日は最新日時
        /// （貸出中レコードがあればその時刻付き日時）を返す。
        /// LastUsageDate は貸出中・新規購入・繰越を除外しない「最新レコード日」である点に注意。
        /// 利用実績としての最終利用日は <see cref="GetAllLastUsageDatesAsync"/> を使う（Issue #1747）。
        /// </remarks>
        Task<Dictionary<string, (int Balance, DateTime? LastUsageDate)>> GetAllLatestBalancesAsync();

        /// <summary>
        /// 過去に入力されたバス停名をスコア順で取得（オートコンプリート用）
        /// </summary>
        Task<IEnumerable<(string BusStops, int UsageCount, DateTime? LastUsedDate)>> GetBusStopSuggestionsAsync();

        /// <summary>
        /// 指定期間の利用履歴をページング付きで取得
        /// </summary>
        Task<(IEnumerable<Ledger> Items, int TotalCount)> GetPagedAsync(
            string cardIdm, DateTime fromDate, DateTime toDate, int page, int pageSize);

        /// <summary>
        /// 指定期間のledgerに紐づく全詳細を取得（CSVエクスポート用）
        /// </summary>
        Task<List<LedgerDetail>> GetAllDetailsInDateRangeAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 複数Ledgerの詳細を一括取得（残高整合性チェック用）
        /// </summary>
        Task<Dictionary<int, List<LedgerDetail>>> GetDetailsByLedgerIdsAsync(IEnumerable<int> ledgerIds);

        /// <summary>
        /// 指定カードの新規購入日（または繰越日）を取得
        /// </summary>
        Task<DateTime?> GetPurchaseDateAsync(string cardIdm);

        /// <summary>
        /// 指定カードの既存の履歴詳細キーを取得（重複チェック用）
        /// </summary>
        Task<HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>> GetExistingDetailKeysAsync(
            string cardIdm, DateTime fromDate);

        /// <summary>
        /// 指定カードの既存の履歴キーを取得（CSVインポート重複チェック用）
        /// </summary>
        Task<HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>> GetExistingLedgerKeysAsync(
            IEnumerable<string> cardIdms);

        /// <summary>
        /// 全カードの最終利用日を一括取得（管理者ダッシュボードの運用状況用、Issue #1747）
        /// </summary>
        /// <remarks>
        /// 「利用実績」の定義は稼働状況の集計（<see cref="GetUsageStatsByCardAsync"/>）と同じ:
        /// 貸出中プレースホルダ（<c>is_lent_record = 1</c>）と繰越レコード
        /// （「新規購入」および組織設定 <c>MidYearCarryoverFormat</c> に従う繰越摘要。
        /// 既定書式では「○月から繰越」、Issue #1749）は利用実績ではないため除外する。
        /// 利用実績が 1 件も無いカードは辞書に含まれない（最終利用日は空欄扱い）。
        /// <see cref="GetAllLatestBalancesAsync"/> の LastUsageDate はこれらを除外しない
        /// 「最新レコード日」であり、登録しただけのカードが「使われている」ように見えるため、
        /// 新しく「最終利用日」を表示する箇所ではこちらを使うこと。
        /// なお既存のメイン画面カード残高ダッシュボード（DashboardService）は #1747 の
        /// スコープ判断により従来どおり「最新レコード日」を表示している（挙動を揃える場合は
        /// 別 Issue で扱う）。
        /// </remarks>
        Task<Dictionary<string, DateTime>> GetAllLastUsageDatesAsync();

        /// <summary>
        /// 指定期間のカード別利用実績を集計して取得（管理者ダッシュボードの稼働状況用、Issue #1692）
        /// </summary>
        /// <remarks>
        /// 台帳は 6 年分保持されるため、全件を読み出さず SQL 側で GROUP BY する。
        /// 貸出中レコード（<c>is_lent_record = 1</c>）は「利用」ではないため除外する。
        /// </remarks>
        Task<IReadOnlyList<CardUsageStatsRow>> GetUsageStatsByCardAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定期間の月別 × 貸出職員別の利用額を集計して取得（Issue #1692）
        /// </summary>
        Task<IReadOnlyList<MonthlyUsageRow>> GetMonthlyUsageByLenderAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定期間のカード別 × 月別の月末残高を取得（Issue #1692）
        /// </summary>
        /// <remarks>
        /// 取引が無かった月は行が返らない。折れ線グラフでは前月の残高を引き継ぐこと。
        /// </remarks>
        Task<IReadOnlyList<MonthEndBalanceRow>> GetMonthEndBalancesByCardAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定日より前の最終レコード時点の残高を全カード分まとめて取得（Issue #1692）
        /// </summary>
        /// <remarks>
        /// 残高推移グラフの起点に使う。集計期間の先頭に取引が無いだけのカードを
        /// 「まだ残高が無かった」と誤読させないため、期間前の残高を引き継ぐ。
        /// </remarks>
        Task<Dictionary<string, int>> GetBalancesBeforeAsync(DateTime beforeDate);
    }
}
