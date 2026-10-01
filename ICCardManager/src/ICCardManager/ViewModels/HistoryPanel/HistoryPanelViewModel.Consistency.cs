using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.ViewModels;

public partial class HistoryPanelViewModel
{
    // === 残高整合性（ハイライトと不整合警告） ===

    #region 残高整合性チェック（Issue #1739 / #2007）

    /// <summary>
    /// 残高整合性チェックで「全期間」を指す範囲（SQLite の date 型互換の範囲）。
    /// </summary>
    /// <remarks>
    /// Issue #1739: 残高不整合警告は表示期間ではなくカード全体の状態を表すため、
    /// <see cref="CheckAndNotifyConsistencyAsync"/> と <see cref="CheckAllCardsConsistencyAsync"/> の
    /// どちらも同じ範囲で判定する。片方だけ範囲が違うと、一方が立てた警告をもう一方が黙って消す。
    /// </remarks>
    private static readonly DateTime FullPeriodStart = new DateTime(2000, 1, 1);
    private static readonly DateTime FullPeriodEnd = new DateTime(2099, 12, 31);

    /// <summary>
    /// 残高不整合警告を組み立てる（表示文言を1か所に集約する）
    /// </summary>
    /// <remarks>
    /// Issue #2007: 不整合が「導入時残高の誤り」の形状なら、件数ではなく原因を名指しする。
    /// 件数の文言だと、ハイライトされる行（従来はチェーンが切れた側＝正しい行）を直す誘導になる。
    /// </remarks>
    private static WarningItem BuildBalanceInconsistencyWarning(
        string cardType, string cardNumber, string cardIdm, ConsistencyResult result)
    {
        var totalCount = result.Inconsistencies.Count + result.DetailInconsistencies.Count;
        return new WarningItem
        {
            DisplayText = result.InitialBalanceCorrection != null
                ? InitialBalanceCorrectionMessage.ForWarningArea(cardType, cardNumber)
                : $"⚠️ 残高の不整合が{totalCount}件あります（{cardType} {cardNumber}）",
            Type = WarningType.BalanceInconsistency,
            CardIdm = cardIdm
        };
    }

    /// <summary>
    /// Issue #2007: 整合性チェック結果から、履歴一覧でハイライトする行と表示値のマップを組み立てる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通常は不整合の行（チェーンが切れた側）をそのまま対象にする。ただし「導入時残高の誤り」の形状
    /// （<see cref="ConsistencyResult.InitialBalanceCorrection"/>）では、切れた側の 2 行目はカード由来の
    /// 正しい行なので対象から外し、代わりに<b>導入行</b>を「期待値＝逆算した残高／実際＝記録されている残高」
    /// で対象にする。2 行目を強調したままだと、利用者が正しい行を誤った導入行に合わせて書き換える誘導になる。
    /// </para>
    /// <para>
    /// 詳細レベルの不整合の親 Ledger も対象に含める（Issue #1059）が、導入時残高の誤りではその詳細不整合も
    /// 導入行の写像（先頭明細の起点が導入行の残高）なので、同様に導入行へ寄せる。
    /// </para>
    /// </remarks>
    internal static Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)> BuildInconsistencyMarkers(
        ConsistencyResult result)
    {
        var correction = result.InitialBalanceCorrection;
        if (correction != null)
        {
            return new Dictionary<int, (int ExpectedBalance, int ActualBalance, bool IsInitialBalanceCorrection)>
            {
                { correction.LedgerId, (correction.SuggestedBalance, correction.RecordedBalance, true) }
            };
        }

        // 親レコード不整合 + 詳細レベル不整合（詳細の親LedgerId単位で集約）
        var markers = result.Inconsistencies
            .ToDictionary(i => i.LedgerId, i => (i.ExpectedBalance, i.ActualBalance, false));

        // Issue #1059: 詳細レベル不整合がある親Ledgerもハイライト対象に追加
        foreach (var detailGroup in result.DetailInconsistencies.GroupBy(d => d.LedgerId))
        {
            if (!markers.ContainsKey(detailGroup.Key))
            {
                var first = detailGroup.First();
                markers[detailGroup.Key] = (first.ExpectedBalance, first.ActualBalance, false);
            }
        }
        return markers;
    }

    /// <summary>
    /// Issue #2007: 行編集を開く前に、その行が導入行で「導入時残高の誤り」が検知されているなら訂正案を返す。
    /// </summary>
    /// <remarks>
    /// 全期間の整合性チェック（6 年分の読み取り）は導入行を開くときだけ走らせる。利用行では null を返し、
    /// 問い合わせない。訂正案の行 ID が編集対象と一致するときだけ返す（一致しなければ別の形状）。
    /// </remarks>
    internal async Task<InitialBalanceCorrection> ResolveInitialBalanceCorrectionForEditAsync(LedgerDto ledger)
    {
        if (ledger == null || !Ledger.IsInitialRecordSummary(ledger.Summary)) return null;

        var result = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
            ledger.CardIdm, FullPeriodStart, FullPeriodEnd);
        var correction = result.InitialBalanceCorrection;
        return correction != null && correction.LedgerId == ledger.Id ? correction : null;
    }

    /// <summary>
    /// Issue #1052: 残高不整合警告のクリックで、カードの履歴を開いて不整合行をハイライトする。
    /// </summary>
    /// <remarks>
    /// Issue #2007: 導入時残高の誤りなら、導入行（何年も前になり得る）を画面に出すためその日付から表示する。
    /// 当月だけ表示すると直すべき行が期間外で見えない。
    /// Issue #2159: 抽出前はメイン画面の警告クリック処理にあった。全期間の判定（6 年分の読み取り）を 2 度しないよう、
    /// 期間の決定に使った結果をそのままハイライトの判定へ渡す流れごと移している。
    /// </remarks>
    public async Task ShowBalanceInconsistencyAsync(IcCard card)
    {
        var fullPeriodResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
            card.CardIdm, FullPeriodStart, FullPeriodEnd);
        await ShowHistoryAsync(card, fullPeriodResult.InitialBalanceCorrection?.Date);

        // ShowHistoryAsync後に期間が確定するため、ここで整合性チェック＆ハイライト適用
        // CheckAndNotifyConsistencyAsync内で_balanceInconsistenciesの更新とマーキングを行う
        // （全期間の結果は直前に取ったものを渡して再取得しない）
        await CheckAndNotifyConsistencyAsync(fullPeriodResult);
    }

    /// <summary>
    /// 残高整合性チェック＆警告表示
    /// </summary>
    /// <remarks>
    /// 不整合を検出した場合、メイン画面右下の警告エリアに警告を表示します。
    /// 交通系ICカード内の履歴に記録されている残高が正であるため、自動修正は行いません。
    /// </remarks>
    /// <param name="fullPeriodResult">
    /// Issue #2007: 呼び出し元が直前に取った全期間の判定結果。渡されたときは再取得しない
    /// （警告クリック経路は導入行の日付を決めるために全期間を先に読んでいる。6 年分を 2 度読まない）。
    /// </param>
    private async Task CheckAndNotifyConsistencyAsync(ConsistencyResult fullPeriodResult = null)
    {
        if (HistoryCard == null) return;

        var checkResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
            HistoryCard.CardIdm, HistoryFromDate, HistoryToDate);

        // Issue #1739: 警告は「このカードに不整合があるか」を全期間で表す。表示期間だけで
        // 判定して警告を消すと、CheckAllCardsConsistencyAsync が全期間で立てた期間外の不整合が、
        // 警告をクリックして履歴（既定は当月）を開いた瞬間に黙って消える。履歴にハイライトも
        // 出ないため「解消済み」と誤解され、不整合が放置される。
        // 表示期間の結果を流用しないのは、チェーンの起点が範囲によって変わるため
        // 部分範囲の判定が全期間の判定と一致する保証がないから。
        var warningResult = fullPeriodResult ?? await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
            HistoryCard.CardIdm, FullPeriodStart, FullPeriodEnd);

        Host.ReplaceBalanceInconsistencyWarning(
            HistoryCard.CardIdm,
            warningResult.IsConsistent
                ? null
                : BuildBalanceInconsistencyWarning(HistoryCard.CardType, HistoryCard.CardNumber, HistoryCard.CardIdm, warningResult));

        // Issue #1052: ハイライトデータを最新の整合性チェック結果で同期更新
        // （ハイライトは画面に出ている行が対象のため、表示期間の結果を使う）
        // レコード編集・削除後にもハイライトが正しく反映される
        if (_balanceInconsistencies.Count > 0 || !checkResult.IsConsistent)
        {
            // Issue #2007: 導入時残高の誤りなら、切れた側ではなく導入行をハイライト対象にする
            _balanceInconsistencies = BuildInconsistencyMarkers(checkResult);
            ApplyBalanceInconsistencyMarkers();
        }
    }

    /// <summary>
    /// Issue #1058: 全カードの残高整合性をチェックし、不整合があれば警告を表示
    /// </summary>
    /// <remarks>
    /// インポート後など、特定のカード・期間に限定できない場合に使用します。
    /// CheckAndNotifyConsistencyAsyncはHistoryCard・HistoryFromDate/ToDateに依存するため、
    /// 履歴画面が開いていない場合や、インポート対象が表示期間外の場合に対応できません。
    /// </remarks>
    internal async Task CheckAllCardsConsistencyAsync()
    {
        var cards = await _cardRepository.GetAllAsync();

        foreach (var card in cards)
        {
            // Issue #1947: 母集団は「運用中のカード」（IcCard.IsInOperation）。
            // 除去側（RefreshDashboardAsync）は残額ダッシュボードの母集団に居ないカードの
            // BalanceInconsistency 警告を取り除くため、ここで払戻済みカードの警告を立てると
            // 次のダッシュボード更新（貸出・返却／共有モードの定期更新）で黙って消える。
            // 生成側と除去側の判定条件を揃える（.claude/rules/business-logic.md #1739）。
            if (!card.IsInOperation) continue;

            var checkResult = await _ledgerConsistencyChecker.CheckBalanceConsistencyAsync(
                card.CardIdm, FullPeriodStart, FullPeriodEnd);

            Host.ReplaceBalanceInconsistencyWarning(
                card.CardIdm,
                checkResult.IsConsistent
                    ? null
                    : BuildBalanceInconsistencyWarning(card.CardType, card.CardNumber, card.CardIdm, checkResult));
        }

        // 現在表示中のカードのハイライトも更新
        if (HistoryCard != null)
        {
            await CheckAndNotifyConsistencyAsync();
        }
    }

    #endregion
}
