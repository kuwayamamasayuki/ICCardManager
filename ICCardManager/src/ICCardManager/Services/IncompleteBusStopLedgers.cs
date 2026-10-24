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
    /// 対象のカード: 運用中のカード（<see cref="IcCard.IsInOperation"/>。未削除 かつ 未払戻）に限る。
    /// 全期間に広げると、論理削除・払い戻したカードの未入力が最長 6 年間、件数に数えられ一覧に残る
    /// （論理削除したカードは一覧でカード名を解決できず IDm がそのまま出る）。もう使わないカードの入力を
    /// 促し続ける警告は、本当に入力が要るカードの案内を埋もれさせる（残額ダッシュボードの母集団 #1947 と同じ判断）。
    /// 判定は <see cref="IcCard.IsInOperation"/> に委ね、ここで書き直さない。
    /// </para>
    /// </remarks>
    public static class IncompleteBusStopLedgers
    {
        /// <summary>
        /// 運用中のカードの、バス停名が未入力の利用履歴を全期間から取得する（利用日の新しい順）
        /// </summary>
        /// <param name="ledgerQuery">台帳の問い合わせ</param>
        /// <param name="cardRepository">カードの母集団（<c>GetAllAsync</c> は論理削除済みを返さない）</param>
        public static async Task<IReadOnlyList<Ledger>> LoadAsync(ILedgerQueryService ledgerQuery, ICardRepository cardRepository)
        {
            var ledgers = await ledgerQuery.GetBySummaryContainingAsync(SummaryGenerator.BusPlaceholder).ConfigureAwait(false);
            var cards = await cardRepository.GetAllAsync().ConfigureAwait(false);
            var operatingCardIdms = new HashSet<string>(cards.Where(c => c.IsInOperation).Select(c => c.CardIdm));

            // Issue #1818: プレースホルダは組織設定（SummaryText.BusPlaceholder）由来のため直書きしない
            return ledgers
                .Where(l => SummaryGenerator.HasIncompleteBusStop(l.Summary) && operatingCardIdms.Contains(l.CardIdm))
                .ToList();
        }
    }
}
