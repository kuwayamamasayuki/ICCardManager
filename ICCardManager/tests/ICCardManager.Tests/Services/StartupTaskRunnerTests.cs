using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1737: 起動時タスク（自動バックアップ → 古いデータ削除 → 月次 VACUUM）が
/// 単一の SQLite 接続上で**直列**に実行されることを固定する回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は <c>App.PerformStartupTasksAsync</c> が
/// <c>_ = backupService.ExecuteAutoBackupAsync();</c> と戻り値を捨てていたため、
/// バックアップの継続（成功日時を settings へ記録する INSERT）が
/// 古いデータ削除のトランザクション内側／VACUUM 実行中の同一接続へ流れ込んでいた。
/// </para>
/// <para>
/// 検出方法は <c>.claude/rules/testing.md</c> の方針どおり「順序」ではなく
/// <b>同時実行数</b>で表明する。順序だけを見るテストは fire-and-forget でも
/// 開始順が変わらないため素通りする。
/// </para>
/// </remarks>
public class StartupTaskRunnerTests
{
    private readonly Mock<DbContext> _dbContextMock = new();
    private readonly Mock<BackupService> _backupServiceMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly Mock<IBackupHealthService> _backupHealthServiceMock = new();
    private readonly Mock<ILogger<StartupTaskRunner>> _loggerMock = new();
    private readonly StartupTaskRunner _runner;

    /// <summary>
    /// VACUUM を試行する日。しきい値は本番の定数から導出する
    /// （テスト側に日付リテラルを再エンコードすると、しきい値変更時に
    /// 「9日は15日より前」のような誤った理由でテストが通り続ける）。
    /// </summary>
    private static readonly DateTime VacuumDay =
        new DateTime(2026, 8, 1).AddDays(StartupTaskRunner.MonthlyVacuumStartDay - 1);

    /// <summary>VACUUM を試行しない日（しきい値の前日）。</summary>
    private static readonly DateTime NonVacuumDay = VacuumDay.AddDays(-1);

    public StartupTaskRunnerTests()
    {
        _backupServiceMock = new Mock<BackupService>(
            _dbContextMock.Object,
            _settingsRepositoryMock.Object,
            NullLogger<BackupService>.Instance);

        // 既定は「すべて成功・削除0件」。各テストで必要な分だけ上書きする。
        _backupServiceMock.Setup(x => x.ExecuteAutoBackupAsync())
            .ReturnsAsync("C:\\backup\\dummy.db");
        _dbContextMock.Setup(x => x.CleanupOldDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, 0));
        _dbContextMock.Setup(x => x.VacuumAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _settingsRepositoryMock.Setup(x => x.TryAcquireMonthlyVacuumLockAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(true);

        _runner = new StartupTaskRunner(
            _dbContextMock.Object,
            _backupServiceMock.Object,
            _settingsRepositoryMock.Object,
            _backupHealthServiceMock.Object,
            _loggerMock.Object);
    }

    #region 直列性（Issue #1737 の本丸）

    [Fact]
    public async Task RunAsync_バックアップと古いデータ削除が同時に実行されないこと()
    {
        // Arrange: 各タスクの「保持区間」を計測する。
        var tracker = SetupTrackedTasks();

        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert: 単一接続上で複数タスクが同時にコマンドを発行してはならない
        tracker.MaxConcurrent.Should().Be(1,
            "DbContext は SQLiteConnection を1本しか持たず、同一接続上の並列コマンド発行は "
            + "SQLITE_MISUSE / トランザクション巻き込みの原因になる（Issue #1452 / #1737）");
    }

    [Fact]
    public async Task RunAsync_バックアップ完了後に古いデータ削除が始まること()
    {
        // Arrange
        var tracker = SetupTrackedTasks();

        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert: バックアップの「終了」が古いデータ削除の「開始」より前にあること。
        // バックアップの成功日時記録（settings への INSERT）はこの区間の内側で走るため、
        // ここが逆転すると cleanup のトランザクションに巻き込まれてロールバックで消える。
        tracker.Order.Should().ContainInOrder(
            "backup:start", "backup:end", "cleanup:start", "cleanup:end", "vacuum:start", "vacuum:end");
    }

    [Fact]
    public async Task RunAsync_バックアップがnullを返したら警告を残し後続タスクを続けること()
    {
        // Arrange: ExecuteAutoBackupAsync は失敗を「例外」ではなく「null 戻り値」で表す
        // （内部で全例外を catch して null を返す）。本番で実際に観測できるのはこちらだけで、
        // 例外経路はモックでしか再現できない。戻り値を捨てていると、バックアップ先が
        // 到達不能でも起動シーケンスのログは正常に見えてしまう。
        _backupServiceMock.Setup(x => x.ExecuteAutoBackupAsync())
            .ReturnsAsync((string)null);

        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert
        VerifyLogged(LogLevel.Warning, "自動バックアップに失敗");
        _dbContextMock.Verify(x => x.CleanupOldDataAsync(It.IsAny<CancellationToken>()), Times.Once,
            "バックアップの失敗で古いデータ削除がスキップされてはならない");
        _dbContextMock.Verify(x => x.VacuumAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_バックアップ成功時は失敗警告を出さないこと()
    {
        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert: 正常運用でログを肥大化させない
        VerifyNotLogged("自動バックアップに失敗");
    }

    [Fact]
    public async Task RunAsync_バックアップが例外で失敗しても後続タスクは実行されること()
    {
        // Arrange: 直列化（await 化）で「バックアップの失敗が後続タスクを巻き添えにする」
        // 退行が起きないことを固定する。
        //
        // 注: 現在の BackupService.ExecuteAutoBackupAsync は全例外を内部で catch するため
        // この経路は本番では到達しない（実際に観測されるのは上の null 戻り値のケース）。
        // ここで固定しているのは「BackupService が将来例外を投げるようになっても
        // 後続タスクが止まらない」という backstop の振る舞い。
        _backupServiceMock.Setup(x => x.ExecuteAutoBackupAsync())
            .ThrowsAsync(new InvalidOperationException("バックアップ先が見つかりません"));

        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert
        _dbContextMock.Verify(x => x.CleanupOldDataAsync(It.IsAny<CancellationToken>()), Times.Once,
            "バックアップの失敗で古いデータ削除がスキップされてはならない");
        _dbContextMock.Verify(x => x.VacuumAsync(It.IsAny<CancellationToken>()), Times.Once,
            "バックアップの失敗で月次 VACUUM がスキップされてはならない");
    }

    #endregion

    #region 月次 VACUUM（Issue #1482 の CAS ロック）

    [Fact]
    public async Task RunAsync_10日より前はVACUUMのロック取得を試みないこと()
    {
        // Act
        await _runner.RunAsync(NonVacuumDay);

        // Assert
        _settingsRepositoryMock.Verify(x => x.TryAcquireMonthlyVacuumLockAsync(It.IsAny<DateTime>()), Times.Never);
        _dbContextMock.Verify(x => x.VacuumAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ロックを取得できなければVACUUMを実行しないこと()
    {
        // Arrange: 共有モードで他 PC が先に当月のロックを取得済み
        _settingsRepositoryMock.Setup(x => x.TryAcquireMonthlyVacuumLockAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(false);

        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert
        _dbContextMock.Verify(x => x.VacuumAsync(It.IsAny<CancellationToken>()), Times.Never);
        _backupHealthServiceMock.Verify(x => x.RecordVacuumMachineAsync(), Times.Never);
    }

    [Fact]
    public async Task RunAsync_VACUUM成功時は実施PC名を記録すること()
    {
        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert
        _settingsRepositoryMock.Verify(x => x.TryAcquireMonthlyVacuumLockAsync(VacuumDay), Times.Once);
        _backupHealthServiceMock.Verify(x => x.RecordVacuumMachineAsync(), Times.Once);
    }

    [Fact]
    public async Task RunAsync_VACUUM失敗時は実施PC名を記録せず来月再試行を警告すること()
    {
        // Arrange
        _dbContextMock.Setup(x => x.VacuumAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        await _runner.RunAsync(VacuumDay);

        // Assert
        _backupHealthServiceMock.Verify(x => x.RecordVacuumMachineAsync(), Times.Never,
            "VACUUM していない PC 名を記録すると、システム管理画面の表示が実態と食い違う");
        VerifyLogged(LogLevel.Warning, "来月再試行");
    }

    #endregion

    #region 失敗しても起動を止めない

    [Fact]
    public async Task RunAsync_古いデータ削除が例外でも起動を止めずErrorログを残すこと()
    {
        // Arrange
        _dbContextMock.Setup(x => x.CleanupOldDataAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB がロックされています"));

        // Act
        Func<Task> act = () => _runner.RunAsync(VacuumDay);

        // Assert
        await act.Should().NotThrowAsync("起動時タスクの失敗でアプリが起動できなくなってはならない");
        VerifyLogged(LogLevel.Error, "起動時タスク");
    }

    [Fact]
    public async Task RunAsync_削除件数が0件のときは削除ログを出さないこと()
    {
        // Act
        await _runner.RunAsync(NonVacuumDay);

        // Assert: 正常運用中のログ肥大化を避ける（削除が発生した時だけ記録する）
        VerifyNotLogged("古い利用履歴");
        VerifyNotLogged("古い操作ログ");
    }

    [Fact]
    public async Task RunAsync_削除件数を件数付きでInformationログに残すこと()
    {
        // Arrange
        _dbContextMock.Setup(x => x.CleanupOldDataAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((3, 7));

        // Act
        await _runner.RunAsync(NonVacuumDay);

        // Assert: 障害調査で「いつ何件消えたか」を追えるよう件数まで載せる
        VerifyLogged(LogLevel.Information, "古い利用履歴を3件削除");
        VerifyLogged(LogLevel.Information, "古い操作ログを7件削除");
    }

    #endregion

    #region ヘルパー

    /// <summary>
    /// 起動時タスク3本を「保持区間を計測するスタブ」に差し替える。
    /// </summary>
    /// <remarks>
    /// 直列性テストが複数あるため 1 か所に集約する。個別にコピーすると、
    /// 4 本目のタスクを足したときに片方だけが更新され、
    /// もう片方は古いタスク集合を計測したまま緑になる。
    /// バックアップ側だけ滞在時間を長くしてあるのは、
    /// fire-and-forget なら後続タスクと保持区間が確実に重なるようにするため。
    /// </remarks>
    private ConcurrencyTracker SetupTrackedTasks()
    {
        var tracker = new ConcurrencyTracker();

        _backupServiceMock.Setup(x => x.ExecuteAutoBackupAsync())
            .Returns(async () =>
            {
                tracker.Enter("backup");
                await Task.Delay(100);
                tracker.Exit("backup");
                return "C:\\backup\\dummy.db";
            });

        _dbContextMock.Setup(x => x.CleanupOldDataAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                tracker.Enter("cleanup");
                await Task.Delay(20);
                tracker.Exit("cleanup");
                return (0, 0);
            });

        _dbContextMock.Setup(x => x.VacuumAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                tracker.Enter("vacuum");
                await Task.Delay(20);
                tracker.Exit("vacuum");
                return true;
            });

        return tracker;
    }

    private void VerifyLogged(LogLevel level, string expectedFragment)
    {
        _loggerMock.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(expectedFragment)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            $"{level} ログに「{expectedFragment}」を含む記録が必要");
    }

    private void VerifyNotLogged(string unexpectedFragment)
    {
        _loggerMock.Verify(
            x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains(unexpectedFragment)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never,
            $"「{unexpectedFragment}」を含むログは出力されないこと");
    }

    /// <summary>
    /// タスクの保持区間を計測し、最大同時実行数と開始/終了の順序を記録する。
    /// </summary>
    private sealed class ConcurrencyTracker
    {
        private readonly object _sync = new();
        private int _current;

        public int MaxConcurrent { get; private set; }

        public List<string> Order { get; } = new();

        public void Enter(string name)
        {
            lock (_sync)
            {
                _current++;
                if (_current > MaxConcurrent)
                {
                    MaxConcurrent = _current;
                }
                Order.Add($"{name}:start");
            }
        }

        public void Exit(string name)
        {
            lock (_sync)
            {
                _current--;
                Order.Add($"{name}:end");
            }
        }
    }

    #endregion
}
