using System.Windows;
using System.Windows.Input;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 「一覧＋編集フォーム」型ダイアログで Escape キーが意味する操作（Issue #2080）
    /// </summary>
    public enum EditFormEscapeAction
    {
        /// <summary>編集を取り消してフォームを閉じる（ダイアログ自体は閉じない）</summary>
        CancelEdit,

        /// <summary>ダイアログを閉じる</summary>
        CloseDialog,
    }

    /// <summary>
    /// 「一覧＋編集フォーム」型ダイアログのキーボード操作の方針（Issue #2080）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 「保存」に <c>IsDefault</c> が無く、Escape が「完了」（<c>IsCancel="True"</c>）へ
    /// 割り当たっていたため、氏名を入力して Enter を押しても保存されず、
    /// 編集中に Escape を押すと確認なしで**入力内容ごとダイアログが閉じて**いた。
    /// </para>
    /// <para>
    /// Escape は「いま開いている入れ子のいちばん内側を閉じる」キーである。
    /// 編集フォームを開いているときの内側は編集フォームなので、Escape は
    /// 「キャンセル」ボタンと同じ経路（<see cref="IEditFormViewModel.CancelEdit"/>）を通し、
    /// 一覧へ戻る。もう一度 Escape を押せば従来どおりダイアログが閉じる。
    /// 破棄確認は出さない — 「キャンセル」ボタンが確認なしで破棄する以上、
    /// 同じ判断を 2 か所に持たせない（#1763）。
    /// </para>
    /// <para>
    /// <see cref="ResolveEscapeAction"/> を純関数として切り出してあるのは、
    /// <see cref="Window"/> のコードビハインドが STA 依存で xUnit から実行できないため。
    /// 判断は単体テストで、結線はソーステキストの静的検査で固定する
    /// （<c>error-messages.md</c> #1817 と同じ作法）。
    /// </para>
    /// </remarks>
    public static class EditFormKeyPolicy
    {
        /// <summary>
        /// Escape キーが意味する操作を決める
        /// </summary>
        /// <param name="isEditing">編集フォームを表示中か</param>
        public static EditFormEscapeAction ResolveEscapeAction(bool isEditing)
        {
            return isEditing ? EditFormEscapeAction.CancelEdit : EditFormEscapeAction.CloseDialog;
        }

        /// <summary>
        /// ダイアログの <c>PreviewKeyDown</c> から呼び、Escape キーを処理する
        /// </summary>
        /// <remarks>
        /// <c>PreviewKeyDown</c>（トンネル）で拾うのは、入力欄にフォーカスがある状態でも
        /// 確実に届かせるため。Escape 以外のキーには触れない。
        /// </remarks>
        /// <param name="dialog">対象のダイアログ</param>
        /// <param name="viewModel">ダイアログの ViewModel</param>
        /// <param name="e">キーイベント</param>
        public static void HandleEscape(Window dialog, IEditFormViewModel viewModel, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            switch (ResolveEscapeAction(viewModel.IsEditing))
            {
                case EditFormEscapeAction.CancelEdit:
                    viewModel.CancelEdit();
                    break;

                default:
                    dialog.Close();
                    break;
            }

            e.Handled = true;
        }
    }
}
