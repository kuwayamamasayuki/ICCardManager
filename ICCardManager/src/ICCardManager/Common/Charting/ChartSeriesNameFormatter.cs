namespace ICCardManager.Common.Charting
{
    /// <summary>
    /// グラフの系列名を組み立てる純粋関数群（Issue #1858）。
    /// </summary>
    /// <remarks>
    /// 上位以外を集約した系列は、集約であることを<b>ラベル自体</b>に含める。
    /// <see cref="Dtos.MonthlyUsageSeries.IsOther"/> は内部的に集約系列を区別できるが、
    /// 凡例・代替一覧・Excel が表示するのは名前の文字列だけなので、フラグの区別は
    /// 利用者には届かない。氏名が「その他」の職員（職員マスタに無い <c>ledger.staff_name</c> を
    /// そのまま系列名に使う経路がある）が上位に入ると、凡例に「その他」が 2 行並び、
    /// どちらが集約分か判別できなくなる。
    /// <para>
    /// 組み立ては本クラス 1 か所に置き、画面（凡例・代替一覧）と Excel 出力の双方が
    /// 同じ結果を使う。消費側それぞれが接尾辞を付ける形にすると、片方だけ変わる日が来る。
    /// </para>
    /// </remarks>
    internal static class ChartSeriesNameFormatter
    {
        /// <summary>
        /// 集約系列の基底の表示名（人数を添えない形）。
        /// </summary>
        /// <remarks>
        /// Issue #1884: この値は<b>どの系列の <c>Name</c> とも一致しない</b>。本番は必ず
        /// <see cref="BuildOtherSeriesName(int)"/> を 1 以上の件数で呼ぶため、集約系列の名前は
        /// 常に「その他（N 名）」になる。外部へ公開すると <c>series.Name == OtherSeriesBaseName</c>
        /// のような<b>静かに常に偽になる判定</b>を書けてしまい、次に読む人へ誤った契約を提示する
        /// （Issue #1858 が <c>AdminDashboardService.OtherSeriesName</c> を削除したのと同じ理由）。
        /// 書式の組み立て以外の用途を持たせないため <c>private</c> に閉じる。
        /// </remarks>
        private const string OtherSeriesBaseName = "その他";

        /// <summary>
        /// 上位以外を集約した系列の表示名を組み立てる。
        /// </summary>
        /// <param name="aggregatedCount">集約された系列（職員）の数。1 以上であること</param>
        /// <returns>「その他（N 名）」</returns>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="aggregatedCount"/> が 0 以下のとき。
        /// </exception>
        /// <remarks>
        /// 人数を添えるのは職員名との衝突を避けるためだが、同時に利用者にとっての情報量も増える。
        /// 「その他」だけでは何人分の合算なのか分からない。
        /// <para>
        /// Issue #1882: 0 以下は集約が起きていない状態で、この関数が返せる正しい表示名は無い。
        /// 基底名「その他」へ倒すと <b>Issue #1858 が消したはずの衝突ラベルそのもの</b>が復活し、
        /// 氏名が「その他」の職員の系列と凡例・代替一覧・Excel で判別できなくなる。
        /// 定義域外は黙って別の値へ丸めず、呼び出し側の誤りとして弾く。
        /// 唯一の呼び出し元（<c>AdminDashboardService.BuildUsageSeries</c>）は
        /// 系列数が上限を超えたときだけこの分岐へ入るため、必ず 1 以上を渡す。
        /// </para>
        /// </remarks>
        internal static string BuildOtherSeriesName(int aggregatedCount)
        {
            if (aggregatedCount <= 0)
            {
                throw new System.ArgumentOutOfRangeException(
                    nameof(aggregatedCount),
                    aggregatedCount,
                    "集約系列名には 1 以上の件数を渡してください。0 以下では集約が起きておらず、"
                        + "表示名を組み立てられません。");
            }

            // Issue #1885: 件数の整形は金額ラベル（ChartScale）と同じ ChartNumberFormat に委ねる。
            // 同一グラフ上の 2 種類のラベルが別の数値書式規則に従わないようにするため。
            return $"{OtherSeriesBaseName}（{ChartNumberFormat.FormatInteger(aggregatedCount)} 名）";
        }
    }
}
