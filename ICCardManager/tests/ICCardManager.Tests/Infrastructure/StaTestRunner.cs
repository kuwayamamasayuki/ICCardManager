using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;

namespace ICCardManager.Tests.Infrastructure;

/// <summary>
/// WPF の <c>Window</c> / <c>FlowDocument</c> のように STA スレッドでしか扱えない型を
/// 検証するための、専用スレッド実行ヘルパー（Issue #2083）。
/// </summary>
/// <remarks>
/// <para>
/// 本ヘルパーを使うテストクラスには必ず
/// <c>[Collection(StaThreadCollection.Name)]</c> を付与すること。
/// STA スレッドは初回に <c>PresentationFramework</c> の初期化を伴い、
/// 2 コア共有の CI ランナーでは他のテストコレクションと CPU を奪い合う。
/// 待っているのは計算時間ではなくスケジューリングなので、
/// <b>上限の倍率ではなく競合の側を減らす</b>（付与漏れは
/// <see cref="StaThreadCollectionConventionTests"/> が静的検査で検出する）。
/// </para>
/// <para>
/// <c>Thread.Join</c> 以外に完了を待つ手段が無いため上限は残すが、
/// 打ち切ったときに「何秒かかったか」「どの段階まで完了したか」を残す。
/// 上限だけを表明していた旧実装（同一の <c>RunOnSta</c> が 2 箇所に複製されていた）は、
/// 落ちたときに検証対象の値へ一度も到達せず、
/// ログを見た人が「印刷プレビューが壊れた」と読む失敗メッセージしか残さなかった。
/// </para>
/// </remarks>
internal static class StaTestRunner
{
    /// <summary>
    /// STA スレッドの完了を待つ既定の上限。
    /// </summary>
    /// <remarks>
    /// 本番の実測は 0.1 秒程度で、この値は「本当にハングしたときに
    /// テスト実行全体を止めないための backstop」である。
    /// 競合によるランダム失敗の対策は <see cref="StaThreadCollection"/> 側が担うので、
    /// 間欠的に打ち切られるようになったときは<b>この値を増やす前に</b>
    /// 並列実行の相手が増えていないかを確かめること。
    /// </remarks>
    public static readonly TimeSpan DefaultCompletionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// STA スレッド上で <paramref name="action"/> を実行し、完了を待つ。
    /// </summary>
    public static void Run(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        Run(_ => action());
    }

    /// <summary>
    /// STA スレッド上で <paramref name="action"/> を実行し、完了を待つ。
    /// <paramref name="action"/> は受け取った <see cref="StaStageLog"/> へ
    /// 段階の完了を記録でき、打ち切り時の失敗メッセージに残る。
    /// </summary>
    public static void Run(Action<StaStageLog> action)
        => Run(action, DefaultCompletionTimeout);

    /// <summary>
    /// 上限を指定して実行する（打ち切り時の診断を検証するテスト専用）。
    /// </summary>
    /// <remarks>
    /// <b>打ち切った場合、STA スレッドはそのまま走り続ける。</b>
    /// その後にアクションが投げた例外は誰も読まない（打ち切りの失敗が先に確定するため）。
    /// これは意図した割り切りで、止める手段（<c>Thread.Abort</c>）は .NET Framework でも
    /// 任意の位置で例外を起こすため、WPF のオブジェクトを半端な状態で壊す。
    /// 打ち切りのメッセージが「検証対象の値には到達していない」と述べているのはこのため。
    /// </remarks>
    internal static void Run(Action<StaStageLog> action, TimeSpan timeout)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        var stages = new StaStageLog();
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action(stages);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;

        var stopwatch = Stopwatch.StartNew();
        thread.Start();
        var completed = thread.Join(timeout);
        stopwatch.Stop();

        completed.Should().BeTrue(BuildTimeoutReason(stopwatch.Elapsed, timeout, stages));

        if (captured != null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    /// <summary>
    /// 打ち切り時の失敗メッセージを組み立てる。
    /// </summary>
    /// <remarks>
    /// 実行を伴わずに検証できるよう純関数として切り出している
    /// （`.claude/rules/error-messages.md` #1817「実機でしか再現しない失敗は
    /// 文言生成を純関数へ切り出して固定する」と同じ形）。
    /// </remarks>
    internal static string BuildTimeoutReason(TimeSpan elapsed, TimeSpan timeout, StaStageLog stages)
    {
        var elapsedText = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        var timeoutText = timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

        return $"STA スレッドが {timeoutText} 秒以内に完了すること" +
               $"（実測 {elapsedText} 秒で打ち切り／完了した段階: {stages.Describe()}）。" +
               "検証対象の値には到達していないため、この失敗は検証対象の不具合を意味しない。" +
               "CI では並列実行の相手（StaThreadCollection の設定）を確認すること";
    }
}

/// <summary>
/// STA スレッド上で完了した段階を記録する。打ち切り時の診断に使う。
/// </summary>
/// <remarks>
/// 記録は STA スレッドから、読み出しは待機側スレッドから行われるため lock で保護する。
/// </remarks>
internal sealed class StaStageLog
{
    private readonly object _gate = new();
    private readonly List<string> _completed = new();

    /// <summary>
    /// 段階の完了を記録する。
    /// </summary>
    public void Complete(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException("段階名が空です。何が完了したかを表す名前を渡してください。", nameof(stage));
        }

        lock (_gate)
        {
            _completed.Add(stage);
        }
    }

    /// <summary>
    /// 記録済みの段階を完了順に返す。
    /// </summary>
    public IReadOnlyList<string> CompletedStages
    {
        get
        {
            lock (_gate)
            {
                return _completed.ToArray();
            }
        }
    }

    /// <summary>
    /// 失敗メッセージへ埋め込む文字列を返す。
    /// </summary>
    public string Describe()
    {
        var stages = CompletedStages;
        return stages.Count == 0
            ? "（1 段階も完了していない）"
            : string.Join(" → ", stages);
    }
}
