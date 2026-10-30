#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// バス停名が未入力（摘要にプレースホルダ <see cref="SummaryGenerator.BusPlaceholder"/> を含む）の利用履歴を集める（Issue #2329）
    /// </summary>
    /// <remarks>
    /// <para>
    /// メイン画面の警告の件数（<see cref="WarningService.CheckIncompleteBusStopsAsync"/>）と
    /// 「バス停名未入力の履歴」一覧（<c>IncompleteBusStopViewModel</c>）の唯一の取得元。
    /// 件数と一覧の母集団を食い違わせないため、どちらもここを通す（#1763「同じ判断を配らない」）。
    /// </para>
    /// <para>
    /// 対象期間: 利用日の期間で区切らず、台帳に残っている全期間（保存期間 6 年）。以前は直近 1 年に区切っており、
    /// 1 年以上入力し残した「バス（★）」は警告からも一覧からも消えて二度と案内されなかった。
    /// </para>
    /// <para>
    /// 対象のカード: 論理削除していないカード（払戻済みを含む）。論理削除したカードは登録の誤り等で台帳ごと
    /// 扱わなくなったもので、全期間に広げると最長 6 年間、一覧に IDm のまま残り件数に数えられ続ける。
    /// 一方で払戻済みカードの未入力は残す。返却時にバス停名の入力をスキップしたまま払い戻すと、カードは手元に無く
    /// 履歴にも直接は辿り着きにくいため、ここで案内しないと直す手段が無くなり、6 年保存の物品出納簿に
    /// 「バス（★）」のまま印刷され続ける（Issue #2329 のコードレビューで判断）。運用中のカード（<see cref="IcCard.IsInOperation"/>）に
    /// 絞る残額ダッシュボード（#1947）とは、目的（入力し残しを直す／補充が要るカードを探す）が違うため母集団も違う。
    /// </para>
    /// </remarks>
    public static class IncompleteBusStopLedgers
    {
        /// <summary>
        /// 論理削除していないカードの、バス停名が未入力の利用履歴を全期間から取得する（利用日の新しい順）
        /// </summary>
        /// <param name="ledgerQuery">台帳の問い合わせ</param>
        /// <param name="cards">
        /// カードの一覧（<c>ICardRepository.GetAllAsync</c> の結果。論理削除済みを返さない）。呼び出し側がカード名の解決にも
        /// 使えるよう、取得は呼び出し側に任せて 2 度読まない
        /// </param>
        public static async Task<IReadOnlyList<Ledger>> LoadAsync(ILedgerQueryService ledgerQuery, IEnumerable<IcCard> cards)
        {
            var ledgers = await ledgerQuery.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder).ConfigureAwait(false);

            // GetAllAsync は論理削除済みを返さないが、母集団の条件をこのメソッドの内側で完結させるため明示する
            var targetCardIdms = new HashSet<string>(cards.Where(c => !c.IsDeleted).Select(c => c.CardIdm));

            // Issue #1818: プレースホルダは組織設定（SummaryText.BusPlaceholder）由来のため直書きしない
            return ledgers
                .Where(l => SummaryGenerator.HasIncompleteBusStop(l.Summary) && targetCardIdms.Contains(l.CardIdm))
                .ToList();
        }
    }
}
