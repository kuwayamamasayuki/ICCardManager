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
        "07FE112233445566", // はやかけん H-001
        "05FE112233445567", // nimoca N-001
        "06FE112233445568", // SUGOCA S-001
        "01FE112233445569", // Suica Su-001
        "07FE112233445570", // はやかけん H-002
        "05FE112233445571", // nimoca N-002
    };

    /// <summary>
    /// シミュレーション用の職員証IDm
    /// </summary>
    public List<string> MockStaffCards { get; } = new()
    {
        "FFFF000000000001", // 山田太郎
        "FFFF000000000002", // 鈴木花子
        "FFFF000000000003", // 佐藤一郎
        "FFFF000000000004", // 田中美咲
        "FFFF000000000005", // 伊藤健二
    };

    /// <summary>
    /// カスタム履歴データ生成の設定
    /// </summary>
    public MockHistorySettings HistorySettings { get; set; } = new();

    /// <summary>
    /// 現在の残高設定
    /// </summary>
    public int MockBalance { get; set; } = 4980;

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
        // カスタム履歴データがあればそれを使用
        if (HistorySettings.CustomHistory.TryGetValue(idm, out var customHistory))
        {
            return Task.FromResult<IEnumerable<LedgerDetail>>(customHistory);
        }

        // 設定に基づいて履歴データを生成
        var details = GenerateMockHistory(HistorySettings.Days, HistorySettings.IncludeBus, HistorySettings.IncludeCharge);
        return Task.FromResult<IEnumerable<LedgerDetail>>(details);
    }

    /// <summary>
    /// モック履歴データを生成
    /// </summary>
    private List<LedgerDetail> GenerateMockHistory(int days, bool includeBus, bool includeCharge)
    {
        var details = new List<LedgerDetail>();
        var random = new Random(MockBalance); // 残高をシードにして再現性を確保
        var balance = MockBalance + 1000; // 履歴の残高は現在より高い状態から開始

        // 福岡周辺の駅名
        string[] stations = { "博多", "天神", "薬院", "大橋", "春日", "二日市", "福岡空港", "貝塚", "香椎" };

        for (int i = 0; i < days; i++)
        {
            var date = DateTime.Now.AddDays(-(i + 1));

            // チャージを含める場合、最初にチャージを追加
            if (includeCharge && i == days - 1)
            {
                details.Add(new LedgerDetail
                {
                    UseDate = date,
                    EntryStation = null,
                    ExitStation = null,
                    Amount = 3000,
                    Balance = balance,
                    IsCharge = true,
                    IsBus = false
                });
                balance -= 3000;
            }

            // 鉄道利用
            var fromIdx = random.Next(stations.Length);
            var toIdx = (fromIdx + random.Next(1, 4)) % stations.Length;
            var fare = 200 + random.Next(8) * 30;

            details.Add(new LedgerDetail
            {
                UseDate = date,
                EntryStation = stations[fromIdx],
                ExitStation = stations[toIdx],
                Amount = fare,
                Balance = balance,
                IsCharge = false,
                IsBus = false
            });
            balance -= fare;

            // バス利用を含める場合
            if (includeBus && i % 2 == 0)
            {
                var busFare = 200 + random.Next(3) * 30;
                details.Add(new LedgerDetail
                {
                    UseDate = date,
                    EntryStation = null,
                    ExitStation = null,
                    Amount = busFare,
                    Balance = balance,
                    IsCharge = false,
                    IsBus = true
                });
                balance -= busFare;
            }
        }

        return details;
    }

    /// <inheritdoc/>
    public Task<int?> ReadBalanceAsync(string idm)
    {
        // カスタム残高があればそれを使用
        if (HistorySettings.CustomBalances.TryGetValue(idm, out var customBalance))
        {
            return Task.FromResult<int?>(customBalance);
        }

        return Task.FromResult<int?>(MockBalance);
    }

    /// <summary>
    /// 任意のIDmでカード読み取りをシミュレート
    /// </summary>
    /// <param name="idm">シミュレートするIDm（登録済みでなくてもOK）</param>
    /// <param name="systemCode">システムコード（省略可）</param>
    public void SimulateAnyCardRead(string idm, string? systemCode = null)
    {
        if (_isReading)
        {
            CardRead?.Invoke(this, new CardReadEventArgs
            {
                Idm = idm,
                SystemCode = systemCode ?? "0003"
            });
            System.Diagnostics.Debug.WriteLine($"[MockReader] カード読み取りシミュレート: {idm}");
        }
    }

    /// <summary>
    /// 特定カードの履歴データを設定
    /// </summary>
    public void SetCustomHistory(string idm, List<LedgerDetail> history)
    {
        HistorySettings.CustomHistory[idm] = history;
    }

    /// <summary>
    /// 特定カードの残高を設定
    /// </summary>
    public void SetCustomBalance(string idm, int balance)
    {
        HistorySettings.CustomBalances[idm] = balance;
    }

    /// <summary>
    /// カスタム設定をリセット
    /// </summary>
    public void ResetCustomSettings()
    {
        HistorySettings = new MockHistorySettings();
        MockBalance = 4980;
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

/// <summary>
/// モック履歴データ生成の設定
/// </summary>
public class MockHistorySettings
{
    /// <summary>
    /// 生成する履歴の日数（デフォルト: 3日）
    /// </summary>
    public int Days { get; set; } = 3;

    /// <summary>
    /// バス利用を含めるか
    /// </summary>
    public bool IncludeBus { get; set; } = true;

    /// <summary>
    /// チャージを含めるか
    /// </summary>
    public bool IncludeCharge { get; set; } = true;

    /// <summary>
    /// カードIDmごとのカスタム履歴データ
    /// </summary>
    public Dictionary<string, List<LedgerDetail>> CustomHistory { get; } = new();

    /// <summary>
    /// カードIDmごとのカスタム残高
    /// </summary>
    public Dictionary<string, int> CustomBalances { get; } = new();
}
