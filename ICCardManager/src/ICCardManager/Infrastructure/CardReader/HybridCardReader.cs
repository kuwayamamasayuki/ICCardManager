#nullable enable
#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// 物理カードリーダーと仮想タッチ機能を統合するデコレーター（DEBUGビルド専用）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 実際のカードリーダー（FelicaCardReader）をラップし、
    /// 物理カードの読み取りはそのまま委譲しつつ、仮想タッチ（SimulateCardRead）や
    /// カスタム履歴・残高の設定機能を追加します。
    /// </para>
    /// <para>
    /// Issue #640: 仮想タッチ機能を使いつつ、物理カードリーダーも利用可能にする。
    /// </para>
    /// </remarks>
    public class HybridCardReader : ICardReader
    {
        private readonly ICardReader _realReader;
        private readonly MockHistorySettings _settings = new();
        private bool _disposed;

        public event EventHandler<CardReadEventArgs>? CardRead;
        public event EventHandler<Exception>? Error;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        /// <inheritdoc/>
        public bool IsReading => _realReader.IsReading;

        /// <inheritdoc/>
        public CardReaderConnectionState ConnectionState => _realReader.ConnectionState;

        public HybridCardReader(ICardReader realReader)
        {
            _realReader = realReader ?? throw new ArgumentNullException(nameof(realReader));

            // 実カードリーダーのイベントを転送
            _realReader.CardRead += (_, e) => CardRead?.Invoke(this, e);
            _realReader.Error += (_, e) => Error?.Invoke(this, e);
            _realReader.ConnectionStateChanged += (_, e) => ConnectionStateChanged?.Invoke(this, e);
        }

        /// <inheritdoc/>
        public Task StartReadingAsync() => _realReader.StartReadingAsync();

        /// <inheritdoc/>
        public Task StopReadingAsync() => _realReader.StopReadingAsync();

        /// <inheritdoc/>
        public async Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm)
        {
            // カスタム履歴データがあればそれを使用（仮想タッチで設定されたデータ）
            if (_settings.CustomHistory.TryGetValue(idm, out var customHistory))
            {
                return CloneHistory(customHistory);
            }

            // カスタムデータがなければ実カードリーダーに委譲
            return await _realReader.ReadHistoryAsync(idm).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<CardReadResult<IReadOnlyList<LedgerDetail>>> TryReadHistoryAsync(string idm)
        {
            // Issue #1169: カスタム履歴データがあれば成功として返す
            if (_settings.CustomHistory.TryGetValue(idm, out var customHistory))
            {
                return CardReadResult<IReadOnlyList<LedgerDetail>>.Ok(CloneHistory(customHistory));
            }

            return await _realReader.TryReadHistoryAsync(idm).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<int?> ReadBalanceAsync(string idm)
        {
            // カスタム残高があればそれを使用（仮想タッチで設定されたデータ）
            if (_settings.CustomBalances.TryGetValue(idm, out var customBalance))
            {
                return customBalance;
            }

            // カスタムデータがなければ実カードリーダーに委譲
            return await _realReader.ReadBalanceAsync(idm).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public Task<bool> CheckConnectionAsync() => _realReader.CheckConnectionAsync();

        /// <inheritdoc/>
        public Task ReconnectAsync() => _realReader.ReconnectAsync();

        /// <summary>
        /// カード読み取りをシミュレート（仮想タッチ用）
        /// </summary>
        /// <param name="idm">シミュレートするカードのIDm</param>
        public void SimulateCardRead(string idm)
        {
            CardRead?.Invoke(this, new CardReadEventArgs
            {
                Idm = idm,
                SystemCode = "0003"
            });
        }

        /// <summary>
        /// 設定した履歴を、読むたびに複製して返す（台帳へ記録すると明細の LedgerId が書き換わるため、
        /// 同じ明細のインスタンスを次の読み取りへ持ち越さない。実カードリーダーも読むたびに新しい明細を作る）
        /// </summary>
        private static List<LedgerDetail> CloneHistory(IEnumerable<LedgerDetail> history) =>
            history.Select(d => Common.LedgerCloner.CloneDetail(d)!).ToList();

        /// <summary>
        /// 仮想タッチしたカードが返す残額・利用履歴を設定する（Issue #2293。<see cref="DebugCardData"/> が起動時に呼ぶ）
        /// </summary>
        /// <param name="idm">カードの IDm</param>
        /// <param name="balance">残額。null なら設定しない（実カードリーダーへ委譲する）</param>
        /// <param name="history">利用履歴（新しい順）。null なら設定しない</param>
        public void SetCustomCardData(string idm, int? balance, IEnumerable<LedgerDetail>? history)
        {
            if (balance.HasValue)
            {
                _settings.CustomBalances[idm] = balance.Value;
            }

            if (history != null)
            {
                _settings.CustomHistory[idm] = history.ToList();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _realReader.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
#endif
