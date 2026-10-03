using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2177: 利用履歴詳細ダイアログの保存（明細の置換・摘要の更新・監査ログ）が
/// 1 つのトランザクションで確定することを、実 DB（インメモリ SQLite）の行で検証する。
/// </summary>
/// <remarks>
/// <para>
/// 以前は明細の置換（<c>ReplaceDetailsAsync</c>）が自前のトランザクションで先に確定し、摘要の更新が別の
/// トランザクションだった。摘要の更新が失敗すると「明細は新しいのに摘要は古い」食い違いが 6 年保存の台帳に残った。
/// </para>
/// <para>
/// 巻き戻ったかはモックでは観測できない（testing.md「ロールバックは書き込みが残らないことで観測する」）ため、
/// 台帳と操作ログは実リポジトリへ委譲しつつ、特定の呼び出しだけに失敗を注入して DB を読み返す。
/// <b>tx なしオーバーロードも実リポジトリへ委譲し、失敗も両方へ注入する</b> — tx なし経路（＝旧実装の形）へ
/// 退行したとき、注入が効かずに成功してしまうと巻き戻りの表明が意味を失う（testing.md #1745）。
/// </para>
/// </remarks>
public sealed class LedgerDetailSaveServiceTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";
    private const string OriginalSummary = "鉄道（博多～天神、天神～博多）";

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _realLedgerRepository;
    private readonly OperationLogRepository _realOperationLogRepository;

    public LedgerDetailSaveServiceTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _realLedgerRepository = new LedgerRepository(_dbContext);
        _realOperationLogRepository = new OperationLogRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region ヘルパー

    /// <summary>tx あり・なしの呼び出しが受け取ったトランザクション（null は tx なし）。</summary>
    private sealed class Recorded
    {
        public List<SQLiteTransaction?> ReplaceTransactions { get; } = new();
        public List<SQLiteTransaction?> UpdateTransactions { get; } = new();
        public List<SQLiteTransaction?> AuditTransactions { get; } = new();
    }

    /// <summary>
    /// 実リポジトリへ委譲するサービスを組み立てる。各 Func は実際の書き込みを行ったあとで呼ばれ、
    /// 戻り値を差し替える（例外も投げられる）。null なら実リポジトリの結果をそのまま返す。
    /// </summary>
    private (LedgerDetailSaveService Service, Mock<ILedgerRepository> LedgerMock, Recorded Recorded) CreateService(
        Func<bool, bool>? replaceBehavior = null,
        Func<bool, bool>? updateBehavior = null,
        Action? auditBehavior = null)
    {
        var recorded = new Recorded();
        var ledgerMock = new Mock<ILedgerRepository>();

        async Task<bool> RunReplace(int id, IEnumerable<LedgerDetail> details, SQLiteTransaction? tx)
        {
            recorded.ReplaceTransactions.Add(tx);
            var replaced = tx != null
                ? await _realLedgerRepository.ReplaceDetailsAsync(id, details, tx)
                : await _realLedgerRepository.ReplaceDetailsAsync(id, details);
            return replaceBehavior != null ? replaceBehavior(replaced) : replaced;
        }

        async Task<bool> RunUpdate(Ledger ledger, SQLiteTransaction? tx)
        {
            recorded.UpdateTransactions.Add(tx);
            var updated = tx != null
                ? await _realLedgerRepository.UpdateAsync(ledger, tx)
                : await _realLedgerRepository.UpdateAsync(ledger);
            return updateBehavior != null ? updateBehavior(updated) : updated;
        }

        // Issue #2212: 摘要の更新は摘要だけを SET する UpdateSummaryAsync。全列の UpdateAsync（上）も実リポジトリへ
        // 委譲し失敗も注入するのは、全列の更新へ退行したときに注入が効かず成功してしまわないため（#1745）
        async Task<bool> RunUpdateSummary(int id, string summary, SQLiteTransaction tx)
        {
            recorded.UpdateTransactions.Add(tx);
            var updated = await _realLedgerRepository.UpdateSummaryAsync(id, summary, tx);
            return updateBehavior != null ? updateBehavior(updated) : updated;
        }

        ledgerMock.Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()))
            .Returns<int, IEnumerable<LedgerDetail>, SQLiteTransaction>((id, d, tx) => RunReplace(id, d, tx));
        ledgerMock.Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Returns<int, IEnumerable<LedgerDetail>>((id, d) => RunReplace(id, d, null));
        ledgerMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Returns<Ledger, SQLiteTransaction>((l, tx) => RunUpdate(l, tx));
        ledgerMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(l => RunUpdate(l, null));
        ledgerMock.Setup(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()))
            .Returns<int, string, SQLiteTransaction>((id, s, tx) => RunUpdateSummary(id, s, tx));

        async Task<int> RunAudit(OperationLog log, SQLiteTransaction? tx)
        {
            recorded.AuditTransactions.Add(tx);
            var id = tx != null
                ? await _realOperationLogRepository.InsertAsync(log, tx)
                : await _realOperationLogRepository.InsertAsync(log);
            auditBehavior?.Invoke();
            return id;
        }

        var operationLogMock = new Mock<IOperationLogRepository>();
        operationLogMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, tx) => RunAudit(log, tx));
        operationLogMock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Returns<OperationLog>(log => RunAudit(log, null));

        var operationLogger = new OperationLogger(operationLogMock.Object, Mock.Of<ICurrentOperatorContext>());
        var service = new LedgerDetailSaveService(
            _dbContext, ledgerMock.Object, operationLogger, NullLogger<LedgerDetailSaveService>.Instance);
        return (service, ledgerMock, recorded);
    }

    /// <summary>往復 2 区間（グループ未設定）の履歴を投入し、その id を返す。</summary>
    private async Task<int> SeedLedgerAsync()
    {
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')";
            command.Parameters.AddWithValue("@idm", TestCardIdm);
            await command.ExecuteNonQueryAsync();
        }

        var date = new DateTime(2026, 2, 10);
        var ledgerId = await _realLedgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = OriginalSummary,
            Expense = 520,
            Balance = 480
        });
        (await _realLedgerRepository.InsertDetailsAsync(ledgerId, new[]
        {
            new LedgerDetail { LedgerId = ledgerId, UseDate = date.AddHours(18), EntryStation = "天神", ExitStation = "博多", Amount = 260, Balance = 480 },
            new LedgerDetail { LedgerId = ledgerId, UseDate = date.AddHours(8), EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 740 },
        })).Should().BeTrue("前提: 明細を投入できていること");
        return ledgerId;
    }

    /// <summary>
    /// 画面と同じ手順で「2 区間を別グループに分け、摘要を変える」保存の引数を組み立てる。
    /// </summary>
    private async Task<(Ledger Before, Ledger After, List<LedgerDetail> Details)> PrepareSplitAsync(int ledgerId, string newSummary)
    {
        var ledger = (await _realLedgerRepository.GetByIdAsync(ledgerId))!;
        var before = LedgerCloner.Clone(ledger);
        var details = ledger.Details.OrderBy(d => d.UseDate).ToList();
        details[0].GroupId = 1;
        details[1].GroupId = 2;
        ledger.Summary = newSummary;
        return (before, ledger, details);
    }

    private async Task AssertUnchangedAsync(int ledgerId, string because)
    {
        var ledger = (await _realLedgerRepository.GetByIdAsync(ledgerId))!;
        ledger.Summary.Should().Be(OriginalSummary, because);
        ledger.Details.Should().HaveCount(2, because);
        ledger.Details.Should().OnlyContain(d => d.GroupId == null, because + "（明細のグループ分けも元のまま）");
    }

    private async Task<int> CountAuditLogsAsync(int ledgerId) =>
        (await _realOperationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, ledgerId.ToString(System.Globalization.CultureInfo.InvariantCulture))).Count();

    #endregion

    #region 欠陥を突く側 — どこで失敗しても何も変更されない

    [Fact]
    public async Task 摘要の更新が例外_明細の置換も巻き戻り監査ログも残らないこと()
    {
        // Arrange
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(ledgerId, "鉄道（博多～天神）、鉄道（天神～博多）");
        var (service, ledgerMock, _) = CreateService(
            updateBehavior: _ => throw new InvalidOperationException("injected summary update failure"));

        // Act
        var act = () => service.SaveAsync(before, after, details, summaryChanged: true);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        ledgerMock.Verify(r => r.ReplaceDetailsAsync(ledgerId, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()),
            Times.Once, "前提: 明細の置換を実行したあとで摘要の更新が失敗していること");
        await AssertUnchangedAsync(ledgerId, "明細の置換と摘要の更新は 1 つのトランザクションで巻き戻る（Issue #2177）");
        (await CountAuditLogsAsync(ledgerId)).Should().Be(0);
    }

    [Fact]
    public async Task 摘要の更新が0行_明細の置換も巻き戻り競合として返すこと()
    {
        // Arrange: 他 PC が統合・削除した競合。実際の UPDATE は同じ tx の内側では 0 行にできないため戻り値を差し替える
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(ledgerId, "鉄道（博多～天神）、鉄道（天神～博多）");
        var (service, _, _) = CreateService(updateBehavior: _ => false);

        // Act
        var result = await service.SaveAsync(before, after, details, summaryChanged: true);

        // Assert
        result.Should().Be(LedgerDetailSaveResult.Conflict);
        await AssertUnchangedAsync(ledgerId, "0 行の競合でも明細の置換を確定させない");
        (await CountAuditLogsAsync(ledgerId)).Should().Be(0, "起きていない変更を監査ログに残さない");
    }

    [Fact]
    public async Task 監査ログの書き込みが例外_明細も摘要も巻き戻ること()
    {
        // Arrange
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(ledgerId, "鉄道（博多～天神）、鉄道（天神～博多）");
        var (service, _, _) = CreateService(
            auditBehavior: () => throw new InvalidOperationException("injected audit failure"));

        // Act
        var act = () => service.SaveAsync(before, after, details, summaryChanged: true);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        await AssertUnchangedAsync(ledgerId, "誰が変えたか分からない変更を確定させない");
        (await CountAuditLogsAsync(ledgerId)).Should().Be(0);
    }

    [Fact]
    public async Task 明細の置換がfalse_何も変更せず摘要を更新しないこと()
    {
        // Arrange
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(ledgerId, "鉄道（博多～天神）、鉄道（天神～博多）");
        var (service, ledgerMock, _) = CreateService(replaceBehavior: _ => false);

        // Act
        var result = await service.SaveAsync(before, after, details, summaryChanged: true);

        // Assert
        result.Should().Be(LedgerDetailSaveResult.DetailsNotReplaced);
        ledgerMock.Verify(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        await AssertUnchangedAsync(ledgerId, "置換を実際に書いたうえで false を返しても確定させない");
        (await CountAuditLogsAsync(ledgerId)).Should().Be(0);
    }

    /// <summary>
    /// Issue #2177（コードレビューで検出）: 他 PC がこの履歴を削除した（または他の履歴へ統合して統合元を消した）あとで
    /// 保存すると、外部キーが有効なので先に走る明細の INSERT が外部キー違反になる。摘要の UPDATE の 0 行判定まで
    /// 届かないため、これを競合として返すこと（汎用の SQLite の失敗にすると「しばらく待ってから再度実行」という
    /// 何度やっても成功しない案内になり、閉じても一覧が読み込み直されない）。摘要が変わる保存・変わらない保存の両方で。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 保存の前に履歴の行が削除されていたら_競合として返し何も書かないこと(bool summaryChanged)
    {
        // Arrange: ダイアログを開いたあとで、他 PC が履歴の行を削除した（明細は ON DELETE CASCADE で消える）
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(
            ledgerId, summaryChanged ? "鉄道（博多～天神）、鉄道（天神～博多）" : OriginalSummary);
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM ledger WHERE id = @id";
            command.Parameters.AddWithValue("@id", ledgerId);
            (await command.ExecuteNonQueryAsync()).Should().Be(1, "前提: 履歴の行を削除できていること");
        }

        var (service, _, _) = CreateService();

        // Act
        var result = await service.SaveAsync(before, after, details, summaryChanged);

        // Assert
        result.Should().Be(LedgerDetailSaveResult.Conflict);
        (await _realLedgerRepository.GetByIdAsync(ledgerId)).Should().BeNull("削除された履歴を作り直さない");
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = @id";
            command.Parameters.AddWithValue("@id", ledgerId);
            Convert.ToInt32(await command.ExecuteScalarAsync()).Should().Be(0, "親の無い明細を残さない");
        }

        (await CountAuditLogsAsync(ledgerId)).Should().Be(0, "起きていない変更を監査ログに残さない");
    }

    #endregion

    #region 正当な保存を塞いでいない側

    /// <summary>
    /// 対の表明。これが無いと「常に巻き戻す」実装でも上の表明は緑になる。tx は非 null かつ同一参照であること
    /// （null を渡しても同じ接続上の暗黙参加で原子性が保たれてしまい、DB の状態だけでは検査できない。testing.md #2103）。
    /// </summary>
    [Fact]
    public async Task 通常の保存_明細と摘要と監査ログがそろって確定し同じトランザクションを通ること()
    {
        // Arrange
        var ledgerId = await SeedLedgerAsync();
        const string newSummary = "鉄道（博多～天神）、鉄道（天神～博多）";
        var (before, after, details) = await PrepareSplitAsync(ledgerId, newSummary);
        var (service, ledgerMock, recorded) = CreateService();

        // Act
        var result = await service.SaveAsync(before, after, details, summaryChanged: true);

        // Assert
        result.Should().Be(LedgerDetailSaveResult.Saved);
        var saved = (await _realLedgerRepository.GetByIdAsync(ledgerId))!;
        saved.Summary.Should().Be(newSummary);
        saved.Details.Select(d => d.GroupId).OrderBy(g => g).Should().Equal(1, 2);
        // Issue #1913: 新しい順で渡すので、小さい id（SequenceNumber）が新しい明細（18 時の天神→博多）
        saved.Details.OrderBy(d => d.SequenceNumber).First().EntryStation.Should().Be("天神");
        (await CountAuditLogsAsync(ledgerId)).Should().Be(1);

        var transactions = recorded.ReplaceTransactions.Concat(recorded.UpdateTransactions).Concat(recorded.AuditTransactions).ToList();
        transactions.Should().HaveCount(3);
        transactions.Should().OnlyContain(t => t != null, "tx を明示的に渡す（db-write-conventions.md の「①」）");
        transactions.Distinct().Should().ContainSingle("明細・摘要・監査ログは 1 つのトランザクションで確定する");
        ledgerMock.Verify(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()), Times.Never,
            "自前のトランザクションで確定する tx なしの置換（＝旧実装）を使わない");
    }

    [Fact]
    public async Task 摘要が変わらない保存_明細は保存され摘要を更新せず監査ログは記録すること()
    {
        // Arrange
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(ledgerId, OriginalSummary);
        var (service, ledgerMock, _) = CreateService();

        // Act
        var result = await service.SaveAsync(before, after, details, summaryChanged: false);

        // Assert
        result.Should().Be(LedgerDetailSaveResult.Saved);
        ledgerMock.Verify(r => r.UpdateSummaryAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never,
            "摘要が変わらないなら摘要の UPDATE は行わない");
        var saved = (await _realLedgerRepository.GetByIdAsync(ledgerId))!;
        saved.Details.Select(d => d.GroupId).OrderBy(g => g).Should().Equal(1, 2);
        (await CountAuditLogsAsync(ledgerId)).Should().Be(1,
            "明細のグループ分けは台帳の一部なので、摘要が変わらなくても監査ログに残す（Issue #2177）");
    }

    #endregion

    #region Issue #2212 — 画面を開いている間に他 PC が直した列を巻き戻さない

    /// <summary>
    /// 他 PC がダイアログを開いた後に備考・同行者数を直す（共有モードの別 PC からの書き込みに相当）。
    /// </summary>
    private async Task EditNoteAndCompanionCountElsewhereAsync(int ledgerId)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "UPDATE ledger SET note = '領収書あり', companion_count = 2 WHERE id = @id";
        command.Parameters.AddWithValue("@id", ledgerId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1, "前提: 他 PC の書き込みが届いていること");
    }

    [Fact]
    public async Task 摘要を変えて保存_開いた後に他PCが直した備考と同行者数を巻き戻さないこと()
    {
        // Arrange: ダイアログを開いた時点（備考なし・同行者 0）のスナップショットを作ってから、他 PC が直す
        var ledgerId = await SeedLedgerAsync();
        var (before, after, details) = await PrepareSplitAsync(ledgerId, "鉄道（博多～天神）、鉄道（天神～博多）");
        after.Note.Should().BeNull("前提: 開いた時点の備考は空");
        await EditNoteAndCompanionCountElsewhereAsync(ledgerId);
        var (service, ledgerMock, _) = CreateService();

        // Act
        var result = await service.SaveAsync(before, after, details, summaryChanged: true);

        // Assert — 「巻き戻さない」と「摘要は更新される」を対で表明する
        result.Should().Be(LedgerDetailSaveResult.Saved);
        var stored = (await _realLedgerRepository.GetByIdAsync(ledgerId))!;
        stored.Note.Should().Be("領収書あり", "開いた時点の空の備考で上書きしない（Issue #2212）");
        stored.CompanionCount.Should().Be(2, "開いた時点の同行者数 0 で上書きしない");
        stored.Summary.Should().Be("鉄道（博多～天神）、鉄道（天神～博多）", "摘要は保存した値になる");
        ledgerMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        ledgerMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never);
    }

    #endregion
}
