using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using PCSC;
using PCSC.Exceptions;
using PCSC.Monitoring;
using PcscICardReader = PCSC.ICardReader;

namespace ICCardManager.Infrastructure.CardReader
{
/// <summary>
    /// PC/SC APIを使用したICカードリーダー実装です。
    /// PaSoRi等のNFCリーダーで交通系ICカード（FeliCa）を読み取ります。
    /// </summary>
    /// <remarks>
    /// <para>
    /// このクラスは以下の機能を提供します：
    /// </para>
    /// <list type="bullet">
    /// <item><description>カードの検出と自動読み取り（<see cref="StartReadingAsync"/>）</description></item>
    /// <item><description>利用履歴の読み取り（<see cref="ReadHistoryAsync"/>）- 最大20件</description></item>
    /// <item><description>残高の読み取り（<see cref="ReadBalanceAsync"/>）</description></item>
    /// <item><description>接続状態の監視と自動再接続</description></item>
    /// </list>
    /// <para>
    /// <strong>接続状態管理:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="CardReaderConnectionState.Connected"/>: 正常接続中</description></item>
    /// <item><description><see cref="CardReaderConnectionState.Disconnected"/>: 切断（リーダー未接続/抜去）</description></item>
    /// <item><description><see cref="CardReaderConnectionState.Reconnecting"/>: 自動再接続試行中（最大10回）</description></item>
    /// </list>
    /// <para>
    /// <strong>読み取り重複防止:</strong>
    /// 同一カードの連続読み取りを防止するため、1秒以内の再読み取りは無視されます。
    /// </para>
    /// <para>
    /// <strong>対応カード:</strong>
    /// サイバネ規格（システムコード 0x0003）の交通系ICカード
    /// （Suica、PASMO、ICOCA、nimoca、SUGOCA、はやかけん等）
    /// </para>
    /// </remarks>
    public class PcScCardReader : ICardReader
    {
        private readonly IPcScProvider _provider;
        private readonly ILogger<PcScCardReader> _logger;
        private ISCardMonitor _monitor;
        private System.Timers.Timer _healthCheckTimer;
        private System.Timers.Timer _reconnectTimer;
        private bool _isReading;
        private bool _disposed;
        private CardReaderConnectionState _connectionState = CardReaderConnectionState.Disconnected;
        private int _reconnectAttempts;
        private string[] _lastKnownReaderNames;

        /// <summary>
        /// 最後に読み取ったカードのIDm
        /// </summary>
        private string _lastReadIdm;

        /// <summary>
        /// 最後にカードを読み取った時刻
        /// </summary>
        private DateTime _lastReadTime = DateTime.MinValue;

        /// <summary>
        /// カードがリーダーから離されたかどうか（置きっぱなし検出用）
        /// </summary>
        /// <remarks>
        /// Issue #323 対応: カードを置きっぱなしにした場合の連続読み取りを防止するためのフラグ。
        /// OnCardRemovedイベントで true に設定され、次のOnCardInsertedで false にリセットされる。
        /// 同じカードが検出された場合、このフラグが true の場合のみイベントを発火する。
        /// </remarks>
        private volatile bool _cardWasLifted = true;

        /// <summary>
        /// FeliCaのシステムコード（サイバネ規格）
        /// </summary>
        private const ushort CyberneSystemCode = 0x0003;

        /// <summary>
        /// ヘルスチェック間隔（ミリ秒）
        /// </summary>
        internal const int HealthCheckIntervalMs = 10000;

        /// <summary>
        /// 再接続間隔（ミリ秒）
        /// </summary>
        internal const int ReconnectIntervalMs = 3000;

        /// <summary>
        /// 最大再接続試行回数
        /// </summary>
        internal const int MaxReconnectAttempts = 10;

        public event EventHandler<CardReadEventArgs> CardRead;
        public event EventHandler<Exception> Error;
        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

        public bool IsReading => _isReading;
        public CardReaderConnectionState ConnectionState => _connectionState;

        /// <summary>
        /// PcScCardReaderの新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="logger">ロガー</param>
        public PcScCardReader(ILogger<PcScCardReader> logger)
            : this(logger, new DefaultPcScProvider())
        {
        }

        /// <summary>
        /// テスト用のコンストラクタ。PC/SCプロバイダーを注入できます。
        /// </summary>
        /// <param name="logger">ロガー</param>
        /// <param name="provider">PC/SCプロバイダー（テスト時はモックを注入）</param>
        internal PcScCardReader(ILogger<PcScCardReader> logger, IPcScProvider provider)
        {
            _logger = logger;
            _provider = provider;
        }

        /// <summary>
        /// カードリーダーの監視を開始し、カード検出時にイベントを発火します。
        /// </summary>
        /// <returns>監視開始処理のTask</returns>
        /// <exception cref="InvalidOperationException">
        /// カードリーダーが見つからない場合、またはスマートカードサービスが起動していない場合
        /// </exception>
        /// <remarks>
        /// <para>処理フロー：</para>
        /// <list type="number">
        /// <item><description>PC/SCコンテキストからリーダー一覧を取得</description></item>
        /// <item><description>SCardMonitorでカード挿入/取り外しを監視</description></item>
        /// <item><description>ヘルスチェックタイマーを開始（10秒間隔）</description></item>
        /// </list>
        /// <para>
        /// カードが検出されると <see cref="CardRead"/> イベントが発火します。
        /// 切断時は自動再接続が試行されます。
        /// </para>
        /// </remarks>
        public Task StartReadingAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var readerNames = _provider.GetReaders();
                    if (readerNames == null || readerNames.Length == 0)
                    {
                        SetConnectionState(CardReaderConnectionState.Disconnected, "カードリーダーが見つかりません");
                        throw new InvalidOperationException(
                            "カードリーダーが見つかりません。PaSoRiが接続されていることを確認してください。");
                    }

                    _logger.LogInformation("検出されたカードリーダー: {ReaderNames}", string.Join(", ", readerNames));

                    _lastKnownReaderNames = readerNames;
                    _monitor = _provider.CreateMonitor();
                    _monitor.CardInserted += OnCardInserted;
                    _monitor.CardRemoved += OnCardRemoved;
                    _monitor.MonitorException += OnMonitorException;
                    _monitor.Start(readerNames);

                    _isReading = true;
                    _reconnectAttempts = 0;
                    SetConnectionState(CardReaderConnectionState.Connected);
                    _logger.LogInformation("カードリーダー監視を開始しました");

                    // ヘルスチェックタイマーを開始
                    StartHealthCheckTimer();
                }
                catch (PCSCException ex)
                {
                    CardReaderException cardReaderException = ex.SCardError switch
                    {
                        SCardError.NoService => CardReaderException.ServiceNotAvailable(ex),
                        SCardError.NoReadersAvailable => CardReaderException.NotConnected(ex),
                        _ => CardReaderException.ReadFailed(ex.Message, ex)
                    };
                    SetConnectionState(CardReaderConnectionState.Disconnected, cardReaderException.UserFriendlyMessage);
                    Error?.Invoke(this, cardReaderException);
                    throw cardReaderException;
                }
                catch (Exception ex)
                {
                    var cardReaderException = CardReaderException.ReadFailed(ex.Message, ex);
                    SetConnectionState(CardReaderConnectionState.Disconnected, cardReaderException.UserFriendlyMessage);
                    Error?.Invoke(this, cardReaderException);
                    throw cardReaderException;
                }
            });
        }

        /// <inheritdoc/>
        public Task StopReadingAsync()
        {
            return Task.Run(StopReadingCore);
        }

        /// <summary>
        /// カード読み取り停止の内部処理（同期版）
        /// </summary>
        /// <remarks>
        /// Disposeメソッドから直接呼び出し可能。
        /// StopReadingAsyncはこのメソッドをTask.Runでラップして呼び出す。
        /// </remarks>
        private void StopReadingCore()
        {
            // タイマーを停止
            StopHealthCheckTimer();
            StopReconnectTimer();

            if (_monitor != null)
            {
                _monitor.CardInserted -= OnCardInserted;
                _monitor.CardRemoved -= OnCardRemoved;
                _monitor.MonitorException -= OnMonitorException;
                _monitor.Cancel();
                _monitor.Dispose();
                _monitor = null;
            }

            _isReading = false;
            _lastReadIdm = null;
            _cardWasLifted = true;  // 次回開始時に最初のカードを検出できるようにリセット
            SetConnectionState(CardReaderConnectionState.Disconnected);
            _logger.LogInformation("カードリーダー監視を停止しました");
        }

        /// <summary>
        /// ICカードから利用履歴を読み取ります。
        /// </summary>
        /// <param name="idm">読み取り対象カードのIDm（16桁の16進数文字列）</param>
        /// <returns>利用履歴詳細のリスト（最大20件、新しい順）</returns>
        /// <remarks>
        /// <para>
        /// FeliCaの履歴サービス（サービスコード: 0x090F）から履歴データを読み取り、
        /// <see cref="LedgerDetail"/> オブジェクトに変換します。
        /// </para>
        /// <para>
        /// <strong>履歴データの構造（16バイト）:</strong>
        /// </para>
        /// <list type="bullet">
        /// <item><description>バイト0: 機器種別</description></item>
        /// <item><description>バイト1: 利用種別（0x02=チャージ）</description></item>
        /// <item><description>バイト4-5: 日付（2000年起点のビットフィールド）</description></item>
        /// <item><description>バイト6-7: 入場駅コード</description></item>
        /// <item><description>バイト8-9: 出場駅コード</description></item>
        /// <item><description>バイト10-11: 残高（リトルエンディアン）</description></item>
        /// </list>
        /// <para>
        /// <strong>バス利用判定:</strong>
        /// 入場駅・出場駅が両方0で、かつチャージでない場合はバス利用と判定されます。
        /// </para>
        /// </remarks>
        public async Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm)
        {
            var details = new List<LedgerDetail>();

            await Task.Run(() =>
            {
                try
                {
                    var readerNames = _provider.GetReaders();
                    if (readerNames == null || readerNames.Length == 0)
                    {
#if DEBUG
                        System.Diagnostics.Debug.WriteLine("履歴読み取り: カードリーダーが見つかりません");
#endif
                        return;
                    }

                    using var reader = _provider.ConnectReader(readerNames[0], SCardShareMode.Shared, SCardProtocol.Any);

                    // FeliCaの履歴読み取りコマンド
                    // サービスコード: 0x090F（履歴情報）
                    var serviceCode = new byte[] { 0x0F, 0x09 };
                    const int maxHistoryCount = 20;

                    // すべての履歴データを読み取る
                    var historyDataList = new List<byte[]>();
                    for (var blockIndex = 0; blockIndex < maxHistoryCount; blockIndex++)
                    {
                        var historyData = ReadBlock(reader, idm, serviceCode, blockIndex);
                        if (historyData == null || historyData.All(b => b == 0))
                        {
                            break;
                        }
                        historyDataList.Add(historyData);
                    }

#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"履歴読み取り: {historyDataList.Count}件のデータを取得");
#endif

                    // 駅名解決にはCardType.Unknownを使用
                    // 注: IDmの先頭2バイトは製造者コードであり、カード種別ではないため、
                    //     CardTypeDetector.DetectFromIdmは信頼できない
                    //     StationMasterServiceのUnknown優先順位（九州優先）が使用される
                    var cardType = CardType.Unknown;

                    // 履歴データをパースして金額を計算
                    for (var i = 0; i < historyDataList.Count; i++)
                    {
                        var currentData = historyDataList[i];
                        var nextData = i + 1 < historyDataList.Count ? historyDataList[i + 1] : null;

                        var detail = ParseHistoryData(currentData, nextData, cardType);
                        if (detail != null)
                        {
                            details.Add(detail);
                        }
                    }
                }
                catch (PCSCException ex)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"履歴読み取りエラー(PCSC): {ex.Message}");
#endif
                    CardReaderException cardReaderException = ex.SCardError switch
                    {
                        SCardError.RemovedCard => CardReaderException.CardRemoved(ex),
                        _ => CardReaderException.HistoryReadFailed(ex.Message, ex)
                    };
                    Error?.Invoke(this, cardReaderException);
                }
                catch (Exception ex)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"履歴読み取りエラー: {ex.Message}");
#endif
                    var cardReaderException = CardReaderException.HistoryReadFailed(ex.Message, ex);
                    Error?.Invoke(this, cardReaderException);
                }
            });

            return details;
        }

        /// <summary>
        /// ICカードの現在残高を読み取ります。
        /// </summary>
        /// <param name="idm">読み取り対象カードのIDm（16桁の16進数文字列）</param>
        /// <returns>残高（円）。読み取り失敗時は <c>null</c></returns>
        /// <remarks>
        /// 履歴の最新レコード（ブロック0）から残高を取得します。
        /// 残高はバイト10-11にリトルエンディアンで格納されています。
        /// </remarks>
        public async Task<int?> ReadBalanceAsync(string idm)
        {
            return await Task.Run<int?>(() =>
            {
                try
                {
                    var readerNames = _provider.GetReaders();
                    if (readerNames == null || readerNames.Length == 0)
                    {
                        return null;
                    }

                    using var reader = _provider.ConnectReader(readerNames[0], SCardShareMode.Shared, SCardProtocol.Any);

                    // 残高読み取り（履歴の最新レコードから取得）
                    var serviceCode = new byte[] { 0x0F, 0x09 };
                    var historyData = ReadBlock(reader, idm, serviceCode, 0);

                    if (historyData != null && historyData.Length >= 12)
                    {
                        // バイト10-11が残高（リトルエンディアン）
                        var balance = historyData[10] + (historyData[11] << 8);
#if DEBUG
                        System.Diagnostics.Debug.WriteLine($"残高読み取り: {balance}円");
#endif
                        return balance;
                    }

                    return null;
                }
                catch (PCSCException ex)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"残高読み取りエラー(PCSC): {ex.Message}");
#endif
                    CardReaderException cardReaderException = ex.SCardError switch
                    {
                        SCardError.RemovedCard => CardReaderException.CardRemoved(ex),
                        _ => CardReaderException.BalanceReadFailed(ex.Message, ex)
                    };
                    Error?.Invoke(this, cardReaderException);
                    return null;
                }
                catch (Exception ex)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"残高読み取りエラー: {ex.Message}");
#endif
                    var cardReaderException = CardReaderException.BalanceReadFailed(ex.Message, ex);
                    Error?.Invoke(this, cardReaderException);
                    return null;
                }
            });
        }

        /// <summary>
        /// カード挿入時のイベントハンドラ
        /// </summary>
        private void OnCardInserted(object sender, CardStatusEventArgs e)
        {
            try
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"カード検出: リーダー={e.ReaderName}");
#endif

                using var reader = _provider.ConnectReader(e.ReaderName, SCardShareMode.Shared, SCardProtocol.Any);

                // IDmを読み取り
                var idm = ReadIdm(reader);
                if (string.IsNullOrEmpty(idm))
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("IDmの読み取りに失敗しました");
#endif
                    return;
                }

#if DEBUG
                System.Diagnostics.Debug.WriteLine($"IDm読み取り成功: {idm}");
#endif

                // 同一カードの連続読み取りを防止
                var now = DateTime.Now;
                if (idm == _lastReadIdm)
                {
                    // 【重要】まずカード離脱をチェック
                    // Issue #323: カードが離されていない場合（置きっぱなし）は常に無視
                    // PC/SCモニターはカード挿入時にOnCardInsertedが呼ばれるが、
                    // 一部のリーダー/ドライバーではカードを置いたままでも
                    // 周期的にOnCardInsertedが呼ばれることがある
                    if (!_cardWasLifted)
                    {
#if DEBUG
                        System.Diagnostics.Debug.WriteLine("同一カードの連続読み取りを無視（カード未離脱）");
#endif
                        return;
                    }

                    // カードが離された場合：
                    // 30秒ルール（誤操作キャンセル機能）を正しく動作させるため、
                    // 時間制限は設けない。
                    // OnCardRemoved イベントが発火しないと _cardWasLifted は true にならないため、
                    // 物理的にカードが離れない限り再読み取りは発生しない。
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("カードが離されて再度置かれました");
#endif
                }

                // 新しいカード、または離されてから再度置かれたカードとして処理
                _lastReadIdm = idm;
                _lastReadTime = now;
                _cardWasLifted = false;  // カードが検出されたのでフラグをリセット

                CardRead?.Invoke(this, new CardReadEventArgs
                {
                    Idm = idm,
                    SystemCode = CyberneSystemCode.ToString("X4")
                });
            }
            catch (PCSCException ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"カード読み取りエラー(PCSC): {ex.Message}, SCardError={ex.SCardError}");
#endif
                // カードが素早く離された場合は専用の例外で通知
                if (ex.SCardError == SCardError.RemovedCard)
                {
                    // カードが素早く離された場合はデバッグログのみ（エラー通知不要）
                    _logger.LogDebug("カードが素早く離されました");
                    // Issue #323: カード離脱とみなす
                    // OnCardRemovedイベントより先にこの例外が発生することがあるため、
                    // ここでもフラグを設定する
                    _cardWasLifted = true;
                }
                else
                {
                    var cardReaderException = CardReaderException.ReadFailed(ex.Message, ex);
                    Error?.Invoke(this, cardReaderException);
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"カード読み取りエラー: {ex.Message}");
#endif
                var cardReaderException = CardReaderException.ReadFailed(ex.Message, ex);
                Error?.Invoke(this, cardReaderException);
            }
        }

        /// <summary>
        /// カード取り外し時のイベントハンドラ
        /// </summary>
        /// <remarks>
        /// Issue #323: カードがリーダーから離されたことを記録。
        /// 次回同じカードが検出されたときにイベントを発火するためのフラグを設定する。
        /// </remarks>
        private void OnCardRemoved(object sender, CardStatusEventArgs e)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"カード取り外し: リーダー={e.ReaderName}");
#endif
            // Issue #323: 次回同じカードが検出されたときにイベントを発火するためフラグを立てる
            _cardWasLifted = true;
        }

        /// <summary>
        /// モニター例外時のイベントハンドラ
        /// </summary>
        private void OnMonitorException(object sender, PCSCException ex)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"モニター例外: {ex.Message}");
#endif
            var monitorException = CardReaderException.MonitorError(ex.Message, ex);
            Error?.Invoke(this, monitorException);

            // 切断として処理し、自動再接続を開始
            if (_connectionState == CardReaderConnectionState.Connected)
            {
                SetConnectionState(CardReaderConnectionState.Disconnected, monitorException.UserFriendlyMessage);
                StartReconnectTimer();
            }
        }

        #region 接続状態監視・再接続

        /// <summary>
        /// 接続状態を設定し、イベントを発火
        /// </summary>
        private void SetConnectionState(CardReaderConnectionState state, string message = null, int retryCount = 0)
        {
            if (_connectionState == state && retryCount == 0)
            {
                return; // 状態が変わらない場合はスキップ
            }

            _connectionState = state;
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"接続状態変更: {state} (メッセージ: {message}, リトライ: {retryCount})");
#endif
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message, retryCount));
        }

        /// <summary>
        /// ヘルスチェックタイマーを開始
        /// </summary>
        private void StartHealthCheckTimer()
        {
            StopHealthCheckTimer();

            _healthCheckTimer = new System.Timers.Timer(HealthCheckIntervalMs);
            _healthCheckTimer.Elapsed += async (s, e) => await OnHealthCheckAsync();
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"ヘルスチェックタイマー開始 ({HealthCheckIntervalMs}ms間隔)");
#endif
        }

        /// <summary>
        /// ヘルスチェックタイマーを停止
        /// </summary>
        private void StopHealthCheckTimer()
        {
            if (_healthCheckTimer != null)
            {
                _healthCheckTimer.Stop();
                _healthCheckTimer.Dispose();
                _healthCheckTimer = null;
            }
        }

        /// <summary>
        /// ヘルスチェック実行
        /// </summary>
        private async Task OnHealthCheckAsync()
        {
            if (_connectionState == CardReaderConnectionState.Reconnecting)
            {
                return; // 再接続中はスキップ
            }

            var isConnected = await CheckConnectionAsync();
            if (!isConnected && _connectionState == CardReaderConnectionState.Connected)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine("ヘルスチェック: 接続が失われました");
#endif
                SetConnectionState(CardReaderConnectionState.Disconnected, "接続が失われました");
                StartReconnectTimer();
            }
        }

        /// <inheritdoc/>
        public Task<bool> CheckConnectionAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    var readerNames = _provider.GetReaders();
                    return readerNames != null && readerNames.Length > 0;
                }
                catch (PCSCException)
                {
                    return false;
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// 再接続タイマーを開始
        /// </summary>
        private void StartReconnectTimer()
        {
            StopReconnectTimer();
            StopHealthCheckTimer(); // 再接続中はヘルスチェックを停止

            _reconnectAttempts = 0;
            _reconnectTimer = new System.Timers.Timer(ReconnectIntervalMs);
            _reconnectTimer.Elapsed += async (s, e) => await OnReconnectAttemptAsync();
            _reconnectTimer.AutoReset = true;
            _reconnectTimer.Start();

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"再接続タイマー開始 ({ReconnectIntervalMs}ms間隔, 最大{MaxReconnectAttempts}回)");
#endif
        }

        /// <summary>
        /// 再接続タイマーを停止
        /// </summary>
        private void StopReconnectTimer()
        {
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
                _reconnectTimer.Dispose();
                _reconnectTimer = null;
            }
        }

        /// <summary>
        /// 再接続試行
        /// </summary>
        private Task OnReconnectAttemptAsync()
        {
            _reconnectAttempts++;
#if DEBUG
            System.Diagnostics.Debug.WriteLine($"再接続試行: {_reconnectAttempts}/{MaxReconnectAttempts}");
#endif

            SetConnectionState(CardReaderConnectionState.Reconnecting, null, _reconnectAttempts);

            if (_reconnectAttempts > MaxReconnectAttempts)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine("再接続失敗: 最大試行回数に達しました");
#endif
                StopReconnectTimer();
                var reconnectException = CardReaderException.ReconnectFailed(MaxReconnectAttempts);
                SetConnectionState(CardReaderConnectionState.Disconnected, reconnectException.UserFriendlyMessage);
                Error?.Invoke(this, reconnectException);
                return Task.CompletedTask;
            }

            try
            {
                // 既存のモニターを停止
                if (_monitor != null)
                {
                    _monitor.CardInserted -= OnCardInserted;
                    _monitor.CardRemoved -= OnCardRemoved;
                    _monitor.MonitorException -= OnMonitorException;
                    try { _monitor.Cancel(); } catch { }
                    try { _monitor.Dispose(); } catch { }
                    _monitor = null;
                }

                // リーダーを再検索
                var readerNames = _provider.GetReaders();
                if (readerNames == null || readerNames.Length == 0)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine("再接続: カードリーダーが見つかりません");
#endif
                    return Task.CompletedTask; // 次のリトライを待つ
                }

                // 新しいモニターを開始
                _lastKnownReaderNames = readerNames;
                _monitor = _provider.CreateMonitor();
                _monitor.CardInserted += OnCardInserted;
                _monitor.CardRemoved += OnCardRemoved;
                _monitor.MonitorException += OnMonitorException;
                _monitor.Start(readerNames);

                _isReading = true;
                _reconnectAttempts = 0;

#if DEBUG
                System.Diagnostics.Debug.WriteLine("再接続成功");
#endif
                StopReconnectTimer();
                SetConnectionState(CardReaderConnectionState.Connected, "再接続しました");

                // ヘルスチェックを再開
                StartHealthCheckTimer();
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"再接続試行エラー: {ex.Message}");
#endif
                // 次のリトライを待つ
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// カードリーダーへの手動再接続を試行します。
        /// </summary>
        /// <returns>再接続処理のTask</returns>
        /// <remarks>
        /// <para>
        /// 既存のモニターを停止し、新しいモニターを作成して接続を試みます。
        /// 再接続に失敗した場合は、自動再接続タイマーが開始されます（3秒間隔、最大10回）。
        /// </para>
        /// <para>
        /// 接続状態の変化は <see cref="ConnectionStateChanged"/> イベントで通知されます。
        /// </para>
        /// </remarks>
        public async Task ReconnectAsync()
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("手動再接続を開始");
#endif

            if (_connectionState == CardReaderConnectionState.Reconnecting)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine("既に再接続中です");
#endif
                return;
            }

            StopReconnectTimer();
            StopHealthCheckTimer();

            SetConnectionState(CardReaderConnectionState.Reconnecting);
            _reconnectAttempts = 0;

            await OnReconnectAttemptAsync();

            // 再接続に失敗した場合は自動再接続を開始
            if (_connectionState != CardReaderConnectionState.Connected)
            {
                StartReconnectTimer();
            }
        }

        #endregion

        /// <summary>
        /// FeliCaカードからIDmを読み取る
        /// </summary>
        private string ReadIdm(PcscICardReader reader)
        {
            // Get Data (UID) コマンド - FeliCaのIDmを取得
            var getUidCommand = new byte[]
            {
                0xFF, 0xCA, 0x00, 0x00, 0x00
            };

            var receiveBuffer = new byte[256];
            var bytesReturned = reader.Transmit(getUidCommand, receiveBuffer);

            // レスポンス: IDm(8バイト) + SW1 SW2(2バイト)
            if (bytesReturned >= 10)
            {
                // ステータスワードをチェック（90 00 = 成功）
                if (receiveBuffer[bytesReturned - 2] == 0x90 && receiveBuffer[bytesReturned - 1] == 0x00)
                {
                    // IDmは8バイト
                    var idm = BitConverter.ToString(receiveBuffer, 0, 8).Replace("-", "");
                    return idm;
                }
            }

            // 代替方法: Polling コマンドを試行
            return TryPollingCommand(reader);
        }

        /// <summary>
        /// FeliCa Pollingコマンドでカードを検索
        /// </summary>
        private string TryPollingCommand(PcscICardReader reader)
        {
            try
            {
                // FeliCa Polling コマンド
                // FF FE 00 00 06 00 [システムコード2バイト] [リクエストコード] [タイムスロット]
                var pollingCommand = new byte[]
                {
                    0xFF, 0xFE, 0x00, 0x00, 0x06, // ヘッダ
                    0x00,                          // Length
                    0x00, 0x03,                    // システムコード (0x0003 = サイバネ)
                    0x00,                          // リクエストコード
                    0x0F                           // タイムスロット
                };

                var receiveBuffer = new byte[256];
                var bytesReturned = reader.Transmit(pollingCommand, receiveBuffer);

                // レスポンス解析
                // 成功時: [Length] 01 [IDm 8バイト] [PMm 8バイト] ...
                if (bytesReturned >= 18 && receiveBuffer[1] == 0x01)
                {
                    var idm = BitConverter.ToString(receiveBuffer, 2, 8).Replace("-", "");
                    return idm;
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"Pollingコマンドエラー: {ex.Message}");
#endif
            }

            return null;
        }

        /// <summary>
        /// 指定したブロックを読み取る
        /// </summary>
        private byte[] ReadBlock(PcscICardReader reader, string idm, byte[] serviceCode, int blockIndex)
        {
            try
            {
                // FeliCa Read Without Encryption コマンド
                var idmBytes = StringToBytes(idm);

                // コマンド構築
                // FF FE 00 00 [Lc] [データ長] 06 [IDm 8バイト] [サービス数] [サービスコード2バイト] [ブロック数] [ブロックリスト]
                var command = new byte[32];
                var pos = 0;

                // PC/SC ヘッダ
                command[pos++] = 0xFF;
                command[pos++] = 0xFE;
                command[pos++] = 0x00;
                command[pos++] = 0x00;

                // Lcは後で設定
                var lcPos = pos++;

                // データ長は後で設定
                var lengthPos = pos++;

                // コマンドコード (Read Without Encryption = 0x06)
                command[pos++] = 0x06;

                // IDm
                Array.Copy(idmBytes, 0, command, pos, 8);
                pos += 8;

                // サービス数
                command[pos++] = 0x01;

                // サービスコード（リトルエンディアン）
                command[pos++] = serviceCode[0];
                command[pos++] = serviceCode[1];

                // ブロック数
                command[pos++] = 0x01;

                // ブロックリスト（2バイト形式）
                command[pos++] = 0x80; // 2バイトブロックリスト要素
                command[pos++] = (byte)blockIndex;

                // 長さを設定
                command[lcPos] = (byte)(pos - lcPos - 1);
                command[lengthPos] = (byte)(pos - lengthPos - 1);

                var receiveBuffer = new byte[256];
                // .NET Framework 4.8ではRange表現が使えないためArraySegmentを使用
                var commandToSend = new byte[pos];
                Array.Copy(command, 0, commandToSend, 0, pos);
                var bytesReturned = reader.Transmit(commandToSend, receiveBuffer);

                // レスポンス解析
                // [データ長] 07 [IDm 8バイト] [ステータス1] [ステータス2] [ブロック数] [ブロックデータ 16バイト] ...
                if (bytesReturned >= 28)
                {
                    // ステータスフラグをチェック
                    var statusFlag1 = receiveBuffer[10];
                    var statusFlag2 = receiveBuffer[11];

                    if (statusFlag1 == 0x00 && statusFlag2 == 0x00)
                    {
                        // ブロックデータを抽出（13バイト目から16バイト）
                        var data = new byte[16];
                        Array.Copy(receiveBuffer, 13, data, 0, 16);
                        return data;
                    }
                    else
                    {
#if DEBUG
                        System.Diagnostics.Debug.WriteLine($"ブロック読み取りエラー: ステータス={statusFlag1:X2} {statusFlag2:X2}");
#endif
                    }
                }
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"ブロック{blockIndex}読み取り失敗: {ex.Message}");
#endif
            }

            return null;
        }

        /// <summary>
        /// 履歴データをパースしてLedgerDetailに変換
        /// </summary>
        /// <param name="currentData">現在のレコードデータ</param>
        /// <param name="previousData">前回のレコードデータ（金額計算用）</param>
        /// <param name="cardType">カード種別（駅名検索の優先エリア決定に使用）</param>
        private LedgerDetail ParseHistoryData(byte[] currentData, byte[] previousData, CardType cardType)
        {
            if (currentData == null || currentData.Length < 16)
            {
                return null;
            }

            try
            {
                // バイト0: 機器種別
                var terminalType = currentData[0];

                // バイト1: 利用種別
                var usageType = currentData[1];

                // バイト2: 支払種別
                // バイト3: 入出場種別

                // バイト4-5: 日付（年月日、2000年起点のビットフィールド）
                var dateValue = (currentData[4] << 8) | currentData[5];
                var year = 2000 + ((dateValue >> 9) & 0x7F);
                var month = (dateValue >> 5) & 0x0F;
                var day = dateValue & 0x1F;

                DateTime? useDate = null;
                if (year >= 2000 && month >= 1 && month <= 12 && day >= 1 && day <= 31)
                {
                    try
                    {
                        useDate = new DateTime(year, month, day);
                    }
                    catch
                    {
                        // 無効な日付は無視
                    }
                }

                // バイト6-7: 入場駅コード
                var entryStationCode = (currentData[6] << 8) | currentData[7];

                // バイト8-9: 出場駅コード
                var exitStationCode = (currentData[8] << 8) | currentData[9];

                // バイト10-11: 残額（リトルエンディアン）
                var balance = currentData[10] + (currentData[11] << 8);

                // 前回の残高を取得（金額計算用）
                int? previousBalance = null;
                if (previousData != null && previousData.Length >= 12)
                {
                    previousBalance = previousData[10] + (previousData[11] << 8);
                }

                // 利用種別の判定
                // 0x02 = チャージ, 0x07 = 物販, その他 = 交通利用
                var isCharge = usageType == 0x02;

                // バス利用の判定: 駅コードが両方0かつチャージでない場合
                var isBus = !isCharge && entryStationCode == 0 && exitStationCode == 0;

                // 金額の計算
                int? amount = null;
                if (previousBalance.HasValue)
                {
                    if (isCharge)
                    {
                        // チャージ: 現在残高 - 前回残高 = チャージ額
                        amount = balance - previousBalance.Value;
                    }
                    else
                    {
                        // 利用: 前回残高 - 現在残高 = 利用額
                        amount = previousBalance.Value - balance;
                    }
                }

#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"履歴: 日付={useDate:yyyy/MM/dd}, 入場={entryStationCode:X4}, 出場={exitStationCode:X4}, " +
                    $"残高={balance}, 金額={amount}, チャージ={isCharge}, バス={isBus}");
#endif

                // 生データを保持（デバッグ・診断用）
                var rawBytes = new byte[16];
                Array.Copy(currentData, 0, rawBytes, 0, Math.Min(currentData.Length, 16));

                return new LedgerDetail
                {
                    UseDate = useDate,
                    EntryStation = entryStationCode > 0 ? GetStationName(entryStationCode, cardType) : null,
                    ExitStation = exitStationCode > 0 ? GetStationName(exitStationCode, cardType) : null,
                    Amount = amount,
                    Balance = balance,
                    IsCharge = isCharge,
                    IsBus = isBus,
                    RawBytes = rawBytes
                };
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"履歴データのパースエラー: {ex.Message}");
#endif
                return null;
            }
        }

        /// <summary>
        /// 駅コードから駅名を取得
        /// </summary>
        /// <remarks>
        /// StationMasterServiceを使用して駅コードマスタから駅名を解決する。
        /// カード種別に応じて適切なエリア（関東/関西/中部/九州）を優先的に検索する。
        /// </remarks>
        /// <param name="stationCode">駅コード（上位バイト:路線コード, 下位バイト:駅番号）</param>
        /// <param name="cardType">カード種別（優先エリアの決定に使用）</param>
        private static string GetStationName(int stationCode, CardType cardType)
        {
            return StationMasterService.Instance.GetStationName(stationCode, cardType);
        }

        /// <summary>
        /// 16進数文字列をバイト配列に変換
        /// </summary>
        private static byte[] StringToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
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
                    // 同期版メソッドを直接呼び出してデッドロックを防止
                    StopReadingCore();
                    _provider.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
