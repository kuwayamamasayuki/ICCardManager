#nullable enable

namespace ICCardManager.Common
{
    /// <summary>
    /// 更新・復元・削除の対象行が見つからなかった（影響行数 0）ときのユーザー向け文言を組み立てる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1759: リポジトリの <c>UpdateAsync</c> / <c>RestoreAsync</c> / <c>DeleteAsync</c> が
    /// <c>false</c> を返すのは、WHERE 句（<c>is_deleted = 0</c> または <c>is_deleted = 1</c>）に
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
    /// 原因究明が止まる（<c>.claude/rules/development-conventions.md</c>
    /// 「エラー文言で原因を断定する前に、その原因が成立する構成かを確認する」）。
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
        /// 復元対象の行（削除済みの行）が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        public static string ForRestore(string target, string listName)
            => Build(target, "復元", RestoredReason, listName);

        /// <summary>
        /// 登録が書き込めなかった（同じ識別子の行が既にある）ときの文言を組み立てる（Issue #2263）。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        /// <remarks>
        /// 登録の前に重複を確かめていても、職員証の認証を待つ間に他のパソコンが同じ職員証を登録し得る。
        /// リポジトリの <c>InsertAsync</c> は一過性でない SQLite の失敗（重複のほか、ディスク満杯・読み取り専用・破損等）を
        /// すべて <c>false</c> に畳むため、呼び出し側は読み直して行があることを確かめてから、この文言で案内すること。
        /// </remarks>
        public static string ForRegister(string target, string listName)
            => Build(target, "登録", "他のパソコンや別の操作で先に登録された可能性があります。", listName);

        /// <summary>
        /// 削除対象の行が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        public static string ForDelete(string target, string listName)
            => Build(target, "削除", "他のパソコンや別の操作で先に削除された可能性があります。", listName);

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

        /// <summary>対象の行が削除されていた（更新・払い戻しの「なぜ」）</summary>
        private const string DeletedReason = "他のパソコンや別の操作で削除された可能性があります。";

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
