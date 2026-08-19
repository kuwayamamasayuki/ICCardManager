using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// バックアップ世代の保持ルールの単体テスト（Issue #1813）
/// </summary>
/// <remarks>
/// <para>
/// 「起動ごとに 1 世代・合計 30 ファイル」という旧ルールは、共有フォルダーへ最大 20 台が
/// 書き込む運用では 1 日あたり 20 世代前後を生み、実効保持期間が約 1.5 日にしかならなかった
/// （金曜に混入した破損に月曜気付いても、遡れる世代がすべて破損後のものに入れ替わっている）。
/// </para>
/// <para>
/// 判定を <see cref="BackupService.SelectBackupsToDelete"/> という純関数へ切り出してあるため、
/// 実際にバックアップを何十回も作らずに「20 台運用」「1 日 30 回再起動」「1 年分の履歴」を
/// 直接表現できる。ファイルは名前だけが意味を持つので中身は空でよい。
/// </para>
/// </remarks>
public class BackupServiceRetentionTests : IDisposable
{
    private readonly string _directory;

    public BackupServiceRetentionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"BackupRetentionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // テスト用一時ディレクトリの後始末失敗はテスト結果に影響させない
        }
        GC.SuppressFinalize(this);
    }

    #region 自動バックアップ: 日ごとに 1 世代

    /// <summary>
    /// 共有モードの 20 台運用（同じ日に 20 世代が生まれる）で、その日は最新 1 世代だけが残ること。
    /// 旧ルールでは 20 世代すべてが保持され、30 ファイルの上限を 1.5 日で使い切っていた。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_同一日に20世代あっても最新1世代だけを残すこと()
    {
        var files = Enumerable.Range(0, 20)
            .Select(i => CreateAutomaticBackup(new DateTime(2026, 8, 19, 8, 0, 0).AddMinutes(i * 15)))
            .ToList();

        var deleted = BackupService.SelectBackupsToDelete(files);

        var kept = Remaining(files, deleted);
        kept.Should().ContainSingle();
        kept[0].Name.Should().Be("backup_20260819_124500.db", "その日の最新世代（＝最も新しい状態）を残す");
    }

    /// <summary>
    /// 保持日数ちょうどの日数分は 1 件も削除されないこと（境界: 30 日）。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_保持日数ちょうどなら削除しないこと()
    {
        var files = CreateAutomaticBackupsForConsecutiveDays(
            new DateTime(2026, 8, 19, 9, 0, 0), AppConstants.BackupRetentionDays);

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().BeEmpty();
    }

    /// <summary>
    /// 保持日数を 1 日超えたら、最も古い日の世代だけが削除されること（境界: 31 日）。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_保持日数を超えた最古の日を削除すること()
    {
        var newest = new DateTime(2026, 8, 19, 9, 0, 0);
        var files = CreateAutomaticBackupsForConsecutiveDays(
            newest, AppConstants.BackupRetentionDays + 1);

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().ContainSingle();
        BackupService.ResolveBackupTimestamp(deleted[0]).Date
            .Should().Be(newest.AddDays(-AppConstants.BackupRetentionDays).Date);
    }

    /// <summary>
    /// 1 日に何回起動しても実効保持期間が変わらないこと。
    /// 単一 PC で 1 日 30 回再起動しても、旧ルールでは 30 世代を 1 日で使い切っていた。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_1日30回起動しても直近30日分が残ること()
    {
        var files = new List<FileInfo>();
        var newestDay = new DateTime(2026, 8, 19);
        for (int day = 0; day < AppConstants.BackupRetentionDays; day++)
        {
            for (int run = 0; run < 30; run++)
            {
                files.Add(CreateAutomaticBackup(newestDay.AddDays(-day).AddHours(8).AddMinutes(run)));
            }
        }

        var deleted = BackupService.SelectBackupsToDelete(files);

        var keptDays = Remaining(files, deleted)
            .Select(f => BackupService.ResolveBackupTimestamp(f).Date)
            .ToList();
        keptDays.Should().HaveCount(AppConstants.BackupRetentionDays);
        keptDays.Distinct().Should().HaveCount(AppConstants.BackupRetentionDays,
            "1 日につき 1 世代だけが残る");
    }

    /// <summary>
    /// 保持の単位は暦日ではなく「バックアップが存在する日」であること。
    /// 長期休暇でアプリを起動しなかった期間があっても、直近 30 回分の稼働日が残る。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_起動しなかった日は保持日数を消費しないこと()
    {
        // 週 1 回だけ起動する運用を 30 週分（＝約 7 か月にわたる 30 日分）
        var files = Enumerable.Range(0, AppConstants.BackupRetentionDays)
            .Select(i => CreateAutomaticBackup(new DateTime(2026, 8, 19, 9, 0, 0).AddDays(-7 * i)))
            .ToList();

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().BeEmpty("暦日で切ると 30 日より前の世代が消えてしまう");
    }

    #endregion

    #region 手動・リストア前バックアップ

    /// <summary>
    /// リストア前バックアップが、同じ日の自動バックアップに巻き込まれて消えないこと。
    /// 「リストア → 再起動 → 自動バックアップ」の流れで、リストア直前の唯一の退避が
    /// 同じ日のうちに失われるのを防ぐ。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_リストア前バックアップを同日の自動バックアップで消さないこと()
    {
        var day = new DateTime(2026, 8, 19);
        var preRestore = CreateBackup($"backup_pre_restore_{Stamp(day.AddHours(10))}.db");
        var files = new List<FileInfo>
        {
            preRestore,
            CreateAutomaticBackup(day.AddHours(9)),
            CreateAutomaticBackup(day.AddHours(11)),
        };

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().NotContain(f => f.Name == preRestore.Name);
        Remaining(files, deleted).Select(f => f.Name).Should().Contain(preRestore.Name);
    }

    /// <summary>
    /// 手動バックアップは日単位で間引かず、新しい順に上限件数だけ残すこと。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_手動バックアップは最新の上限件数だけ残すこと()
    {
        var baseTime = new DateTime(2026, 8, 19, 9, 0, 0);
        var files = Enumerable.Range(0, AppConstants.MaxManualBackupGenerations + 3)
            .Select(i => CreateBackup($"backup_manual_{Stamp(baseTime.AddMinutes(-i))}.db"))
            .ToList();

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().HaveCount(3);
        Remaining(files, deleted).Should().HaveCount(AppConstants.MaxManualBackupGenerations);
        deleted.Select(f => BackupService.ResolveBackupTimestamp(f))
            .Should().OnlyContain(t => t <= baseTime.AddMinutes(-AppConstants.MaxManualBackupGenerations),
                "削除されるのは古い側");
    }

    /// <summary>
    /// 手動バックアップの件数は自動バックアップの日数を消費しないこと（枠が独立していること）。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_手動バックアップが自動バックアップの保持枠を奪わないこと()
    {
        var files = CreateAutomaticBackupsForConsecutiveDays(
            new DateTime(2026, 8, 19, 9, 0, 0), AppConstants.BackupRetentionDays);
        var manual = Enumerable.Range(0, AppConstants.MaxManualBackupGenerations)
            .Select(i => CreateBackup($"backup_manual_{Stamp(new DateTime(2026, 8, 19, 20, 0, 0).AddMinutes(-i))}.db"))
            .ToList();
        files.AddRange(manual);

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().BeEmpty();
    }

    #endregion

    #region 命名の判別

    /// <summary>
    /// 自動バックアップと見なすのは <c>backup_yyyyMMdd_HHmmss.db</c> に完全一致する名前だけであること。
    /// 前方一致で判定すると手動・リストア前バックアップまで日単位の間引きに巻き込む。
    /// </summary>
    [Theory]
    [InlineData("backup_20260819_090000.db", true)]
    [InlineData("backup_manual_20260819_090000.db", false)]
    [InlineData("backup_pre_restore_20260819_090000.db", false)]
    [InlineData("backup_20260819.db", false)]
    [InlineData("backup_20260819_090000.db.a1b2c3d4.tmp", false)]
    [InlineData("other_20260819_090000.db", false)]
    public void TryParseAutomaticBackupTimestamp_命名を厳密に判別すること(string fileName, bool expected)
    {
        BackupService.TryParseAutomaticBackupTimestamp(fileName, out var timestamp).Should().Be(expected);

        if (expected)
        {
            timestamp.Should().Be(new DateTime(2026, 8, 19, 9, 0, 0));
        }
    }

    /// <summary>
    /// 命名を解析できないファイルは自動バックアップと見なさず、日単位の間引きで削除しないこと。
    /// 消してしまうと復旧手段が無いため、取りこぼす側へ倒す。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_解析できない命名のファイルを日単位で消さないこと()
    {
        var day = new DateTime(2026, 8, 19);
        var unknown = CreateBackup("backup_unknown_naming.db");
        var files = new List<FileInfo>
        {
            unknown,
            CreateAutomaticBackup(day.AddHours(9)),
            CreateAutomaticBackup(day.AddHours(10)),
        };

        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().ContainSingle().Which.Name.Should().Be($"backup_{Stamp(day.AddHours(9))}.db");
    }

    /// <summary>
    /// 日の判定にはファイル名のタイムスタンプを使い、作成日時には依存しないこと。
    /// 共有フォルダーへコピー・移動されると作成日時は書き換わり得るが、
    /// ファイル名は書いた側が意図した日時をそのまま保つ。
    /// </summary>
    [Fact]
    public void SelectBackupsToDelete_作成日時ではなくファイル名の日時で判定すること()
    {
        var older = CreateAutomaticBackup(new DateTime(2026, 8, 18, 9, 0, 0));
        var newer = CreateAutomaticBackup(new DateTime(2026, 8, 19, 9, 0, 0));

        // 作成日時だけを逆転させる（フォルダーへコピーし直した状況）
        File.SetCreationTime(older.FullName, new DateTime(2026, 8, 20, 12, 0, 0));
        File.SetCreationTime(newer.FullName, new DateTime(2026, 8, 20, 11, 0, 0));

        var files = new List<FileInfo> { new FileInfo(older.FullName), new FileInfo(newer.FullName) };
        var deleted = BackupService.SelectBackupsToDelete(files);

        deleted.Should().BeEmpty("別々の日のバックアップなので、どちらも保持対象");
        BackupService.ResolveBackupTimestamp(new FileInfo(older.FullName))
            .Should().Be(new DateTime(2026, 8, 18, 9, 0, 0));
    }

    /// <summary>
    /// 名前から日時を解析できない場合は作成日時へフォールバックすること。
    /// </summary>
    [Fact]
    public void ResolveBackupTimestamp_解析できない場合は作成日時を使うこと()
    {
        var file = CreateBackup("backup_unknown_naming.db");
        var creationTime = new DateTime(2026, 8, 15, 13, 45, 0);
        File.SetCreationTime(file.FullName, creationTime);

        BackupService.ResolveBackupTimestamp(new FileInfo(file.FullName))
            .Should().BeCloseTo(creationTime, TimeSpan.FromSeconds(2));
    }

    #endregion

    #region ヘルパー

    private static string Stamp(DateTime value) =>
        value.ToString(BackupService.BackupTimestampFormat, CultureInfo.InvariantCulture);

    private FileInfo CreateAutomaticBackup(DateTime timestamp) =>
        CreateBackup($"backup_{Stamp(timestamp)}.db");

    private List<FileInfo> CreateAutomaticBackupsForConsecutiveDays(DateTime newestDay, int dayCount) =>
        Enumerable.Range(0, dayCount)
            .Select(i => CreateAutomaticBackup(newestDay.AddDays(-i)))
            .ToList();

    private FileInfo CreateBackup(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, "dummy");
        return new FileInfo(path);
    }

    private static List<FileInfo> Remaining(IEnumerable<FileInfo> all, IEnumerable<FileInfo> deleted)
    {
        var deletedNames = new HashSet<string>(deleted.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
        return all.Where(f => !deletedNames.Contains(f.FullName)).ToList();
    }

    #endregion
}
