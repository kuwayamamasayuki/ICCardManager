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
        /// <param name="cards">判定の母集団（同じ IDm が複数あっても 1 枚として扱う）</param>
        /// <param name="fileNameOf">
        /// カード種別と管理番号から年度ファイル名を得る関数。帳票の生成側と同じもの
        /// （<see cref="IReportFileNameFactory.GetFiscalYearFileName"/> に年度を束縛したもの）を渡す
        /// </param>
        /// <returns>
        /// 衝突するカードの IDm → 同じファイル名になる他のカード（入力順）。衝突しないカードは含まない
        /// </returns>
        public static IReadOnlyDictionary<string, IReadOnlyList<ReportExportTarget>> Find(
            IEnumerable<ReportExportTarget> cards,
            Func<string, string, string> fileNameOf)
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

            // 同じカードが母集団に二重に入っていても、自分自身との衝突として数えない
            var distinctCards = cards
                .Where(c => c != null && !string.IsNullOrEmpty(c.CardIdm))
                .GroupBy(c => c.CardIdm)
                .Select(g => g.First())
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
