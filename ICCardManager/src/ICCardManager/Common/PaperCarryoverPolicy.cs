#nullable enable
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
        /// <param name="fiscalYearLedgers">その年度の台帳（年度の初日から帳票の月末まで）</param>
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

            var previousYearCarryoverSummary = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
            return !fiscalYearLedgers.Any(l => !l.IsLentRecord && l.Summary == previousYearCarryoverSummary);
        }
    }
}
