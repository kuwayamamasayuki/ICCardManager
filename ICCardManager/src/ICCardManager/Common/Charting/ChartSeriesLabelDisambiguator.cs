using System;
using System.Collections.Generic;

namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// 同一の表示名を持つ系列に識別情報を添えて一意にする純粋関数（Issue #1886）。
    /// </summary>
    /// <remarks>
    /// 集約系列の表示名を組み立てる <see cref="ChartSeriesNameFormatter"/> とは関心が別。
    /// あちらは「上位以外の合算である」という事実を 1 系列のラベルへ埋め込み、
    /// こちらは<b>系列どうしの関係</b>（同名になっているか）からラベルを決める。
    /// </remarks>
    internal static class ChartSeriesLabelDisambiguator
    {
        /// <summary>
        /// 同一の表示名を持つ系列を、識別情報を添えて一意にする（Issue #1886）。
        /// </summary>
        /// <param name="sources">系列ごとの基底表示名と識別情報。表示順に並んでいること</param>
        /// <returns><paramref name="sources"/> と同じ並び・同じ件数の表示名</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="sources"/> または要素が null のとき。
        /// </exception>
        /// <remarks>
        /// 凡例・代替一覧・Excel が表示するのは名前の文字列だけであり、系列を内部で区別できる
        /// キー（IDm・氏名）は利用者には届かない。とくに代替一覧は色以外のチャネルとして
        /// 用意されている（Issue #1856）ためスウォッチが無く、<b>ラベルが唯一の判別手段</b>になる。
        /// 同姓同名の職員 2 名や、職員名が空の行が潰れた「（職員名なし）」が複数あると、
        /// そこで完全に判別不能になる。
        /// <para>
        /// 修飾は<b>衝突したときだけ</b>行う。常に職員番号を添えると、同姓同名が居ない通常運用で
        /// ラベルが長くなるだけで情報量は増えない（Issue #1858 が集約系列に人数を添えたのと同じ判断で、
        /// 「区別が必要な場面でだけ区別のための情報を足す」）。
        /// </para>
        /// <para>
        /// 2 段構えなのは、識別情報を持たない系列が存在するため。<c>lender_idm</c> を持たない
        /// 過去のインポート行は氏名でバケット化され、職員マスタを引けないので職員番号が無い。
        /// 職員番号だけでは一意にならない組が残り得るので、残った重複には通し番号を添えて
        /// <b>必ず一意にする</b>。通し番号は表示順（＝利用額の降順）に依存するため期間を変えると
        /// 入れ替わり得るが、これは識別情報がそもそも無い縮退ケースであり、
        /// 「同じラベルが 2 行並ぶ」よりは判別できる方が望ましい。
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> DisambiguateDuplicateNames(
            IReadOnlyList<ChartSeriesLabelSource> sources)
        {
            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            var baseNames = new string[sources.Count];
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] == null)
                {
                    throw new ArgumentNullException(
                        nameof(sources),
                        "系列ラベルの入力に null が含まれています。");
                }

                baseNames[i] = sources[i].BaseName ?? string.Empty;
            }

            // 第 1 段: 基底名が衝突している系列にだけ職員番号を添える。
            var baseCounts = CountOccurrences(baseNames);
            var qualified = new string[sources.Count];
            for (var i = 0; i < sources.Count; i++)
            {
                var qualifier = sources[i].Qualifier;
                qualified[i] = baseCounts[baseNames[i]] > 1 && !string.IsNullOrWhiteSpace(qualifier)
                    ? $"{baseNames[i]}（職員番号 {qualifier.Trim()}）"
                    : baseNames[i];
            }

            // 第 2 段: 職員番号を持たない（または職員番号まで同じ）系列に通し番号を添える。
            // 衝突した組は全員に添える。片方だけ「（2 人目）」にすると、
            // 修飾の無い側が「1 人目」なのか無関係な系列なのか読み取れない。
            var qualifiedCounts = CountOccurrences(qualified);
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            var result = new string[sources.Count];
            for (var i = 0; i < sources.Count; i++)
            {
                if (qualifiedCounts[qualified[i]] <= 1)
                {
                    result[i] = qualified[i];
                    continue;
                }

                ordinals.TryGetValue(qualified[i], out var used);
                ordinals[qualified[i]] = used + 1;

                // Issue #1885: 件数の整形は金額ラベルと同じ ChartNumberFormat に委ねる。
                result[i] = $"{qualified[i]}（{ChartNumberFormat.FormatInteger(used + 1)} 人目）";
            }

            return result;
        }

        private static Dictionary<string, int> CountOccurrences(IEnumerable<string> values)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                counts.TryGetValue(value, out var count);
                counts[value] = count + 1;
            }

            return counts;
        }
    }
}
