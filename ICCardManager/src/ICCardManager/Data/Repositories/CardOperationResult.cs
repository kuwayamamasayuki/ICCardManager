namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// カード操作（削除・払戻等）の結果
    /// </summary>
    /// <remarks>
    /// Issue #1109: bool戻り値では失敗理由が区別できないため、
    /// 結果型を導入して呼び出し元で適切なエラーメッセージを表示可能にする。
    /// </remarks>
    public enum CardOperationResult
    {
        /// <summary>
        /// 操作成功
        /// </summary>
        Success,

        /// <summary>
        /// カードが見つからない（未登録または削除済み）
        /// </summary>
        NotFound,

        /// <summary>
        /// カードが貸出中のため操作不可
        /// </summary>
        CardIsLent,

        /// <summary>
        /// 他のPCで状態が変更されたため操作が競合した
        /// （キャッシュの状態とDBの実際の状態が異なる）
        /// </summary>
        Conflict
    }
}
