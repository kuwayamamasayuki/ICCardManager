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

        /// <summary>何もしない（処理中）</summary>
        Ignore,
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
    /// <b>処理中（<see cref="IEditFormViewModel.IsBusy"/>）は何もしない</b>（コードレビューで検出）。
    /// 処理中オーバーレイが塞ぐのはマウスのヒットテストだけで、キーボードは配下へ届く（#1761）。
    /// <c>SaveAsync</c> は <c>BeginBusy</c> スコープの内側で DB を待ち、その継続で
    /// <c>EditCardIdm</c> 等の入力欄を読むため、待機中に <c>CancelEdit()</c> が走ると
    /// <b>空の値で UPDATE が組み立てられ</b>、影響行数 0 から「他のパソコンで削除された可能性があります」
    /// という、試みてすらいない編集についての競合案内が出る。登録経路ではカード読み取り抑制（#1807）も
    /// 解放されるため、その瞬間のタッチが貸出として処理される。
    /// 是正前（<c>IsCancel="True"</c>）の Escape はウィンドウを閉じるだけで入力欄を消さなかったので、
    /// <b>これは是正が持ち込み得た新しい故障</b>であり、握り潰す側（何もしない）へ倒す。
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
        /// <param name="isBusy">処理中か</param>
        public static EditFormEscapeAction ResolveEscapeAction(bool isEditing, bool isBusy)
        {
            if (isBusy)
            {
                // 編集中かどうかに関わらず握り潰す。非編集中でも、削除や払い戻しの待機中に
                // ダイアログを閉じると Closed → Cleanup が処理の途中で走る。
                return EditFormEscapeAction.Ignore;
            }

            return isEditing ? EditFormEscapeAction.CancelEdit : EditFormEscapeAction.CloseDialog;
        }

        /// <summary>
        /// ダイアログの <c>KeyDown</c> から呼び、Escape キーを処理する
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>トンネル（<c>PreviewKeyDown</c>）ではなくバブル（<c>KeyDown</c>）で拾う</b>
        /// （コードレビューで検出）。<c>PreviewKeyDown</c> はウィンドウが最初に受け取るため、
        /// <b>Escape を正当に消費するコントロールより先に走る</b>。カード種別の <c>ComboBox</c> は
        /// ドロップダウンを開いている間の Escape を <c>OnKeyDown</c>（バブル）で閉じるので、
        /// トンネルで拾うと「候補を開いたが選び直さずに閉じる」操作が
        /// <b>編集フォームごとの破棄</b>になる — この Issue が消そうとしている故障そのもの。
        /// </para>
        /// <para>
        /// バブルでも届かなくなる入力欄は無い。<c>TextBox</c> は Escape を消費せず、
        /// これらのダイアログの <c>DataGrid</c> は <c>IsReadOnly="True"</c> でセル編集に入らないため
        /// Escape を消費しない。消費するコントロールが増えたときは、そのコントロールが
        /// Escape を持つのが正しいので、バブルのままでよい。
        /// </para>
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

            switch (ResolveEscapeAction(viewModel.IsEditing, viewModel.IsBusy))
            {
                case EditFormEscapeAction.Ignore:
                    // 処理中。e.Handled は立てる（このまま抜けると既定の処理へ流れ得るため）
                    break;

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
