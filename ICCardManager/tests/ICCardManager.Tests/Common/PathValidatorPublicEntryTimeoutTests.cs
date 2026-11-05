using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2383: 公開の入口（<c>ValidateBackupPath(path)</c> / <c>ValidateBackupPathAsync(path)</c>）が、
/// 書き込み確認・ドライブの準備確認を上限付きの既定の確認で待つこと。
/// </summary>
/// <remarks>
/// <para>
/// 確認の関数を注入する内部オーバーロードのテストだけでは、公開の入口から既定の確認までの配線を
/// 上限の無い形（<c>(p, _) =&gt; CheckWritePermission(p)</c> 等）に戻しても緑になる。
/// ここでは確認の本体の最も内側（テストファイルの書き込み・<c>DriveInfo.IsReady</c>）だけを戻らないものへ差し替え、
/// 公開の入口が上限で返ることを表明する。
/// </para>
/// <para>
/// 既定の確認は同じパス（ドライブはルート）への確認を 1 本に限るので、差し替えは自分専用のフォルダーと
/// テストごとに別の、他のテストが使わないドライブ文字（<see cref="SyncDriveRoot"/>・<see cref="AsyncDriveRoot"/>）でだけ介入し、
/// それ以外は既定の動作に委ねる（同じドライブを共有すると、前のテストで解放した確認に次のテストが相乗りし得る）。
/// </para>
/// <para>
/// 上限はテスト用フック（<c>WriteProbeTimeoutMs</c>・<c>DriveReadyTimeoutMs</c>）で 1 秒に縮め、本番の上限（10 秒・5 秒）を
/// 実時間で待たない（testing.md「実時間の待ちを仕様の代わりにしない」）。配線を上限の無い形に戻すと、
/// 戻らない確認が差し込みの上限（<see cref="HangCap"/>）まで返らず、<see cref="ReturnDeadline"/> を超えて赤になる。
/// 確認の上限そのものの挙動（上限で打ち切る・1 本に限る）は <c>PathValidatorWriteProbeTests</c> が確かめる。
/// </para>
/// </remarks>
public sealed class PathValidatorPublicEntryTimeoutTests : IDisposable
{
    /// <summary>同期の検証のテストで戻らない準備確認を差し込むドライブ（他のテストが使わない文字）。</summary>
    private const string SyncDriveRoot = @"Q:\";

    /// <summary>非同期の検証のテストで戻らない準備確認を差し込むドライブ（同上。同期のテストとも分ける）。</summary>
    private const string AsyncDriveRoot = @"R:\";

    /// <summary>テストで使う確認の上限（ミリ秒）。</summary>
    private const int TestTimeoutMs = 1_000;

    /// <summary>上限で返ったかを判定する余裕込みの待ち時間。</summary>
    private static readonly TimeSpan ReturnDeadline = TimeSpan.FromSeconds(8);

    /// <summary>戻らない確認が自ら戻るまでの時間（<see cref="ReturnDeadline"/> より長く取る）。</summary>
    private static readonly TimeSpan HangCap = TimeSpan.FromSeconds(15);

    /// <summary>戻らない確認を止めておくゲート。</summary>
    private readonly ManualResetEventSlim _release = new(false);

    private readonly string _testDirectory;

    public PathValidatorPublicEntryTimeoutTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"PathValidatorPublicEntry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        // 止めたままの確認（専用スレッド）を解放する
        _release.Set();

        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>自分専用のフォルダーへの書き込みだけを戻らなくする（それ以外は既定の書き込み）。</summary>
    private void HangingWriteProbe(string testFile)
    {
        if (testFile.StartsWith(_testDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _release.Wait(HangCap);
            return;
        }

        File.WriteAllText(testFile, "test");
        File.Delete(testFile);
    }

    /// <summary>指定したドライブの準備確認だけを戻らなくする（それ以外は既定の確認）。</summary>
    private Func<string, bool> HangingDriveProbe(string hangingRoot) => root =>
    {
        if (string.Equals(root, hangingRoot, StringComparison.OrdinalIgnoreCase))
        {
            return _release.Wait(HangCap);
        }

        return new DriveInfo(root).IsReady;
    };

    private static string WriteTimeoutMessage
        => PathValidator.CreateWriteProbeTimeoutFailure(TestTimeoutMs).ErrorMessage!;

    private static string DriveTimeoutMessage(string root)
        => PathValidator.CreateDriveReadyTimeoutFailure(root, TestTimeoutMs).ErrorMessage!;

    private static string DrivePath(string root) => Path.Combine(root, $"PathValidatorPublicEntry_{Guid.NewGuid():N}", "backup");

    /// <summary>上限を縮め、確認の本体を差し替える（テストの実行コンテキストにのみ見える）。</summary>
    private static void ShortenTimeouts()
    {
        PathValidator.WriteProbeTimeoutMs = TestTimeoutMs;
        PathValidator.DriveReadyTimeoutMs = TestTimeoutMs;
    }

    private static async Task<PathValidator.ValidationResult> AwaitWithDeadline(Task<PathValidator.ValidationResult> validation)
    {
        var completed = await Task.WhenAny(validation, Task.Delay(ReturnDeadline));
        completed.Should().BeSameAs(validation, "戻らない確認は上限で打ち切られ、検証は返ること");
        return await validation;
    }

    [Fact]
    public void 公開の同期の検証は戻らない書き込み確認を上限で打ち切ること()
    {
        ShortenTimeouts();
        PathValidator.WriteProbeFile = HangingWriteProbe;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = PathValidator.ValidateBackupPath(_testDirectory);
            stopwatch.Stop();

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(WriteTimeoutMessage);
            stopwatch.Elapsed.Should().BeLessThan(ReturnDeadline);
        }
        finally
        {
            PathValidator.ResetProbeHooks();
        }
    }

    [Fact]
    public async Task 公開の非同期の検証は戻らない書き込み確認を上限で打ち切ること()
    {
        ShortenTimeouts();
        PathValidator.WriteProbeFile = HangingWriteProbe;
        try
        {
            var result = await AwaitWithDeadline(PathValidator.ValidateBackupPathAsync(_testDirectory));

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(WriteTimeoutMessage);
        }
        finally
        {
            PathValidator.ResetProbeHooks();
        }
    }

    [Fact]
    public void 公開の同期の検証は戻らないドライブの準備確認を上限で打ち切ること()
    {
        ShortenTimeouts();
        PathValidator.DriveIsReadyProbe = HangingDriveProbe(SyncDriveRoot);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = PathValidator.ValidateBackupPath(DrivePath(SyncDriveRoot));
            stopwatch.Stop();

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(DriveTimeoutMessage(SyncDriveRoot));
            stopwatch.Elapsed.Should().BeLessThan(ReturnDeadline);
        }
        finally
        {
            PathValidator.ResetProbeHooks();
        }
    }

    [Fact]
    public async Task 公開の非同期の検証は戻らないドライブの準備確認を上限で打ち切ること()
    {
        ShortenTimeouts();
        PathValidator.DriveIsReadyProbe = HangingDriveProbe(AsyncDriveRoot);
        try
        {
            var result = await AwaitWithDeadline(PathValidator.ValidateBackupPathAsync(DrivePath(AsyncDriveRoot)));

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(DriveTimeoutMessage(AsyncDriveRoot));
        }
        finally
        {
            PathValidator.ResetProbeHooks();
        }
    }
}
