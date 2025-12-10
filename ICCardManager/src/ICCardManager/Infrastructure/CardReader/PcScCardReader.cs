using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Services;
using PCSC;
using PCSC.Exceptions;
using PCSC.Monitoring;
using PcscICardReader = PCSC.ICardReader;

namespace ICCardManager.Infrastructure.CardReader;

/// <summary>
/// PC/SC APIを使用したICカードリーダー実装
/// PaSoRi等のNFCリーダーで交通系ICカードを読み取る
/// </summary>
public class PcScCardReader : ICardReader
{
    private readonly ISCardContext _context;
    private ISCardMonitor? _monitor;
    private bool _isReading;
    private bool _disposed;

    /// <summary>
    /// 最後に読み取ったカードのIDm
    /// </summary>
    private string? _lastReadIdm;

    /// <summary>
    /// 最後にカードを読み取った時刻
    /// </summary>
    private DateTime _lastReadTime = DateTime.MinValue;

    /// <summary>
    /// 同一カードの連続読み取りを防止する時間（ミリ秒）
    /// </summary>
    private const int DuplicateReadPreventionMs = 1000;

    /// <summary>
    /// FeliCaのシステムコード（サイバネ規格）
    /// </summary>
    private const ushort CyberneSystemCode = 0x0003;

    public event EventHandler<CardReadEventArgs>? CardRead;
    public event EventHandler<Exception>? Error;

    public bool IsReading => _isReading;

    public PcScCardReader()
    {
        _context = ContextFactory.Instance.Establish(SCardScope.System);
    }

    /// <inheritdoc/>
    public Task StartReadingAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var readerNames = _context.GetReaders();
                if (readerNames == null || readerNames.Length == 0)
                {
                    throw new InvalidOperationException(
                        "カードリーダーが見つかりません。PaSoRiが接続されていることを確認してください。");
                }

                System.Diagnostics.Debug.WriteLine($"検出されたカードリーダー: {string.Join(", ", readerNames)}");

                _monitor = MonitorFactory.Instance.Create(SCardScope.System);
                _monitor.CardInserted += OnCardInserted;
                _monitor.CardRemoved += OnCardRemoved;
                _monitor.MonitorException += OnMonitorException;
                _monitor.Start(readerNames);

                _isReading = true;
                System.Diagnostics.Debug.WriteLine("カードリーダー監視を開始しました");
            }
            catch (PCSCException ex)
            {
                var errorMessage = ex.SCardError switch
                {
                    SCardError.NoService => "スマートカードサービスが起動していません。",
                    SCardError.NoReadersAvailable => "カードリーダーが見つかりません。",
                    _ => $"カードリーダーエラー: {ex.Message}"
                };
                var wrappedException = new InvalidOperationException(errorMessage, ex);
                Error?.Invoke(this, wrappedException);
                throw wrappedException;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, ex);
                throw;
            }
        });
    }

    /// <inheritdoc/>
    public Task StopReadingAsync()
    {
        return Task.Run(() =>
        {
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
            System.Diagnostics.Debug.WriteLine("カードリーダー監視を停止しました");
        });
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm)
    {
        var details = new List<LedgerDetail>();

        await Task.Run(() =>
        {
            try
            {
                var readerNames = _context.GetReaders();
                if (readerNames == null || readerNames.Length == 0)
                {
                    System.Diagnostics.Debug.WriteLine("履歴読み取り: カードリーダーが見つかりません");
                    return;
                }

                using var reader = _context.ConnectReader(readerNames[0], SCardShareMode.Shared, SCardProtocol.Any);

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

                System.Diagnostics.Debug.WriteLine($"履歴読み取り: {historyDataList.Count}件のデータを取得");

                // IDmからカード種別を判定
                var cardType = CardTypeDetector.DetectFromIdm(idm);

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
                System.Diagnostics.Debug.WriteLine($"履歴読み取りエラー(PCSC): {ex.Message}");
                Error?.Invoke(this, new InvalidOperationException($"カードの履歴読み取りに失敗しました: {ex.Message}", ex));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"履歴読み取りエラー: {ex.Message}");
                Error?.Invoke(this, ex);
            }
        });

        return details;
    }

    /// <inheritdoc/>
    public async Task<int?> ReadBalanceAsync(string idm)
    {
        return await Task.Run<int?>(() =>
        {
            try
            {
                var readerNames = _context.GetReaders();
                if (readerNames == null || readerNames.Length == 0)
                {
                    return null;
                }

                using var reader = _context.ConnectReader(readerNames[0], SCardShareMode.Shared, SCardProtocol.Any);

                // 残高読み取り（履歴の最新レコードから取得）
                var serviceCode = new byte[] { 0x0F, 0x09 };
                var historyData = ReadBlock(reader, idm, serviceCode, 0);

                if (historyData != null && historyData.Length >= 12)
                {
                    // バイト10-11が残高（リトルエンディアン）
                    var balance = historyData[10] + (historyData[11] << 8);
                    System.Diagnostics.Debug.WriteLine($"残高読み取り: {balance}円");
                    return balance;
                }

                return null;
            }
            catch (PCSCException ex)
            {
                System.Diagnostics.Debug.WriteLine($"残高読み取りエラー(PCSC): {ex.Message}");
                Error?.Invoke(this, new InvalidOperationException($"カードの残高読み取りに失敗しました: {ex.Message}", ex));
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"残高読み取りエラー: {ex.Message}");
                Error?.Invoke(this, ex);
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
            System.Diagnostics.Debug.WriteLine($"カード検出: リーダー={e.ReaderName}");

            using var reader = _context.ConnectReader(e.ReaderName, SCardShareMode.Shared, SCardProtocol.Any);

            // IDmを読み取り
            var idm = ReadIdm(reader);
            if (string.IsNullOrEmpty(idm))
            {
                System.Diagnostics.Debug.WriteLine("IDmの読み取りに失敗しました");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"IDm読み取り成功: {idm}");

            // 同一カードの連続読み取りを防止
            var now = DateTime.Now;
            if (idm == _lastReadIdm && (now - _lastReadTime).TotalMilliseconds < DuplicateReadPreventionMs)
            {
                System.Diagnostics.Debug.WriteLine("同一カードの連続読み取りを無視");
                return;
            }

            _lastReadIdm = idm;
            _lastReadTime = now;

            CardRead?.Invoke(this, new CardReadEventArgs
            {
                Idm = idm,
                SystemCode = CyberneSystemCode.ToString("X4")
            });
        }
        catch (PCSCException ex)
        {
            System.Diagnostics.Debug.WriteLine($"カード読み取りエラー(PCSC): {ex.Message}, SCardError={ex.SCardError}");
            // カードが素早く離された場合などは無視
            if (ex.SCardError != SCardError.RemovedCard)
            {
                Error?.Invoke(this, new InvalidOperationException($"カードの読み取りに失敗しました: {ex.Message}", ex));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"カード読み取りエラー: {ex.Message}");
            Error?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// カード取り外し時のイベントハンドラ
    /// </summary>
    private void OnCardRemoved(object sender, CardStatusEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"カード取り外し: リーダー={e.ReaderName}");
    }

    /// <summary>
    /// モニター例外時のイベントハンドラ
    /// </summary>
    private void OnMonitorException(object sender, PCSCException ex)
    {
        System.Diagnostics.Debug.WriteLine($"モニター例外: {ex.Message}");
        Error?.Invoke(this, new InvalidOperationException($"カードリーダー監視エラー: {ex.Message}", ex));
    }

    /// <summary>
    /// FeliCaカードからIDmを読み取る
    /// </summary>
    private string? ReadIdm(PcscICardReader reader)
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
    private string? TryPollingCommand(PcscICardReader reader)
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
            System.Diagnostics.Debug.WriteLine($"Pollingコマンドエラー: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 指定したブロックを読み取る
    /// </summary>
    private byte[]? ReadBlock(PcscICardReader reader, string idm, byte[] serviceCode, int blockIndex)
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
            var bytesReturned = reader.Transmit(command[..pos], receiveBuffer);

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
                    System.Diagnostics.Debug.WriteLine($"ブロック読み取りエラー: ステータス={statusFlag1:X2} {statusFlag2:X2}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ブロック{blockIndex}読み取り失敗: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 履歴データをパースしてLedgerDetailに変換
    /// </summary>
    /// <param name="currentData">現在のレコードデータ</param>
    /// <param name="previousData">前回のレコードデータ（金額計算用）</param>
    /// <param name="cardType">カード種別（駅名検索の優先エリア決定に使用）</param>
    private LedgerDetail? ParseHistoryData(byte[] currentData, byte[]? previousData, CardType cardType)
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

            System.Diagnostics.Debug.WriteLine(
                $"履歴: 日付={useDate:yyyy/MM/dd}, 入場={entryStationCode:X4}, 出場={exitStationCode:X4}, " +
                $"残高={balance}, 金額={amount}, チャージ={isCharge}, バス={isBus}");

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
            System.Diagnostics.Debug.WriteLine($"履歴データのパースエラー: {ex.Message}");
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
                StopReadingAsync().Wait();
                _context.Dispose();
            }
            _disposed = true;
        }
    }
}
