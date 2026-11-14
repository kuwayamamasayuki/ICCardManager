#nullable enable
namespace ICCardManager.Services
{
    /// <summary>
    /// ViewModel を公開するダイアログ（Issue #2475）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="INavigationService.ShowDialogAsync{TDialog, TViewModel}"/> が、ダイアログの初期化と
    /// 閉じた後の結果の読み取りを<b>ダイアログの Window ではなく ViewModel に対して</b>行うための継ぎ目。
    /// 呼び出し元は Window を実体化せずに済むので、ダイアログを開いてから閉じるまでの流れ
    /// （初期化 → 職員の操作 → 閉じた後の分岐）を ViewModel の単体テストから踏める。
    /// </para>
    /// <para>
    /// 導入前、利用履歴行編集ダイアログからの削除（Issue #750）は <c>LedgerRowEditDialog</c> を
    /// 取り出して要求を読んでいたため単体テストから到達できず、UI から到達しない履歴一覧の削除コマンド
    /// （Issue #635 の <c>DeleteLedgerRow</c>）が削除の挙動テストの唯一の入口として残っていた。
    /// </para>
    /// </remarks>
    /// <typeparam name="TViewModel">ダイアログの ViewModel の型</typeparam>
    public interface IViewModelDialog<out TViewModel>
        where TViewModel : class
    {
        /// <summary>
        /// ダイアログの ViewModel（ダイアログの生成時に確定し、以後変わらない）
        /// </summary>
        TViewModel ViewModel { get; }
    }
}
