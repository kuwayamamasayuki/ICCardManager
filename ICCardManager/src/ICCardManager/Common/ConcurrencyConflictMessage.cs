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
            => Build(target, "更新", "他のパソコンや別の操作で削除された可能性があります。", listName);

        /// <summary>
        /// 復元対象の行（削除済みの行）が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        public static string ForRestore(string target, string listName)
            => Build(target, "復元", "他のパソコンや別の操作で先に復元された可能性があります。", listName);

        /// <summary>
        /// 削除対象の行が見つからなかったときの文言を組み立てる。
        /// </summary>
        /// <param name="target">対象の説明。「何が」に相当する</param>
        /// <param name="listName">再読込した一覧の名称</param>
        public static string ForDelete(string target, string listName)
            => Build(target, "削除", "他のパソコンや別の操作で先に削除された可能性があります。", listName);

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
