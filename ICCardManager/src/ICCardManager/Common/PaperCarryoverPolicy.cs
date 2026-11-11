#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.Common
{
    /// <summary>
    /// 紙の出納簿から移行したカード（Issue #510 / #1215）の累計受入・累計払出を、どの登録・どの年度の帳票で
    /// 扱うかの判定（Issue #2326）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 3 月から繰越は新年度の始まり（導入行「前年度より繰越」、新年度の 4/1・受入＝残額）で、紙の出納簿の
    /// 3 月末時点の累計は<b>旧年度</b>のもの。新年度の累計は 0 から始まる（受入には導入行の残高が入る）。
    /// 一方 <c>CarryoverFiscalYear</c> は導入行の日付（新年度の 4/1）から決まるため新年度になり、
    /// 紙の累計を保存・加算すると新年度の 5 月以降の累計行に旧年度の累計が上乗せされ、
    /// 物品出納簿の「受入 − 払出 = 残額」が年度を通して成り立たなくなる。
    /// </para>
    /// <para>
    /// 登録時（保存するか・入力欄を有効にするか）と帳票の作成時（加算するか）の判定をここに置き、
    /// 「3 月は年度の区切り」という判断を 1 か所に寄せる（#1763「同じ判断を配らない」）。
    /// </para>
    /// <para>
    /// 帳票が前年度繰越を合成・加算するか（Issue #2418）も、同じ「その年度の台帳に導入行『前年度より繰越』があるか」
    /// （<see cref="HasFiscalYearStartCarryoverRecord"/>）で決める。紙の累計と前年度繰越のどちらも、
    /// 導入行が年度の始まりを担うなら帳票が足してはいけない。
    /// </para>
    /// </remarks>
    public static class PaperCarryoverPolicy
    {
        /// <summary>
        /// 繰越月が年度の区切り（3 月）かどうか。3 月から繰越の導入行は「前年度より繰越」になる。
        /// </summary>
        public static bool IsFiscalYearStartCarryoverMonth(int carryoverMonth) => carryoverMonth == 3;

        /// <summary>
        /// 登録時に、紙の出納簿の累計受入・累計払出を受け付けて保存するかどうか。
        /// 繰越モードで、繰越月が 3 月以外のときだけ真。
        /// </summary>
        public static bool CarriesPaperCumulative(bool isNewPurchase, int? carryoverMonth) =>
            !isNewPurchase
            && carryoverMonth.HasValue
            && !IsFiscalYearStartCarryoverMonth(carryoverMonth.Value);

        /// <summary>
        /// 帳票の年度累計へ、カードに保存された紙の出納簿の累計を加算するかどうか。
        /// </summary>
        /// <param name="card">対象のカード（<c>CarryoverFiscalYear</c> を見る）</param>
        /// <param name="fiscalYearStartYear">帳票の年度</param>
        /// <param name="fiscalYearLedgers">その年度の台帳。年度で最も早い日付の行を含むこと（帳票は <c>GetFiscalYearFirstDayLedgersAsync</c> の結果を渡す）</param>
        /// <remarks>
        /// <para>
        /// 加算するのは繰越年度の帳票で、かつ<b>その年度の導入行が年度初めの繰越（「前年度より繰越」）でない</b>とき。
        /// 後者は登録時の判定（<see cref="CarriesPaperCumulative"/>）と同じ事実を、保存済みの台帳から読み直したもの。
        /// #2326 より前に 3 月繰越で累計を入力して登録したカードは DB に累計が残っているが、DB は書き換えずに
        /// 帳票側で加算しないことで、既存のデータも正しく出力する。年度途中の繰越（「○月から繰越」）のカードは
        /// その年度に「前年度より繰越」の行を持たない（翌年度の繰越行は帳票が in-memory で合成する）ので従来どおり加算する。
        /// </para>
        /// <para>
        /// <b>既知の制約（#2044）</b>: 導入行が「前年度より繰越」かは、摘要を現在の組織設定の文言
        /// （<see cref="SummaryGenerator.GetCarryoverFromPreviousYearSummary"/>）と比べて判定する。導入行には
        /// 種別を表すフラグが無く、保存済みの行自身の情報で判定する手段が無いため。組織設定でこの文言を変えると、
        /// 変更前に登録した 3 月繰越のカードは再び加算される（利用実績の集計の除外も同じ制約を持つ）。
        /// </para>
        /// </remarks>
        public static bool AddsPaperCumulativeToFiscalYear(
            IcCard card, int fiscalYearStartYear, IEnumerable<Ledger> fiscalYearLedgers)
        {
            if (card.CarryoverFiscalYear != fiscalYearStartYear)
            {
                return false;
            }

            return !HasFiscalYearStartCarryoverRecord(fiscalYearLedgers);
        }

        /// <summary>
        /// その年度の台帳が導入行「前年度より繰越」から始まるかどうか（年度で最も早い日付の行に、その摘要の行があるか）。
        /// 3 月を繰越月にして登録したカードの、登録した年度で真になる。
        /// </summary>
        /// <param name="fiscalYearLedgers">
        /// その年度の台帳（貸出中レコードは数えない）。<b>年度で最も早い日付の行が含まれている</b>こと —
        /// 範囲の途中から渡すと別の行が先頭に見える。帳票・履歴・整合性チェックは、年度全体のうち最も早い日の行だけを
        /// <c>ILedgerQueryService.GetFiscalYearFirstDayLedgersAsync</c> で取って渡す（呼び出し元ごとに範囲が違うと、
        /// 導入行を年度の途中へ動かしたときに帳票と履歴で判定が食い違う）
        /// </param>
        /// <remarks>
        /// <para>
        /// 真のとき、帳票・履歴は前年度繰越の行を合成せず、帳票は 4 月計・年度累計の受入にも前年度繰越を加算しない
        /// （Issue #2418。導入行の受入＝残高が通常の ledger 行として月計・累計に入るので、足すと二重になる）。
        /// 導入行より前の日付の行（3 月中の貸出中レコード・履歴の「追加」・CSV の取り込み）があると
        /// 「前年度末の残高」が存在してしまうため、「前年度末の残高があるか」ではなく導入行の有無で決める。
        /// 紙の累計の加算（<see cref="AddsPaperCumulativeToFiscalYear"/>）も同じ判定を使う。
        /// </para>
        /// <para>
        /// <b>日付を 4/1 に固定しない</b>（コードレビューで検出）。導入行の日付は履歴の行編集で動かせる（4/1 → 4/3 等）ので、
        /// 日付まで一致させると、動かした時点で紙の累計（#2326）が再び加算され、前年度繰越も二重になる。
        /// 一方「年度内のどこかにその摘要の行があるか」では、前年度から台帳があるカードに同じ摘要の行が手入力された
        /// だけで合成をやめてしまう。導入行は年度の台帳の先頭にあるという形で判定する。
        /// </para>
        /// <para>
        /// <b>既知の制約（#2044 / #2371）</b>: 導入行かどうかは摘要を現在の組織設定の文言と比べて判定する
        /// （導入行に種別のフラグが無いため）。文言を変える前に登録した導入行は認識できない。
        /// 貸出中レコードだけが導入行より前にある形は、前年度繰越の母集団から貸出中レコードを除くこと
        /// （<c>ILedgerQueryService.GetCarryoverBalanceAsync</c>）でも塞いでいる。
        /// </para>
        /// </remarks>
        public static bool HasFiscalYearStartCarryoverRecord(IEnumerable<Ledger> fiscalYearLedgers)
        {
            var settled = fiscalYearLedgers.Where(l => !l.IsLentRecord).ToList();
            if (settled.Count == 0)
            {
                return false;
            }

            var firstDay = settled.Min(l => l.Date.Date);
            var previousYearCarryoverSummary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
            return settled.Any(l => l.Date.Date == firstDay && l.Summary == previousYearCarryoverSummary);
        }

        /// <summary>
        /// 期間（帳票の月・履歴の表示期間・整合性チェックの範囲）の開始日が、年度の台帳の先頭にある導入行
        /// 「前年度より繰越」以前かどうか。真なら、その期間には「前の残高」（繰越行・残高チェーンのシード）が無い。
        /// </summary>
        /// <param name="periodStart">期間の開始日</param>
        /// <param name="fiscalYearFirstDayLedgers">
        /// 開始日が属する年度で最も早い日付の行（<c>ILedgerQueryService.GetFiscalYearFirstDayLedgersAsync</c>）
        /// </param>
        /// <remarks>
        /// Issue #2418: 導入行が台帳の始まりの残高を担うので、それより前の日付の行（3 月中の貸出中レコード・手入力・CSV）の
        /// 残高を「前の残高」にすると、帳票・履歴は繰越行を二重に出し、整合性チェックは導入行と同じ日の利用の順序を
        /// 取り違えて正しい行を不整合と報告する。帳票・履歴・整合性チェックはこの 1 か所で「前の残高を使うか」を決める。
        /// </remarks>
        public static bool PeriodOpensWithFiscalYearStartCarryover(
            DateTime periodStart, IEnumerable<Ledger> fiscalYearFirstDayLedgers)
        {
            var settled = fiscalYearFirstDayLedgers.Where(l => !l.IsLentRecord).ToList();
            return HasFiscalYearStartCarryoverRecord(settled)
                && periodStart.Date <= settled.Min(l => l.Date.Date);
        }

        /// <summary>
        /// 期間の中で、残高チェーンが導入行から始まり直す日を古い順に返す。導入行は、年度の台帳の先頭にある「前年度より繰越」と、
        /// 「○月から繰越」（年度途中の導入。Issue #2441）。
        /// </summary>
        /// <param name="periodStart">期間の開始日</param>
        /// <param name="periodLedgers">期間の台帳（開始日以降の行。貸出中レコードを含んでよい。判定では数えない）</param>
        /// <param name="periodStartFiscalYearFirstDayLedgers">
        /// 開始日が属する年度で最も早い日付の行（<c>ILedgerQueryService.GetFiscalYearFirstDayLedgersAsync</c>）
        /// </param>
        /// <remarks>
        /// <para>
        /// Issue #2437: 導入行は台帳の始まりの残高を担う（登録時の繰越額。前の行の残高から計算した値ではない）ので、
        /// 導入行より前の日付の行（3 月中の貸出中レコード・手入力・CSV の 3 月の行）からチェーンを続けて検査すると、
        /// 正しい導入行が不整合と報告される。整合性チェックは、ここで返す日の手前でチェーンを切り、導入行を始点にする。
        /// </para>
        /// <para>
        /// 開始日が属する年度は、期間の台帳だけでは年度の先頭の日が分からない（期間が年度の途中から始まり得る）ので、
        /// 帳票・履歴と同じ <see cref="PeriodOpensWithFiscalYearStartCarryover"/> で決める（真のとき、その年度の先頭の日を返す）。
        /// それより後の年度は年度の初日から期間に含まれるので、期間の台帳の年度ごとの行で
        /// <see cref="HasFiscalYearStartCarryoverRecord"/> を判定する。表示期間（開始日が導入行以前）の検査も
        /// 全期間の検査も、判定はこの 2 つに寄せる（#1763「同じ判断を配らない」）。
        /// </para>
        /// <para>
        /// Issue #2441: 年度途中で導入したカードの導入行「○月から繰越」は繰越月の翌月 1 日付けで保存され
        /// （<see cref="SummaryGenerator.GetMidYearCarryoverDate"/>）、登録日より後の日付になり得るので、その手前にも
        /// 返却の利用の行（#2237 より前の記録に限る）・手入力・CSV の行・貸出中レコードが入る。「○月から繰越」は年度の途中にしか現れない摘要なので
        /// 年度の先頭かどうかは問わず、期間の台帳にある帳簿の行（貸出中レコードを除く）の日をすべて返す。
        /// 期間の外の行は数えないので、全期間と表示期間で同じ日になる。台帳の途中に手入力された「○月から繰越」も始点になるが、
        /// その残高の誤りは直後の行で検出され、#2007 の訂正案がその行を指す。
        /// </para>
        /// </remarks>
        public static IReadOnlyList<DateTime> GetChainRestartDates(
            DateTime periodStart,
            IEnumerable<Ledger> periodLedgers,
            IEnumerable<Ledger> periodStartFiscalYearFirstDayLedgers)
        {
            var periodStartFiscalYear = FiscalYearHelper.GetFiscalYear(periodStart);
            var ledgers = periodLedgers.ToList();
            var dates = new List<DateTime>();

            var firstDayLedgers = periodStartFiscalYearFirstDayLedgers.ToList();
            if (PeriodOpensWithFiscalYearStartCarryover(periodStart, firstDayLedgers))
            {
                dates.Add(firstDayLedgers.Where(l => !l.IsLentRecord).Min(l => l.Date.Date));
            }

            foreach (var fiscalYearLedgers in ledgers
                .GroupBy(l => FiscalYearHelper.GetFiscalYear(l.Date))
                .Where(g => g.Key > periodStartFiscalYear)
                .OrderBy(g => g.Key))
            {
                if (HasFiscalYearStartCarryoverRecord(fiscalYearLedgers))
                {
                    dates.Add(fiscalYearLedgers.Where(l => !l.IsLentRecord).Min(l => l.Date.Date));
                }
            }

            dates.AddRange(ledgers
                .Where(l => !l.IsLentRecord && SummaryGenerator.IsMidYearCarryoverSummary(l.Summary))
                .Select(l => l.Date.Date));

            return dates.Distinct().OrderBy(d => d).ToList();
        }
    }
}
