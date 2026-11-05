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
/// 他のテストが使わないドライブ文字（<see cref="HangingDriveRoot"/>）でだけ介入し、それ以外は既定の動作に委ねる。
/// 上限は本番の既定値（書き込み 10 秒・ドライブ 5 秒）なので、各テストはその分だけ時間がかかる。
/// </para>
/// </remarks>
public sealed class PathValidatorPublicEntryTimeoutTests : IDisposable
{
    /// <summary>戻らない準備確認を差し込むドライブ（他のテストが使わない文字）。</summary>
    private const string HangingDriveRoot = @"Q:\";

    /// <summary>上限で返ったかを判定する余裕込みの待ち時間。</summary>
    private static readonly TimeSpan ReturnDeadline = TimeSpan.FromSeconds(30);

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
            _release.Wait(TimeSpan.FromSeconds(60));
            return;
        }

        File.WriteAllText(testFile, "test");
        File.Delete(testFile);
    }

    /// <summary><see cref="HangingDriveRoot"/> の準備確認だけを戻らなくする（それ以外は既定の確認）。</summary>
    private bool HangingDriveProbe(string root)
    {
        if (string.Equals(root, HangingDriveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return _release.Wait(TimeSpan.FromSeconds(60));
        }

        return new DriveInfo(root).IsReady;
    }

    private static string WriteTimeoutMessage
        => PathValidator.CreateWriteProbeTimeoutFailure(PathValidator.DefaultWriteProbeTimeoutMs).ErrorMessage!;

    private static string DriveTimeoutMessage
        => PathValidator.CreateDriveReadyTimeoutFailure(HangingDriveRoot, PathValidator.DefaultDriveReadyTimeoutMs).ErrorMessage!;

    private string HangingDrivePath => Path.Combine(HangingDriveRoot, $"PathValidatorPublicEntry_{Guid.NewGuid():N}", "backup");

    private static async Task<PathValidator.ValidationResult> AwaitWithDeadline(Task<PathValidator.ValidationResult> validation)
    {
        var completed = await Task.WhenAny(validation, Task.Delay(ReturnDeadline));
        completed.Should().BeSameAs(validation, "戻らない確認は上限で打ち切られ、検証は返ること");
        return await validation;
    }

    [Fact]
    public void 公開の同期の検証は戻らない書き込み確認を上限で打ち切ること()
    {
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
            PathValidator.WriteProbeFile = null;
        }
    }

    [Fact]
    public async Task 公開の非同期の検証は戻らない書き込み確認を上限で打ち切ること()
    {
        PathValidator.WriteProbeFile = HangingWriteProbe;
        try
        {
            var result = await AwaitWithDeadline(PathValidator.ValidateBackupPathAsync(_testDirectory));

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(WriteTimeoutMessage);
        }
        finally
        {
            PathValidator.WriteProbeFile = null;
        }
    }

    [Fact]
    public void 到達確認だけを注入する内部の検証も戻らない書き込み確認を上限で打ち切ること()
    {
        // 3 引数の内部オーバーロード（既存のテストが使う）も、書き込み確認は上限付きの既定を使う
        PathValidator.WriteProbeFile = HangingWriteProbe;
        try
        {
            var result = PathValidator.ValidateBackupPath(_testDirectory, (_, _) => true, PathValidator.DefaultUncTimeoutMs);

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(WriteTimeoutMessage);
        }
        finally
        {
            PathValidator.WriteProbeFile = null;
        }
    }

    [Fact]
    public void 公開の同期の検証は戻らないドライブの準備確認を上限で打ち切ること()
    {
        PathValidator.DriveIsReadyProbe = HangingDriveProbe;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = PathValidator.ValidateBackupPath(HangingDrivePath);
            stopwatch.Stop();

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(DriveTimeoutMessage);
            stopwatch.Elapsed.Should().BeLessThan(ReturnDeadline);
        }
        finally
        {
            PathValidator.DriveIsReadyProbe = null;
        }
    }

    [Fact]
    public async Task 公開の非同期の検証は戻らないドライブの準備確認を上限で打ち切ること()
    {
        PathValidator.DriveIsReadyProbe = HangingDriveProbe;
        try
        {
            var result = await AwaitWithDeadline(PathValidator.ValidateBackupPathAsync(HangingDrivePath));

            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().Be(DriveTimeoutMessage);
        }
        finally
        {
            PathValidator.DriveIsReadyProbe = null;
        }
    }
}
