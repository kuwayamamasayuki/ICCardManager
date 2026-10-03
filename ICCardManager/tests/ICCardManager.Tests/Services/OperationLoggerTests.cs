using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;


namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLoggerの単体テスト
/// Issue #1265: 操作者情報は ICurrentOperatorContext から一元的に解決される。
/// 操作者を引数で受け取るオーバーロードは持たない（監査ログなりすまし防止。Issue #2164）。
/// </summary>
public class OperationLoggerTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly Mock<ISystemClock> _clockMock;
    private readonly CurrentOperatorContext _operatorContext;
    private readonly OperationLogger _logger;
    private DateTime _now;

    public OperationLoggerTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _operationLogRepository = new OperationLogRepository(_dbContext);

        _now = new DateTime(2026, 4, 17, 10, 0, 0);
        _clockMock = new Mock<ISystemClock>();
        _clockMock.Setup(c => c.Now).Returns(() => _now);
        _operatorContext = new CurrentOperatorContext(_clockMock.Object);

        _logger = new OperationLogger(_operationLogRepository, _operatorContext);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GuiOperator 定数テスト

    /// <summary>GUI操作用識別子の値が正しいことを確認</summary>
    [Fact]
    public void GuiOperator_HasCorrectValues()
    {
        OperationLogger.GuiOperator.Idm.Should().Be("0000000000000000");
        OperationLogger.GuiOperator.Idm.Should().HaveLength(16);
        OperationLogger.GuiOperator.Name.Should().Be("GUI操作");
    }

    #endregion

    #region context なし → GuiOperator フォールバック

    [Fact]
    public async Task LogLedgerUpdateAsync_WithoutContext_UsesGuiIdentifier()
    {
        // Arrange: context 未設定
        var beforeLedger = CreateTestLedger(summary: "変更前");
        var afterLedger = CreateTestLedger(summary: "変更後");

        // Act: 操作者を引数で渡さない（context から解決する）
        await _logger.LogLedgerUpdateAsync(beforeLedger, afterLedger);

        // Assert
        var logs = await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        log.Action.Should().Be(OperationLogger.Actions.Update);
    }

    [Fact]
    public async Task LogStaffInsertAsync_WithoutContext_UsesGuiIdentifier()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffInsertAsync(staff);

        var logs = await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.Action.Should().Be(OperationLogger.Actions.Insert);
    }

    #endregion

    #region context あり → context 値を使用

    [Fact]
    public async Task LogLedgerDeleteAsync_WithContext_RecordsContextOperator()
    {
        // Arrange: 認証済み operator を context に設定
        const string authIdm = "AAAA000000000001";
        const string authName = "認証済み職員";
        _operatorContext.BeginSession(authIdm, authName);

        var ledger = CreateTestLedger(id: 99, summary: "削除対象");

        // Act
        await _logger.LogLedgerDeleteAsync(ledger);

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "99");
        var log = logs.Single();
        log.OperatorIdm.Should().Be(authIdm);
        log.OperatorName.Should().Be(authName);
        log.Action.Should().Be(OperationLogger.Actions.Delete);
    }

    [Fact]
    public async Task LogStaffInsertAsync_WithContext_RecordsContextOperator()
    {
        _operatorContext.BeginSession("BBBB000000000002", "山田 花子");

        var staff = CreateTestStaff();
        await _logger.LogStaffInsertAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.OperatorIdm.Should().Be("BBBB000000000002");
        log.OperatorName.Should().Be("山田 花子");
    }

    #endregion

    #region Issue #1265: 監査ログなりすまし防止

    /// <summary>
    /// 操作者を引数で受け取る公開メソッドが無いこと（Issue #2164）。
    /// 旧シグネチャ（先頭に operatorIdm）は渡された値を無視する互換用として残っていたが、
    /// 削除したことで「引数で他人の IDm を渡す」経路そのものが無くなった。
    /// 同じ形のオーバーロードが再び足されたら、この表明が止める。
    /// </summary>
    [Fact]
    public void PublicLogMethods_DoNotAcceptOperatorIdentityParameter()
    {
        var logMethods = typeof(OperationLogger)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("Log", StringComparison.Ordinal))
            .ToList();

        // 対の表明: 走査対象が空に縮んでいないこと（空なら下の表明は無条件に緑になる）
        logMethods.Select(m => m.Name).Should().Contain(
            new[] { "LogStaffInsertAsync", "LogCardUpdateAsync", "LogLedgerDeleteAsync", "LogLedgerSplitAsync" });

        // 許可形で判定する（禁止する名前の列挙は、staffIdm / idm / actorIdm のような別名で素通りする）。
        // 操作者の識別子は文字列でしか渡せないので、string 型の引数は既知の用途（対象テーブル名・ファイルパス）に限る
        var allowedStringParameters = new[] { "tableName", "filePath" };
        var offending = logMethods
            .SelectMany(m => m.GetParameters()
                .Where(p => p.ParameterType == typeof(string) && !allowedStringParameters.Contains(p.Name))
                .Select(p => $"{m.Name}({p.Name})"))
            .ToList();
        offending.Should().BeEmpty(
            "操作者は ICurrentOperatorContext からのみ解決し、引数では受け取らない（string 型の引数は対象テーブル名・ファイルパスだけ）");
    }

    /// <summary>
    /// 監査ログを記録する上位の層（Service・ViewModel・View）にも、操作者の識別子を受け取る引数が無いこと（Issue #2164）。
    /// </summary>
    /// <remarks>
    /// OperationLogger から操作者の引数を消しても、1 つ上の層（統合・分割・行編集）に「受け取って捨てる」引数が残っていた。
    /// 読んだ人は「渡した IDm が記録される」と受け取り、記録を正しくしようとこの引数を使い始めると、#1265 が塞いだ
    /// 「引数経由のなりすまし」の形が復活する。リポジトリの検索条件（<c>GetByOperatorAsync</c>）は Data 層なので対象外。
    /// 判定は名前の前方一致（<c>operator…</c>）に留まり、<c>authIdm</c> のような別名には効かない。上位の層には <c>cardIdm</c> /
    /// <c>staffIdm</c>（貸出者などの業務データ）という正当な引数があるため、<c>OperationLogger</c> のような許可形にはできない。
    /// 識別子は文字列でしか渡せないので string 型の引数を見る（<c>ICurrentOperatorContext</c> の注入は操作者の正しい情報源なので対象外）。
    /// </remarks>
    [Fact]
    public void UpperLayers_DoNotAcceptOperatorIdentityParameter()
    {
        var assembly = typeof(OperationLogger).Assembly;
        var upperLayerNamespaces = new[] { "ICCardManager.Services", "ICCardManager.ViewModels", "ICCardManager.Views" };
        var types = assembly.GetTypes()
            .Where(t => t.Namespace != null && upperLayerNamespaces.Any(ns => t.Namespace == ns || t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)))
            .ToList();

        // 対の表明: 走査対象に、かつて引数が残っていた型が含まれていること（空振り防止）
        types.Select(t => t.Name).Should().Contain(new[] { "LedgerMergeService", "LedgerSplitService", "LedgerRowEditViewModel" });

        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offending = types
            .SelectMany(t => t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All))
                .SelectMany(m => m.GetParameters()
                    .Where(p => p.ParameterType == typeof(string) && p.Name != null && p.Name.StartsWith("operator", StringComparison.OrdinalIgnoreCase))
                    .Select(p => $"{t.FullName}.{m.Name}({p.Name})")))
            .ToList();
        offending.Should().BeEmpty("操作者は ICurrentOperatorContext からのみ解決し、上位の層も引数では受け取らない");
    }

    /// <summary>
    /// セッション失効後は、失効前の操作者ではなく GUI 操作として記録される。
    /// </summary>
    [Fact]
    public async Task AfterContextExpiration_UsesGuiIdentifier()
    {
        // Arrange: 短い有効期間の context
        var shortLived = new CurrentOperatorContext(_clockMock.Object, TimeSpan.FromSeconds(10));
        shortLived.BeginSession("CCCC000000000003", "期限切れ予定職員");
        var logger = new OperationLogger(_operationLogRepository, shortLived);

        // 11 秒経過 → セッション失効
        _now = _now.AddSeconds(11);

        var ledger = CreateTestLedger(id: 52, summary: "失効後ターゲット");

        // Act
        await logger.LogLedgerDeleteAsync(ledger);

        // Assert: GUI 操作としてフォールバック
        var logs = await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "52");
        var log = logs.Single();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    #endregion

    #region 各テーブルのログ記録

    [Fact]
    public async Task LogStaffUpdateAsync_RecordsBeforeAndAfter()
    {
        var before = CreateTestStaff(name: "旧氏名");
        var after = CreateTestStaff(name: "新氏名");

        await _logger.LogStaffUpdateAsync(before, after);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, after.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("旧氏名");
        log.AfterData.Should().Contain("新氏名");
    }

    [Fact]
    public async Task LogStaffDeleteAsync_RecordsBeforeOnly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffDeleteAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.BeforeData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().BeNull();
    }

    [Fact]
    public async Task LogStaffRestoreAsync_RecordsAfterOnly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffRestoreAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogCardInsertAsync_RecordsCorrectly()
    {
        var card = CreateTestCard();

        await _logger.LogCardInsertAsync(card);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, card.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogCardUpdateAsync_RecordsBeforeAndAfter()
    {
        var before = CreateTestCard(cardNumber: "OLD-001");
        var after = CreateTestCard(cardNumber: "NEW-001");

        await _logger.LogCardUpdateAsync(before, after);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, after.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("OLD-001");
        log.AfterData.Should().Contain("NEW-001");
    }

    [Fact]
    public async Task LogCardDeleteAsync_RecordsBeforeOnly()
    {
        var card = CreateTestCard();

        await _logger.LogCardDeleteAsync(card);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, card.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.BeforeData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().BeNull();
    }

    [Fact]
    public async Task LogCardRestoreAsync_RecordsAfterOnly()
    {
        var card = CreateTestCard();

        await _logger.LogCardRestoreAsync(card);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, card.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogLedgerInsertAsync_RecordsCorrectly()
    {
        var ledger = CreateTestLedger(id: 42, summary: "新規行");

        await _logger.LogLedgerInsertAsync(ledger);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "42")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().Contain("新規行");
    }

    [Fact]
    public async Task LogLedgerDeleteAsync_WithoutContext_RecordsBeforeOnlyAsGui()
    {
        var ledger = CreateTestLedger(id: 77, summary: "GUI削除対象");

        await _logger.LogLedgerDeleteAsync(ledger);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "77")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.BeforeData.Should().Contain("GUI削除対象");
        log.AfterData.Should().BeNull();
    }

    [Fact]
    public async Task LogLedgerMergeAsync_RecordsSourcesAndMerged()
    {
        var src1 = CreateTestLedger(id: 1, summary: "元1");
        var src2 = CreateTestLedger(id: 2, summary: "元2");
        var merged = CreateTestLedger(id: 3, summary: "統合後");

        await _logger.LogLedgerMergeAsync(new List<Ledger> { src1, src2 }, merged);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "3")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Merge);
        log.BeforeData.Should().Contain("元1").And.Contain("元2");
        log.AfterData.Should().Contain("統合後");
    }

    [Fact]
    public async Task LogLedgerSplitAsync_RecordsOriginalAndSplits()
    {
        var original = CreateTestLedger(id: 10, summary: "元");
        var split1 = CreateTestLedger(id: 11, summary: "分割1");
        var split2 = CreateTestLedger(id: 12, summary: "分割2");

        await _logger.LogLedgerSplitAsync(original, new List<Ledger> { split1, split2 });

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "10")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Split);
        log.BeforeData.Should().Contain("元");
        log.AfterData.Should().Contain("分割1").And.Contain("分割2");
    }

    #endregion

    #region JSONシリアライズ

    /// <summary>日本語・特殊文字を含むデータが読みやすく記録される</summary>
    [Fact]
    public async Task LogStaffInsertAsync_PreservesJapaneseAndSpecialChars()
    {
        var staff = CreateTestStaff(name: "山田 \"太郎\" & 花子");

        await _logger.LogStaffInsertAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.AfterData.Should().Contain("山田");
        log.AfterData.Should().Contain("花子");
    }

    #endregion

    #region Helper Methods

    private static Staff CreateTestStaff(
        string idm = "1234000000000001",
        string name = "テスト職員")
    {
        return new Staff
        {
            StaffIdm = idm,
            Name = name,
        };
    }

    private static IcCard CreateTestCard(
        string cardIdm = "07FE112233445566",
        string cardNumber = "TEST-001")
    {
        return new IcCard
        {
            CardIdm = cardIdm,
            CardNumber = cardNumber,
        };
    }

    private static Ledger CreateTestLedger(
        int id = 1,
        string cardIdm = "07FE112233445566",
        string summary = "テスト摘要",
        int income = 0,
        int expense = 200,
        int balance = 4800)
    {
        return new Ledger
        {
            Id = id,
            CardIdm = cardIdm,
            Date = DateTime.Now.Date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = "テスト職員",
            Note = "テストデータ"
        };
    }

    #endregion

    #region Issue #1302: Import / Export / Backup / Restore のテスト

    [Fact]
    public async Task LogImportAsync_WithSession_RecordsImportAction()
    {
        // Arrange
        _operatorContext.BeginSession("1234567890ABCDEF", "田中 太郎");

        // Act
        await _logger.LogImportAsync(
            OperationLogger.Tables.Staff,
            @"C:\import\staff_20260419.csv",
            insertedCount: 10,
            skippedCount: 2,
            errorCount: 0);

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Staff, "staff_20260419.csv");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Import);
        log.TargetTable.Should().Be(OperationLogger.Tables.Staff);
        log.TargetId.Should().Be("staff_20260419.csv");
        log.OperatorIdm.Should().Be("1234567890ABCDEF");
        log.OperatorName.Should().Be("田中 太郎");
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().Contain("\"InsertedCount\":10");
        log.AfterData.Should().Contain("\"SkippedCount\":2");
        log.AfterData.Should().Contain("\"ErrorCount\":0");
        log.AfterData.Should().Contain("staff_20260419.csv");
    }

    [Fact]
    public async Task LogImportAsync_WithoutSession_FallsBackToGuiOperator()
    {
        // Arrange: セッション未開始

        // Act
        await _logger.LogImportAsync(
            OperationLogger.Tables.Ledger,
            @"D:\data\ledger.csv",
            insertedCount: 100,
            skippedCount: 0,
            errorCount: 0);

        // Assert
        var logs = await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm);
        logs.Should().HaveCount(1);
        logs.First().OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        logs.First().OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        logs.First().TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        logs.First().Action.Should().Be(OperationLogger.Actions.Import);
    }

    [Fact]
    public async Task LogExportAsync_WithSession_RecordsExportAction()
    {
        // Arrange
        _operatorContext.BeginSession("AABBCCDDEEFF0011", "山田 花子");

        // Act
        await _logger.LogExportAsync(
            OperationLogger.Tables.Ledger,
            @"C:\export\ledgers_20260419_20260419.csv",
            recordCount: 523);

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Ledger, "ledgers_20260419_20260419.csv");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Export);
        log.TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        log.TargetId.Should().Be("ledgers_20260419_20260419.csv");
        log.OperatorIdm.Should().Be("AABBCCDDEEFF0011");
        log.OperatorName.Should().Be("山田 花子");
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().Contain("\"RecordCount\":523");
    }

    [Fact]
    public async Task LogExportAsync_LedgerDetailTable_RecordsCorrectly()
    {
        // Act
        await _logger.LogExportAsync(
            OperationLogger.Tables.LedgerDetail,
            @"C:\export\ledger_details.csv",
            recordCount: 1500);

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.LedgerDetail, "ledger_details.csv");
        logs.Should().HaveCount(1);
        logs.First().TargetTable.Should().Be("ledger_detail");
        logs.First().AfterData.Should().Contain("\"RecordCount\":1500");
    }

    [Fact]
    public async Task LogBackupAsync_RecordsBackupActionWithDatabaseTarget()
    {
        // Arrange
        _operatorContext.BeginSession("FFEEDDCCBBAA9988", "管理者");

        // Act
        await _logger.LogBackupAsync(@"C:\backup\iccard_20260419_100000.db");

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Database, "iccard_20260419_100000.db");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Backup);
        log.TargetTable.Should().Be(OperationLogger.Tables.Database);
        log.TargetId.Should().Be("iccard_20260419_100000.db");
        log.OperatorName.Should().Be("管理者");
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().Contain("iccard_20260419_100000.db");
    }

    [Fact]
    public async Task LogRestoreAsync_RecordsRestoreActionWithDatabaseTarget()
    {
        // Arrange
        _operatorContext.BeginSession("1122334455667788", "管理者2");

        // Act
        await _logger.LogRestoreAsync(@"C:\backup\iccard_backup.db");

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Database, "iccard_backup.db");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.TargetTable.Should().Be(OperationLogger.Tables.Database);
        log.TargetId.Should().Be("iccard_backup.db");
        log.OperatorName.Should().Be("管理者2");
        log.AfterData.Should().Contain("iccard_backup.db");
    }

    [Fact]
    public async Task LogRestoreAsync_DistinctFromStaffRestore_ByTargetTable()
    {
        // Arrange
        var staff = CreateTestStaff();

        // Act — 両方の RESTORE を実行
        await _logger.LogStaffRestoreAsync(staff);
        await _logger.LogRestoreAsync(@"C:\backup.db");

        // Assert — Action=RESTORE が2件、TargetTable で区別可能
        var allLogs = (await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm)).ToList();
        allLogs.Where(l => l.Action == OperationLogger.Actions.Restore).Should().HaveCount(2);
        allLogs.Should().Contain(l =>
            l.Action == OperationLogger.Actions.Restore && l.TargetTable == OperationLogger.Tables.Staff);
        allLogs.Should().Contain(l =>
            l.Action == OperationLogger.Actions.Restore && l.TargetTable == OperationLogger.Tables.Database);
    }

    #endregion
}
