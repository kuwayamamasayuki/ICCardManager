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
/// Issue #2383: バックアップ先の書き込み確認（<see cref="PathValidator.CreateWritePermissionChecker"/>）を
/// 専用スレッドで上限付きに待ち、検証全体（<see cref="PathValidator.ValidateBackupPathAsync(string, CancellationToken)"/>）も
/// スレッドプールで起動しないこと。
/// </summary>
/// <remarks>
/// <para>
/// 書き込み確認（テストファイルの書き込みと削除）は上限が無く、応答しない共有では戻らない。以前は検証全体を
/// <c>Task.Run</c> で起動していたため、保存の操作を繰り返すたびにスレッドプールのスレッドが 1 本ずつ塞がり、
/// 同じプールを使う DB の処理の開始が遅れ得た。キャンセルしても待ちは終わらなかった。
/// </para>
/// <para>
/// 確認の本体を差し替え、応答しない共有を確定的に再現する（UNC の到達確認の
/// <c>PathValidatorUncReachabilityCheckerTests</c> と同じ形）。
/// </para>
/// </remarks>
public sealed class PathValidatorWriteProbeTests : IDisposable
{
    private const string FolderPath = @"\\server\share\backup";

    /// <summary>応答しない共有を模した確認を止めておくゲート。</summary>
    private readonly ManualResetEventSlim _release = new(false);

    /// <summary>確認が始まったことの合図。</summary>
    private readonly ManualResetEventSlim _checkStarted = new(false);

    private readonly string _testDirectory;

    private int _checkCallCount;

    public PathValidatorWriteProbeTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"PathValidatorWriteProbeTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        // 止めたままの確認（専用スレッド）を解放する（待機ハンドルは破棄しない。解放後に確認が戻るため）
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

    private int CheckCallCount => Volatile.Read(ref _checkCallCount);

    /// <summary>ゲートが開くまで戻らない確認（応答しない共有）。開いたら書き込めたとして成功を返す。</summary>
    private PathValidator.ValidationResult HangingCheck(string path)
    {
        Interlocked.Increment(ref _checkCallCount);
        _checkStarted.Set();
        _release.Wait(TimeSpan.FromSeconds(30));
        return PathValidator.ValidationResult.Success();
    }

    /// <summary>ゲートが開くまで戻らない書き込み確認を、検証へ注入する形（上限・本数の制御を通さない）。</summary>
    private PathValidator.ValidationResult HangingWriteChecker(string path, int timeoutMs)
        => HangingCheck(path);

    private void WaitUntilCheckStarted()
        => _checkStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("前提: 確認が始まること");

    [Fact]
    public void 書き込み確認が上限内に終わらなければ終わらなかったことを理由に検証エラーを返すこと()
    {
        // Arrange
        var checker = PathValidator.CreateWritePermissionChecker(HangingCheck);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = checker(FolderPath, 100);
        stopwatch.Stop();

        // Assert
        result.IsValid.Should().BeFalse("応答しない共有をバックアップ先として受け入れない");
        result.ErrorMessage.Should().Be(PathValidator.CreateWriteProbeTimeoutFailure(100).ErrorMessage);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(10_000, "上限で打ち切って戻ること（確認の終わりを待たない）");
    }

    [Fact]
    public void 上限内に終わった書き込み確認の結果をそのまま返すこと()
    {
        // 対の表明: 上限を設けても、正常に終わった確認（書き込める・書き込めない）の判定は変えない
        var denied = PathValidator.ValidationResult.Failure("指定されたフォルダへの書き込み権限がありません。");
        var deniedChecker = PathValidator.CreateWritePermissionChecker(_ => denied);
        var allowedChecker = PathValidator.CreateWritePermissionChecker(_ => PathValidator.ValidationResult.Success());

        deniedChecker(FolderPath, 5_000).Should().BeSameAs(denied);
        allowedChecker(FolderPath, 5_000).IsValid.Should().BeTrue();
    }

    [Fact]
    public void 応答しない共有へ続けて確認しても書き込み確認は1本に限られること()
    {
        // Arrange
        var checker = PathValidator.CreateWritePermissionChecker(HangingCheck);

        // Act: 上限で打ち切られた後にもう一度確認する（設定画面の保存をやり直した、など）
        checker(FolderPath, 50).IsValid.Should().BeFalse();
        WaitUntilCheckStarted();
        checker(FolderPath, 50).IsValid.Should().BeFalse();
        checker(FolderPath.ToUpperInvariant(), 50).IsValid.Should().BeFalse();

        // Assert: 戻らない確認を積み上げない（専用スレッドが呼び直しのたびに増えない）
        CheckCallCount.Should().Be(1);
    }

    [Fact]
    public void 終わった書き込み確認の結果は使い回さず次の呼び出しで確認し直すこと()
    {
        var calls = 0;
        var checker = PathValidator.CreateWritePermissionChecker(_ =>
        {
            Interlocked.Increment(ref calls);
            return PathValidator.ValidationResult.Success();
        });

        checker(FolderPath, 5_000).IsValid.Should().BeTrue();
        checker(FolderPath, 5_000).IsValid.Should().BeTrue();

        calls.Should().Be(2, "共有の状態は変わり得るので、終わった確認の結果は使い回さない");
    }

    [Fact]
    public void 書き込み確認の本体が例外を投げたら従来どおり通過させること()
    {
        // CheckWritePermission は想定外の例外では検証を通す（実際の書き込み時にエラーになる）。上限付きにしても変えない
        var checker = PathValidator.CreateWritePermissionChecker(
            _ => throw new InvalidOperationException("unexpected"));

        checker(FolderPath, 5_000).IsValid.Should().BeTrue();
    }

    [Fact]
    public void 書き込み確認はスレッドプールではなく専用スレッドで走ること()
    {
        bool? ranOnPoolThread = null;
        var checker = PathValidator.CreateWritePermissionChecker(_ =>
        {
            ranOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return PathValidator.ValidationResult.Success();
        });

        checker(FolderPath, 5_000).IsValid.Should().BeTrue();

        ranOnPoolThread.Should().BeFalse("応答しない共有ではプールのスレッドを塞ぎ続けるため");
    }

    [Fact]
    public void 検証は書き込み確認を上限付きで待ち上限を超えたら検証エラーを返すこと()
    {
        // 検証（ValidateBackupPath）が書き込み確認の上限を渡し、その結果を返していること（配線の確認）
        int? passedTimeoutMs = null;

        var result = PathValidator.ValidateBackupPath(
            _testDirectory,
            (_, _) => true,
            PathValidator.DefaultUncTimeoutMs,
            (_, timeoutMs) =>
            {
                passedTimeoutMs = timeoutMs;
                return PathValidator.CreateWriteProbeTimeoutFailure(timeoutMs);
            },
            1_234);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(PathValidator.CreateWriteProbeTimeoutFailure(1_234).ErrorMessage);
        passedTimeoutMs.Should().Be(1_234);
    }

    [Fact]
    public void 既定の書き込み確認は書き込めるフォルダーを通すこと()
    {
        // 対の表明: 既定の確認（実際のファイル書き込み）を専用スレッドへ移しても、書き込めるフォルダーは通る
        var result = PathValidator.ValidateBackupPath(_testDirectory);

        result.IsValid.Should().BeTrue(result.ErrorMessage);
        Directory.GetFiles(_testDirectory).Should().BeEmpty("確認用のファイルは削除されること");
    }

    [Fact]
    public void ドライブの準備確認が上限内に終わらなければ終わらなかったことを理由に検証エラーを返すこと()
    {
        // マップドドライブ（共有モード、#1559）は UNC 扱いにならず到達確認を通らない。切断されたドライブでは
        // DriveInfo.IsReady が戻らないことがあるため、上限付きで待つ（Issue #2383 のレビュー）
        var checker = PathValidator.CreateDriveReadinessChecker(root =>
        {
            Interlocked.Increment(ref _checkCallCount);
            _checkStarted.Set();
            return _release.Wait(TimeSpan.FromSeconds(30));
        });

        var stopwatch = Stopwatch.StartNew();
        var result = checker(@"Z:\", 100);
        stopwatch.Stop();

        result.IsValid.Should().BeFalse("応答しないドライブをバックアップ先として受け入れない");
        result.ErrorMessage.Should().Be(PathValidator.CreateDriveReadyTimeoutFailure(@"Z:\", 100).ErrorMessage);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(10_000, "上限で打ち切って戻ること");

        // 続けて確認しても戻らない確認を積み上げない
        WaitUntilCheckStarted();
        checker(@"z:\", 50).IsValid.Should().BeFalse();
        CheckCallCount.Should().Be(1);
    }

    [Fact]
    public void ドライブの準備確認が上限内に終わればその結果で判定すること()
    {
        // 対の表明: 上限を設けても、準備できているドライブは通り、準備できていないドライブは従来の文言で弾く
        PathValidator.CreateDriveReadinessChecker(_ => true)(@"C:\", 5_000).IsValid.Should().BeTrue();

        var notReady = PathValidator.CreateDriveReadinessChecker(_ => false)(@"E:\", 5_000);
        notReady.IsValid.Should().BeFalse();
        notReady.ErrorMessage.Should().Contain("ドライブ E:\\ が利用できません");

        // Path API の例外等は従来どおり致命的としない（他の検証項目に任せる）
        PathValidator.CreateDriveReadinessChecker(_ => throw new ArgumentException("bad"))(@"C:\", 5_000)
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void ドライブの準備確認はスレッドプールではなく専用スレッドで走ること()
    {
        bool? ranOnPoolThread = null;
        var checker = PathValidator.CreateDriveReadinessChecker(_ =>
        {
            ranOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            return true;
        });

        checker(@"C:\", 5_000).IsValid.Should().BeTrue();

        ranOnPoolThread.Should().BeFalse();
    }

    [Fact]
    public void 検証はローカルパスのドライブの準備確認を上限付きで待ち上限を超えたら検証エラーを返すこと()
    {
        // 検証（ValidateBackupPath）がドライブのルートと上限を渡し、その結果を返していること（配線の確認）
        string? passedRoot = null;
        int? passedTimeoutMs = null;
        var writeCheckerCalled = false;

        var result = PathValidator.ValidateBackupPath(
            _testDirectory,
            (_, _) => true,
            PathValidator.DefaultUncTimeoutMs,
            (_, _) =>
            {
                writeCheckerCalled = true;
                return PathValidator.ValidationResult.Success();
            },
            PathValidator.DefaultWriteProbeTimeoutMs,
            (root, timeoutMs) =>
            {
                passedRoot = root;
                passedTimeoutMs = timeoutMs;
                return PathValidator.CreateDriveReadyTimeoutFailure(root, timeoutMs);
            },
            1_234);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(
            PathValidator.CreateDriveReadyTimeoutFailure(Path.GetPathRoot(_testDirectory)!, 1_234).ErrorMessage);
        passedRoot.Should().Be(Path.GetPathRoot(_testDirectory));
        passedTimeoutMs.Should().Be(1_234);
        writeCheckerCalled.Should().BeFalse("ドライブが使えなければ書き込み確認へ進まない");
    }

    [Fact]
    public async Task 非同期の検証はスレッドプールではなく専用スレッドで走ること()
    {
        // 以前は検証全体を Task.Run で起動しており、到達確認・書き込み確認の上限付きの待ちでプールのスレッドを塞いでいた
        bool? ranOnPoolThread = null;

        var result = await PathValidator.ValidateBackupPathAsync(
            _testDirectory,
            (_, _) => true,
            PathValidator.DefaultUncTimeoutMs,
            (_, _) =>
            {
                ranOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
                return PathValidator.ValidationResult.Success();
            },
            PathValidator.DefaultWriteProbeTimeoutMs,
            CancellationToken.None);

        result.IsValid.Should().BeTrue();
        ranOnPoolThread.Should().BeFalse();
    }

    [Fact]
    public async Task 非同期の検証はキャンセルされたら確認の終わりを待たずに戻ること()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var validation = PathValidator.ValidateBackupPathAsync(
            _testDirectory,
            (_, _) => true,
            PathValidator.DefaultUncTimeoutMs,
            HangingWriteChecker,
            PathValidator.DefaultWriteProbeTimeoutMs,
            cts.Token);
        WaitUntilCheckStarted();

        // Act
        cts.Cancel();
        var completed = await Task.WhenAny(validation, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert
        completed.Should().BeSameAs(validation, "キャンセルしたら、戻らない確認を待ち続けない");
        await validation.Invoking(v => v).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task 非同期の検証は開始前にキャンセルされていたら確認を始めないこと()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var validation = PathValidator.ValidateBackupPathAsync(
            _testDirectory,
            (_, _) => true,
            PathValidator.DefaultUncTimeoutMs,
            HangingWriteChecker,
            PathValidator.DefaultWriteProbeTimeoutMs,
            cts.Token);

        await validation.Invoking(v => v).Should().ThrowAsync<OperationCanceledException>();
        CheckCallCount.Should().Be(0);
    }
}
