#if DEBUG
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// 物理カードリーダーと仮想タッチ機能を統合するデコレーター（DEBUGビルド専用）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 実際のカードリーダー（FelicaCardReader/PcScCardReader）をラップし、
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

        public event EventHandler<CardReadEventArgs> CardRead;
        public event EventHandler<Exception> Error;
        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

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
                return customHistory;
            }

            // カスタムデータがなければ実カードリーダーに委譲
            return await _realReader.ReadHistoryAsync(idm);
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
            return await _realReader.ReadBalanceAsync(idm);
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
        /// 特定カードの履歴データを設定（仮想タッチ用）
        /// </summary>
        public void SetCustomHistory(string idm, List<LedgerDetail> history)
        {
            _settings.CustomHistory[idm] = history;
        }

        /// <summary>
        /// 特定カードの残高を設定（仮想タッチ用）
        /// </summary>
        public void SetCustomBalance(string idm, int balance)
        {
            _settings.CustomBalances[idm] = balance;
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
