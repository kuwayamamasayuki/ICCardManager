using System;
using System.Collections.Generic;
using System.Linq;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// 帳票ファイル名の衝突（別々のカードが同じ年度ファイルへ書かれること）を判定する（Issue #2154）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 事前チェック（<see cref="ReportPreflightChecker"/>）・一括作成（<c>ReportViewModel</c>）・
    /// 出力状況（<see cref="ReportExportStatusService"/>）はいずれもこのメソッドで判定する。
    /// 「何を同じファイルとみなすか」（<see cref="ReportFileNameFactory.FileNameComparer"/>）と
    /// 「どのカードと比べるか」を 3 か所へ書き写すと、1 か所だけ取り残される（#1763）。
    /// </para>
    /// <para>
    /// <b>比べる相手は作成対象のカードだけでは足りない。</b> 年度ファイルは 1 年度ぶんの月シートを
    /// 積み上げるため、今回選んでいないカードの年度ファイルも、名前が同じなら上書きの対象になる
    /// （4 月に <c>h001</c> で作った年度ファイルへ、5 月に <c>H001</c> の 5 月シートが追加される）。
    /// 呼び出し元は帳票作成画面に並ぶ全カードを母集団として渡す。
    /// </para>
    /// </remarks>
    public static class ReportFileNameCollisions
    {
        /// <summary>
        /// 母集団の中で、他のカードと同じ年度ファイル名になるカードを返す
        /// </summary>
        /// <param name="cards">
        /// 判定の母集団（同じ IDm が複数あっても 1 枚として扱う）。呼び出し元は払戻済みを含む未削除の全カードを渡し、
        /// 絞り込みはこのメソッドに任せる（<see cref="MayHaveFiscalYearFile"/>）
        /// </param>
        /// <param name="fiscalYear">年度（<paramref name="fileNameOf"/> に束縛したものと同じ値）</param>
        /// <param name="fileNameOf">
        /// カード種別と管理番号から年度ファイル名を得る関数。帳票の生成側と同じもの
        /// （<see cref="IReportFileNameFactory.GetFiscalYearFileName"/> に年度を束縛したもの）を渡す
        /// </param>
        /// <param name="targetIdms">
        /// 今回帳票を作るカードの IDm（省略可）。これらは払戻日にかかわらず母集団に含める
        /// （作る以上、その年度ファイルへ書き込むため）
        /// </param>
        /// <returns>
        /// 衝突するカードの IDm → 同じファイル名になる他のカード（入力順）。衝突しないカードは含まない
        /// </returns>
        public static IReadOnlyDictionary<string, IReadOnlyList<ReportExportTarget>> Find(
            IEnumerable<ReportExportTarget> cards,
            int fiscalYear,
            Func<string, string, string> fileNameOf,
            IEnumerable<string> targetIdms = null)
        {
            if (fileNameOf == null)
            {
                throw new ArgumentNullException(nameof(fileNameOf));
            }

            var result = new Dictionary<string, IReadOnlyList<ReportExportTarget>>();
            if (cards == null)
            {
                return result;
            }

            var targets = new HashSet<string>(targetIdms ?? Enumerable.Empty<string>());

            // 同じカードが母集団に二重に入っていても、自分自身との衝突として数えない
            var distinctCards = cards
                .Where(c => c != null && !string.IsNullOrEmpty(c.CardIdm))
                .GroupBy(c => c.CardIdm)
                .Select(g => g.First())
                .Where(c => targets.Contains(c.CardIdm) || MayHaveFiscalYearFile(c, fiscalYear))
                .ToList();

            var groups = ReportFileNameFactory.FindCollidingGroups(
                distinctCards,
                c => fileNameOf(c.CardType, c.CardNumber));

            foreach (var group in groups)
            {
                foreach (var card in group)
                {
                    result[card.CardIdm] = group.Where(other => other.CardIdm != card.CardIdm).ToList();
                }
            }

            return result;
        }

        /// <summary>
        /// そのカードが、指定した年度の年度ファイルを持ち得るかを判定する（Issue #2154）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 払戻日が年度の初日（4 月 1 日）より前のカードは、その年度に利用も帳票も無い。母集団に残すと、
        /// 何年も前に払い戻したカードのために、稼働中のカードが毎月「作成しませんでした」となり、
        /// 物理ラベルと結び付いた管理番号の変更を強いられる。
        /// </para>
        /// <para>
        /// 払戻済みなのに払戻日が無いカード（払戻日を記録する前のデータ）は、持ち得る側へ倒す。
        /// 取りこぼすと上書きが黙って起きるが、残すと警告が出るだけで済む。
        /// </para>
        /// <para>
        /// 論理削除したカードはそもそも母集団に入らない（呼び出し元の一覧は未削除のカードだけ）。
        /// </para>
        /// </remarks>
        internal static bool MayHaveFiscalYearFile(ReportExportTarget card, int fiscalYear)
        {
            if (!card.IsRefunded || !card.RefundedAt.HasValue)
            {
                return true;
            }

            return card.RefundedAt.Value >= new DateTime(fiscalYear, 4, 1);
        }

        /// <summary>
        /// 衝突の原因を「なぜ」の一文で述べる（Issue #2154）
        /// </summary>
        /// <remarks>
        /// 相手がすべて英字の大文字・小文字の違いだけなら、そう名指しする。それ以外（記号の置き換え、
        /// 書式による連結の一致、両方の混在）は記号の置き換えを主として述べる。原因を 1 つに絞らずに
        /// 両方を並べると、職員はどちらを直せばよいのか分からない。
        /// </remarks>
        /// <param name="card">衝突したカード</param>
        /// <param name="others">衝突相手</param>
        public static string DescribeCause(ReportExportTarget card, IEnumerable<ReportExportTarget> others)
        {
            var caseOnly = (others ?? Enumerable.Empty<ReportExportTarget>()).All(o =>
                string.Equals(o.CardType, card.CardType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(o.CardNumber, card.CardNumber, StringComparison.OrdinalIgnoreCase));

            return caseOnly
                ? "管理番号が英字の大文字と小文字だけ違い、Windows はファイル名の大文字と小文字を区別しないため"
                : "ファイル名に使えない記号は「_」に置き換わるため";
        }

        /// <summary>
        /// 衝突相手のカード名を「「はやかけん H001」、「はやかけん h001」」の形で列挙する
        /// </summary>
        /// <param name="others">衝突相手（<see cref="Find"/> の値）</param>
        public static string FormatCardNames(IEnumerable<ReportExportTarget> others)
        {
            return string.Join("、", (others ?? Enumerable.Empty<ReportExportTarget>())
                .Select(c => $"「{c.DisplayName}」"));
        }
    }
}
