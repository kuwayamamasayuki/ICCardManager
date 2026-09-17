using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Services
{
    /// <summary>
    /// 帳票の対象の交通系ICカードが DB に見つからないときの案内文言（Issue #2049 / #2066）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 帳票作成（<see cref="ReportService"/>）と印刷プレビュー（<c>ReportViewModel</c>）は原因が同じなので、
    /// 文言と判断を 1 か所に置く（#1763「同じ判断を配らない」）。
    /// </para>
    /// <para>
    /// どのファクトリも IDm を受け取らない。職員は 16 桁の IDm から対象を特定できず、生の IDm を文言へ出すことは
    /// #1852 / #1986 で禁じている。規約ではなく引数の形で露出を防ぐ。カードは管理番号を含む表示名で名指しする。
    /// </para>
    /// </remarks>
    internal static class ReportCardNotFoundMessage
    {
        /// <summary>
        /// 見出し（1 枚分）。一括作成の失敗一覧・プレビューのステータス欄のように幅や表示項目が限られる場所で使う
        /// </summary>
        public const string Headline = "交通系ICカードが登録されていません。帳票作成画面を開き直してください";

        /// <summary>
        /// 詳細（何が／なぜ／どうすれば）
        /// </summary>
        public const string Detail =
            "対象の交通系ICカードがデータベースに見つかりません。" +
            "画面に表示中の一覧が、データベースの登録内容と食い違っている可能性があります。" +
            "帳票作成画面を開き直し、一覧から対象の交通系ICカードを選び直してください。";

        /// <summary>
        /// 複数枚のプレビューで、選んだカードが 1 枚も見つからなかったときのステータス欄の文言（Issue #2066）
        /// </summary>
        /// <param name="missingCount">見つからなかった枚数（＝選んだ枚数）</param>
        public static string ForAllMissingPreview(int missingCount)
            => $"選択した交通系ICカード{missingCount}件が登録されていません。帳票作成画面を開き直してください";

        /// <summary>
        /// 複数枚のプレビューで一部のカードが見つからなかったとき、残りでプレビューを開くかを尋ねる文言（Issue #2066）
        /// </summary>
        /// <remarks>
        /// 黙って除外すると、職員は抜けたことに気付かないまま印刷し得る（物品出納簿が 1 枚欠ける）。
        /// 抜けたカードを名指しし、残りで続けるかを職員に委ねる。
        /// </remarks>
        /// <param name="missingCardNames">見つからなかったカードの表示名（一覧の並び順）</param>
        /// <param name="foundCount">プレビューに含められる枚数</param>
        public static string ForPartialPreviewConfirmation(IReadOnlyList<string> missingCardNames, int foundCount)
            => "次の交通系ICカードは登録されていないため、プレビューに含められません。\n\n" +
               string.Join("\n", missingCardNames.Select(n => $"・{n}")) + "\n\n" +
               "画面に表示中の一覧が、データベースの登録内容と食い違っている可能性があります。" +
               "すべてを印刷するには、帳票作成画面を開き直して選び直してください。\n\n" +
               $"残りの{foundCount}件だけでプレビューを表示しますか？";

        /// <summary>
        /// 一部が見つからず、確認で「いいえ」を選んでプレビューを中止したときのステータス欄の文言（Issue #2066）
        /// </summary>
        /// <remarks>
        /// 確認ダイアログは閉じているため、名指ししたカードを画面に残す。
        /// </remarks>
        public static string ForPartialPreviewCancelledStatus(IReadOnlyList<string> missingCardNames)
            => $"未登録の交通系ICカード（{string.Join("、", missingCardNames)}）があるため、プレビューを中止しました。" +
               "帳票作成画面を開き直してください";

        /// <summary>
        /// 一部を除いてプレビューを開いたときにステータス欄へ残す文言（Issue #2066）
        /// </summary>
        /// <remarks>
        /// プレビューを閉じた後にも、除外したカードがあったことを画面に残す。
        /// </remarks>
        public static string ForPartialPreviewStatus(IReadOnlyList<string> missingCardNames)
            => $"未登録の交通系ICカード（{string.Join("、", missingCardNames)}）を除いてプレビューしました。" +
               "すべてを印刷するには帳票作成画面を開き直してください";
    }
}
