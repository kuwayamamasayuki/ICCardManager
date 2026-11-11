#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2379: リストアで採番（<c>sqlite_sequence</c>）がバックアップの時点へ巻き戻り、リストア後に作られた行が
/// リストア前の ID を再び使うことを防ぐテスト（実 SQLite）。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は、バックアップの後に作られリストアで消えた行の ID を、リストア後の新しい行が再び使っていた。
/// リストア前に出力した利用履歴 CSV のその ID の行は同じカードの別の履歴を指し、「更新」として検証を通って
/// 台帳の行を別の利用の内容で書き換えた。
/// </para>
/// <para>
/// 「欠陥を突く側」（リストア後の ID がリストア前の ID と重ならない・古い CSV が別の履歴を書き換えない）と、
/// 「正当な動作を塞いでいない側」（リストア後の通常の登録・同じ履歴を指す CSV の取り込み・バックアップの側が大きい採番を戻さない・
/// リストア前の DB を読めなくてもリストアは止まらない）を対で置く。
/// </para>
/// </remarks>
public class BackupServiceRestoreSequenceTests : IDisposable
{
    private const string CardIdm = "0123456789ABCDEF";
    private const string CardNumber = "001";

    /// <summary>UTF-8 with BOM（Excel 対応）。<c>ReadCsvFileAsync</c> の文字コード判別を通すため。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly string _dbPath;
    private readonly string _backupPath;
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly RecordingLogger<BackupService> _logger = new();
    private readonly BackupService _service;

    public BackupServiceRestoreSequenceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RestoreSequenceTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _dbPath = Path.Combine(_testDirectory, "iccard.db");
        _backupPath = Path.Combine(_testDirectory, "backup", "backup_test.db");

        MigratedDatabaseTemplate.CopyTo(_dbPath);
        _dbContext = new DbContext(_dbPath);
        _dbContext.InitializeDatabase();
        _ledgerRepository = new LedgerRepository(_dbContext);

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _service = new BackupService(_dbContext, settingsRepositoryMock.Object, _logger);
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
            // 一時ディレクトリの削除失敗はテスト結果に影響しない
        }

        GC.SuppressFinalize(this);
    }

    #region ヘルパー

    private async Task ExecuteAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value == null || value is DBNull ? 0 : Convert.ToInt64(value);
    }

    private Task SeedCardAsync() => ExecuteAsync(
        $"INSERT INTO ic_card (card_idm, card_type, card_number) VALUES ('{CardIdm}', 'はやかけん', '{CardNumber}')");

    /// <summary>通常の登録経路（<see cref="LedgerRepository.InsertAsync(Ledger)"/>）で履歴と明細を 1 件作り、履歴の ID を返す</summary>
    private async Task<int> InsertUsageAsync(DateTime date, string entry, string exit, int expense, int balance)
    {
        var id = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardIdm,
            Date = date,
            Summary = $"鉄道（{entry}～{exit}）",
            Expense = expense,
            Balance = balance,
            StaffName = "山田太郎"
        });
        await _ledgerRepository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = id,
            UseDate = date,
            EntryStation = entry,
            ExitStation = exit,
            Amount = expense,
            Balance = balance
        });
        return id;
    }

    private Task<int> InsertChargeAsync(DateTime date, int income, int balance) =>
        _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardIdm,
            Date = date,
            Summary = "役務費によりチャージ",
            Income = income,
            Balance = balance
        });

    private Task InsertOperationLogAsync() => ExecuteAsync(
        "INSERT INTO operation_log (operator_idm, operator_name, target_table, target_id, action) " +
        "VALUES ('STAFF00000000001', '山田太郎', 'ledger', '1', 'UPDATE')");

    private Task InsertMergeHistoryAsync() => ExecuteAsync(
        "INSERT INTO ledger_merge_history (target_ledger_id, description, undo_data) VALUES (1, 'テスト', '{}')");

    private void CreateBackup()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        _service.CreateBackup(_backupPath).Should().BeTrue(_logger.FormatEntries());
    }

    private void Restore()
    {
        _service.RestoreFromBackup(_backupPath).Should().BeTrue(_logger.FormatEntries());
    }

    private CsvImportService CreateImportService()
    {
        var cardRepositoryMock = new Mock<ICardRepository>();
        cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = CardIdm, CardType = "はやかけん", CardNumber = CardNumber } });
        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        return new CsvImportService(
            cardRepositoryMock.Object,
            new Mock<IStaffRepository>().Object,
            _ledgerRepository,
            new Mock<IValidationService>().Object,
            _dbContext,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object,
            new OperationLogger(new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>()),
            NullLogger<CsvImportService>.Instance);
    }

    private async Task<string> WriteLedgerCsvAsync(string fileName, params string[] rows)
    {
        var path = Path.Combine(_testDirectory, fileName);
        var content = "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考" + Environment.NewLine +
                      string.Join(Environment.NewLine, rows);
        await Task.Run(() => File.WriteAllText(path, content, CsvEncoding));
        return path;
    }

    private static string CsvRow(int id, DateTime date, string summary, int expense, int balance, string note = "") =>
        $"{id},{date:yyyy-MM-dd HH:mm:ss},{CardIdm},{CardNumber},{summary},,{expense},{balance},山田太郎,{note}";

    #endregion

    #region 欠陥を突く側

    /// <summary>
    /// 採番の大きい DB から採番の小さいバックアップへリストアした後、新しく作った履歴（と明細の親 ID）・操作ログの ID が、
    /// リストア前に使われていた最大の ID より大きいこと。
    /// </summary>
    [Fact]
    public async Task リストア後に作った行のIDはリストア前に使われていたIDより大きいこと()
    {
        await SeedCardAsync();
        await InsertChargeAsync(new DateTime(2025, 1, 10), 10000, 10000);
        await InsertUsageAsync(new DateTime(2025, 1, 11), "天神", "博多", 210, 9790);
        await InsertOperationLogAsync();
        CreateBackup();

        // バックアップの後に作られ、リストアで消える行
        await InsertUsageAsync(new DateTime(2025, 1, 12), "博多", "天神", 210, 9580);
        await InsertUsageAsync(new DateTime(2025, 1, 13), "天神", "薬院", 160, 9420);
        await InsertOperationLogAsync();
        await InsertOperationLogAsync();
        var maxLedgerIdBeforeRestore = await ScalarAsync("SELECT MAX(id) FROM ledger");
        var maxLogIdBeforeRestore = await ScalarAsync("SELECT MAX(id) FROM operation_log");

        Restore();
        (await ScalarAsync("SELECT MAX(id) FROM ledger")).Should().BeLessThan(maxLedgerIdBeforeRestore,
            "前提: リストアでバックアップの後の行は消えている");

        var newLedgerId = await InsertUsageAsync(new DateTime(2025, 1, 12), "天神", "赤坂", 160, 9630);
        await InsertOperationLogAsync();

        newLedgerId.Should().BeGreaterThan((int)maxLedgerIdBeforeRestore,
            "リストア後の履歴がリストア前の ID を再び使うと、リストア前に出力した CSV のその ID の行が別の履歴を指す");
        (await ScalarAsync($"SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = {newLedgerId}")).Should().Be(1,
            "明細は新しい履歴の ID を親として登録される（利用履歴詳細 CSV の ID もリストア前の ID と重ならない）");
        (await ScalarAsync("SELECT MAX(id) FROM operation_log")).Should().BeGreaterThan(maxLogIdBeforeRestore,
            "操作ログの番号も、リストア前に出力した操作ログの番号と重ならない");
    }

    /// <summary>
    /// リストア前に出力した利用履歴 CSV に、バックアップの後に作られた（リストアで消えた）履歴の ID の行があっても、
    /// リストア後に作られた同じカードの別の履歴を書き換えないこと（台帳に無い ID として行エラーになる）。
    /// </summary>
    [Fact]
    public async Task リストア前に出力したCSVのIDがリストア後の別の履歴を書き換えないこと()
    {
        await SeedCardAsync();
        await InsertChargeAsync(new DateTime(2025, 1, 10), 10000, 10000);
        await InsertUsageAsync(new DateTime(2025, 1, 11), "天神", "博多", 210, 9790);
        CreateBackup();

        var removedByRestoreId = await InsertUsageAsync(new DateTime(2025, 1, 12), "博多", "天神", 210, 9580);
        var oldCsv = await WriteLedgerCsvAsync("before_restore.csv",
            CsvRow(removedByRestoreId, new DateTime(2025, 1, 12), "鉄道（博多～天神）", 210, 9580));

        Restore();
        var newLedgerId = await InsertUsageAsync(new DateTime(2025, 1, 12), "天神", "赤坂", 160, 9630);

        var result = await CreateImportService().ImportLedgersAsync(oldCsv);

        var newLedger = await _ledgerRepository.GetByIdAsync(newLedgerId);
        newLedger!.Summary.Should().Be("鉄道（天神～赤坂）",
            "リストア前の CSV の行は、リストア後に作られた別の利用の履歴を書き換えてはならない");
        newLedger.Expense.Should().Be(160);
        newLedger.Balance.Should().Be(9630);
        result.Success.Should().BeFalse("リストアで消えた履歴の ID は台帳に無いので、行エラーになる");
        result.Errors.Should().ContainSingle(e => e.Data == removedByRestoreId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// バックアップの側で一度も INSERT されていない表（<c>sqlite_sequence</c> に行が無い）でも、リストア前の採番まで進むこと。
    /// </summary>
    [Fact]
    public async Task バックアップに採番の行が無い表もリストア前の採番まで進むこと()
    {
        CreateBackup();
        (await ScalarAsync("SELECT COUNT(*) FROM ledger_merge_history")).Should().Be(0, "前提: バックアップには統合履歴が無い");

        await InsertMergeHistoryAsync();
        await InsertMergeHistoryAsync();
        var maxIdBeforeRestore = await ScalarAsync("SELECT MAX(id) FROM ledger_merge_history");

        Restore();
        await InsertMergeHistoryAsync();

        (await ScalarAsync("SELECT MAX(id) FROM ledger_merge_history")).Should().BeGreaterThan(maxIdBeforeRestore);
    }

    #endregion

    #region 正当な動作を塞いでいない側

    /// <summary>
    /// リストア後も、同じ履歴を指す（バックアップの時点から台帳にある履歴の ID の）CSV の行は「更新」として従来どおり取り込めること。
    /// </summary>
    [Fact]
    public async Task リストア後も同じ履歴を指すCSVの行は従来どおり更新として取り込めること()
    {
        await SeedCardAsync();
        await InsertChargeAsync(new DateTime(2025, 1, 10), 10000, 10000);
        var keptId = await InsertUsageAsync(new DateTime(2025, 1, 11), "天神", "博多", 210, 9790);
        CreateBackup();
        await InsertUsageAsync(new DateTime(2025, 1, 12), "博多", "天神", 210, 9580);

        Restore();
        var csv = await WriteLedgerCsvAsync("same_history.csv",
            CsvRow(keptId, new DateTime(2025, 1, 11), "鉄道（天神～博多）", 210, 9790, "出張"));

        var result = await CreateImportService().ImportLedgersAsync(csv);

        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        result.UpdatedCount.Should().Be(1);
        (await _ledgerRepository.GetByIdAsync(keptId))!.Note.Should().Be("出張");
    }

    /// <summary>
    /// バックアップの側の採番がリストア前より大きい（別の PC の新しいバックアップ等）ときは、バックアップの採番を戻さないこと（max を取る）。
    /// </summary>
    [Fact]
    public async Task バックアップの採番のほうが大きいときはバックアップの採番を保つこと()
    {
        await SeedCardAsync();
        await InsertChargeAsync(new DateTime(2025, 1, 10), 10000, 10000);
        for (var day = 11; day <= 15; day++)
        {
            await InsertUsageAsync(new DateTime(2025, 1, day), "天神", "博多", 210, 10000 - (210 * (day - 10)));
        }

        // 末尾の行を消して「採番 > 最大の ID」にする。SQLite は max(seq, 最大の ID) + 1 を採るため、
        // 最大の ID が採番と同じだと、採番を小さい値へ戻す実装でも結果が変わらず検出できない
        await ExecuteAsync("DELETE FROM ledger_detail WHERE ledger_id > 3; DELETE FROM ledger WHERE id > 3");
        var backupSeq = await ScalarAsync("SELECT seq FROM sqlite_sequence WHERE name = 'ledger'");
        (await ScalarAsync("SELECT MAX(id) FROM ledger")).Should().BeLessThan(backupSeq, "前提: バックアップの採番は最大の ID より大きい");
        CreateBackup();

        // リストア前の DB は採番の小さい状態にする（行を消し、採番も小さくする）
        await ExecuteAsync("DELETE FROM ledger_detail; DELETE FROM ledger; UPDATE sqlite_sequence SET seq = 1 WHERE name = 'ledger'");

        Restore();
        var newLedgerId = await InsertChargeAsync(new DateTime(2025, 1, 20), 1000, 9950);

        newLedgerId.Should().Be((int)backupSeq + 1, "リストアした DB の採番より小さい値へは戻さない");
    }

    /// <summary>
    /// リストア前の DB が壊れていて採番を読めなくても、リストアは止めずに完了すること（理由は Warning ログに残る）。
    /// </summary>
    [Fact]
    public async Task リストア前のDBを読めなくてもリストアは完了すること()
    {
        await SeedCardAsync();
        await InsertChargeAsync(new DateTime(2025, 1, 10), 10000, 10000);
        CreateBackup();

        var brokenPath = Path.Combine(_testDirectory, "broken", "iccard.db");
        Directory.CreateDirectory(Path.GetDirectoryName(brokenPath)!);
        File.WriteAllBytes(brokenPath, Encoding.ASCII.GetBytes(new string('x', 4096)));
        var logger = new RecordingLogger<BackupService>();
        using (var brokenContext = new DbContext(brokenPath))
        {
            var service = new BackupService(brokenContext, Mock.Of<ISettingsRepository>(), logger);

            service.RestoreFromBackup(_backupPath).Should().BeTrue(logger.FormatEntries());
        }

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("採番"),
            "採番を進められなかったことは調査のために残す");
        using var restored = new DbContext(brokenPath);
        using var lease = await restored.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1, "バックアップの内容に置き換わっている");
    }

    #endregion
}
