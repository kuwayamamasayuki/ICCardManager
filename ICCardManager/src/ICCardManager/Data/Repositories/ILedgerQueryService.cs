#nullable enable
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
        /// <param name="cardIdm">対象カードの IDm。null の場合は全カード</param>
        /// <param name="fromDate">期間の開始日</param>
        /// <param name="toDate">期間の終了日</param>
        Task<IEnumerable<Ledger>> GetByDateRangeAsync(string? cardIdm, DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定月の利用履歴を取得（帳票用）
        /// </summary>
        Task<IEnumerable<Ledger>> GetByMonthAsync(string cardIdm, int year, int month);

        /// <summary>
        /// IDで利用履歴を取得（詳細含む）
        /// </summary>
        Task<Ledger?> GetByIdAsync(int id);

        /// <summary>
        /// 指定日以前の利用履歴を取得（残額計算用）
        /// </summary>
        /// <remarks>
        /// Issue #1731: 同一日に複数レコードがある場合は id 順ではなく残高チェーン
        /// （Issue #784 の <c>LedgerOrderHelper.ReorderByBalanceChain</c>）で時系列順を
        /// 確定した最終レコードを返す。貸出中レコード（is_lent_record = 1）も対象に含む
        /// （返却処理の残高起点として使われるため）。
        /// </remarks>
        Task<Ledger?> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate);

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
        Task<Ledger?> GetLatestLedgerAsync(string cardIdm);

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
        /// <param name="busStopPlaceholder">
        /// 候補から除外する未入力プレースホルダ（既定「★」。Issue #1818）。
        /// 値は組織設定（<c>SummaryText.BusPlaceholder</c>）由来のため、永続化層では判断せず
        /// 呼び出し元から受け取る（設計書 05 §2a.5 の境界。<c>SummaryGenerator.BusPlaceholder</c> を渡すこと）。
        /// null／空文字は <see cref="System.ArgumentException"/>。
        /// </param>
        /// <exception cref="System.ArgumentException">
        /// <paramref name="busStopPlaceholder"/> が null または空文字の場合。
        /// </exception>
        Task<IEnumerable<(string BusStops, int UsageCount, DateTime? LastUsedDate)>> GetBusStopSuggestionsAsync(
            string busStopPlaceholder);

        /// <summary>
        /// 指定期間の利用履歴をページング付きで取得
        /// </summary>
        /// <param name="cardIdm">対象カードの IDm。null の場合は全カード</param>
        /// <param name="fromDate">期間の開始日</param>
        /// <param name="toDate">期間の終了日</param>
        /// <param name="page">ページ番号（1 始まり）</param>
        /// <param name="pageSize">1 ページの件数</param>
        Task<(IEnumerable<Ledger> Items, int TotalCount)> GetPagedAsync(
            string? cardIdm, DateTime fromDate, DateTime toDate, int page, int pageSize);

        /// <summary>
        /// 指定期間のledgerに紐づく全詳細を取得（CSVエクスポート用）
        /// </summary>
        Task<List<LedgerDetail>> GetAllDetailsInDateRangeAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 複数Ledgerの詳細を一括取得（残高整合性チェック用）
        /// </summary>
        Task<Dictionary<int, List<LedgerDetail>>> GetDetailsByLedgerIdsAsync(IEnumerable<int> ledgerIds);

        /// <summary>
        /// 指定カードの導入日（最も古い導入行の日付）を取得
        /// </summary>
        /// <remarks>
        /// 導入行は <see cref="Models.Ledger.IsInitialRecordSummary"/> の 3 種（「新規購入」／「○月から繰越」／
        /// 3 月登録の「前年度より繰越」）。判定を SQL に書き写さず同じ 1 つの判定を使う（Issue #2046）。
        /// 導入行が無いカード（導入前のデータ）は null。
        /// 判定は摘要の文字列を現在の組織設定と照合するため、登録後に摘要の設定を変えると旧文言の導入行は
        /// 認識されず null になる（スキップしない側へ倒れる。導入行はフラグを持たないので #2044 の形は取れない）。
        /// </remarks>
        Task<DateTime?> GetPurchaseDateAsync(string cardIdm);

        /// <summary>
        /// 指定カードで、利用明細（ledger_detail）を持たない台帳行のうち最も新しい日付を取得（Issue #2237）
        /// </summary>
        /// <remarks>
        /// 導入行（<see cref="Models.Ledger.IsInitialRecordSummary"/>）と貸出中レコードは除く。
        /// 手で追加した行・明細なしで CSV から取り込んだ行が該当する。これらは既存明細との照合（Issue #326）の
        /// キーを持たないため、返却時はこの日付より後の履歴だけを記録する（照合できない記録済みの利用を二重に記録しない）。
        /// 該当する行が無ければ null。
        /// </remarks>
        Task<DateTime?> GetLatestLedgerDateWithoutDetailsAsync(string cardIdm);

        /// <summary>
        /// 指定カードの既存の履歴詳細キーを取得（重複チェック用）
        /// </summary>
        /// <remarks>
        /// 台帳行の日付か明細の利用日のどちらかが <paramref name="fromDate"/> 以降の明細を返す（Issue #2237）。
        /// 履歴の統合は日付をまたいでも行えるため、古い日付の台帳行が新しい利用日の明細を持ち得る。
        /// </remarks>
        Task<HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>> GetExistingDetailKeysAsync(
            string cardIdm, DateTime fromDate);

        /// <summary>
        /// 指定カードの既存の履歴キーを取得（CSVインポート重複チェック用）
        /// </summary>
        Task<HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>> GetExistingLedgerKeysAsync(
            IEnumerable<string> cardIdms);

        /// <summary>
        /// 全カードの最終利用日を一括取得（管理者ダッシュボードの運用状況・メイン画面の残額ダッシュボード用、Issue #1747 / #2153）
        /// </summary>
        /// <remarks>
        /// 「利用実績」の定義は稼働状況の集計（<see cref="GetUsageStatsByCardAsync"/>）と同じ:
        /// 貸出中プレースホルダ（<c>is_lent_record = 1</c>）と繰越レコード
        /// （「新規購入」および組織設定 <c>MidYearCarryoverFormat</c> に従う繰越摘要。
        /// 既定書式では「○月から繰越」、Issue #1749）は利用実績ではないため除外する。
        /// 利用実績が 1 件も無いカードは辞書に含まれない（最終利用日は空欄扱い）。
        /// <see cref="GetAllLatestBalancesAsync"/> の LastUsageDate はこれらを除外しない
        /// 「最新レコード日」であり、登録しただけのカードが「使われている」ように見えるため、
        /// 「最終利用日」を表示する箇所ではこちらを使うこと（メイン画面の残額ダッシュボード
        /// <c>DashboardService</c> も Issue #2153 でこちらへ揃えた）。
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
        /// <remarks>
        /// 貸出中レコード・繰越レコードに加え、払戻台帳（払い戻しで残高全額を払出に計上した行）も除外する
        /// （Issue #2157）。払い戻しは職員の支出ではなく、残すと「（職員名なし）」系列に残高全額が積まれる。
        /// 判定は摘要ではなく行の形で行う。<see cref="GetUsageStatsByCardAsync"/> はこの除外を行わない
        /// （稼働状況は払戻済カードをカード単位で除外しているため）。
        /// </remarks>
        Task<IReadOnlyList<MonthlyUsageRow>> GetMonthlyUsageByLenderAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定期間のカード別 × 月別の月末残高を取得（Issue #1692）
        /// </summary>
        /// <remarks>
        /// 取引が無かった月は行が返らない。折れ線グラフでは前月の残高を引き継ぐこと。
        /// Issue #1770: 月末残高は「その月の最終稼働日」の全レコードを id 順ではなく残高チェーン
        /// （<c>LedgerOrderHelper.ReorderByBalanceChain</c>）で確定した最終レコードの残高を返す
        /// （<see cref="GetLatestBeforeDateAsync"/> と同じ規則）。貸出中レコードは母集団から除外する。
        /// </remarks>
        Task<IReadOnlyList<MonthEndBalanceRow>> GetMonthEndBalancesByCardAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定日より前の最終レコード時点の残高を全カード分まとめて取得（Issue #1692）
        /// </summary>
        /// <remarks>
        /// 残高推移グラフの起点に使う。集計期間の先頭に取引が無いだけのカードを
        /// 「まだ残高が無かった」と誤読させないため、期間前の残高を引き継ぐ。
        /// Issue #1770: 指定日より前の「最終稼働日」の全レコードを id 順ではなく残高チェーン
        /// （<c>LedgerOrderHelper.ReorderByBalanceChain</c>）で確定した最終レコードの残高を返す
        /// （<see cref="GetMonthEndBalancesByCardAsync"/> と同じ規則）。貸出中レコードは母集団から除外する。
        /// </remarks>
        Task<Dictionary<string, int>> GetBalancesBeforeAsync(DateTime beforeDate);
    }
}
