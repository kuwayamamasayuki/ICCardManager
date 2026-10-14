using System;
using System.IO;
using System.Text;
using FluentAssertions;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2262: プレビューしたファイルの状態（内容の SHA-256）の読み取りは、スレッドプールが同期的な待ちで
/// 塞がっていても始まること（<c>DedicatedThread.Run</c> で起動する。viewmodel-conventions.md #2221）
/// </summary>
/// <remarks>
/// 読み取りは共有フォルダー上では応答が無いと長く戻らない同期処理で、UI スレッドで行うと画面が固まり、
/// <c>Task.Run</c> で起動するとプールが詰まっているときに始まらない。プールを塞ぐため、他のテストと並列に走らせない。
/// </remarks>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class DataExportImportPreviewFileThreadPoolTests
{
    /// <summary>待つ上限（読み取り自体は数 ms で終わる。Task.Run なら上限内に始まらない程度に塞ぐ）</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>塞がったと判定した後に足す予備の塞ぎ役の数（上限の間に足されるスレッドを上回る数）</summary>
    private const int ReserveBlockers = 40;

    [Fact]
    public void ファイルの状態の読み取りはスレッドプールが塞がっていても始まること()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fingerprint_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, "カードIDm,カード種別,管理番号,備考\n0102030405060708,はやかけん,001,\n", new UTF8Encoding(true));
        try
        {
            using var saturator = ThreadPoolSaturator.Saturate(ReserveBlockers);
            saturator.IsSaturated.Should().BeTrue(
                "前提: プールが塞がっていて、Task.Run の処理が始まらないこと（塞げていなければ本テストは何も検証しない）");

            var capture = DataExportImportViewModel.PreviewFileFingerprint.CaptureAsync(path);

            capture.Wait(Timeout).Should().BeTrue("読み取りは専用スレッドで走るため、プールの空きを待たずに始まる");
            capture.Result.Should().NotBeNull();
            capture.Result!.Length.Should().Be(new FileInfo(path).Length);

            // 対の表明: 読み取りを待っている間もプールは塞がったままだった
            saturator.SentinelStarted.Should().BeFalse("読み取りを待っている間も、プールが塞がったままであること");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
