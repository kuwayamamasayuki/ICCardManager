namespace ICCardManager.Common
{
    /// <summary>
    /// 交通系ICカードの登録直後に台帳（ledger）への書き込みが失敗したときの、
    /// ユーザー向け文言（ダイアログ本文・タイトル・ステータス欄）を組み立てる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// カード行の INSERT と操作ログは既にコミットされているため、この失敗は
    /// 「登録が失敗した」ではなく「登録は成立したが台帳の受入行が入らなかった」である。
    /// 文言は必ずこの区別を保つこと。登録失敗と誤解した職員は再登録を試み、
    /// 「既に登録されています」に突き当たる（Issue #1727）。
    /// </para>
    /// <para>
    /// Issue #1763: 同じ形の分岐が <c>CardManageViewModel.SaveAsync</c> に 2 つある
    /// （カード内に取り込む履歴が<b>ある</b>経路＝#1727／<b>ない</b>経路＝#1763）。
    /// 呼び出し側に直接書くと「次に対応表を変える人が一部の経路を取りこぼす」
    /// （<c>.claude/rules/error-messages.md</c>「サービス内の『例外 → 文言』の対応表は
    /// 1 か所に集約する」）。ダイアログ本文・タイトル・ステータス欄を 1 つのオブジェクトに
    /// まとめているのは、表示先ごとに別々の場所へ書くと片方だけ更新されて食い違うため。
    /// </para>
    /// <para>
    /// 「どうすれば」を経路ごとに変えているのは、取れる行動が違うため
    /// （<c>.claude/rules/error-messages.md</c>「文言を 1 か所へ集約しても、『どうすれば』は
    /// 経路によって変わり得る」）。履歴がある経路では CSV インポートで利用履歴ごと取り込めるが、
    /// 履歴が無い経路で失われるのは初期残高行 1 行だけであり、
    /// 取り込むべき利用履歴が存在しない以上 CSV インポートは実行できない指示になる。
    /// </para>
    /// <para>
    /// 「なぜ」（<c>reason</c>）は <c>LendingService.GetHistoryImportFailureReason</c> が組み立てる。
    /// 生の <c>Exception.Message</c> は含めない（Issue #1614）。
    /// </para>
    /// </remarks>
    public sealed class RegistrationLedgerFailureMessage
    {
        /// <summary>
        /// 失敗理由が得られなかった場合に用いる「なぜ」。
        /// </summary>
        /// <remarks>
        /// <c>HistoryImportResult.FailureReason</c> が空で返ることは想定していないが、
        /// 空文字のまま本文へ埋め込むと「ただし、〜できませんでした。」の直後が
        /// 途切れて 3 要素の「なぜ」を欠く。
        /// </remarks>
        private const string UnknownReason = "データベースへの書き込み中に問題が発生しました。";

        private RegistrationLedgerFailureMessage(string dialogTitle, string dialogMessage, string statusMessage)
        {
            DialogTitle = dialogTitle;
            DialogMessage = dialogMessage;
            StatusMessage = statusMessage;
        }

        /// <summary>
        /// エラーダイアログのタイトル
        /// </summary>
        public string DialogTitle { get; }

        /// <summary>
        /// エラーダイアログの本文（「何が」「なぜ」「どうすれば」の 3 要素）
        /// </summary>
        public string DialogMessage { get; }

        /// <summary>
        /// カード管理画面のステータス欄に表示する短縮版
        /// </summary>
        public string StatusMessage { get; }

        /// <summary>
        /// カード内の利用履歴と初期残高行をまとめて取り込む経路の失敗（Issue #1727）。
        /// </summary>
        /// <param name="cardNumber">登録した交通系ICカードの管理番号。「何が」の特定に使う</param>
        /// <param name="reason">失敗の「なぜ」（<c>LendingService.GetHistoryImportFailureReason</c> の戻り値）</param>
        public static RegistrationLedgerFailureMessage ForHistoryImport(string cardNumber, string reason)
            => new(
                "利用履歴の取込に失敗",
                Build(
                    cardNumber,
                    what: "カード内の利用履歴を台帳に取り込めませんでした。",
                    reason: reason,
                    consequence:
                        "取込は取り消されたため、この交通系ICカードの台帳には利用履歴の行も" +
                        "登録時の残高の行も記録されていません。このままでは月次帳票（物品出納簿）の" +
                        "残額が実際のカード残高と一致しません。",
                    howTo:
                        "履歴画面のCSVインポートで利用履歴を取り込むか、" +
                        "履歴画面から残高の行を手動で追加してください。"),
                "カードは登録しましたが利用履歴を取り込めませんでした。" +
                "履歴画面のCSVインポートで補完してください。");

        /// <summary>
        /// カード内に取り込む履歴が無く、初期残高行だけを登録する経路の失敗（Issue #1763）。
        /// </summary>
        /// <param name="cardNumber">登録した交通系ICカードの管理番号。「何が」の特定に使う</param>
        /// <param name="reason">失敗の「なぜ」（<c>LendingService.GetHistoryImportFailureReason</c> の戻り値）</param>
        /// <remarks>
        /// ここで失われるのは「新規購入」または「○月から繰越」＝<b>そのカード唯一の受入行</b>で、
        /// 台帳が 0 行のまま払出だけが積み上がる。影響は「残額が合わない」に留まらず、
        /// 年度を通して「受入 − 払出 = 残額」が成立しなくなるため、文言でもそこまで述べる。
        /// </remarks>
        public static RegistrationLedgerFailureMessage ForInitialBalance(string cardNumber, string reason)
            => new(
                "登録時の残高の記録に失敗",
                Build(
                    cardNumber,
                    what: "登録時の残高を台帳に記録できませんでした。",
                    reason: reason,
                    consequence:
                        "この交通系ICカードの台帳には受入の行が1行も記録されていません。" +
                        "このままでは月次帳票（物品出納簿）で「受入 − 払出 = 残額」が" +
                        "年度を通して成立しません。",
                    howTo:
                        "履歴画面から登録時の残高の行を手動で追加してください。"),
                "カードは登録しましたが登録時の残高を記録できませんでした。" +
                "履歴画面から残高の行を追加してください。");

        /// <summary>
        /// 「何が」「なぜ」「どうすれば」の 3 要素を組み立てる。
        /// </summary>
        /// <remarks>
        /// 冒頭で「登録は完了しました」と述べるのは、カード行と操作ログが既にコミット済みだから。
        /// 呼び出し側は<b>成功時と同じ後処理（一覧の再読込・編集モードの終了）を済ませてから</b>
        /// 本文言を表示すること（Issue #1727）。
        /// </remarks>
        private static string Build(string cardNumber, string what, string reason, string consequence, string howTo)
            => $"交通系ICカード（管理番号 {cardNumber}）の登録は完了しました。\n\n" +
               $"ただし、{what}{NormalizeReason(reason)}\n\n" +
               $"{consequence}\n\n" +
               howTo;

        private static string NormalizeReason(string reason)
            => string.IsNullOrWhiteSpace(reason) ? UnknownReason : reason;
    }
}
