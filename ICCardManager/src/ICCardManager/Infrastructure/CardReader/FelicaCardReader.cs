using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using FelicaLib;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Timer = System.Timers.Timer;

namespace ICCardManager.Infrastructure.CardReader
{
    /// <summary>
    /// Sony PaSoRi 用の FeliCa カードリーダー実装です。
    /// FelicaLib.DotNet を使用して交通系ICカードを読み取ります。
    /// </summary>
    /// <remarks>
    /// <para>
    /// このクラスは Sony PaSoRi (RC-S380 等) での FeliCa カード読み取りに特化しています。
    /// PC/SC API では読み取れない残高・履歴データを、FelicaLib.DotNet 経由で取得します。
    /// </para>
    /// <para>
    /// <strong>対応カード:</strong>
    /// 交通系ICカード（Suica、PASMO、ICOCA、nimoca、SUGOCA、はやかけん等）
    /// </para>
    /// <para>
    /// <strong>必要条件:</strong>
    /// </para>
    /// <list type="bullet">
    /// <item><description>Sony NFCポートソフトウェア がインストールされていること</description></item>
    /// <item><description>felicalib.dll が実行フォルダに配置されていること</description></item>
    /// </list>
    /// <para>
    /// <strong>felicalib.dll について:</strong>
    /// felicalib.dll は tmurakam/felicalib プロジェクト (https://github.com/tmurakam/felicalib) の
    /// 成果物です。このライブラリは BSD-2-Clause ライセンスで配布されています。
    /// Sony NFCポートソフトウェアには含まれないため、別途ビルドまたはリリースから取得してください。
    /// </para>
    /// </remarks>
    public class FelicaCardReader : ICardReader
    {
        private readonly ILogger<FelicaCardReader> _logger;

        /// <summary>
        /// FelicaUtility へのアクセスを同期するためのロックオブジェクト。
        /// FelicaLib.DotNet はスレッドセーフではないため、すべてのアクセスを直列化する必要があります。
        /// </summary>
        private readonly object _felicaLock = new object();

        /// <summary>
        /// 最後に読み取ったIDmの同期用ロックオブジェクト
        /// </summary>
        private readonly object _lastReadLock = new object();

        private Timer _pollingTimer;
        private Timer _healthCheckTimer;
        private bool _isReading;
        private bool _disposed;
        private CardReaderConnectionState _connectionState = CardReaderConnectionState.Disconnected;

        /// <summary>
        /// ポーリング処理中かどうか（再入防止用）
        /// </summary>
        private volatile bool _isPolling;

        /// <summary>
        /// 最後に読み取ったカードのIDm
        /// </summary>
        private string _lastReadIdm;

        /// <summary>
        /// 最後にカードを読み取った時刻
        /// </summary>
        private DateTime _lastReadTime = DateTime.MinValue;

        /// <summary>
        /// 同一カードの連続読み取りを防止する時間（ミリ秒）
        /// </summary>
        /// <remarks>
        /// 1500ms に設定した理由:
        /// - カードタッチ後、ユーザーがカードを離すまでの平均時間は約1秒
        /// - 誤って2回読み取りされることを防ぐため、余裕を持って1.5秒に設定
        /// - 30秒ルール（同一カード再タッチで逆処理）はLendingService側で制御
        /// </remarks>
        private const int DuplicateReadPreventionMs = 1500;

        /// <summary>
        /// カード検出のポーリング間隔（ミリ秒）
        /// </summary>
        /// <remarks>
        /// 500ms に設定した理由:
        /// - 短すぎる(100-200ms): CPUリソース消費が大きく、felicalib.dllへの負荷が高い
        /// - 長すぎる(1000ms以上): カードタッチからの反応が遅く、UXが悪化
        /// - 500ms: レスポンスとリソース消費のバランスが良好
        /// - 実測で安定動作を確認済み（300msでは不安定な場合があった）
        /// </remarks>
        private const int PollingIntervalMs = 500;

        /// <summary>
        /// ヘルスチェック間隔（ミリ秒）
        /// </summary>
        private const int HealthCheckIntervalMs = 10000;

        /// <summary>
        /// 最大履歴件数
        /// </summary>
        private const int MaxHistoryCount = 20;

        /// <summary>
        /// 残高サービスコード (サイバネ規格)
        /// </summary>
        private const int SuicaBalanceServiceCode = 0x008B;

        /// <summary>
        /// ワイルドカードシステムコード（すべてのFeliCaカードを検出）
        /// </summary>
        private const int WildcardSystemCode = 0xFFFF;

        public event EventHandler<CardReadEventArgs> CardRead;
        public event EventHandler<Exception> Error;
        public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

        public bool IsReading => _isReading;
        public CardReaderConnectionState ConnectionState => _connectionState;

        /// <summary>
        /// FelicaCardReader の新しいインスタンスを初期化します。
        /// </summary>
        /// <param name="logger">ロガー</param>
        public FelicaCardReader(ILogger<FelicaCardReader> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// カード読み取りを開始します。
        /// </summary>
        /// <remarks>
        /// ポーリングベースでカードを検出し、カードが検出されると <see cref="CardRead"/> イベントを発火します。
        /// </remarks>
        public Task StartReadingAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    // felicalib.dll の存在確認
                    if (!CheckFelicaLibAvailable())
                    {
                        var ex = new InvalidOperationException(
                            "felicalib.dll が見つからないか、Sony NFCポートソフトウェアがインストールされていません。");
                        SetConnectionState(CardReaderConnectionState.Disconnected, ex.Message);
                        throw ex;
                    }

                    _logger.LogInformation("FelicaCardReader: カード読み取りを開始します");

                    // ポーリングタイマーを開始
                    StartPollingTimer();

                    // ヘルスチェックタイマーを開始
                    StartHealthCheckTimer();

                    _isReading = true;
                    SetConnectionState(CardReaderConnectionState.Connected);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FelicaCardReader: 初期化エラー");
                    var cardReaderException = CardReaderException.ReadFailed(ex.Message, ex);
                    SetConnectionState(CardReaderConnectionState.Disconnected, cardReaderException.UserFriendlyMessage);
                    Error?.Invoke(this, cardReaderException);
                    throw cardReaderException;
                }
            });
        }

        /// <summary>
        /// カード読み取りを停止します。
        /// </summary>
        public Task StopReadingAsync()
        {
            return Task.Run(() =>
            {
                StopPollingTimer();
                StopHealthCheckTimer();

                _isReading = false;
                _lastReadIdm = null;
                SetConnectionState(CardReaderConnectionState.Disconnected);

                _logger.LogInformation("FelicaCardReader: カード読み取りを停止しました");
            });
        }

        /// <summary>
        /// カードから履歴を読み取ります。
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>利用履歴詳細のリスト（最大20件、新しい順）</returns>
        /// <remarks>
        /// 読み取り前にカードのIDmを検証し、指定されたカードと一致することを確認します。
        /// カードが載せ替えられた場合は空のリストを返します。
        /// </remarks>
        public async Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm)
        {
            var details = new List<LedgerDetail>();

            await Task.Run(() =>
            {
                lock (_felicaLock)
                {
                    try
                    {
                        // IDm検証: 読み取り対象のカードが載っていることを確認
                        var currentIdmBytes = FelicaUtility.GetIDm(FelicaSystemCode.Suica);
                        var currentIdm = GetIdmString(currentIdmBytes);
                        if (!string.Equals(currentIdm, idm, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("FelicaCardReader: 履歴読み取り時にIDmが一致しません。期待={Expected}, 実際={Actual}", idm, currentIdm);
                            return;
                        }

                        // FelicaUtility.ReadBlocksWithoutEncryption で複数ブロックを一括取得
                        var historyDataList = new List<byte[]>();

                        _logger.LogDebug("FelicaCardReader: 履歴読み取り開始 (システムコード=0x{SystemCode:X4}, サービスコード=0x{ServiceCode:X4})",
                            (int)FelicaSystemCode.Suica, (int)FelicaServiceCode.SuicaHistory);

                        int blockIndex = 0;
                        foreach (var data in FelicaUtility.ReadBlocksWithoutEncryption(
                            FelicaSystemCode.Suica,
                            FelicaServiceCode.SuicaHistory,
                            0,
                            MaxHistoryCount))
                        {
                            if (data == null)
                            {
                                _logger.LogDebug("FelicaCardReader: ブロック{Index}はnull", blockIndex);
                                break;
                            }
                            if (data.All(b => b == 0))
                            {
                                _logger.LogDebug("FelicaCardReader: ブロック{Index}は全て0", blockIndex);
                                break;
                            }
                            _logger.LogDebug("FelicaCardReader: ブロック{Index}: {Data}", blockIndex, BitConverter.ToString(data));
                            historyDataList.Add(data);
                            blockIndex++;
                        }

                        _logger.LogInformation("FelicaCardReader: 履歴データを {Count} 件取得", historyDataList.Count);

                        // 駅名解決にはCardType.Unknownを使用
                        // 注: IDmの先頭2バイトは製造者コードであり、カード種別ではないため、
                        //     CardTypeDetector.DetectFromIdmは信頼できない
                        //     StationMasterServiceのUnknown優先順位（九州優先）が使用される
                        var cardType = CardType.Unknown;

                        // 履歴データをパースして金額を計算
                        for (int i = 0; i < historyDataList.Count; i++)
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
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FelicaCardReader: 履歴読み取りエラー");
                        Error?.Invoke(this, CardReaderException.HistoryReadFailed(ex.Message, ex));
                    }
                }
            });

            return details;
        }

        /// <summary>
        /// カードの残高を読み取ります。
        /// </summary>
        /// <param name="idm">カードのIDm</param>
        /// <returns>残高（円）。読み取り失敗時は null</returns>
        /// <remarks>
        /// <para>
        /// 読み取り前にカードのIDmを検証し、指定されたカードと一致することを確認します。
        /// カードが載せ替えられた場合は null を返します。
        /// </para>
        /// <para>
        /// 残高は以下の順序で取得を試みます：
        /// 1. 残高専用サービスコード (0x008B) から直接取得
        /// 2. 履歴サービスコードの最新レコードから取得（フォールバック）
        /// </para>
        /// </remarks>
        public async Task<int?> ReadBalanceAsync(string idm)
        {
            return await Task.Run<int?>(() =>
            {
                lock (_felicaLock)
                {
                    try
                    {
                        // IDm検証: 読み取り対象のカードが載っていることを確認
                        var currentIdmBytes = FelicaUtility.GetIDm(FelicaSystemCode.Suica);
                        var currentIdm = GetIdmString(currentIdmBytes);
                        if (!string.Equals(currentIdm, idm, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("FelicaCardReader: 残高読み取り時にIDmが一致しません。期待={Expected}, 実際={Actual}", idm, currentIdm);
                            return null;
                        }

                        // 方法1: 残高専用サービスコード (0x008B) から直接取得
                        try
                        {
                            _logger.LogDebug("FelicaCardReader: 残高サービス(0x{ServiceCode:X4})から読み取り試行", SuicaBalanceServiceCode);
                            var balanceData = FelicaUtility.ReadWithoutEncryption(
                                FelicaSystemCode.Suica,
                                SuicaBalanceServiceCode,
                                0);

                            if (balanceData != null && balanceData.Length >= 2)
                            {
                                _logger.LogDebug("FelicaCardReader: 残高サービスデータ: {Data}", BitConverter.ToString(balanceData));
                                // バイト0-1が残高（リトルエンディアン）
                                var balance = balanceData[0] + (balanceData[1] << 8);
                                _logger.LogDebug("FelicaCardReader: 残高読み取り成功（残高サービス）: {Balance}円", balance);
                                return balance;
                            }
                            else
                            {
                                _logger.LogDebug("FelicaCardReader: 残高サービスからのデータがnullまたは短い: Length={Length}", balanceData?.Length ?? -1);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("FelicaCardReader: 残高サービスからの読み取り失敗、履歴にフォールバック: {Message}", ex.Message);
                        }

                        // 方法2: 履歴サービスコードの最新レコードから取得（フォールバック）
                        _logger.LogDebug("FelicaCardReader: 履歴サービス(0x{ServiceCode:X4})から読み取り試行", (int)FelicaServiceCode.SuicaHistory);
                        var historyData = FelicaUtility.ReadWithoutEncryption(
                            FelicaSystemCode.Suica,
                            FelicaServiceCode.SuicaHistory,
                            0);

                        if (historyData != null && historyData.Length >= 12)
                        {
                            _logger.LogDebug("FelicaCardReader: 履歴サービスデータ: {Data}", BitConverter.ToString(historyData));
                            // バイト10-11が残高（リトルエンディアン）
                            var balance = historyData[10] + (historyData[11] << 8);
                            _logger.LogDebug("FelicaCardReader: 残高読み取り成功（履歴サービス）: {Balance}円 (byte10=0x{Byte10:X2}, byte11=0x{Byte11:X2})",
                                balance, historyData[10], historyData[11]);
                            return balance;
                        }
                        else
                        {
                            _logger.LogDebug("FelicaCardReader: 履歴サービスからのデータがnullまたは短い: Length={Length}", historyData?.Length ?? -1);
                        }

                        _logger.LogWarning("FelicaCardReader: 残高データを取得できませんでした");
                        return null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "FelicaCardReader: 残高読み取りエラー");
                        Error?.Invoke(this, CardReaderException.BalanceReadFailed(ex.Message, ex));
                        return null;
                    }
                }
            });
        }

        /// <summary>
        /// 接続状態を確認します。
        /// </summary>
        /// <returns>接続中の場合 true</returns>
        public Task<bool> CheckConnectionAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    return CheckFelicaLibAvailable();
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// 再接続を試行します。
        /// </summary>
        public async Task ReconnectAsync()
        {
            _logger.LogInformation("FelicaCardReader: 再接続を試行します");

            await StopReadingAsync();

            try
            {
                await StartReadingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FelicaCardReader: 再接続に失敗しました");
            }
        }

        #region Private Methods

        /// <summary>
        /// felicalib.dll が利用可能かチェック
        /// </summary>
        private bool CheckFelicaLibAvailable()
        {
            lock (_felicaLock)
            {
                try
                {
                    // FelicaUtility でテスト読み取りを試行
                    // ワイルドカードシステムコードを使用してすべてのFeliCaカードに対応
                    // カードがなくても例外が発生しなければ DLL は存在する
                    _ = FelicaUtility.GetIDm(WildcardSystemCode);
                    return true;
                }
                catch (DllNotFoundException)
                {
                    _logger.LogError("FelicaCardReader: felicalib.dll が見つかりません");
                    return false;
                }
                catch (Exception ex)
                {
                    // カードがない、リーダーが接続されていない等の場合はエラーになるが、
                    // DLL自体は存在する
                    _logger.LogDebug("FelicaCardReader: ヘルスチェック例外（カードなし等）: {Message}", ex.Message);
                    return true;
                }
            }
        }

        /// <summary>
        /// ポーリングタイマーを開始
        /// </summary>
        private void StartPollingTimer()
        {
            StopPollingTimer();

            _pollingTimer = new Timer(PollingIntervalMs);
            _pollingTimer.Elapsed += OnPollingTimerElapsed;
            _pollingTimer.AutoReset = true;
            _pollingTimer.Start();

            _logger.LogDebug("FelicaCardReader: ポーリングタイマー開始 ({Interval}ms)", PollingIntervalMs);
        }

        /// <summary>
        /// ポーリングタイマーを停止
        /// </summary>
        private void StopPollingTimer()
        {
            if (_pollingTimer != null)
            {
                _pollingTimer.Stop();
                _pollingTimer.Elapsed -= OnPollingTimerElapsed;
                _pollingTimer.Dispose();
                _pollingTimer = null;
            }
        }

        /// <summary>
        /// ポーリングタイマーのイベントハンドラ
        /// </summary>
        private void OnPollingTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // 再入防止: 前回のポーリングが完了していない場合はスキップ
            if (_isPolling)
            {
                return;
            }

            try
            {
                _isPolling = true;

                string idm;

                // FelicaUtility へのアクセスはすべてロックで保護
                lock (_felicaLock)
                {
                    // ワイルドカードシステムコード（0xFFFF）ですべてのFeliCaカードを検出
                    // これにより、交通系ICカードだけでなく職員証なども検出できる
                    var idmBytes = FelicaUtility.GetIDm(WildcardSystemCode);
                    if (idmBytes == null || idmBytes.Length == 0)
                    {
                        return; // カードなし
                    }

                    idm = GetIdmString(idmBytes);
                }

                if (string.IsNullOrEmpty(idm))
                {
                    return;
                }

                // 同一カードの連続読み取りを防止（スレッドセーフ）
                lock (_lastReadLock)
                {
                    var now = DateTime.Now;
                    if (idm == _lastReadIdm && (now - _lastReadTime).TotalMilliseconds < DuplicateReadPreventionMs)
                    {
                        return;
                    }

                    _lastReadIdm = idm;
                    _lastReadTime = now;
                }

                _logger.LogInformation("FelicaCardReader: カード検出 IDm={Idm}", idm);

                // イベントを発火（UIスレッドで処理されるため、ここでは即座に返る）
                CardRead?.Invoke(this, new CardReadEventArgs
                {
                    Idm = idm,
                    SystemCode = FelicaSystemCode.Suica.ToString("X4")
                });
            }
            catch (Exception ex)
            {
                // Polling 失敗はカードが載っていない場合も含むので、Traceレベルでログ出力
                // 通常運用時は出力されず、詳細デバッグ時のみ確認可能
                _logger.LogTrace("FelicaCardReader: ポーリング例外（カードなし等）: {Message}", ex.Message);
            }
            finally
            {
                _isPolling = false;
            }
        }

        /// <summary>
        /// ヘルスチェックタイマーを開始
        /// </summary>
        private void StartHealthCheckTimer()
        {
            StopHealthCheckTimer();

            _healthCheckTimer = new Timer(HealthCheckIntervalMs);
            _healthCheckTimer.Elapsed += OnHealthCheckTimerElapsed;
            _healthCheckTimer.AutoReset = true;
            _healthCheckTimer.Start();
        }

        /// <summary>
        /// ヘルスチェックタイマーを停止
        /// </summary>
        private void StopHealthCheckTimer()
        {
            if (_healthCheckTimer != null)
            {
                _healthCheckTimer.Stop();
                _healthCheckTimer.Elapsed -= OnHealthCheckTimerElapsed;
                _healthCheckTimer.Dispose();
                _healthCheckTimer = null;
            }
        }

        /// <summary>
        /// ヘルスチェックタイマーのイベントハンドラ
        /// </summary>
        private void OnHealthCheckTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // ポーリング中はヘルスチェックをスキップ（デッドロック防止）
            if (_isPolling)
            {
                return;
            }

            try
            {
                var isAvailable = CheckFelicaLibAvailable();
                if (!isAvailable && _connectionState == CardReaderConnectionState.Connected)
                {
                    _logger.LogWarning("FelicaCardReader: 接続が失われました");
                    SetConnectionState(CardReaderConnectionState.Disconnected, "接続が失われました");
                }
                else if (isAvailable && _connectionState == CardReaderConnectionState.Disconnected)
                {
                    _logger.LogInformation("FelicaCardReader: 接続が回復しました");
                    SetConnectionState(CardReaderConnectionState.Connected, "接続が回復しました");
                }
            }
            catch (Exception ex)
            {
                // ヘルスチェックの例外はアプリをクラッシュさせない（Traceレベルでログ出力）
                _logger.LogTrace("FelicaCardReader: ヘルスチェック例外: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 接続状態を設定しイベントを発火
        /// </summary>
        private void SetConnectionState(CardReaderConnectionState state, string message = null)
        {
            if (_connectionState == state)
            {
                return;
            }

            _connectionState = state;
            _logger.LogDebug("FelicaCardReader: 接続状態変更 {State} ({Message})", state, message);
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message));
        }

        /// <summary>
        /// IDm バイト配列を16進数文字列に変換
        /// </summary>
        private static string GetIdmString(byte[] idmBytes)
        {
            if (idmBytes == null || idmBytes.Length == 0)
            {
                return null;
            }
            return BitConverter.ToString(idmBytes).Replace("-", "");
        }

        /// <summary>
        /// 履歴データをパースして LedgerDetail に変換
        /// </summary>
        /// <param name="currentData">現在のレコードデータ</param>
        /// <param name="previousData">前回のレコードデータ（金額計算用）</param>
        /// <param name="cardType">カード種別</param>
        private LedgerDetail ParseHistoryData(byte[] currentData, byte[] previousData, CardType cardType)
        {
            if (currentData == null || currentData.Length < 16)
            {
                return null;
            }

            try
            {
                // バイト0: 機器種別
                // バイト1: 利用種別
                var usageType = currentData[1];

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
                var isCharge = usageType == 0x02;

                // バス利用の判定: 駅コードが両方0かつチャージでない場合
                var isBus = !isCharge && entryStationCode == 0 && exitStationCode == 0;

                // 金額の計算
                int? amount = null;
                if (previousBalance.HasValue)
                {
                    if (isCharge)
                    {
                        amount = balance - previousBalance.Value;
                    }
                    else
                    {
                        amount = previousBalance.Value - balance;
                    }
                }

                return new LedgerDetail
                {
                    UseDate = useDate,
                    EntryStation = entryStationCode > 0 ? GetStationName(entryStationCode, cardType) : null,
                    ExitStation = exitStationCode > 0 ? GetStationName(exitStationCode, cardType) : null,
                    Amount = amount,
                    Balance = balance,
                    IsCharge = isCharge,
                    IsBus = isBus
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FelicaCardReader: 履歴データのパースエラー");
                return null;
            }
        }

        /// <summary>
        /// 駅コードから駅名を取得
        /// </summary>
        private static string GetStationName(int stationCode, CardType cardType)
        {
            return StationMasterService.Instance.GetStationName(stationCode, cardType);
        }

        #endregion

        #region IDisposable

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
                    StopPollingTimer();
                    StopHealthCheckTimer();
                }
                _disposed = true;
            }
        }

        #endregion
    }
}
