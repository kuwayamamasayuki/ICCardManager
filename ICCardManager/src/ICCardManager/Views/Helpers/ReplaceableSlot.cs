using System;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// 「同時に 1 つだけ置ける」表示物の置き場（Issue #2141）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 自動では消えないトースト（エラー通知・記録済みの案内）に使う。旧実装は
    /// <c>ShowError</c> のたびに新しい窓を<b>同じ画面隅へ重ねて</b>開き、1 枚ずつクリックしないと
    /// 消えなかった。前の職員宛ての「やり直してください」が、次の職員の操作の上に残り続けた。
    /// </para>
    /// <para>
    /// 置き場の判断（新しいものを置いたら古いものを閉じる／閉じたものが置き場を空ける）を
    /// <see cref="System.Windows.Window"/> から切り離しているのは、<c>Window</c> が STA 依存で
    /// xUnit から実行できないため。判断は単体テストで、結線はソーステキストの静的検査で固定する
    /// （<c>EditFormKeyPolicy</c> と同じ作法）。
    /// </para>
    /// <para>
    /// UI スレッド専用（呼び出し元の <c>Dispatcher.Invoke</c> の内側で使う）なので排他は持たない。
    /// </para>
    /// </remarks>
    /// <typeparam name="T">置くもの</typeparam>
    internal sealed class ReplaceableSlot<T> where T : class
    {
        private readonly Action<T> _close;
        private T _current;

        /// <param name="close">置き場から外れたものを閉じる処理</param>
        public ReplaceableSlot(Action<T> close)
        {
            _close = close ?? throw new ArgumentNullException(nameof(close));
        }

        /// <summary>
        /// いま置かれているもの（無ければ null）
        /// </summary>
        public T Current => _current;

        /// <summary>
        /// 新しいものを置き、それまで置かれていたものを閉じる
        /// </summary>
        public void Put(T item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var previous = _current;
            _current = item;
            if (previous != null && !ReferenceEquals(previous, item))
            {
                _close(previous);
            }
        }

        /// <summary>
        /// 置かれているものを閉じて置き場を空ける
        /// </summary>
        /// <returns>閉じたものがあれば true</returns>
        public bool Dismiss()
        {
            var previous = _current;
            _current = null;
            if (previous == null)
            {
                return false;
            }

            _close(previous);
            return true;
        }

        /// <summary>
        /// 自分で閉じた（クリック等）ものを置き場から外す。閉じる処理は呼ばない。
        /// </summary>
        /// <remarks>
        /// 既に別のものへ差し替わっていれば何もしない。閉じ終わりが差し替えより後に届くと
        /// （フェードアウトの完了は非同期）、新しく置いたものまで置き場から外してしまうため。
        /// </remarks>
        public void Release(T item)
        {
            if (ReferenceEquals(_current, item))
            {
                _current = null;
            }
        }
    }
}
