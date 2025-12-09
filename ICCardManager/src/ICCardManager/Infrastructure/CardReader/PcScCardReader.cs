using ICCardManager.Models;
using PCSC;
using PCSC.Exceptions;
using PCSC.Iso7816;
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
    /// FeliCaのシステムコード（サイバネ規格）
    /// </summary>
    private const ushort CyberneSysytemCode = 0x0003;

    /// <summary>
    /// FeliCa Lite/Lite-Sのシステムコード
    /// </summary>
    private const ushort FeliCaLiteSystemCode = 0x88B4;

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
                    throw new InvalidOperationException("カードリーダーが見つかりません。");
                }

                _monitor = MonitorFactory.Instance.Create(SCardScope.System);
                _monitor.CardInserted += OnCardInserted;
                _monitor.MonitorException += OnMonitorException;
                _monitor.Start(readerNames);

                _isReading = true;
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
                _monitor.MonitorException -= OnMonitorException;
                _monitor.Cancel();
                _monitor.Dispose();
                _monitor = null;
            }

            _isReading = false;
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
                    return;
                }

                using var reader = _context.ConnectReader(readerNames[0], SCardShareMode.Shared, SCardProtocol.Any);

                // FeliCaの履歴読み取りコマンド
                // サービスコード: 0x090F（履歴情報）
                var serviceCode = new byte[] { 0x0F, 0x09 };
                const int maxHistoryCount = 20;

                for (var blockIndex = 0; blockIndex < maxHistoryCount; blockIndex++)
                {
                    var historyData = ReadBlock(reader, idm, serviceCode, blockIndex);
                    if (historyData == null || historyData.All(b => b == 0))
                    {
                        break;
                    }

                    var detail = ParseHistoryData(historyData);
                    if (detail != null)
                    {
                        details.Add(detail);
                    }
                }
            }
            catch (Exception ex)
            {
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
                    return historyData[10] + (historyData[11] << 8);
                }

                return null;
            }
            catch (Exception ex)
            {
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
            using var reader = _context.ConnectReader(e.ReaderName, SCardShareMode.Shared, SCardProtocol.Any);

            // IDmを読み取り
            var idm = ReadIdm(reader);
            if (!string.IsNullOrEmpty(idm))
            {
                CardRead?.Invoke(this, new CardReadEventArgs
                {
                    Idm = idm,
                    SystemCode = CyberneSysytemCode.ToString("X4")
                });
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// モニター例外時のイベントハンドラ
    /// </summary>
    private void OnMonitorException(object sender, PCSCException ex)
    {
        Error?.Invoke(this, ex);
    }

    /// <summary>
    /// FeliCaカードからIDmを読み取る
    /// </summary>
    private string? ReadIdm(PcscICardReader reader)
    {
        // FeliCa Polling コマンド
        var pollingCommand = new byte[]
        {
            0xFF, 0xCA, 0x00, 0x00, 0x00 // Get Data (UID)
        };

        var receiveBuffer = new byte[256];
        var bytesReturned = reader.Transmit(pollingCommand, receiveBuffer);

        if (bytesReturned >= 8)
        {
            // IDmは8バイト
            var idm = BitConverter.ToString(receiveBuffer, 0, 8).Replace("-", "");
            return idm;
        }

        return null;
    }

    /// <summary>
    /// 指定したブロックを読み取る
    /// </summary>
    private byte[]? ReadBlock(PcscICardReader reader, string idm, byte[] serviceCode, int blockIndex)
    {
        // FeliCa Read Without Encryption コマンド
        var idmBytes = StringToBytes(idm);

        var command = new byte[16 + idmBytes.Length];
        var pos = 0;

        // コマンドヘッダ
        command[pos++] = 0xFF;
        command[pos++] = 0xFE;
        command[pos++] = 0x00;
        command[pos++] = 0x00;

        // Lengthは後で設定
        var lengthPos = pos++;

        // コマンドコード (Read Without Encryption)
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

        // ブロックリスト
        command[pos++] = 0x80; // 2バイトブロックリスト
        command[pos++] = (byte)blockIndex;

        // Length設定
        command[lengthPos] = (byte)(pos - lengthPos - 1);

        var receiveBuffer = new byte[256];

        try
        {
            var bytesReturned = reader.Transmit(command[..(pos)], receiveBuffer);

            if (bytesReturned >= 16)
            {
                // レスポンスからデータ部分を抽出
                var data = new byte[16];
                Array.Copy(receiveBuffer, bytesReturned - 18, data, 0, 16);
                return data;
            }
        }
        catch
        {
            // 読み取り失敗
        }

        return null;
    }

    /// <summary>
    /// 履歴データをパースしてLedgerDetailに変換
    /// </summary>
    private LedgerDetail? ParseHistoryData(byte[] data)
    {
        if (data == null || data.Length < 16)
        {
            return null;
        }

        try
        {
            // バイト0: 機器種別
            // バイト1: 利用種別
            var usageType = data[1];

            // バイト2: 支払種別
            // バイト3: 入出場種別

            // バイト4-5: 日付（年月日、2000年起点）
            var dateValue = (data[4] << 8) | data[5];
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
                    // 無効な日付
                }
            }

            // バイト6-7: 入場駅コード
            var entryStationCode = (data[6] << 8) | data[7];

            // バイト8-9: 出場駅コード
            var exitStationCode = (data[8] << 8) | data[9];

            // バイト10-11: 残額（リトルエンディアン）
            var balance = data[10] + (data[11] << 8);

            // バイト12-14: 連番
            // バイト15: 地域コード

            // 利用種別の判定
            var isCharge = usageType == 0x02; // チャージ
            var isBus = !isCharge && entryStationCode == 0 && exitStationCode == 0;

            // 金額の計算（前回残高との差分が必要だが、ここでは簡略化）
            // 実際の実装では前回の残高と比較して金額を計算する

            return new LedgerDetail
            {
                UseDate = useDate,
                EntryStation = entryStationCode > 0 ? GetStationName(entryStationCode) : null,
                ExitStation = exitStationCode > 0 ? GetStationName(exitStationCode) : null,
                Balance = balance,
                IsCharge = isCharge,
                IsBus = isBus
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 駅コードから駅名を取得
    /// </summary>
    /// <remarks>
    /// 実際の実装では駅コードテーブルを参照する必要がある
    /// ここでは仮実装としてコードをそのまま返す
    /// </remarks>
    private string GetStationName(int stationCode)
    {
        // 実際の実装では駅コードマスタを参照
        // ここでは簡略化のため、コードを16進数文字列で返す
        return $"駅{stationCode:X4}";
    }

    /// <summary>
    /// 16進数文字列をバイト配列に変換
    /// </summary>
    private byte[] StringToBytes(string hex)
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
