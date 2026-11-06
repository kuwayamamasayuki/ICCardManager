#nullable enable

using System;

namespace ICCardManager.Common.Exceptions
{
    /// <summary>
    /// 画面を開こうとした利用履歴の行が、読み取りの時点で既に無かったことを表す例外（Issue #2378）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 共有フォルダモードでは、一覧に表示されている行を他のパソコンが削除・統合し得る。履歴パネルは
    /// 画面を開く前にも行の有無を確かめるが、その確認とダイアログ自身の読み取りの間に消えることがある。
    /// 利用履歴詳細ダイアログ（<c>LedgerDetailViewModel.InitializeAsync</c>）は空の画面を出す手段を持たないので、
    /// この例外で初期化を中断し、呼び出し元（<c>HistoryPanelViewModel.ShowLedgerDetail</c>）が
    /// 一覧を読み直してから競合を案内する（<see cref="ConcurrencyConflictMessage.ForOpen"/>）。
    /// </para>
    /// <para>
    /// 呼び出し元は「行が無い」ことだけを捕まえ、それ以外の失敗は従来どおり伝える必要がある。
    /// <see cref="InvalidOperationException"/> を丸ごと捕まえると無関係な不具合まで競合として案内してしまうため、
    /// 専用の型にしている。基底を <see cref="InvalidOperationException"/> にしているのは、従来この経路が
    /// 投げていた型を受けている箇所の挙動を変えないため。
    /// </para>
    /// </remarks>
    public sealed class LedgerRowGoneException : InvalidOperationException
    {
        /// <summary>既定のメッセージで初期化する</summary>
        public LedgerRowGoneException()
            : base("利用履歴の行が見つかりません")
        {
        }

        /// <summary>メッセージを指定して初期化する</summary>
        public LedgerRowGoneException(string message)
            : base(message)
        {
        }

        /// <summary>メッセージと内部例外を指定して初期化する</summary>
        public LedgerRowGoneException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
