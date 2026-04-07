using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader
{
/// <summary>
    /// カードリーダーの接続状態
    /// </summary>
    public enum CardReaderConnectionState
    {
        /// <summary>
        /// 接続中
        /// </summary>
        Connected,

        /// <summary>
        /// 切断
        /// </summary>
        Disconnected,

        /// <summary>
        /// 再接続中
        /// </summary>
        Reconnecting
    }

    /// <summary>
    /// 接続状態変更イベントの引数
    /// </summary>
    public class ConnectionStateChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 新しい接続状態
        /// </summary>
        public CardReaderConnectionState State { get; }

        /// <summary>
        /// メッセージ（エラー詳細など）
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// 再接続試行回数（再接続中の場合）
        /// </summary>
        public int RetryCount { get; }

        public ConnectionStateChangedEventArgs(CardReaderConnectionState state, string message = null, int retryCount = 0)
        {
            State = state;
            Message = message;
            RetryCount = retryCount;
        }
    }

    /// <summary>
    /// カード読み取りイベントの引数
    /// </summary>
    public class CardReadEventArgs : EventArgs
    {
        /// <summary>
        /// 読み取ったカードのIDm
        /// </summary>
        public string Idm { get; set; } = string.Empty;

        /// <summary>
        /// システムコード
        /// </summary>
        public string SystemCode { get; set; }
    }

    /// <summary>
    /// ICカードリーダーインターフェース
    /// </summary>
    public interface ICardReader : IDisposable
    {
        /// <summary>
        /// カードが読み取られた時に発生するイベント
        /// </summary>
        event EventHandler<CardReadEventArgs> CardRead;

        /// <summary>
        /// エラーが発生した時に発生するイベント
        /// </summary>
        event EventHandler<Exception> Error;

        /// <summary>
        /// 接続状態が変更された時に発生するイベント
        /// </summary>
        event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

        /// <summary>
        /// カード読み取りを開始
        /// </summary>
        Task StartReadingAsync();

        /// <summary>
        /// カード読み取りを停止
        /// </summary>
        Task StopReadingAsync();

        /// <summary>
        /// 読み取り中かどうか
        /// </summary>
        bool IsReading { get; }

        /// <summary>
        /// 現在の接続状態
        /// </summary>
        CardReaderConnectionState ConnectionState { get; }

        /// <summary>
        /// カードから履歴を読み取る（後方互換用）
        /// </summary>
        /// <remarks>
        /// Issue #1169: このメソッドは例外を飲み込んで空リストを返すため、
        /// リーダーエラーと「履歴ゼロ件」を区別できない。
        /// 区別が必要な場合は <see cref="TryReadHistoryAsync"/> を使用すること。
        /// </remarks>
        /// <param name="idm">カードのIDm</param>
        /// <returns>利用履歴詳細のリスト（エラー時は空リスト）</returns>
        Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm);

        /// <summary>
        /// Issue #1169: カードから履歴を読み取る（Result型版）。
        /// リーダーエラー発生時はFailを返し、呼び出し元でリーダーエラーと
        /// 「履歴ゼロ件（正常）」を明確に区別できる。
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>成功時は利用履歴詳細のリスト、失敗時はエラー情報</returns>
        Task<CardReadResult<IReadOnlyList<LedgerDetail>>> TryReadHistoryAsync(string idm);

        /// <summary>
        /// カードの残高を読み取る
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>残高</returns>
        Task<int?> ReadBalanceAsync(string idm);

        /// <summary>
        /// 接続状態を確認（ヘルスチェック）
        /// </summary>
        /// <returns>接続中の場合true</returns>
        Task<bool> CheckConnectionAsync();

        /// <summary>
        /// 手動で再接続を試行
        /// </summary>
        Task ReconnectAsync();
    }
}
