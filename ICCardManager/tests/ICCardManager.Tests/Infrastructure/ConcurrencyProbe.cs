using System.Threading.Tasks;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// モックの呼び出しの保持区間が重なった数（最大同時実行数）を数える計測器（Issue #1452）。
/// </summary>
/// <remarks>
/// <para>
/// 呼び出し「順序」だけを見る Moq の <c>MockSequence</c> では並列化のリグレッションを検出できない
/// （<c>Task.WhenAll</c> へ書き換えても開始順序は変わらないため）。SQLite は同一接続での並列コマンドを
/// 許さないので、リポジトリのモックの戻り値をこの計測器に通し、<see cref="MaxConcurrentCalls"/> が 1 であることを表明する。
/// </para>
/// <para>
/// Issue #2108: <c>DashboardServiceTests</c> と <c>AdminDashboardServiceTests</c> に同じ計測器が複製されていたため、ここへ寄せた。
/// </para>
/// </remarks>
public sealed class ConcurrencyProbe
{
    /// <summary>
    /// 1 回の呼び出しが保持区間に留まる時間。並列に起動されていれば、この間に次の呼び出しが入って重なる。
    /// </summary>
    public const int HoldMilliseconds = 20;

    private readonly object _lock = new object();
    private int _activeCalls;

    /// <summary>これまでに観測した最大の同時実行数。</summary>
    public int MaxConcurrentCalls
    {
        get
        {
            lock (_lock)
            {
                return _maxConcurrentCalls;
            }
        }
    }

    private int _maxConcurrentCalls;

    /// <summary>
    /// 保持区間に入り、<see cref="HoldMilliseconds"/> だけ留まってから <paramref name="value"/> を返す。
    /// </summary>
    public async Task<T> TrackAsync<T>(T value)
    {
        lock (_lock)
        {
            _activeCalls++;
            if (_activeCalls > _maxConcurrentCalls)
            {
                _maxConcurrentCalls = _activeCalls;
            }
        }

        // 並列があれば検出されるよう少し滞留させる
        await Task.Delay(HoldMilliseconds).ConfigureAwait(false);

        lock (_lock)
        {
            _activeCalls--;
        }

        return value;
    }
}
