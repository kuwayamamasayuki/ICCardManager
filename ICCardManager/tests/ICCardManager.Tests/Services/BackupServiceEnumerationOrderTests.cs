using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// バックアップの列挙順・表示日時がファイル名のタイムスタンプ由来であることの単体テスト（Issue #1950）
/// </summary>
/// <remarks>
/// <para>
/// Issue #1813 は世代削除の日判定を <c>CreationTime</c> からファイル名へ移したが、
/// 列挙（<see cref="BackupService.EnumerateBackupFiles"/>）と一覧表示
/// （<see cref="BackupService.GetBackupFilesAsync"/>）は <c>CreationTime</c> のまま取り残されていた。
/// 管理者が退避しておいた古いバックアップを共有フォルダーへコピーで戻すと <c>CreationTime</c> が
/// 今日になるため、リストア画面はそれを「最新」として先頭に並べ、選んだ管理者は
/// 6 年保存の台帳 DB を古いコピーで上書きしてしまう。
/// </para>
/// <para>
/// 回帰は「欠陥を突く側」（名前が古く作成日時が新しいファイルを先頭にしないこと）と
/// 「削除側と揃えたフォールバックを壊していない側」（解析できない名前は作成日時で並ぶこと）を
/// 対で置く。前者だけだと並べ替えを丸ごと止めた実装でも緑になり得る。
/// </para>
/// </remarks>
public class BackupServiceEnumerationOrderTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly string _backupDirectory;
    private readonly DbContext _dbContext;
    private readonly BackupService _service;

    public BackupServiceEnumerationOrderTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"BackupEnumOrderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _backupDirectory = Path.Combine(_testDirectory, "backup");
        Directory.CreateDirectory(_backupDirectory);

        _dbContext = new DbContext(Path.Combine(_testDirectory, "test.db"));
        _dbContext.InitializeDatabase();

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings { BackupPath = _backupDirectory });

        _service = new BackupService(
            _dbContext,
            settingsRepositoryMock.Object,
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
        catch (IOException)
        {
            // テスト用一時ディレクトリの後始末失敗はテスト結果に影響させない
        }

        GC.SuppressFinalize(this);
    }

    #region 欠陥を突く側

    /// <summary>
    /// 作成日時が最も新しくても、ファイル名が古いバックアップを先頭に並べないこと。
    /// 退避しておいた世代を共有フォルダーへコピーで戻した状況を再現する。
    /// </summary>
    [Fact]
    public void EnumerateBackupFiles_作成日時が新しくても名前が古いファイルを先頭にしないこと()
    {
        // 名前は 1 年前だが、コピーで戻したため作成日時は今日
        var restored = CreateAutomaticBackup(new DateTime(2026, 1, 1, 9, 0, 0));
        File.SetCreationTime(restored.FullName, new DateTime(2026, 8, 29, 18, 0, 0));

        var older = CreateAutomaticBackup(new DateTime(2026, 8, 27, 8, 0, 0));
        File.SetCreationTime(older.FullName, new DateTime(2026, 8, 27, 8, 0, 0));

        var newest = CreateAutomaticBackup(new DateTime(2026, 8, 28, 8, 0, 0));
        File.SetCreationTime(newest.FullName, new DateTime(2026, 8, 28, 8, 0, 0));

        var result = BackupService.EnumerateBackupFiles(_backupDirectory);

        result.Select(f => f.Name).Should().Equal(
            newest.Name,
            older.Name,
            restored.Name);
    }

    /// <summary>
    /// 一覧の表示日時（<see cref="BackupFileInfo.CreatedAt"/>）もファイル名由来であること。
    /// リストア確認ダイアログはこの値を読み上げるため、作成日時のままだと
    /// 「作成日時: 2026/08/29」と案内しながら 1 年前の DB で上書きすることになる。
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_表示日時がファイル名のタイムスタンプ由来であること()
    {
        var nameTimestamp = new DateTime(2026, 1, 1, 9, 0, 0);
        var restored = CreateAutomaticBackup(nameTimestamp);
        File.SetCreationTime(restored.FullName, new DateTime(2026, 8, 29, 18, 0, 0));

        var files = (await _service.GetBackupFilesAsync()).ToList();

        files.Should().ContainSingle();
        files[0].CreatedAt.Should().Be(nameTimestamp);
    }

    /// <summary>
    /// 一覧の並びも同様にファイル名由来であること（<see cref="BackupService.EnumerateBackupFiles"/> を経由するが、
    /// リストア画面が実際に読むのはこちらなので入口側でも表明する）。
    /// </summary>
    [Fact]
    public async Task GetBackupFilesAsync_作成日時が新しくても名前が古いファイルを先頭にしないこと()
    {
        var restored = CreateAutomaticBackup(new DateTime(2026, 1, 1, 9, 0, 0));
        File.SetCreationTime(restored.FullName, new DateTime(2026, 8, 29, 18, 0, 0));

        var newest = CreateAutomaticBackup(new DateTime(2026, 8, 28, 8, 0, 0));
        File.SetCreationTime(newest.FullName, new DateTime(2026, 8, 28, 8, 0, 0));

        var files = (await _service.GetBackupFilesAsync()).ToList();

        files.Select(f => f.FileName).Should().Equal(newest.Name, restored.Name);
    }

    /// <summary>
    /// 手動・リストア前バックアップも同じ書式で命名されるため、同じ根拠で並ぶこと。
    /// </summary>
    [Fact]
    public void EnumerateBackupFiles_手動バックアップも名前の日時で並ぶこと()
    {
        var manual = CreateBackup($"backup_manual_{Stamp(new DateTime(2026, 3, 1, 10, 0, 0))}.db");
        File.SetCreationTime(manual.FullName, new DateTime(2026, 8, 29, 18, 0, 0));

        var automatic = CreateAutomaticBackup(new DateTime(2026, 8, 28, 8, 0, 0));
        File.SetCreationTime(automatic.FullName, new DateTime(2026, 8, 28, 8, 0, 0));

        var result = BackupService.EnumerateBackupFiles(_backupDirectory);

        result.Select(f => f.Name).Should().Equal(automatic.Name, manual.Name);
    }

    #endregion

    #region 対の表明（削除側と揃えた挙動を壊していない側）

    /// <summary>
    /// 名前と作成日時が一致する通常のケースでは、従来どおり新しい順に並ぶこと。
    /// これが無いと、並べ替えを丸ごと止めた（または昇順にした）実装でも
    /// 「欠陥を突く側」だけが偶然緑になる余地が残る。
    /// </summary>
    [Fact]
    public void EnumerateBackupFiles_名前と作成日時が一致する場合は新しい順に並ぶこと()
    {
        var oldest = CreateAutomaticBackup(new DateTime(2026, 8, 26, 8, 0, 0));
        var middle = CreateAutomaticBackup(new DateTime(2026, 8, 27, 8, 0, 0));
        var newest = CreateAutomaticBackup(new DateTime(2026, 8, 28, 8, 0, 0));

        foreach (var file in new[] { oldest, middle, newest })
        {
            File.SetCreationTime(
                file.FullName,
                BackupService.ResolveBackupTimestamp(new FileInfo(file.FullName)));
        }

        var result = BackupService.EnumerateBackupFiles(_backupDirectory);

        result.Select(f => f.Name).Should().Equal(newest.Name, middle.Name, oldest.Name);
    }

    /// <summary>
    /// 解析できない命名のファイルは削除側（Issue #1813）と同じく作成日時へフォールバックすること。
    /// 削除側は「自動バックアップと見なさず消さない」＝取りこぼす側へ倒しているので、
    /// 列挙側も同じ根拠（<see cref="BackupService.ResolveBackupTimestamp"/>）を使う。
    /// </summary>
    [Fact]
    public void EnumerateBackupFiles_解析できない名前は作成日時で並ぶこと()
    {
        var unknown = CreateBackup("backup_unknown_naming.db");
        File.SetCreationTime(unknown.FullName, new DateTime(2026, 8, 29, 18, 0, 0));

        var automatic = CreateAutomaticBackup(new DateTime(2026, 8, 28, 8, 0, 0));
        File.SetCreationTime(automatic.FullName, new DateTime(2026, 8, 28, 8, 0, 0));

        var result = BackupService.EnumerateBackupFiles(_backupDirectory);

        result.Select(f => f.Name).Should().Equal(unknown.Name, automatic.Name);
    }

    #endregion

    #region ヘルパー

    private static string Stamp(DateTime value) =>
        value.ToString(BackupService.BackupTimestampFormat, CultureInfo.InvariantCulture);

    private FileInfo CreateAutomaticBackup(DateTime timestamp) =>
        CreateBackup($"backup_{Stamp(timestamp)}.db");

    private FileInfo CreateBackup(string fileName)
    {
        var path = Path.Combine(_backupDirectory, fileName);
        File.WriteAllText(path, "dummy");
        return new FileInfo(path);
    }

    #endregion
}
