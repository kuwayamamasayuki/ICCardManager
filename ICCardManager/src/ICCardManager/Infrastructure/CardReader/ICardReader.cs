using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader;

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
    public string? SystemCode { get; set; }
}

/// <summary>
/// ICカードリーダーインターフェース
/// </summary>
public interface ICardReader : IDisposable
{
    /// <summary>
    /// カードが読み取られた時に発生するイベント
    /// </summary>
    event EventHandler<CardReadEventArgs>? CardRead;

    /// <summary>
    /// エラーが発生した時に発生するイベント
    /// </summary>
    event EventHandler<Exception>? Error;

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
    /// カードから履歴を読み取る
    /// </summary>
    /// <param name="idm">カードのIDm</param>
    /// <returns>利用履歴詳細のリスト</returns>
    Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm);

    /// <summary>
    /// カードの残高を読み取る
    /// </summary>
    /// <param name="idm">カードのIDm</param>
    /// <returns>残高</returns>
    Task<int?> ReadBalanceAsync(string idm);
}
