namespace ICCardManager.ViewModels
{
    /// <summary>
    /// 「一覧＋編集フォーム」型ダイアログの ViewModel が満たす契約（Issue #2080）
    /// </summary>
    /// <remarks>
    /// このダイアログ群は 1 つのウィンドウの中に「一覧を見ている状態」と
    /// 「編集フォームに入力している状態」の 2 つを持つ。キーボード操作
    /// （Enter＝保存 / Escape＝編集の取り消し）は状態によって意味が変わるため、
    /// 判断を各コードビハインドへ配らず
    /// <see cref="Views.Helpers.EditFormKeyPolicy"/> 1 か所へ寄せる
    /// （<c>db-write-conventions.md</c> #1763「同じ判断を配らない」）。
    ///
    /// <see cref="CancelEdit"/> は「キャンセル」ボタンが実行する
    /// <c>CancelEditCommand</c> と同じメソッドであること。別経路にすると、
    /// ボタンで取り消したときだけ走る後始末（カード読み取り抑制の解放 #1807 や
    /// 編集対象の退避値の破棄 #1761）が Escape では走らない状態が生まれる。
    /// </remarks>
    public interface IEditFormViewModel
    {
        /// <summary>編集フォームを表示中か</summary>
        bool IsEditing { get; }

        /// <summary>編集を取り消してフォームを閉じる</summary>
        void CancelEdit();
    }
}
