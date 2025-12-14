using ICCardManager.Models;

namespace ICCardManager.Infrastructure.CardReader;

/// <summary>
/// テスト用のモックICカードリーダー
/// </summary>
public class MockCardReader : ICardReader
{
    private bool _isReading;
    private bool _disposed;
    private CancellationTokenSource? _cancellationTokenSource;

    public event EventHandler<CardReadEventArgs>? CardRead;
    public event EventHandler<Exception>? Error;
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public bool IsReading => _isReading;
    public CardReaderConnectionState ConnectionState => CardReaderConnectionState.Connected;

    /// <summary>
    /// シミュレーション用のカードIDm
    /// </summary>
    public List<string> MockCards { get; } = new()
    {
        "07FE112233445566", // はやかけん
        "05FE112233445567", // nimoca
        "06FE112233445568", // SUGOCA
        "01FE112233445569", // Suica
    };

    /// <summary>
    /// シミュレーション用の職員証IDm
    /// </summary>
    public List<string> MockStaffCards { get; } = new()
    {
        "FFFF000000000001",
        "FFFF000000000002",
        "FFFF000000000003",
    };

    /// <inheritdoc/>
    public Task StartReadingAsync()
    {
        _isReading = true;
        _cancellationTokenSource = new CancellationTokenSource();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopReadingAsync()
    {
        _isReading = false;
        _cancellationTokenSource?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// カード読み取りをシミュレート
    /// </summary>
    /// <param name="idm">シミュレートするカードのIDm</param>
    public void SimulateCardRead(string idm)
    {
        if (_isReading)
        {
            CardRead?.Invoke(this, new CardReadEventArgs
            {
                Idm = idm,
                SystemCode = "0003"
            });
        }
    }

    /// <summary>
    /// エラーをシミュレート
    /// </summary>
    /// <param name="message">エラーメッセージ</param>
    public void SimulateError(string message)
    {
        Error?.Invoke(this, new Exception(message));
    }

    /// <inheritdoc/>
    public Task<IEnumerable<LedgerDetail>> ReadHistoryAsync(string idm)
    {
        // テスト用のダミー履歴データを返す
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = DateTime.Now.AddDays(-1),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                Balance = 5240,
                IsCharge = false,
                IsBus = false
            },
            new LedgerDetail
            {
                UseDate = DateTime.Now.AddDays(-1),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 260,
                Balance = 4980,
                IsCharge = false,
                IsBus = false
            },
            new LedgerDetail
            {
                UseDate = DateTime.Now.AddDays(-2),
                EntryStation = null,
                ExitStation = null,
                Amount = 230,
                Balance = 5500,
                IsCharge = false,
                IsBus = true
            },
            new LedgerDetail
            {
                UseDate = DateTime.Now.AddDays(-3),
                EntryStation = null,
                ExitStation = null,
                Amount = 3000,
                Balance = 5730,
                IsCharge = true,
                IsBus = false
            }
        };

        return Task.FromResult<IEnumerable<LedgerDetail>>(details);
    }

    /// <inheritdoc/>
    public Task<int?> ReadBalanceAsync(string idm)
    {
        // テスト用のダミー残高を返す
        return Task.FromResult<int?>(4980);
    }

    /// <inheritdoc/>
    public Task<bool> CheckConnectionAsync()
    {
        // モックは常に接続済み
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task ReconnectAsync()
    {
        // モックでは再接続は不要（常に接続済み）
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(CardReaderConnectionState.Connected));
        return Task.CompletedTask;
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
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            _disposed = true;
        }
    }
}
