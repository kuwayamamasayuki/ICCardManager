namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 処理中（保存・取込・リストア等の待機中）かどうかを公開する画面の状態（Issue #2141）
    /// </summary>
    /// <remarks>
    /// 処理中は利用者の操作でダイアログを閉じさせない判定（<c>Views.Helpers.BusyCloseGuard</c>）が参照する。
    /// 判定を型（<see cref="ViewModelBase"/> の派生か）ではなく性質で書くのは、
    /// <see cref="LedgerDetailViewModel"/> のように <see cref="ViewModelBase"/> を継承せずに
    /// 独自の処理中フラグを持つ画面があるため（#1786「走査対象は性質から導出する」）。
    /// </remarks>
    public interface IBusyState
    {
        /// <summary>
        /// 処理中かどうか
        /// </summary>
        bool IsBusy { get; }
    }
}
