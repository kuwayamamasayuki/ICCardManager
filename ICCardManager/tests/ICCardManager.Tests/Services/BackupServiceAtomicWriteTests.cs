using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// バックアップの不可分な書き込み（一時ファイル経由）に関する単体テスト（Issue #1748）
/// </summary>
/// <remarks>
/// 最終ファイル名へ直接書いていた頃は、コピー途中で失敗した切り詰めファイルが
/// そのまま有効な世代として残り、(1) 世代数を消費して正常な最古世代を余分に削除させ、
/// (2) リストア画面で選ばれて本番 DB を上書きし得た。
/// 本テストはその 2 つの実害と、修正で新たに生まれる残骸（一時ファイル）の後始末を固定する。
/// </remarks>
public class BackupServiceAtomicWriteTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _testDbPath;
    private readonly string _backupDirectory;
    private readonly DbContext _dbContext;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly BackupService _service;

    /// <summary>
    /// 切り詰めを再現するサイズ。ページ 1（マジックヘッダを含む）だけが書かれた状態を模す。
    /// </summary>
    private const int TruncatedLength = 4096;

    public BackupServiceAtomicWriteTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BackupAtomicTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _testDbPath = Path.Combine(_testDirectory, "test.db");
        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);

        _dbContext = new DbContext(_testDbPath);
        _dbContext.InitializeDatabase();

        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });

        _service = new BackupService(
            _dbContext,
            _settingsRepositoryMock.Object,
            NullLogger<BackupService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();

        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // クリーンアップ失敗は無視
        }

        GC.SuppressFinalize(this);
    }

    #region 一時ファイル経由の書き込み

    /// <summary>
    /// コピー途中で失敗したとき、最終ファイル名（＝有効な世代）を残さないことを確認
    /// </summary>
    /// <remarks>
    /// 修正前は最終名で書いていたため、切り詰められた backup_*.db がそのまま残った。
    /// </remarks>
    [Fact]
    public void CreateBackup_コピー途中で失敗したとき破損した世代を残さないこと()
    {
        // Arrange
        var service = CreateFailingService();
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");

        // Act
        var result = service.CreateBackup(backupPath);

        // Assert
        result.Should().BeFalse("コピーが中断されたため失敗を返すこと");
        File.Exists(backupPath).Should().BeFalse("中断されたコピーが有効な世代として残ってはならない");
        ListBackupGenerations().Should().BeEmpty();
    }

    /// <summary>
    /// コピー途中で失敗したとき、一時ファイルも残さないことを確認
    /// </summary>
    [Fact]
    public void CreateBackup_コピー途中で失敗したとき一時ファイルを削除すること()
    {
        // Arrange
        var service = CreateFailingService();
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");

        // Act
        service.CreateBackup(backupPath);

        // Assert
        service.LastCopyDestination.Should().NotBeNull("一時ファイル経由で書き込むこと");
        service.LastCopyDestination.Should().NotBe(backupPath, "最終ファイル名へ直接書いてはならない");
        ListTempFiles().Should().BeEmpty("失敗時は一時ファイルを後始末すること");
    }

    /// <summary>
    /// 成功時に一時ファイルが残らないことを確認
    /// </summary>
    [Fact]
    public void CreateBackup_成功時に一時ファイルを残さないこと()
    {
        // Arrange
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");

        // Act
        var result = _service.CreateBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(backupPath).Should().BeTrue();
        ListTempFiles().Should().BeEmpty();
    }

    /// <summary>
    /// 一時ファイル名が世代の検索パターンに一致しないことを確認
    /// </summary>
    /// <remarks>
    /// 「一時ファイルは世代として列挙されない」という本 Issue の前提そのもの。
    /// Win32 のワイルドカード照合は 8.3 短縮名にも一致するなど実装依存の癖があるため、
    /// 前提を推論で済ませず実際の列挙結果で固定する。
    /// </remarks>
    [Fact]
    public void EnumerateBackupFiles_一時ファイルを世代として拾わないこと()
    {
        // Arrange: 実際の一時ファイル名と同じ形（最終名 + GUID + .tmp）で作る
        var generation = Path.Combine(_backupDirectory, "backup_20260813_090000.db");
        File.WriteAllText(generation, "dummy");
        var tempFile = $"{generation}.{Guid.NewGuid():N}{BackupService.BackupTempFileExtension}";
        File.WriteAllText(tempFile, "dummy");

        // Act
        var generations = BackupService.EnumerateBackupFiles(_backupDirectory);
        var temps = BackupService.EnumerateBackupTempFiles(_backupDirectory);

        // Assert
        generations.Select(f => f.FullName).Should().ContainSingle().Which.Should().Be(generation);
        temps.Select(f => f.FullName).Should().ContainSingle().Which.Should().Be(tempFile);
    }

    /// <summary>
    /// バックアップ一覧（リストア候補）に一時ファイルが現れないことを確認
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_一時ファイルを一覧に含めないこと()
    {
        // Arrange
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");
        _service.CreateBackup(backupPath).Should().BeTrue();
        var tempFile = $"{Path.Combine(_backupDirectory, "backup_20260813_100000.db")}.{Guid.NewGuid():N}{BackupService.BackupTempFileExtension}";
        File.WriteAllText(tempFile, "dummy");

        // Act
        var files = await _service.GetBackupFilesAsync();

        // Assert
        files.Select(f => f.FilePath).Should().ContainSingle().Which.Should().Be(backupPath);
    }

    #endregion

    #region 世代数の消費

    /// <summary>
    /// 一時ファイルが保持世代数の枠を消費しないことを確認
    /// </summary>
    /// <remarks>
    /// 一時ファイルを世代として数えると、その分だけ正常な最古世代が余分に削除される。
    /// 一時ファイルの作成日時を**最も新しく**しているのは、誤って数えた場合に
    /// 削除対象が実在の世代へ回ることを確実に観測するため。
    /// </remarks>
    [Fact]
    public async Task ExecuteAutoBackupAsync_一時ファイルが世代数を消費しないこと()
    {
        // Arrange: 保持上限ちょうどの世代を作る（古い順に作成日時を割り当てる）
        var baseTime = DateTime.Now.AddDays(-AppConstants.MaxBackupGenerations - 1);
        for (int i = 0; i < AppConstants.MaxBackupGenerations; i++)
        {
            var path = Path.Combine(_backupDirectory, $"backup_2026070{i % 10}_{i:D6}.db");
            File.WriteAllText(path, "dummy");
            File.SetCreationTime(path, baseTime.AddDays(i));
        }

        // 一時ファイルは最新の作成日時を持たせる（世代として数えられたら実在世代が削られる）
        const int TempFileCount = 3;
        for (int i = 0; i < TempFileCount; i++)
        {
            var temp = Path.Combine(
                _backupDirectory,
                $"backup_20260813_00000{i}.db.{Guid.NewGuid():N}{BackupService.BackupTempFileExtension}");
            File.WriteAllText(temp, "dummy");
            File.SetCreationTime(temp, DateTime.Now);
            File.SetLastWriteTime(temp, DateTime.Now);
        }

        // Act: 1 世代増える → 上限超過分の 1 件だけが削除されるはず
        var created = await _service.ExecuteAutoBackupAsync();

        // Assert
        created.Should().NotBeNull();
        ListBackupGenerations().Should().HaveCount(
            AppConstants.MaxBackupGenerations,
            "一時ファイルは世代に数えないため、超過分の 1 件だけが削除されること");
        ListTempFiles().Should().HaveCount(TempFileCount, "書き込み中の可能性がある一時ファイルは残すこと");
    }

    #endregion

    #region 中断された一時ファイルの回収

    /// <summary>
    /// 十分に古い一時ファイル（プロセス強制終了の残骸）が削除されることを確認
    /// </summary>
    [Fact]
    public async Task ExecuteAutoBackupAsync_古い一時ファイルを削除すること()
    {
        // Arrange
        var stale = CreateTempFile(DateTime.Now.AddHours(-AppConstants.BackupTempFileStaleHours - 1));

        // Act
        await _service.ExecuteAutoBackupAsync();

        // Assert
        File.Exists(stale).Should().BeFalse();
    }

    /// <summary>
    /// バックアップが失敗したときも古い一時ファイルを回収することを確認
    /// </summary>
    /// <remarks>
    /// 回収を成功時にしか行わないと、UNC 断でコピーが失敗し続ける環境
    /// （＝まさに残骸が生まれる環境）でだけ回収が動かず、DB 本体大のファイルが
    /// 起動のたびに積み上がる。
    /// </remarks>
    [Fact]
    public async Task ExecuteAutoBackupAsync_バックアップ失敗時も古い一時ファイルを回収すること()
    {
        // Arrange
        var stale = CreateTempFile(DateTime.Now.AddHours(-AppConstants.BackupTempFileStaleHours - 1));
        var service = CreateFailingService();

        // Act
        var result = await service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().BeNull("コピーが中断されたためバックアップは失敗すること");
        File.Exists(stale).Should().BeFalse("失敗時も回収は行われること");
    }

    /// <summary>
    /// 書き込み中の可能性がある一時ファイルを削除しないことを確認
    /// </summary>
    /// <remarks>
    /// 共有モードでは最大 20 台が同じフォルダーへ書き込む。
    /// 他 PC が書いている最中の一時ファイルを消すと、そのバックアップを失敗させてしまう。
    /// </remarks>
    [Fact]
    public async Task ExecuteAutoBackupAsync_新しい一時ファイルは残すこと()
    {
        // Arrange
        var inProgress = CreateTempFile(DateTime.Now.AddHours(-1));

        // Act
        await _service.ExecuteAutoBackupAsync();

        // Assert
        File.Exists(inProgress).Should().BeTrue();
    }

    #endregion

    #region 後片付けの失敗をバックアップの成否に混ぜない

    /// <summary>
    /// 世代削除に失敗してもバックアップを成功として扱い、成功日時を記録することを確認
    /// </summary>
    /// <remarks>
    /// 最終名へのリネームが済んだ時点でバックアップは確定している。そのあとの後片付けの
    /// 失敗を成否判定に混ぜると `last_backup_success_at` が更新されず、実際には書けているのに
    /// 7 日後に `BackupStale` 警告が出る（`.claude/rules/development-conventions.md`
    /// 「コミット確定後の後処理を、成否の判定に巻き込まない」）。
    /// </remarks>
    [Fact]
    public async Task ExecuteAutoBackupAsync_世代削除の失敗をバックアップの失敗にしないこと()
    {
        // Arrange
        var service = new CleanupFailingBackupService(_dbContext, _settingsRepositoryMock.Object);

        // Act
        var result = await service.ExecuteAutoBackupAsync();

        // Assert
        result.Should().NotBeNull("バックアップファイルは最終名で確定しているため成功として扱うこと");
        File.Exists(result!).Should().BeTrue();
        ListBackupGenerations().Should().ContainSingle().Which.Should().Be(result);

        _settingsRepositoryMock.Verify(
            x => x.SetAsync(SettingsRepository.KeyLastBackupSuccessAt, It.IsAny<string>()),
            Times.Once,
            "後片付けの失敗で成功日時の記録まで飛ばしてはならない");
    }

    #endregion

    #region ジャーナルファイルの後始末

    /// <summary>
    /// コピー失敗時に一時ファイルのロールバックジャーナルも削除することを確認
    /// </summary>
    /// <remarks>
    /// ジャーナルの名前は `.tmp` で終わらないため回収処理の対象外。ここで消さないと誰も消さない。
    /// </remarks>
    [Fact]
    public void CreateBackup_コピー失敗時に一時ファイルのジャーナルを残さないこと()
    {
        // Arrange
        var service = CreateFailingService();
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");

        // Act
        service.CreateBackup(backupPath);

        // Assert
        service.LastCopyDestination.Should().NotBeNull();
        File.Exists(service.LastCopyDestination + "-journal").Should().BeFalse();
        ListJournalFiles().Should().BeEmpty();
    }

    /// <summary>
    /// コピー成功時にも一時ファイルのロールバックジャーナルを残さないことを確認
    /// </summary>
    /// <remarks>
    /// 一時ファイル本体は最終名へリネームされるが、取り残されたジャーナルは
    /// 名前が `.tmp` で終わらないため回収処理では拾えない。
    /// </remarks>
    [Fact]
    public void CreateBackup_コピー成功時にも一時ファイルのジャーナルを残さないこと()
    {
        // Arrange
        var service = new JournalLeavingBackupService(_dbContext, _settingsRepositoryMock.Object);
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");

        // Act
        var result = service.CreateBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        File.Exists(backupPath).Should().BeTrue();
        ListJournalFiles().Should().BeEmpty();
    }

    #endregion

    #region 破損バックアップからのリストア拒否

    /// <summary>
    /// マジックヘッダは正しいが切り詰められたバックアップを、リストアが拒否することを確認
    /// </summary>
    /// <remarks>
    /// 本修正より前に作られた破損世代が保存先に残っている可能性があるため、
    /// 読み取り側でも検証する。拒否されないと 6 年保存の台帳が破損コピーで上書きされる。
    /// </remarks>
    [Fact]
    public void RestoreFromBackup_切り詰められたバックアップを拒否し本番DBを保持すること()
    {
        // Arrange: 正常なバックアップを作ってから、途中まで書けた状態に切り詰める
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");
        _service.CreateBackup(backupPath).Should().BeTrue();

        new FileInfo(backupPath).Length.Should().BeGreaterThan(
            TruncatedLength,
            "切り詰めが意味を持つ大きさの DB でなければ、このテストは何も検証していない");

        Truncate(backupPath, TruncatedLength);
        ReadMagicHeader(backupPath).Should().Be(
            "SQLite format 3\0",
            "切り詰めてもマジックヘッダは残る（旧実装がこれを有効と判定していた）");

        var originalLength = new FileInfo(_testDbPath).Length;

        // Act
        var result = _service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeFalse();
        new FileInfo(_testDbPath).Length.Should().Be(originalLength, "本番 DB が上書きされていないこと");
    }

    /// <summary>
    /// 正常なバックアップはリストアできることを確認（検証強化による誤検知がないこと）
    /// </summary>
    [Fact]
    public void RestoreFromBackup_正常なバックアップを受け入れること()
    {
        // Arrange
        var backupPath = Path.Combine(_backupDirectory, "backup_20260813_090000.db");
        _service.CreateBackup(backupPath).Should().BeTrue();
        var backupLength = new FileInfo(backupPath).Length;

        // Act
        var result = _service.RestoreFromBackup(backupPath);

        // Assert
        result.Should().BeTrue();
        new FileInfo(_testDbPath).Length.Should().Be(backupLength);
    }

    /// <summary>
    /// ヘッダに満たない長さのファイルを拒否することを確認
    /// </summary>
    [Fact]
    public void IsValidSqliteFile_ヘッダ長に満たないファイルを拒否すること()
    {
        // Arrange: マジックヘッダだけの 16 バイトファイル
        var path = Path.Combine(_backupDirectory, "backup_20260813_090000.db");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0"));

        // Act / Assert
        BackupService.IsValidSqliteFile(path).Should().BeFalse();
    }

    #endregion

    #region ヘルパー

    private FailingCopyBackupService CreateFailingService()
    {
        return new FailingCopyBackupService(
            _dbContext,
            _settingsRepositoryMock.Object,
            TruncatedLength);
    }

    private List<string> ListBackupGenerations()
    {
        return BackupService.EnumerateBackupFiles(_backupDirectory)
            .Select(f => f.FullName)
            .ToList();
    }

    private List<string> ListTempFiles()
    {
        // 世代の検索パターンに依存せず、拡張子で直接探す
        return Directory.GetFiles(_backupDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(BackupService.BackupTempFileExtension, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<string> ListJournalFiles()
    {
        return Directory.GetFiles(_backupDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith("-journal", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string CreateTempFile(DateTime lastWriteTime)
    {
        var path = Path.Combine(
            _backupDirectory,
            $"backup_20260813_090000.db.{Guid.NewGuid():N}{BackupService.BackupTempFileExtension}");
        File.WriteAllText(path, "incomplete");
        File.SetLastWriteTime(path, lastWriteTime);
        return path;
    }

    private static void Truncate(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
        stream.SetLength(length);
    }

    private static string ReadMagicHeader(string path)
    {
        var header = new byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        stream.Read(header, 0, header.Length);
        return System.Text.Encoding.ASCII.GetString(header);
    }

    /// <summary>
    /// コピー途中でのネットワーク断を再現するテスト用サービス
    /// </summary>
    /// <remarks>
    /// 「ページ 1 まで書けた状態でファイルが残り、そのあと例外になる」という
    /// 実機での失敗の形をそのまま再現する。空ファイルを置いて throw するだけでは、
    /// マジックヘッダを備えた破損ファイルという本 Issue の核心を再現できない。
    /// </remarks>
    private sealed class FailingCopyBackupService : BackupService
    {
        private readonly long _truncateTo;

        public FailingCopyBackupService(
            DbContext dbContext,
            ISettingsRepository settingsRepository,
            long truncateTo)
            : base(dbContext, settingsRepository, NullLogger<BackupService>.Instance)
        {
            _truncateTo = truncateTo;
        }

        /// <summary>
        /// 最後にコピー先として渡されたパス（一時ファイルであることの検証に使う）
        /// </summary>
        /// <remarks>コピーが一度も実行されていなければ null。</remarks>
        public string? LastCopyDestination { get; private set; }

        internal override void CopyDatabaseTo(string destinationPath)
        {
            LastCopyDestination = destinationPath;

            // いったん正しくコピーしてから切り詰めることで「途中まで書けた」状態を作る
            base.CopyDatabaseTo(destinationPath);
            Truncate(destinationPath, _truncateTo);

            // コピー先の接続が異常終了してロールバックジャーナルが取り残された状態も併せて再現する
            File.WriteAllText(destinationPath + "-journal", "orphaned journal");

            throw new IOException("ネットワーク共有への書き込みが中断されました");
        }
    }

    /// <summary>
    /// コピーは成功するが、ロールバックジャーナルが取り残された状況を再現するテスト用サービス
    /// </summary>
    private sealed class JournalLeavingBackupService : BackupService
    {
        public JournalLeavingBackupService(DbContext dbContext, ISettingsRepository settingsRepository)
            : base(dbContext, settingsRepository, NullLogger<BackupService>.Instance)
        {
        }

        internal override void CopyDatabaseTo(string destinationPath)
        {
            base.CopyDatabaseTo(destinationPath);
            File.WriteAllText(destinationPath + "-journal", "orphaned journal");
        }
    }

    /// <summary>
    /// バックアップ自体は成功するが、後片付け（世代削除）が失敗する状況を再現するテスト用サービス
    /// </summary>
    /// <remarks>
    /// 実機では共有フォルダーの切断・権限エラーで <c>Directory.GetFiles</c> が失敗する。
    /// 非同期の失敗（<c>Task.Run</c> 内の例外）と同じ形にするため、同期 throw ではなく
    /// 失敗済みの <see cref="Task"/> を返す。
    /// </remarks>
    private sealed class CleanupFailingBackupService : BackupService
    {
        public CleanupFailingBackupService(DbContext dbContext, ISettingsRepository settingsRepository)
            : base(dbContext, settingsRepository, NullLogger<BackupService>.Instance)
        {
        }

        internal override Task CleanupOldBackupsAsync(string backupPath)
        {
            return Task.FromException(
                new IOException("ネットワーク共有からバックアップ一覧を取得できませんでした"));
        }
    }

    #endregion
}
