using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2262: CSV インポートが既存データを書き換えた行を、変更前・変更後とともに
/// 書き込みと同じトランザクションで操作ログへ記録すること
/// </summary>
/// <remarks>
/// <para>
/// 以前の CSV インポートは、利用履歴の更新・職員の登録・変更・復元・利用履歴詳細の置き換えを行っても
/// 1 行ごとの操作ログを残さず、取り込み全体についてファイル名と件数だけを記録していた（変更前の値が残らない）。
/// </para>
/// <para>
/// 「巻き戻った」ことはモックの呼び出しでは観測できないため、実 DB（インメモリ SQLite）と実リポジトリを使い、
/// 操作ログの書き込み先だけを差し替えて失敗を注入する。操作者は実物の <see cref="CurrentOperatorContext"/> の
/// セッションから解決される（取り込みを認証した職員のスコープの内側で呼ばれる形を再現する）。
/// </para>
/// </remarks>
public class CsvImportServiceAuditLogTests : IDisposable
{
    private const string TestCardIdm = "0123456789ABCDEF";
    private const string OperatorIdm = "FEDCBA9876543210";
    private const string OperatorName = "監査 担当";

    private const string LedgerHeader = "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考,同行者数";
    private const string DetailHeader =
        "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";
    private const string StaffHeader = "職員IDm,氏名,職員番号,備考";
    private const string CardHeader = "カードIDm,カード種別,管理番号,備考";

    /// <summary>UTF-8 with BOM（<c>ReadCsvFileAsync</c> の文字コード判別を通すため）。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly DbContext _dbContext;
    private readonly CacheService _cacheService;
    private readonly LedgerRepository _ledgerRepository;
    private readonly StaffRepository _staffRepository;
    private readonly CardRepository _cardRepository;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly CurrentOperatorContext _operatorContext;

    /// <summary>true にすると、1 行ごとの操作ログ（トランザクションを受け取る書き込み）が失敗する</summary>
    private bool _failRowAuditLog;

    public CsvImportServiceAuditLogTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportAuditLog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _dbContext = TestDbContextFactory.Create();
        _cacheService = new CacheService(NullLogger<CacheService>.Instance);
        _ledgerRepository = new LedgerRepository(_dbContext);
        _staffRepository = new StaffRepository(
            _dbContext, _cacheService, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
        _cardRepository = new CardRepository(
            _dbContext, _cacheService, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _operationLogRepository = new OperationLogRepository(_dbContext);
        _operatorContext = new CurrentOperatorContext(new SystemClock());
    }

    public void Dispose()
    {
        _cacheService.Dispose();
        _dbContext.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region ヘルパー

    private CsvImportService CreateService()
    {
        // 1 行ごとの操作ログ（tx を受け取る書き込み）だけを差し替え可能にし、それ以外は実リポジトリへ委譲する
        var operationLogRepositoryMock = new Mock<IOperationLogRepository>();
        operationLogRepositoryMock
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, transaction) => _failRowAuditLog
                ? throw new SQLiteException(SQLiteErrorCode.IoErr, "disk I/O error")
                : _operationLogRepository.InsertAsync(log, transaction));
        operationLogRepositoryMock
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Returns<OperationLog>(log => _operationLogRepository.InsertAsync(log));

        var validationServiceMock = new Mock<IValidationService>();
        validationServiceMock.Setup(v => v.ValidateCardIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        validationServiceMock.Setup(v => v.ValidateStaffIdm(It.IsAny<string>())).Returns(ValidationResult.Success());

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        return new CsvImportService(
            _cardRepository,
            _staffRepository,
            _ledgerRepository,
            validationServiceMock.Object,
            _dbContext,
            _cacheService,
            settingsRepositoryMock.Object,
            new OperationLogger(operationLogRepositoryMock.Object, _operatorContext),
            NullLogger<CsvImportService>.Instance);
    }

    private string WriteCsv(string header, params string[] lines)
    {
        var path = Path.Combine(_testDirectory, $"import_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, header + "\n" + string.Join("\n", lines) + "\n", CsvEncoding);
        return path;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private Task SeedCardAsync() =>
        ExecuteAsync(
            "INSERT INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')",
            ("@idm", TestCardIdm));

    /// <summary>取り込み前からある利用履歴（チャージ 1 行）を投入し、その id を返す</summary>
    private async Task<int> SeedLedgerAsync()
    {
        await SeedCardAsync();
        return await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2025, 1, 10),
            Summary = "役務費によりチャージ",
            Income = 10000,
            Expense = 0,
            Balance = 10000,
            Note = "変更前の備考"
        });
    }

    /// <summary>投入した利用履歴の備考だけを変える CSV 行</summary>
    private static string LedgerLineWithNote(int ledgerId, string note) =>
        $"{ledgerId},2025-01-10 00:00:00,{TestCardIdm},001,役務費によりチャージ,10000,,10000,,{note},0";

    private Task SeedStaffAsync(string idm, string name, string? number, bool deleted) =>
        ExecuteAsync(
            "INSERT INTO staff (staff_idm, name, number, is_deleted, deleted_at) VALUES (@idm, @name, @number, @deleted, @deletedAt)",
            ("@idm", idm),
            ("@name", name),
            ("@number", number),
            ("@deleted", deleted ? 1 : 0),
            ("@deletedAt", deleted ? "2025-01-01 00:00:00" : null));

    /// <summary>記録された操作ログ（1 行ごとの記録。取り込み全体の IMPORT は含まない）</summary>
    private async Task<List<OperationLog>> GetRowLogsAsync(string targetTable)
    {
        var logs = new List<OperationLog>();
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = @"SELECT operator_idm, operator_name, target_table, target_id, action, before_data, after_data
FROM operation_log WHERE target_table = @table AND action <> 'IMPORT' ORDER BY id";
        command.Parameters.AddWithValue("@table", targetTable);
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(new OperationLog
            {
                OperatorIdm = reader.IsDBNull(0) ? null : reader.GetString(0),
                OperatorName = reader.IsDBNull(1) ? null : reader.GetString(1),
                TargetTable = reader.GetString(2),
                TargetId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Action = reader.GetString(4),
                BeforeData = reader.IsDBNull(5) ? null : reader.GetString(5),
                AfterData = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }

        return logs;
    }

    private static string? ReadJsonString(string? json, string property)
    {
        json.Should().NotBeNull();
        using var document = JsonDocument.Parse(json!);
        var element = document.RootElement.GetProperty(property);
        return element.ValueKind == JsonValueKind.Null ? null : element.ToString();
    }

    private static int CountJsonArray(string? json, string property)
    {
        json.Should().NotBeNull();
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.GetProperty(property).GetArrayLength();
    }

    #endregion

    #region 利用履歴の更新

    /// <summary>
    /// 欠陥を突く側: 既存の利用履歴を書き換えたら、変更前・変更後と操作者（認証した職員）を記録すること
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_既存の行を更新したら変更前と変更後を認証した職員で記録すること()
    {
        var ledgerId = await SeedLedgerAsync();
        var csv = WriteCsv(LedgerHeader, LedgerLineWithNote(ledgerId, "変更後の備考"));
        var service = CreateService();

        CsvImportResult result;
        using (_operatorContext.BeginSession(OperatorIdm, OperatorName))
        {
            result = await service.ImportLedgersAsync(csv);
        }

        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(1, "既存の行の更新は更新件数として返す（操作ログで追加と区別するため）");

        var log = (await GetRowLogsAsync(OperationLogger.Tables.Ledger)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.TargetId.Should().Be(ledgerId.ToString(CultureInfo.InvariantCulture));
        log.OperatorIdm.Should().Be(OperatorIdm, "取り込みを認証した職員の操作として記録する");
        log.OperatorName.Should().Be(OperatorName);
        ReadJsonString(log.BeforeData, "Note").Should().Be("変更前の備考");
        ReadJsonString(log.AfterData, "Note").Should().Be("変更後の備考");
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 新しい行を追加するだけの取り込みは、行ごとの記録を作らない
    /// （取り込み全体の記録の件数で残す。移行時の大量の追加で操作ログを膨らませない）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_追加だけの取り込みは行ごとの記録を作らず更新件数も0であること()
    {
        await SeedCardAsync();
        var csv = WriteCsv(
            "日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考",
            $"2025-01-10 00:00:00,{TestCardIdm},001,役務費によりチャージ,10000,,10000,,");
        var service = CreateService();

        var result = await service.ImportLedgersAsync(csv);

        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(0);
        (await GetRowLogsAsync(OperationLogger.Tables.Ledger)).Should().BeEmpty();
    }

    /// <summary>
    /// 1 行ごとの記録が書き込めなければ、更新も巻き戻ること（記録の無い書き換えを残さない）
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_操作ログを書き込めなければ更新も巻き戻ること()
    {
        var ledgerId = await SeedLedgerAsync();
        var csv = WriteCsv(LedgerHeader, LedgerLineWithNote(ledgerId, "変更後の備考"));
        var service = CreateService();
        _failRowAuditLog = true;

        var result = await service.ImportLedgersAsync(csv);

        result.Success.Should().BeFalse();
        (await _ledgerRepository.GetByIdAsync(ledgerId))!.Note.Should().Be("変更前の備考",
            "操作ログを残せない書き換えは確定させない");
    }

    /// <summary>
    /// 利用履歴の更新は明細に触れない。変更後の記録にも明細を載せ、「明細 N 件 → なし」という
    /// 起きていない変更を記録しないこと
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_更新の記録は変更後にも変更前と同じ明細を載せること()
    {
        var ledgerId = await SeedLedgerAsync();
        (await _ledgerRepository.InsertDetailsAsync(ledgerId, new[]
        {
            new LedgerDetail { LedgerId = ledgerId, UseDate = new DateTime(2025, 1, 10, 9, 0, 0), Amount = 10000, Balance = 10000, IsCharge = true }
        })).Should().BeTrue("前提: 明細を投入できていること");
        var csv = WriteCsv(LedgerHeader, LedgerLineWithNote(ledgerId, "変更後の備考"));
        var service = CreateService();

        var result = await service.ImportLedgersAsync(csv);

        result.Success.Should().BeTrue();
        var log = (await GetRowLogsAsync(OperationLogger.Tables.Ledger)).Should().ContainSingle().Subject;
        CountJsonArray(log.BeforeData, "Details").Should().Be(1);
        CountJsonArray(log.AfterData, "Details").Should().Be(1, "この更新は明細を書き換えていない");
    }

    #endregion

    #region 職員の登録・変更・復元

    /// <summary>
    /// 欠陥を突く側: 削除済みの職員を復元したら、復元と（氏名が変わったなら）変更を記録すること。
    /// 変更の記録の変更前は削除済みの状態で、削除前の氏名が残る
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_削除済みの職員を復元したら復元と変更前後を記録すること()
    {
        await SeedStaffAsync("1111222233334444", "旧氏名", "001", deleted: true);
        var csv = WriteCsv(StaffHeader, "1111222233334444,新氏名,001,");
        var service = CreateService();

        CsvImportResult result;
        using (_operatorContext.BeginSession(OperatorIdm, OperatorName))
        {
            result = await service.ImportStaffAsync(csv);
        }

        result.Success.Should().BeTrue();
        result.UpdatedCount.Should().Be(1, "復元は既存データの書き換えとして数える");
        var restored = await _staffRepository.GetByIdmAsync("1111222233334444");
        restored.Should().NotBeNull();
        restored!.Name.Should().Be("新氏名");

        var logs = await GetRowLogsAsync(OperationLogger.Tables.Staff);
        logs.Select(l => l.Action).Should().Equal(OperationLogger.Actions.Restore, OperationLogger.Actions.Update);
        logs.Should().OnlyContain(l => l.OperatorIdm == OperatorIdm && l.OperatorName == OperatorName);
        var update = logs[1];
        ReadJsonString(update.BeforeData, "Name").Should().Be("旧氏名", "削除前は誰として登録されていた職員証かを残す");
        ReadJsonString(update.BeforeData, "IsDeleted").Should().Be("True");
        ReadJsonString(update.AfterData, "Name").Should().Be("新氏名");
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 氏名等が削除前と同じ復元は、復元だけを記録する（起きていない変更を記録しない）
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_内容が同じ復元は復元だけを記録すること()
    {
        await SeedStaffAsync("1111222233334444", "同じ氏名", "001", deleted: true);
        var csv = WriteCsv(StaffHeader, "1111222233334444,同じ氏名,001,");
        var service = CreateService();

        var result = await service.ImportStaffAsync(csv);

        result.Success.Should().BeTrue();
        (await GetRowLogsAsync(OperationLogger.Tables.Staff)).Select(l => l.Action)
            .Should().Equal(OperationLogger.Actions.Restore);
    }

    /// <summary>
    /// 登録済みの職員の変更は変更前・変更後を、新しい職員は登録を記録すること
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_変更は変更前後を_登録は登録を記録すること()
    {
        await SeedStaffAsync("1111222233334444", "変更前", "001", deleted: false);
        var csv = WriteCsv(StaffHeader, "1111222233334444,変更後,001,", "5555666677778888,新しい職員,002,");
        var service = CreateService();

        var result = await service.ImportStaffAsync(csv);

        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(2);
        result.UpdatedCount.Should().Be(1);
        var logs = await GetRowLogsAsync(OperationLogger.Tables.Staff);
        var update = logs.Should().ContainSingle(l => l.Action == OperationLogger.Actions.Update).Subject;
        update.TargetId.Should().Be("1111222233334444");
        ReadJsonString(update.BeforeData, "Name").Should().Be("変更前");
        ReadJsonString(update.AfterData, "Name").Should().Be("変更後");
        var insert = logs.Should().ContainSingle(l => l.Action == OperationLogger.Actions.Insert).Subject;
        insert.TargetId.Should().Be("5555666677778888");
        insert.BeforeData.Should().BeNull();
    }

    /// <summary>
    /// 1 行ごとの記録が書き込めなければ、復元・登録を含む取り込み全体が巻き戻ること
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_操作ログを書き込めなければ復元も登録も巻き戻ること()
    {
        await SeedStaffAsync("1111222233334444", "旧氏名", "001", deleted: true);
        var csv = WriteCsv(StaffHeader, "1111222233334444,新氏名,001,", "5555666677778888,新しい職員,002,");
        var service = CreateService();
        _failRowAuditLog = true;

        var result = await service.ImportStaffAsync(csv);

        result.Success.Should().BeFalse();
        (await _staffRepository.GetByIdmAsync("1111222233334444")).Should().BeNull("復元は確定させない");
        (await _staffRepository.GetByIdmAsync("5555666677778888", includeDeleted: true)).Should().BeNull("登録も確定させない");
    }

    #endregion

    #region 利用履歴詳細の置き換え

    /// <summary>
    /// 欠陥を突く側: 明細を置き換えたら、明細・摘要・金額の変更前と変更後を記録すること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_明細を置き換えたら変更前と変更後を記録すること()
    {
        var ledgerId = await SeedLedgerWithRailDetailAsync();
        var csv = WriteCsv(DetailHeader, ChangedDetailLine(ledgerId));
        var service = CreateService();

        CsvImportResult result;
        using (_operatorContext.BeginSession(OperatorIdm, OperatorName))
        {
            result = await service.ImportLedgerDetailsAsync(csv);
        }

        result.Success.Should().BeTrue();
        result.UpdatedCount.Should().Be(1, "置き換えた明細の行数を更新件数として返す");
        var log = (await GetRowLogsAsync(OperationLogger.Tables.Ledger)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.TargetId.Should().Be(ledgerId.ToString(CultureInfo.InvariantCulture));
        log.OperatorIdm.Should().Be(OperatorIdm);
        ReadJsonString(log.BeforeData, "Expense").Should().Be("260");
        ReadJsonString(log.AfterData, "Expense").Should().Be("300");
        log.BeforeData.Should().Contain("天神").And.NotContain("中洲川端", "変更前は置き換える前の明細");
        log.AfterData.Should().Contain("中洲川端", "変更後は取り込んだ明細");
    }

    /// <summary>
    /// 1 行ごとの記録が書き込めなければ、その利用履歴の明細の置き換えも巻き戻ること
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_操作ログを書き込めなければ置き換えも巻き戻ること()
    {
        var ledgerId = await SeedLedgerWithRailDetailAsync();
        var csv = WriteCsv(DetailHeader, ChangedDetailLine(ledgerId));
        var service = CreateService();
        _failRowAuditLog = true;

        var result = await service.ImportLedgerDetailsAsync(csv);

        result.Success.Should().BeFalse();
        result.UpdatedCount.Should().Be(0);
        var ledger = await _ledgerRepository.GetByIdAsync(ledgerId);
        ledger!.Expense.Should().Be(260);
        ledger.Details.Should().ContainSingle().Which.ExitStation.Should().Be("天神");
    }

    private async Task<int> SeedLedgerWithRailDetailAsync()
    {
        await SeedCardAsync();
        var useDate = new DateTime(2024, 1, 15, 10, 30, 0);
        var ledgerId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = useDate.Date,
            Summary = "鉄道（博多～天神）",
            Expense = 260,
            Balance = 9740
        });
        (await _ledgerRepository.InsertDetailsAsync(ledgerId, new[]
        {
            new LedgerDetail
            {
                LedgerId = ledgerId, UseDate = useDate, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9740
            }
        })).Should().BeTrue("前提: 既存の明細を投入できていること");
        return ledgerId;
    }

    /// <summary>既存の明細を「博多→中洲川端 300 円、残額 9,700 円」へ変える CSV 行</summary>
    private static string ChangedDetailLine(int ledgerId) =>
        $"{ledgerId},2024-01-15 10:30:00,{TestCardIdm},001,博多,中洲川端,,300,9700,0,0,0,";

    #endregion

    #region 直接インポート（新しいカードの登録だけ）

    /// <summary>
    /// 欠陥を突く側: 直接インポートは、登録済みのカードを変更する行・削除済みのカードを復元する行があれば何も取り込まないこと
    /// </summary>
    [Fact]
    public async Task ImportNewCardsAsync_登録済みのカードを変更復元する行があれば何も取り込まないこと()
    {
        await SeedCardAsync();
        await ExecuteAsync(
            "INSERT INTO ic_card (card_idm, card_type, card_number, is_deleted, deleted_at) VALUES ('1111111111111111', 'nimoca', '009', 1, '2025-01-01 00:00:00')");
        var csv = WriteCsv(
            CardHeader,
            $"{TestCardIdm},はやかけん,001,備考を変更",
            "1111111111111111,nimoca,009,",
            "2222222222222222,SUGOCA,010,");
        var service = CreateService();

        var result = await service.ImportNewCardsAsync(csv);

        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        result.Errors.Select(e => e.LineNumber).Should().BeEquivalentTo(new[] { 2, 3 });
        result.Errors.Should().OnlyContain(e => e.Message.Contains("「プレビュー」"), "プレビューを経た取り込みへ案内する");
        result.Errors.Should().OnlyContain(e => !e.Message.Contains(TestCardIdm) && !e.Message.Contains("1111111111111111"),
            "IDm は画面に出さない（#1986）");
        (await _cardRepository.GetByIdmAsync("2222222222222222")).Should().BeNull("他の行も取り込まない");
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.Note.Should().BeNullOrEmpty();
        (await _cardRepository.GetByIdmAsync("1111111111111111", includeDeleted: true))!.IsDeleted.Should().BeTrue();
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 新しいカードは登録し、内容が同じ登録済みのカードはスキップすること
    /// </summary>
    [Fact]
    public async Task ImportNewCardsAsync_新しいカードは登録し内容が同じ登録済みのカードはスキップすること()
    {
        await SeedCardAsync();
        var csv = WriteCsv(
            CardHeader,
            $"{TestCardIdm},はやかけん,001,",
            "2222222222222222,SUGOCA,010,");
        var service = CreateService();

        var result = await service.ImportNewCardsAsync(csv);

        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        (await _cardRepository.GetByIdmAsync("2222222222222222")).Should().NotBeNull();
    }

    /// <summary>
    /// プレビュー経由の取り込み（<see cref="CsvImportService.ImportCardsAsync"/>）は従来どおり登録済みのカードを更新し、
    /// 更新件数を返すこと（直接インポートの制限をプレビュー経由へ持ち込まない）
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_プレビュー経由は登録済みのカードを更新し更新件数を返すこと()
    {
        await SeedCardAsync();
        var csv = WriteCsv(CardHeader, $"{TestCardIdm},はやかけん,001,備考を変更");
        var service = CreateService();

        var result = await service.ImportCardsAsync(csv);

        result.Success.Should().BeTrue();
        result.UpdatedCount.Should().Be(1);
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.Note.Should().Be("備考を変更");
    }

    #endregion
}
