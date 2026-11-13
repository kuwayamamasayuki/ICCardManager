#nullable enable

namespace ICCardManager.Common
{
    /// <summary>
    /// 更新・復元・削除の対象行が見つからなかった（影響行数 0）ときのユーザー向け文言を組み立てる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1759: リポジトリの <c>UpdateAsync</c> / <c>RestoreAsync</c> / <c>DeleteAsync</c> が
    /// <c>false</c> を返すのは、WHERE 句（未削除＝<c>is_deleted</c> が 0 または NULL、または削除済み＝<c>is_deleted = 1</c>）に
    /// <b>1 行も一致しなかった場合だけ</b>である（Issue #1753 で影響行数による競合検出を導入した）。
    /// つまり「原因不明の失敗」ではなく<b>競合の検出</b>であり、原因を具体的に案内できる。
    /// </para>
    /// <para>
    /// 文言をここへ集約するのは、同じ形の分岐が <c>CardManageViewModel</c> と
    /// <c>StaffManageViewModel</c> に計 7 か所あるため。呼び出し側に直接書くと
    /// 「次に対応表を変える人が一部の経路を取りこぼす」
    /// （<c>.claude/rules/error-messages.md</c>「サービス内の『例外 → 文言』の対応表は 1 か所に集約する」）。
    /// </para>
    /// <para>
    /// 「なぜ」を<b>可能性</b>として述べ「他のパソコンで削除されました」と断定しないのは、
    /// ローカルモード（単一 PC）でも同じ分岐に到達し得るため。存在しない相手を探させると
    /// 原因究明が止まる（<c>.claude/rules/error-messages-implementation.md</c>「『影響行数 0』は失敗ではなく競合」の
    /// 「原因を断定する前に、その原因が成立する構成かを確認する」）。
    /// </para>
    /// </remarks>
    public static class ConcurrencyConflictMessage
    {
        /// <summary>
        /// 更新対象の行が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明（例: <c>交通系ICカード「はやかけん H-001」</c>）。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称（例: <c>カード一覧</c>）</param>
        public static string ForUpdate(string target, string listName)
            => Build(target, "更新", DeletedReason, listName);

        /// <summary>
        /// 編集ダイアログで更新しようとした行が見つからなかったときの文言を組み立てる（Issue #2325）。
        /// </summary>
        /// <param name="target">対象の説明（ダイアログを開いた時点の値で名指しする）。「何が」に相当する</param>
        /// <param name="listName">ダイアログを閉じたあとに再読込する一覧の名称</param>
        /// <remarks>
        /// 「なぜ」は <see cref="ForUpdate"/> と同じ。違うのは「どうすれば」で、ダイアログは背後の一覧を
        /// 読み直す手段を持たず、一覧は<b>閉じたあとに呼び出し元が</b>読み直す（<c>LedgerDetailViewModel.NeedsHistoryReload</c>
        /// と同じ形）。<see cref="ForUpdate"/> の「再読み込みしましたので」はその時点では事実にならないため分ける
        /// （#1753「案内するなら、案内する側が先に再読込する」）。同じ行は何度保存しても失敗するので、
        /// 入力が保存されないことも明示する。
        /// </remarks>
        public static string ForUpdateReloadOnClose(string target, string listName)
            => $"{target}を更新できませんでした。{DeletedReason}入力した内容は保存していません。" +
               $"この画面を閉じると{listName}を再読み込みしますので、一覧で状態を確認してからやり直してください。";

        /// <summary>
        /// 編集画面を開こうとした行が見つからなかったときの文言を組み立てる（Issue #2378）。
        /// </summary>
        /// <param name="target">対象の説明（一覧に表示されていた値で名指しする）。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// 「なぜ」は <see cref="ForUpdate"/> と同じ（対象行が削除された）。職員は開こうとしただけで更新は
        /// していないので「何が」を「開けませんでした」と述べる（#1760「操作名が違えばファクトリを分ける」）。
        /// 呼び出し側は<b>一覧を再読込してから</b>本文言を表示すること（#1753）。
        /// </remarks>
        public static string ForOpen(string target, string listName)
            => $"{target}を開けませんでした。{DeletedReason}" +
               $"{listName}を再読み込みしましたので、一覧で状態を確認してからやり直してください。";

        /// <summary>
        /// 編集ダイアログを開いた時点の読み取りで行が見つからなかったときの文言を組み立てる（Issue #2378）。
        /// </summary>
        /// <param name="target">対象の説明（ダイアログを開いた時点の値で名指しする）。「何が」に相当する</param>
        /// <param name="listName">ダイアログを閉じたあとに再読込する一覧の名称</param>
        /// <remarks>
        /// <see cref="ForOpen"/> とは「どうすれば」だけが違う。ダイアログは背後の一覧を読み直す手段を持たず、
        /// 閉じたあとに呼び出し元が読み直す（<see cref="ForUpdateReloadOnClose"/> と同じ形）。
        /// まだ何も入力していないので、入力の扱いには触れない。
        /// </remarks>
        public static string ForOpenReloadOnClose(string target, string listName)
            => $"{target}を開けませんでした。{DeletedReason}" +
               $"この画面を閉じると{listName}を再読み込みしますので、一覧で状態を確認してからやり直してください。";

        /// <summary>
        /// 払い戻し対象の行が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// Issue #1760: 「なぜ」は <see cref="ForUpdate"/> と同じ（対象行が削除された）だが、
        /// <b>「何が」は利用者が実際に行った操作で述べる</b>。払い戻しを試みた職員に
        /// 「更新できませんでした」と出すと、自分の操作と結果が結び付かない。
        /// </remarks>
        public static string ForRefund(string target, string listName)
            => Build(target, "払い戻し", DeletedReason, listName);

        /// <summary>
        /// 払戻済への更新が、行はあるが削除済み・払戻済のため拒否されたときの文言を組み立てる（Issue #2421）。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// 払い戻しの書き込みが 0 行で、事後の診断（<c>CardRepository.DiagnoseFailureAsync</c>）が行を見つけ、
        /// 貸出中でもなかった場合（<c>CardOperationResult.Conflict</c>）。払戻済への更新の WHERE が拒否するのは
        /// 削除済みか払戻済なので、<see cref="ForRefund"/> の「削除された可能性」だけでは他のパソコンで
        /// 先に払い戻された場合を説明しない。
        /// </remarks>
        public static string ForRefundStateChanged(string target, string listName)
            => Build(target, "払い戻し", DeletedOrRefundedReason, listName);

        /// <summary>
        /// 復元対象の行（削除済みの行）が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        public static string ForRestore(string target, string listName)
            => Build(target, "復元", RestoredReason, listName);

        /// <summary>
        /// 登録が書き込めなかった（同じ IDm の行が既にあり、一覧に載っている）ときの文言を組み立てる（Issue #2263 / #2382）。
        /// </summary>
        /// <param name="target">
        /// <b>読み直した既存の行</b>の説明（入力した値ではない）。他のパソコンが別の番号・氏名で先に登録していれば、
        /// 入力した値は一覧のどこにも無く、「一覧で確認して」が実行できない（#2382 のレビューで検出）
        /// </param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// 登録の前に重複を確かめていても、確認から書き込みまでの間（職員証の認証を待つ間など）に他のパソコンが同じ IDm を登録し得る。
        /// リポジトリの <c>InsertAsync</c> は一過性でない SQLite の失敗（重複のほか、ディスク満杯・読み取り専用・破損等）を
        /// すべて <c>false</c> に畳むため、呼び出し側は読み直して行があることを確かめてから、この文言で案内すること
        /// （判断は <c>ViewModels/RegisterFailureNotice</c> に 1 か所）。
        /// </remarks>
        public static string ForRegister(string target, string listName)
            => $"{RegisteredHead(target)}{listName}を再読み込みしましたので、一覧で{target}を確認してからやり直してください。";

        /// <summary>
        /// <see cref="ForRegister"/> と同じ状況だが、案内の前の一覧の再読込にも失敗したときの文言（Issue #2382）。
        /// </summary>
        /// <param name="target">読み直した既存の行の説明</param>
        /// <param name="listName">再読込できなかった一覧の名称</param>
        /// <remarks>
        /// 公開のファクトリは「再読み込みしました」と述べるため、再読込に失敗したときには使えない（#1753）。
        /// 一覧の再読込を前提にする公開のファクトリと形が違うため internal に置く。
        /// </remarks>
        internal static string ForRegisterReloadFailed(string target, string listName)
            => $"{RegisteredHead(target)}{listName}の再読み込みにも失敗したため、" +
               $"画面を閉じて開き直し、一覧で{target}を確認してからやり直してください。";

        /// <summary>
        /// <see cref="ForRestore"/> と同じ状況だが、案内の前の一覧の再読込にも失敗したときの文言（Issue #2382）。
        /// </summary>
        /// <param name="target">復元しようとした行の説明</param>
        /// <param name="listName">再読込できなかった一覧の名称</param>
        /// <remarks>公開のファクトリは「再読み込みしました」と述べるため、再読込に失敗したときには使えない（#1753）。</remarks>
        internal static string ForRestoreReloadFailed(string target, string listName)
            => $"{target}を復元できませんでした。{RestoredReason}{listName}の再読み込みにも失敗したため、" +
               $"画面を閉じて開き直し、一覧で{target}を確認してからやり直してください。";

        /// <summary>
        /// 登録が書き込めなかった原因が、同じ IDm の<b>削除済み</b>の行だったときの文言（Issue #2382）。
        /// </summary>
        /// <param name="target">読み直した既存の（削除済みの）行の説明</param>
        /// <param name="restoreRequiresStaffCardAuthentication">
        /// 復元に職員証の認証が続くか（職員の復元。#2263）。true なら「はい」の後に認証する手順まで述べる
        /// </param>
        /// <remarks>
        /// 削除済みの行は一覧に出ないので、一覧での確認は案内できない。もう一度保存すると、登録前の確認が
        /// 削除済みと判定して復元するかを尋ねる（職員・カードとも同じ流れ）。一覧の再読込を前提にしないため internal に置く。
        /// </remarks>
        internal static string ForRegisterFoundDeleted(string target, bool restoreRequiresStaffCardAuthentication)
            => $"読み取ったIDmは既に{target}として登録され、削除済みになっていたため、登録できませんでした。" +
               "他のパソコンや別の操作で登録・削除された可能性があります。" +
               (restoreRequiresStaffCardAuthentication
                   ? "復元する場合は、もう一度「保存」を押し、表示される確認で「はい」を選択してから、登録済みの職員の職員証で認証してください。"
                   : "復元する場合は、もう一度「保存」を押し、表示される確認で「はい」を選択してください。");

        private static string RegisteredHead(string target)
            => $"読み取ったIDmは既に{target}として登録されていたため、登録できませんでした。{RegisteredReason}";

        /// <summary>
        /// 削除対象の行が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        public static string ForDelete(string target, string listName)
            => Build(target, "削除", DeletedFirstReason, listName);

        /// <summary>
        /// 削除が競合で拒否されたが、原因（先に削除されたか、払い戻されたか）を確かめられなかったときの文言を組み立てる（Issue #2421）。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// 論理削除の WHERE が拒否するのは削除済みか払戻済（貸出中は事後の診断で別に分かる）。原因を確かめる読み直しに
        /// 失敗したとき、<see cref="ForDelete"/> の「先に削除された可能性」とだけ述べると、払い戻されて一覧に「払戻済」で
        /// 残っているカードについて事実でなくなる。<see cref="ForRefundStateChanged"/> と対の形。
        /// </remarks>
        public static string ForDeleteStateChanged(string target, string listName)
            => Build(target, "削除", DeletedOrRefundedReason, listName);

        /// <summary>
        /// 削除しようとした交通系ICカードが払戻済になっていたときの文言を組み立てる（Issue #2370）。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// 払戻済のカードは削除できない（Issue #530）。一覧を読んだ後に他のパソコンや別の操作で払い戻されると、
        /// 画面の CanDelete（一覧の値）では止まらず、削除の直前の読み直しか論理削除の WHERE（<c>is_refunded = 0</c>）で拒否される。
        /// 「どうすれば」は<see cref="ForDelete"/> の「やり直してください」ではなく状態の確認にする。
        /// 払戻済のカードは何度やり直しても削除できないため。公開のファクトリは「やり直してください」で
        /// 終わることを全件で検証している（<c>ConcurrencyConflictMessageTests</c>）ので、形の違う本ファクトリは internal に置く。
        /// </remarks>
        internal static string ForDeleteRefunded(string target, string listName)
            => $"{target}を削除できませんでした。{RefundedReason}" +
               "払戻済の交通系ICカードは削除できません。" +
               $"{listName}を再読み込みしましたので、一覧で払戻済になっていることを確認してください。";

        /// <summary>
        /// 払い戻そうとした交通系ICカードが、書き込む前の読み直しで払戻済になっていたときの文言を組み立てる（Issue #2421）
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// <see cref="ForDeleteRefunded"/> と対。払戻済のカードは何度やり直しても払い戻せないため、「どうすれば」は
        /// 「やり直してください」ではなく状態の確認にする（公開のファクトリと形が違うため internal に置く）。
        /// </remarks>
        internal static string ForRefundRefunded(string target, string listName)
            => $"{target}を払い戻しできませんでした。{RefundedReason}" +
               $"{listName}を再読み込みしましたので、一覧で払戻済になっていることを確認してください。";

        /// <summary>
        /// 書き込まなかったことの案内の前に、一覧の再読込にも失敗したときの文言を組み立てる（Issue #2421）
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="operationName">利用者が行った操作の名前（「削除」「払い戻し」）</param>
        /// <param name="reason">再読込できたときの文言と同じ「なぜ」（このクラスの理由文など）</param>
        /// <param name="listName">再読込できなかった一覧の名称</param>
        /// <remarks>
        /// 公開のファクトリは「再読み込みしました」と述べるため、再読込に失敗したときには使えない（#1753）。
        /// 形は <see cref="ForRestoreReloadFailed"/> と同じ。
        /// </remarks>
        internal static string ForReloadFailed(string target, string operationName, string reason, string listName)
            => $"{target}を{operationName}できませんでした。{reason}{listName}の再読み込みにも失敗したため、" +
               $"画面を閉じて開き直し、一覧で{target}の状態を確認してください。";

        /// <summary>
        /// CSV インポートで、更新しようとした行が無くなっていたときの文言（Issue #2262）。
        /// </summary>
        /// <param name="target">対象の説明（CSV の行の内容で名指しする。例: <c>職員「山田 太郎」</c>）</param>
        /// <param name="screenName">状態を確かめる画面（例: <c>職員管理画面（F2）</c>）</param>
        /// <remarks>
        /// 「なぜ」は <see cref="ForUpdate"/> と同じ。取り込みでは一覧を再読込しない（画面に一覧が無い）ため、
        /// 公開のファクトリの「一覧を再読み込みしました」は事実にならない。取り込みは全件を巻き戻すので、
        /// 「どうすれば」は状態を確かめて CSV を直し、あらためてプレビューから取り込むこと。
        /// 一覧の再読込を前提にする公開のファクトリと形が違うため internal に置く（全ファクトリの品質検証は
        /// 公開のファクトリを (target, listName) で呼ぶ）。
        /// </remarks>
        internal static string ForImportUpdate(string target, string screenName)
            => BuildForImport(target, "更新", DeletedReason, screenName);

        /// <summary>
        /// CSV インポートで、復元しようとした削除済みの行が削除済みでなくなっていたときの文言（Issue #2262）。
        /// </summary>
        /// <param name="target">対象の説明（CSV の行の内容で名指しする）</param>
        /// <param name="screenName">状態を確かめる画面</param>
        internal static string ForImportRestore(string target, string screenName)
            => BuildForImport(target, "復元", RestoredReason, screenName);

        /// <summary>
        /// 職員証の差し替え（Issue #2461）の対象の職員が見つからなかった（競合）
        /// </summary>
        /// <param name="target">対象の職員（一覧に載っていた表記で名指しする）</param>
        /// <param name="listName">再読み込みした一覧の名前</param>
        /// <remarks>
        /// 差し替えは職員の主キー（職員証の IDm）を変えるため、対象が見つからない原因は削除のほかに
        /// 「他のパソコンで先に職員証が差し替えられた」もある。
        /// </remarks>
        public static string ForReplaceStaffCard(string target, string listName)
            => $"{target}の職員証を差し替えできませんでした。{DeletedOrCardReplacedReason}職員証は差し替えていません。" +
               $"{listName}を再読み込みしましたので、一覧で状態を確認してからやり直してください。";

        /// <summary>対象の職員が削除された、または職員証が差し替えられた（職員証の差し替えの「なぜ」。Issue #2461）</summary>
        private const string DeletedOrCardReplacedReason = "他のパソコンや別の操作で削除された、または先に職員証が差し替えられた可能性があります。";

        /// <summary>対象の行が削除されていた（更新・払い戻しの「なぜ」）</summary>
        internal const string DeletedReason = "他のパソコンや別の操作で削除された可能性があります。";

        /// <summary>対象の行が削除された、または払い戻された（状態の変わった削除・払い戻しの「なぜ」。Issue #2421）</summary>
        internal const string DeletedOrRefundedReason = "他のパソコンや別の操作で削除された、または払い戻された可能性があります。";

        /// <summary>対象の行が先に削除されていた（削除の「なぜ」）</summary>
        internal const string DeletedFirstReason = "他のパソコンや別の操作で先に削除された可能性があります。";

        /// <summary>対象の行が払い戻されていた（払戻済のため削除・払い戻しできなかったときの「なぜ」。Issue #2421）</summary>
        internal const string RefundedReason = "他のパソコンや別の操作で払い戻された可能性があります。";

        /// <summary>同じ識別子の行が先に登録されていた（登録の「なぜ」）</summary>
        private const string RegisteredReason = "他のパソコンや別の操作で先に登録された可能性があります。";

        /// <summary>削除済みの行が先に復元されていた（復元の「なぜ」）</summary>
        private const string RestoredReason = "他のパソコンや別の操作で先に復元された可能性があります。";

        private static string BuildForImport(string target, string operationName, string reason, string screenName)
            => $"{target}を{operationName}できませんでした。{reason}取り込みは行っていません。" +
               $"{screenName}で状態を確認してCSVファイルを直し、あらためてプレビューしてからインポートしてください。";

        /// <summary>
        /// 「何が」「なぜ」「どうすれば」の 3 要素を組み立てる。
        /// </summary>
        /// <remarks>
        /// 「どうすれば」で一覧の再読込済みを伝えるため、呼び出し側は<b>必ず一覧を再読込してから</b>
        /// 本文言を設定すること。再読込しないと文言が案内する操作を実行できず、
        /// 同じエラーを繰り返す（Issue #1753）。
        /// </remarks>
        private static string Build(string target, string operationName, string reason, string listName)
            => $"{target}を{operationName}できませんでした。{reason}" +
               $"{listName}を再読み込みしましたので、一覧で状態を確認してからやり直してください。";
    }
}
