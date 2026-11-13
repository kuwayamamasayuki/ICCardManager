using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2461: 職員証の差し替え（<see cref="StaffRepository.ReplaceStaffIdmAsync"/> /
/// <see cref="StaffManagementService.ReplaceCardAsync"/>）を実 SQLite で検証する。
/// </summary>
/// <remarks>
/// <para>
/// 差し替えは主キー <c>staff.staff_idm</c> を変え、その職員を指す記録（台帳の貸出者・交通系ICカードの最終貸出者・
/// 統合の取り消し用データ）を新しい IDm へ付け替える。外部キー（<c>PRAGMA foreign_keys = ON</c>）を壊さない順序と、
/// 途中で失敗したときに何も確定しないこと（巻き戻り）は、モックでは観測できないため実 DB の行で表明する
/// （db-write-conventions.md「ロールバックの検証はモックでは観測できない」）。
/// </para>
/// <para>
/// 操作ログは監査記録なので書き換えない。差し替えの操作を 1 件（UPDATE。変更前に古い IDm、変更後に新しい IDm）記録する。
/// </para>
/// </remarks>
public class StaffCardReplacementTests : IDisposable
{
    private const string OldIdm = "0123456789ABCDEF";
    private const string NewIdm = "FEDCBA9876543210";
    private const string OtherStaffIdm = "1111222233334444";
    private const string CardIdm = "0102030405060708";
    private const string SecondCardIdm = "0102030405060709";
    private const string OperatorIdm = "AAAA0000BBBB1111";
    private const string OperatorName = "認証 太郎";

    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock = new();
    private readonly StaffRepository _staffRepository;
    private readonly CardRepository _cardRepository;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CurrentOperatorContext _operatorContext =
        new(new FixedSystemClock(new DateTime(2026, 11, 13, 9, 0, 0)));
    private readonly OperationLogger _operationLogger;
    private readonly StaffManagementService _service;

    public StaffCardReplacementTests()
    {
        _dbContext = TestDbContextFactory.Create();

        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());

        _staffRepository = new StaffRepository(
            _dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<StaffRepository>.Instance);
        _cardRepository = new CardRepository(
            _dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _ledgerRepository = new LedgerRepository(_dbContext);
        _operationLogger = new OperationLogger(new OperationLogRepository(_dbContext), _operatorContext);
        _service = new StaffManagementService(
            _dbContext, _staffRepository, _operationLogger, NullLogger<StaffManagementService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 準備

    private async Task SeedStaffAsync(string idm, string name, string? number = null, string? note = null)
    {
        (await _staffRepository.InsertAsync(new Staff { StaffIdm = idm, Name = name, Number = number, Note = note }))
            .Should().BeTrue("前提の職員登録は成功する");
    }

    private async Task SeedCardAsync(string idm, string cardNumber)
    {
        (await _cardRepository.InsertAsync(new IcCard { CardIdm = idm, CardType = "はやかけん", CardNumber = cardNumber }))
            .Should().BeTrue("前提のカード登録は成功する");
    }

    private Task<int> SeedLedgerAsync(string? lenderIdm, string staffName, int balance, string summary = "鉄道（薬院～博多）")
        => _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardIdm,
            LenderIdm = lenderIdm,
            Date = new DateTime(2026, 4, 1, 9, 0, 0),
            Summary = summary,
            Expense = 210,
            Balance = balance,
            StaffName = staffName
        });

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string?>> QueryStringsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var values = new List<string?>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        }

        return values;
    }

    private Task<List<string?>> StaffIdmsAsync()
        => QueryStringsAsync("SELECT staff_idm FROM staff ORDER BY staff_idm");

    /// <summary>
    /// 差し替える職員（台帳 2 行・貸出中のカード 1 枚）と、別の職員（台帳 1 行）を用意する
    /// </summary>
    private async Task SeedTypicalAsync()
    {
        await SeedStaffAsync(OldIdm, "博多 花子", "S-001", "総務課");
        await SeedStaffAsync(OtherStaffIdm, "天神 次郎", "S-002");
        await SeedCardAsync(CardIdm, "H-001");
        await SeedCardAsync(SecondCardIdm, "H-002");

        await SeedLedgerAsync(OldIdm, "博多 花子", 2186);
        await SeedLedgerAsync(OldIdm, "博多 花子", 1976);
        await SeedLedgerAsync(OtherStaffIdm, "天神 次郎", 1766);

        // 差し替える職員が H-001 を貸出中、別の職員が H-002 を最後に借りた
        await ExecuteAsync("UPDATE ic_card SET is_lent = 1, last_lent_staff = @idm WHERE card_idm = @card",
            ("@idm", OldIdm), ("@card", CardIdm));
        await ExecuteAsync("UPDATE ic_card SET last_lent_staff = @idm WHERE card_idm = @card",
            ("@idm", OtherStaffIdm), ("@card", SecondCardIdm));
    }

    /// <summary>認証した職員のセッションの中で差し替える（本番は認証のスコープの中で呼ばれる）</summary>
    private async Task<StaffCardReplacementResult> ReplaceAsOperatorAsync(string currentIdm, string newIdm)
    {
        using (_operatorContext.BeginSession(OperatorIdm, OperatorName))
        {
            return await _service.ReplaceCardAsync(currentIdm, newIdm);
        }
    }

    #endregion

    #region 付け替え

    [Fact]
    public async Task 差し替えると職員は新しいIDmで残り_古いIDmの行は残らないこと()
    {
        await SeedTypicalAsync();

        var result = await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        result.Status.Should().Be(StaffCardReplacementStatus.Ok);
        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { NewIdm, OtherStaffIdm },
            "古い職員証の行は消え、新しい職員証の行だけが残る");

        var replaced = await _staffRepository.GetByIdmAsync(NewIdm);
        replaced.Should().NotBeNull();
        replaced!.Name.Should().Be("博多 花子");
        replaced.Number.Should().Be("S-001");
        replaced.Note.Should().Be("総務課");
        replaced.IsDeleted.Should().BeFalse();
        (await _staffRepository.GetByIdmAsync(OldIdm, includeDeleted: true)).Should().BeNull(
            "削除済みとしても残さない（残すと同じ職員が 2 行になり、古い職員証で復元できてしまう）");
    }

    [Fact]
    public async Task 差し替えると台帳の貸出者と交通系ICカードの最終貸出者が新しいIDmを指すこと()
    {
        await SeedTypicalAsync();

        var result = await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        result.LedgerRowsUpdated.Should().Be(2);
        result.CardRowsUpdated.Should().Be(1);
        (await QueryStringsAsync("SELECT lender_idm FROM ledger ORDER BY id"))
            .Should().Equal(new[] { NewIdm, NewIdm, OtherStaffIdm }, "差し替えた職員の行だけを付け替え、別の職員の行は変えない");
        (await QueryStringsAsync("SELECT last_lent_staff FROM ic_card ORDER BY card_idm"))
            .Should().Equal(new[] { NewIdm, OtherStaffIdm }, "貸出中のカードは新しい職員証で返却できるよう付け替える");
        (await QueryStringsAsync("SELECT is_lent FROM ic_card WHERE card_idm = @c", ("@c", CardIdm)))
            .Should().Equal(new[] { "1" }, "貸出中のまま（差し替えで返却扱いにしない）");
    }

    [Fact]
    public async Task 差し替えた後も外部キーの違反が無いこと()
    {
        await SeedTypicalAsync();

        await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        (await QueryStringsAsync("PRAGMA foreign_key_check")).Should().BeEmpty();
        (await QueryStringsAsync("PRAGMA foreign_keys")).Should().Equal(new[] { "1" }, "検査が外部キーを有効にした接続で行われていること");
    }

    [Fact]
    public async Task 返却者の列に古いIDmがあれば同じく付け替えること()
    {
        await SeedTypicalAsync();
        await ExecuteAsync("UPDATE ledger SET returner_idm = @idm WHERE id = 1", ("@idm", OldIdm));

        await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        (await QueryStringsAsync("SELECT returner_idm FROM ledger WHERE id = 1")).Should().Equal(NewIdm);
    }

    [Fact]
    public async Task 貸出中のカードを差し替えた後に新しい職員証で返却者として照合できること()
    {
        await SeedTypicalAsync();

        await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        var card = await _cardRepository.GetByIdmAsync(CardIdm);
        card!.LastLentStaff.Should().Be(NewIdm);
        (await _staffRepository.GetByIdmAsync(card.LastLentStaff!)).Should().NotBeNull(
            "最終貸出者は登録済みの職員を指す（返却時の照合で見つかる）");
    }

    #endregion

    #region 統合の取り消し用データ

    [Fact]
    public async Task 差し替えた後に統合を取り消しても外部キー違反にならず_統合元は新しいIDmで戻ること()
    {
        await SeedStaffAsync(OldIdm, "博多 花子");
        await SeedCardAsync(CardIdm, "H-001");
        var targetId = await SeedLedgerAsync(OldIdm, "博多 花子", 2186, "鉄道（薬院～博多）");
        await _ledgerRepository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = targetId,
            UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
            EntryStation = "薬院",
            ExitStation = "博多",
            Amount = 210,
            Balance = 2186
        });
        var sourceId = await SeedLedgerAsync(OldIdm, "博多 花子", 1976, "鉄道（博多～薬院）");
        await _ledgerRepository.InsertDetailAsync(new LedgerDetail
        {
            LedgerId = sourceId,
            UseDate = new DateTime(2026, 4, 1, 9, 0, 0),
            EntryStation = "博多",
            ExitStation = "薬院",
            Amount = 210,
            Balance = 1976
        });

        var mergeService = new LedgerMergeService(
            _ledgerRepository, new SummaryGenerator(), _operationLogger, _dbContext, NullLogger<LedgerMergeService>.Instance);
        (await mergeService.MergeAsync(new List<int> { targetId, sourceId })).Success.Should().BeTrue("前提の統合");

        var result = await ReplaceAsOperatorAsync(OldIdm, NewIdm);
        result.MergeHistoryRowsUpdated.Should().Be(1);

        var undoData = (await QueryStringsAsync("SELECT undo_data FROM ledger_merge_history")).Single()!;
        undoData.Should().NotContain(OldIdm, "取り消しで挿入し直す統合元が古い IDm を持たないこと");
        undoData.Should().Contain(StaffRepository.MergeUndoDataIdmToken("LenderIdm", NewIdm));
        undoData.Should().Contain(CardIdm, "カードの IDm など他の項目は書き換えない");
        JsonDocument.Parse(undoData).Should().NotBeNull("書き換えた後も JSON として読める");

        var historyId = (await mergeService.GetUndoableMergeHistoriesAsync()).Single().Id;
        var undo = await mergeService.UnmergeAsync(historyId);
        undo.Success.Should().BeTrue($"差し替えた後も統合を取り消せる: {undo.ErrorMessage}");
        (await QueryStringsAsync("SELECT DISTINCT lender_idm FROM ledger")).Should().Equal(NewIdm);
    }

    [Fact]
    public async Task 統合の取り消し用データは職員IDmの項目だけを書き換えること()
    {
        await SeedTypicalAsync();
        // 同じ 16 文字が職員 IDm 以外の項目に現れても書き換えない（プロパティ名ごと照合する）
        var undoData = "{\"OriginalTarget\":{\"CardIdm\":\"" + OldIdm + "\",\"LenderIdm\":\"" + OldIdm + "\"}," +
                       "\"DeletedSources\":[{\"LenderIdm\":\"" + OtherStaffIdm + "\",\"ReturnerIdm\":\"" + OldIdm + "\"}]}";
        await ExecuteAsync(
            "INSERT INTO ledger_merge_history (target_ledger_id, description, undo_data) VALUES (1, 'test', @d)",
            ("@d", undoData));

        await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        (await QueryStringsAsync("SELECT undo_data FROM ledger_merge_history")).Single().Should().Be(
            "{\"OriginalTarget\":{\"CardIdm\":\"" + OldIdm + "\",\"LenderIdm\":\"" + NewIdm + "\"}," +
            "\"DeletedSources\":[{\"LenderIdm\":\"" + OtherStaffIdm + "\",\"ReturnerIdm\":\"" + NewIdm + "\"}]}");
    }

    #endregion

    #region 拒否（何も書かない）

    public static IEnumerable<object[]> RejectionCases()
    {
        yield return new object[] { "同じ職員証", OldIdm, StaffCardReplacementStatus.SameCard };
        yield return new object[] { "ほかの職員の職員証", OtherStaffIdm, StaffCardReplacementStatus.RegisteredStaff };
        yield return new object[] { "削除済みの職員の職員証", "DDDD0000DDDD0000", StaffCardReplacementStatus.DeletedStaff };
        yield return new object[] { "交通系ICカード", SecondCardIdm, StaffCardReplacementStatus.RegisteredCard };
        yield return new object[] { "削除済みの交通系ICカード", "CCCC0000CCCC0000", StaffCardReplacementStatus.RegisteredCard };
    }

    private async Task SeedRejectionTargetsAsync()
    {
        await SeedTypicalAsync();
        await SeedStaffAsync("DDDD0000DDDD0000", "退職 三郎");
        await ExecuteAsync("UPDATE staff SET is_deleted = 1 WHERE staff_idm = 'DDDD0000DDDD0000'");
        await SeedCardAsync("CCCC0000CCCC0000", "H-099");
        await ExecuteAsync("UPDATE ic_card SET is_deleted = 1 WHERE card_idm = 'CCCC0000CCCC0000'");
    }

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public async Task 差し替えに使えないIDmは確認でも書き込みでも拒否し_何も書かないこと(
        string description, string newIdm, StaffCardReplacementStatus expected)
    {
        await SeedRejectionTargetsAsync();
        var staffBefore = await StaffIdmsAsync();

        (await _service.CheckCardReplacementAsync(OldIdm, newIdm)).Status.Should().Be(expected, description);
        var result = await ReplaceAsOperatorAsync(OldIdm, newIdm);

        result.Status.Should().Be(expected, description);
        (await StaffIdmsAsync()).Should().Equal(staffBefore, $"{description}: 職員は変わらない");
        (await QueryStringsAsync("SELECT lender_idm FROM ledger ORDER BY id"))
            .Should().Equal(new[] { OldIdm, OldIdm, OtherStaffIdm }, $"{description}: 台帳は変わらない");
        (await QueryStringsAsync("SELECT count(*) FROM operation_log WHERE action = 'UPDATE'"))
            .Should().Equal(new[] { "0" }, $"{description}: 操作ログも書かない");
    }

    [Theory]
    [InlineData("0123456789abcdef", StaffCardReplacementStatus.SameCard)]
    [InlineData("bbbb0000cccc1111", StaffCardReplacementStatus.RegisteredStaff)]
    [InlineData("abcdef0123456789", StaffCardReplacementStatus.RegisteredCard)]
    [InlineData("aaaa0000aaaa000a", StaffCardReplacementStatus.Ok)]
    public async Task 小文字で渡したIDmも大文字の保存値と同じ規則で判定すること(string newIdm, StaffCardReplacementStatus expected)
    {
        // 判定ごとに大文字小文字の扱いが違うと、同じ IDm を「同じ職員証」とは見なすのに「登録済み」とは見なさない、のように食い違う
        await SeedTypicalAsync();
        await SeedStaffAsync("BBBB0000CCCC1111", "大文字 五郎");
        await SeedCardAsync("ABCDEF0123456789", "H-003");

        (await _service.CheckCardReplacementAsync(OldIdm.ToLowerInvariant(), newIdm)).Status.Should().Be(expected);
    }

    [Fact]
    public async Task 小文字で渡した新しいIDmは大文字で保存すること()
    {
        await SeedTypicalAsync();

        var result = await ReplaceAsOperatorAsync(OldIdm, NewIdm.ToLowerInvariant());

        result.IsOk.Should().BeTrue();
        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { NewIdm, OtherStaffIdm });
        (await QueryStringsAsync("SELECT DISTINCT lender_idm FROM ledger WHERE staff_name = '博多 花子'"))
            .Should().Equal(new[] { NewIdm });
    }

    [Fact]
    public async Task ほかの職員の職員証で拒否したときは_その職員を名指しできること()
    {
        await SeedTypicalAsync();

        var result = await _service.CheckCardReplacementAsync(OldIdm, OtherStaffIdm);

        result.ConflictingStaff.Should().NotBeNull();
        result.ConflictingStaff!.Name.Should().Be("天神 次郎");
    }

    [Fact]
    public async Task 使えるIDmの確認では何も書かないこと()
    {
        await SeedTypicalAsync();

        (await _service.CheckCardReplacementAsync(OldIdm, NewIdm)).Status.Should().Be(StaffCardReplacementStatus.Ok);

        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { OldIdm, OtherStaffIdm });
    }

    [Fact]
    public async Task 対象の職員が削除されていたら競合として何も書かないこと()
    {
        await SeedTypicalAsync();
        // 確認の後、書き込みの前に他のパソコンが削除した
        (await _service.CheckCardReplacementAsync(OldIdm, NewIdm)).IsOk.Should().BeTrue();
        await ExecuteAsync("UPDATE staff SET is_deleted = 1 WHERE staff_idm = @idm", ("@idm", OldIdm));

        var result = await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        result.Status.Should().Be(StaffCardReplacementStatus.TargetNotFound);
        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { OldIdm, OtherStaffIdm });
        (await QueryStringsAsync("SELECT last_lent_staff FROM ic_card WHERE card_idm = @c", ("@c", CardIdm)))
            .Should().Equal(OldIdm);
    }

    [Fact]
    public async Task 先に差し替えられていたら2回目は競合になること()
    {
        await SeedTypicalAsync();
        (await ReplaceAsOperatorAsync(OldIdm, NewIdm)).IsOk.Should().BeTrue();

        // 他のパソコンが同じ職員を別の新しい職員証へ差し替えようとした（古い IDm はもう無い）
        var second = await ReplaceAsOperatorAsync(OldIdm, "9999888877776666");

        second.Status.Should().Be(StaffCardReplacementStatus.TargetNotFound);
        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { NewIdm, OtherStaffIdm });
    }

    [Fact]
    public async Task トランザクションを渡さない呼び出しは受け付けないこと()
    {
        await SeedTypicalAsync();

        Func<Task> act = () => _staffRepository.ReplaceStaffIdmAsync(OldIdm, NewIdm, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region 巻き戻り・操作ログ

    [Fact]
    public async Task 途中の付け替えで失敗したら全体が巻き戻ること()
    {
        await SeedTypicalAsync();
        // 台帳の付け替えの後（交通系ICカードの付け替え）で失敗させる
        await ExecuteAsync(@"CREATE TRIGGER fail_card_update BEFORE UPDATE OF last_lent_staff ON ic_card
BEGIN SELECT RAISE(ABORT, 'injected failure'); END");
        var logCountBefore = await QueryStringsAsync("SELECT count(*) FROM operation_log");

        Func<Task> act = () => ReplaceAsOperatorAsync(OldIdm, NewIdm);

        await act.Should().ThrowAsync<SQLiteException>();
        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { OldIdm, OtherStaffIdm }, "新しい行の追加も巻き戻る");
        (await QueryStringsAsync("SELECT lender_idm FROM ledger ORDER BY id"))
            .Should().Equal(new[] { OldIdm, OldIdm, OtherStaffIdm }, "台帳の付け替えも巻き戻る");
        (await QueryStringsAsync("SELECT count(*) FROM operation_log")).Should().Equal(logCountBefore, "操作ログも残らない");
    }

    [Fact]
    public async Task 操作ログの書き込みに失敗したら差し替えも巻き戻ること()
    {
        await SeedTypicalAsync();
        await ExecuteAsync(@"CREATE TRIGGER fail_log_insert BEFORE INSERT ON operation_log
BEGIN SELECT RAISE(ABORT, 'injected failure'); END");

        Func<Task> act = () => ReplaceAsOperatorAsync(OldIdm, NewIdm);

        await act.Should().ThrowAsync<SQLiteException>();
        (await StaffIdmsAsync()).Should().BeEquivalentTo(new[] { OldIdm, OtherStaffIdm });
        (await QueryStringsAsync("SELECT last_lent_staff FROM ic_card WHERE card_idm = @c", ("@c", CardIdm)))
            .Should().Equal(OldIdm);
    }

    [Fact]
    public async Task 差し替えの操作ログを1件記録し_過去の操作ログは書き換えないこと()
    {
        await SeedTypicalAsync();
        // 差し替える職員が以前に行った操作の記録（古い IDm のまま残るべき監査記録）
        await ExecuteAsync(@"INSERT INTO operation_log (operator_idm, operator_name, target_table, target_id, action, after_data)
VALUES (@idm, '博多 花子', 'staff', @idm, 'INSERT', '{}')", ("@idm", OldIdm));

        await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        (await QueryStringsAsync("SELECT operator_idm FROM operation_log WHERE action = 'INSERT'"))
            .Should().Equal(new[] { OldIdm }, "過去の操作ログはその時点の事実として残す");
        (await QueryStringsAsync("SELECT target_id FROM operation_log WHERE action = 'INSERT'"))
            .Should().Equal(OldIdm);

        const string UpdateLog = "FROM operation_log WHERE action = 'UPDATE'";
        (await QueryStringsAsync("SELECT count(*) " + UpdateLog)).Should().Equal(new[] { "1" }, "差し替えの操作を 1 件記録する");
        (await QueryStringsAsync("SELECT target_table " + UpdateLog)).Should().Equal(OperationLogger.Tables.Staff);
        (await QueryStringsAsync("SELECT target_id " + UpdateLog)).Should().Equal(NewIdm);
        (await QueryStringsAsync("SELECT operator_idm " + UpdateLog)).Should().Equal(new[] { OperatorIdm }, "操作者は認証した職員");
        (await QueryStringsAsync("SELECT operator_name " + UpdateLog)).Should().Equal(OperatorName);
        var before = (await QueryStringsAsync("SELECT before_data " + UpdateLog)).Single()!;
        var after = (await QueryStringsAsync("SELECT after_data " + UpdateLog)).Single()!;
        JsonDocument.Parse(before).RootElement.GetProperty("StaffIdm").GetString().Should().Be(OldIdm);
        JsonDocument.Parse(after).RootElement.GetProperty("StaffIdm").GetString().Should().Be(NewIdm);
        JsonDocument.Parse(after).RootElement.GetProperty("Name").GetString().Should().Be("博多 花子");
    }

    [Fact]
    public async Task 差し替えの後に職員一覧と交通系ICカードのキャッシュを破棄すること()
    {
        await SeedTypicalAsync();
        _cacheServiceMock.Invocations.Clear();

        await ReplaceAsOperatorAsync(OldIdm, NewIdm);

        _cacheServiceMock.Verify(c => c.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation), Times.AtLeastOnce());
        _cacheServiceMock.Verify(c => c.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation), Times.AtLeastOnce(),
            "最終貸出者を書き換えるので交通系ICカードの一覧も古くなる");
    }

    [Fact]
    public async Task 拒否したときもキャッシュを破棄すること()
    {
        await SeedTypicalAsync();
        _cacheServiceMock.Invocations.Clear();

        await ReplaceAsOperatorAsync(OldIdm, OtherStaffIdm);

        _cacheServiceMock.Verify(c => c.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation), Times.AtLeastOnce(),
            "拒否は「手元の一覧が古い」ことの証拠になり得る（#1759）");
    }

    #endregion
}
