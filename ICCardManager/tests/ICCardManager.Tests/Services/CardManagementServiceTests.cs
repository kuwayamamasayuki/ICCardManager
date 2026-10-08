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
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2156: 交通系ICカードの登録・更新・削除・復元が、監査ログと 1 トランザクションで確定することを
/// 実 DB（インメモリ SQLite）の行で検証する。
/// </summary>
/// <remarks>
/// <para>
/// 以前は <c>CardManageViewModel</c> が <c>ic_card</c> への書き込みのコミット後に監査ログを記録しており、
/// 監査ログだけが失敗すると「操作は反映されたのに記録が残らない」状態が確定した。
/// </para>
/// <para>
/// ロールバックはモックでは観測できない（db-write-conventions #1727）ため、カードは実リポジトリで書き、
/// 操作ログのリポジトリだけを差し替えて失敗を注入する。差し替えは tx なしの旧オーバーロードも実装へ委譲する —
/// 未設定のままだと、旧経路へ戻った実装が「そもそも 1 行も書かれない」ことで行数の表明を素通りする（testing.md #1745）。
/// </para>
/// <para>
/// 「欠陥を突く側」（監査ログの失敗で本処理も巻き戻る）と「正当な操作を塞いでいない側」（通常の操作で
/// 本処理と監査ログがそろって 1 件ずつ残る／競合・重複は従来どおりの結果）を対で置く。
/// </para>
/// </remarks>
public sealed class CardManagementServiceTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string OtherCardIdm = "07FE665544332211";
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "庶務 担当";

    private readonly DbContext _dbContext;
    private readonly RecordingRetryDelay _retryDelay;
    private readonly CardRepository _cardRepository;
    private readonly OperationLogRepository _realOperationLogRepository;
    /// <summary>
    /// キャッシュの破棄を観測する（Issue #2156: tx を渡した書き込みは破棄しないため、サービスの finally だけが破棄する）
    /// </summary>
    private readonly Mock<ICacheService> _cacheService;
    /// <summary>キャッシュを破棄した時点でトランザクションが開いていたか（破棄ごとに記録）</summary>
    private readonly List<bool> _invalidatedWhileTransactionOpen = new();

    public CardManagementServiceTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        // Issue #2108: リトライのバックオフを実際に待たず、要求された待機時間を記録する
        _retryDelay = RecordingRetryDelay.AttachTo(_dbContext);

        _cardRepository = new CardRepository(
            _dbContext, (_cacheService = CreatePassThroughCacheService()).Object, Options.Create(new CacheOptions()),
            NullLogger<CardRepository>.Instance);
        _realOperationLogRepository = new OperationLogRepository(_dbContext);
        _cacheService.Setup(c => c.InvalidateByPrefix(It.IsAny<string>()))
            .Callback(() => _invalidatedWhileTransactionOpen.Add(_dbContext.HasActiveTransactionScope));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 欠陥を突く側 — 監査ログの失敗で本処理も巻き戻る

    [Fact]
    public async Task RegisterAsync_監査ログの書き込みに失敗したら_カードも登録されないこと()
    {
        // Arrange
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.RegisterAsync(NewCard(TestCardIdm, "H-001"));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true)).Should().BeNull(
            "監査ログの無い登録を確定させない（誰が登録したか分からないカードが残る）");
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_監査ログの書き込みに失敗したら_カード情報も更新されないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001", note: "変更前のメモ");
        var before = await _cardRepository.GetByIdmAsync(TestCardIdm);
        var after = CopyWith(before!, note: "変更後のメモ");
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.UpdateAsync(before!, after);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.Note.Should().Be("変更前のメモ");
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_監査ログの書き込みに失敗したら_カードも削除されないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var card = await _cardRepository.GetByIdmAsync(TestCardIdm);
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.DeleteAsync(card!);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _cardRepository.GetByIdmAsync(TestCardIdm)).Should().NotBeNull(
            "削除は監査上もっとも重要な操作で、誰が削除したか分からない削除は後から復元できない");
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    [Fact]
    public async Task RestoreAsync_監査ログの書き込みに失敗したら_カードも復元されないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);
        var deleted = await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true);
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.RestoreAsync(deleted!);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue();
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    #endregion

    #region 正当な操作を塞いでいない側 — 本処理と監査ログがそろって 1 件ずつ残る

    [Fact]
    public async Task RegisterAsync_通常の登録_カードと監査ログがそろって1件ずつ残ること()
    {
        // Arrange
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.RegisterAsync(NewCard(TestCardIdm, "H-001"));

        // Assert
        result.Should().BeTrue();
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.CardNumber.Should().Be("H-001");
        var log = (await ReadLogsAsync(TestCardIdm)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(OperatorIdm);
        log.OperatorName.Should().Be(OperatorName);
        log.BeforeData.Should().BeNull();
        JsonSerializer.Deserialize<IcCard>(log.AfterData!)!.CardNumber.Should().Be("H-001");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never,
            "監査ログは本処理と同じトランザクションで書く（tx なしの旧経路を通らない）");
    }

    [Fact]
    public async Task UpdateAsync_通常の更新_更新と監査ログがそろって1件ずつ残ること()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001", note: "変更前のメモ");
        var before = await _cardRepository.GetByIdmAsync(TestCardIdm);
        var after = CopyWith(before!, note: "変更後のメモ");
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.UpdateAsync(before!, after);

        // Assert
        result.Should().BeTrue();
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.Note.Should().Be("変更後のメモ");
        var log = (await ReadLogsAsync(TestCardIdm)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        JsonSerializer.Deserialize<IcCard>(log.BeforeData!)!.Note.Should().Be("変更前のメモ");
        JsonSerializer.Deserialize<IcCard>(log.AfterData!)!.Note.Should().Be("変更後のメモ");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_通常の削除_削除と監査ログがそろって1件ずつ残ること()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var card = await _cardRepository.GetByIdmAsync(TestCardIdm);
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.DeleteAsync(card!);

        // Assert
        result.Should().Be(CardOperationResult.Success);
        (await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue();
        var log = (await ReadLogsAsync(TestCardIdm)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.AfterData.Should().BeNull();
        JsonSerializer.Deserialize<IcCard>(log.BeforeData!)!.CardNumber.Should().Be("H-001");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    /// <summary>
    /// 復元の監査ログの変更後データは、復元前のデータの削除状態だけを戻したものであること
    /// </summary>
    /// <remarks>
    /// 以前はコミット後に読み直しており、読めなければ復元前のデータで補っていた（Issue #1760）。
    /// 同じトランザクションの中では補いが常に成り立つので組み立てに一本化した。この操作が変えていない列
    /// （開始ページ番号・繰越累計）が既定値に落ちないこと（#1726 の虚偽の差分）も併せて固定する。
    /// </remarks>
    [Fact]
    public async Task RestoreAsync_通常の復元_復元と監査ログがそろって1件ずつ残り_変えていない列は保たれること()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001", startingPageNumber: 7, carryoverIncomeTotal: 12000);
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);
        var deleted = await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true);
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.RestoreAsync(deleted!);

        // Assert
        result.Should().BeTrue();
        (await _cardRepository.GetByIdmAsync(TestCardIdm)).Should().NotBeNull();
        var log = (await ReadLogsAsync(TestCardIdm)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        var after = JsonSerializer.Deserialize<IcCard>(log.AfterData!)!;
        after.IsDeleted.Should().BeFalse();
        after.DeletedAt.Should().BeNull();
        after.StartingPageNumber.Should().Be(7, "この操作が変えていない列は復元前の値を保つこと");
        after.CarryoverIncomeTotal.Should().Be(12000);
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    #endregion

    #region 競合・重複 — 従来どおりの結果を返し、監査ログは書かない

    [Fact]
    public async Task UpdateAsync_編集中に削除されていたら_falseを返し監査ログを書かないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var before = await _cardRepository.GetByIdmAsync(TestCardIdm);
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.UpdateAsync(before!, CopyWith(before!, note: "変更後のメモ"));

        // Assert
        result.Should().BeFalse("影響行数 0 は競合（Issue #1753）。ViewModel が ConcurrencyConflictMessage で案内する");
        (await CountLogsAsync(TestCardIdm)).Should().Be(0, "起きていない更新の記録を残さない");
    }

    [Fact]
    public async Task DeleteAsync_貸出中なら_CardIsLentを返し監査ログを書かないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var card = await _cardRepository.GetByIdmAsync(TestCardIdm);
        (await _cardRepository.UpdateLentStatusAsync(TestCardIdm, true, new DateTime(2025, 6, 1), null)).Should().BeTrue();
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.DeleteAsync(card!);

        // Assert
        result.Should().Be(CardOperationResult.CardIsLent, "失敗原因の診断（Issue #1109）はトランザクション経由でも変わらない");
        (await _cardRepository.GetByIdmAsync(TestCardIdm)).Should().NotBeNull();
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    [Fact]
    public async Task RestoreAsync_先に復元されていたら_falseを返し監査ログを書かないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);
        var deleted = await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true);
        (await _cardRepository.RestoreAsync(TestCardIdm)).Should().BeTrue("他 PC が先に復元した");
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.RestoreAsync(deleted!);

        // Assert
        result.Should().BeFalse();
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_管理番号が重複したら_重複の例外を投げ何も残さないこと()
    {
        // Arrange
        await SeedCardAsync(OtherCardIdm, "H-001");
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var act = () => service.RegisterAsync(NewCard(TestCardIdm, "H-001"));

        // Assert — 型を変えずに届くこと（ViewModel は UserFriendlyMessage をその場で出す。Issue #1757）
        await act.Should().ThrowAsync<DuplicateCardNumberException>();
        (await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true)).Should().BeNull();
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
        _retryDelay.Delays.Should().BeEmpty("重複は何度やっても失敗するのでリトライしない");
    }

    [Fact]
    public async Task UpdateAsync_管理番号が重複したら_重複の例外を投げ何も残さないこと()
    {
        // Arrange
        await SeedCardAsync(OtherCardIdm, "H-001");
        await SeedCardAsync(TestCardIdm, "H-002");
        var before = await _cardRepository.GetByIdmAsync(TestCardIdm);
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var act = () => service.UpdateAsync(before!, CopyWith(before!, cardNumber: "H-001"));

        // Assert
        await act.Should().ThrowAsync<DuplicateCardNumberException>();
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.CardNumber.Should().Be("H-002");
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    /// <summary>
    /// 監査ログの書き込みが一過性のロック競合に当たっても、リトライで本処理ごとやり直して確定すること
    /// </summary>
    /// <remarks>
    /// 巻き戻しに素の <c>Rollback()</c> を使うと、二次例外が SQLITE_BUSY を置き換えてリトライが効かなくなる
    /// （Issue #1831）。1 回目だけ Busy を投げ、2 回目で成功させて、行が二重にならないことまで見る。
    /// </remarks>
    [Fact]
    public async Task RegisterAsync_監査ログが一過性のロック競合に当たったら_リトライして1件ずつ確定すること()
    {
        // Arrange
        var attempts = 0;
        var operationLogs = PassThroughOperationLogRepository();
        operationLogs.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, tx) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
                }
                return _realOperationLogRepository.InsertAsync(log, tx);
            });
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.RegisterAsync(NewCard(TestCardIdm, "H-001"));

        // Assert
        result.Should().BeTrue();
        attempts.Should().Be(2);
        _retryDelay.Delays.Should().HaveCount(1, "1 回だけ待ってからやり直す");
        (await CountAsync("SELECT COUNT(*) FROM ic_card WHERE card_idm = @idm", TestCardIdm)).Should().Be(1,
            "1 回目の登録は巻き戻っているので、やり直しで二重にならない");
        (await CountLogsAsync(TestCardIdm)).Should().Be(1);
    }

    #endregion

    #region キャッシュの破棄 — コミット・ロールバックの後に破棄する

    /// <summary>
    /// 成功したら、トランザクションを閉じた後でキャッシュを破棄すること
    /// </summary>
    /// <remarks>
    /// tx を渡したリポジトリの書き込みは成功してもキャッシュを破棄しない（ロールバックされれば根拠が消える）。
    /// サービスの finally が破棄しないと、登録したカードが一覧のキャッシュ（既定 TTL 60 秒）に現れない。
    /// </remarks>
    [Fact]
    public async Task RegisterAsync_成功_トランザクションを閉じた後でキャッシュを破棄すること()
    {
        // Arrange
        var service = CreateService(PassThroughOperationLogRepository().Object);
        _cacheService.Invocations.Clear();
        _invalidatedWhileTransactionOpen.Clear();

        // Act
        (await service.RegisterAsync(NewCard(TestCardIdm, "H-001"))).Should().BeTrue();

        // Assert
        _invalidatedWhileTransactionOpen.Should().NotBeEmpty("登録後の一覧に新しいカードが出るよう破棄すること")
            .And.OnlyContain(open => open == false, "コミット前に破棄すると、ロールバックされたとき破棄した根拠が消える");
    }

    /// <summary>
    /// 監査ログの失敗で巻き戻したときも、ロールバックの後でキャッシュを破棄すること（対の表明）
    /// </summary>
    [Fact]
    public async Task UpdateAsync_監査ログの失敗で巻き戻したときも_キャッシュを破棄すること()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var before = await _cardRepository.GetByIdmAsync(TestCardIdm);
        var service = CreateService(FailingOperationLogRepository());
        _invalidatedWhileTransactionOpen.Clear();

        // Act
        var act = () => service.UpdateAsync(before!, CopyWith(before!, note: "変更後のメモ"));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _invalidatedWhileTransactionOpen.Should().NotBeEmpty().And.OnlyContain(open => open == false);
    }

    #endregion

    #region 繰越情報の復旧（Issue #2255）

    [Fact]
    public async Task RecoverCarryoverInfoAsync_通常の復旧_4項目が戻り監査ログに変更前後が残ること()
    {
        // Arrange: 消失した状態（既定値）。貸出中・備考ありで、この操作が変えない列を持たせる
        await SeedCardAsync(TestCardIdm, "H-001", note: "引き出し2段目");
        (await _cardRepository.UpdateLentStatusAsync(TestCardIdm, true, new DateTime(2026, 9, 1), null)).Should().BeTrue();
        var before = (await _cardRepository.GetByIdmAsync(TestCardIdm))!;
        var replacement = new CarryoverInfo(7, 45000, 37500, 2025);
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.RecoverCarryoverInfoAsync(before, replacement);

        // Assert
        result.Should().BeTrue();
        CarryoverInfo.From((await _cardRepository.GetByIdmAsync(TestCardIdm))!).Should().Be(replacement);

        var log = (await ReadLogsAsync(TestCardIdm)).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.OperatorIdm.Should().Be(OperatorIdm, "認証した職員を操作者として残す");
        var loggedBefore = JsonSerializer.Deserialize<IcCard>(log.BeforeData!)!;
        var loggedAfter = JsonSerializer.Deserialize<IcCard>(log.AfterData!)!;
        CarryoverInfo.From(loggedBefore).Should().Be(new CarryoverInfo(1, 0, 0, null));
        CarryoverInfo.From(loggedAfter).Should().Be(replacement);
        loggedAfter.IsLent.Should().BeTrue("この操作が変えていない列は変更前の値を保つ（虚偽の差分を残さない。#1726）");
        loggedAfter.Note.Should().Be("引き出し2段目");
    }

    [Fact]
    public async Task RecoverCarryoverInfoAsync_監査ログの書き込みに失敗したら_繰越情報も書き換わらないこと()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var before = (await _cardRepository.GetByIdmAsync(TestCardIdm))!;
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.RecoverCarryoverInfoAsync(before, new CarryoverInfo(7, 45000, 37500, 2025));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        CarryoverInfo.From((await _cardRepository.GetByIdmAsync(TestCardIdm))!)
            .Should().Be(new CarryoverInfo(1, 0, 0, null), "誰が戻したか分からない書き換えを確定させない");
        (await CountLogsAsync(TestCardIdm)).Should().Be(0);
    }

    [Fact]
    public async Task RecoverCarryoverInfoAsync_読み取った後に他のパソコンが先に復旧していたら_falseを返し監査ログを書かないこと()
    {
        // Arrange: 読み取った時点は既定値。その後に別の PC が 5 ページ目で復旧した
        await SeedCardAsync(TestCardIdm, "H-001");
        var before = (await _cardRepository.GetByIdmAsync(TestCardIdm))!;
        await RecoverDirectlyAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(5, 0, 0, null));
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.RecoverCarryoverInfoAsync(before, new CarryoverInfo(7, 45000, 37500, 2025));

        // Assert
        result.Should().BeFalse("見ていない値を黙って上書きしない");
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(5);
        (await CountLogsAsync(TestCardIdm)).Should().Be(0, "起きていない復旧の記録を残さない");
    }

    [Fact]
    public async Task RecoverCarryoverInfoAsync_成功_トランザクションを閉じた後でキャッシュを破棄すること()
    {
        // Arrange
        await SeedCardAsync(TestCardIdm, "H-001");
        var before = (await _cardRepository.GetByIdmAsync(TestCardIdm))!;
        var service = CreateService(PassThroughOperationLogRepository().Object);
        _invalidatedWhileTransactionOpen.Clear();

        // Act
        (await service.RecoverCarryoverInfoAsync(before, new CarryoverInfo(7, 0, 0, null))).Should().BeTrue();

        // Assert: 復旧後の一覧（交通系ICカード管理画面など）がキャッシュの古い値を返さないこと
        _invalidatedWhileTransactionOpen.Should().NotBeEmpty().And.OnlyContain(open => open == false);
    }

    [Fact]
    public void CreateCarryoverRecoveredSnapshot_繰越情報以外のすべての列を変更前から引き継ぐこと()
    {
        // 列を IcCard に足してスナップショットへ書き足し忘れても、コンパイルも既存テストも通る。
        // 書き込み可能なプロパティをリフレクションで走査し、引き継ぎ漏れを検出する（LedgerClonerCoverageTests と同じ作法）。
        var carryoverProperties = new HashSet<string>
        {
            nameof(IcCard.StartingPageNumber), nameof(IcCard.CarryoverIncomeTotal),
            nameof(IcCard.CarryoverExpenseTotal), nameof(IcCard.CarryoverFiscalYear),
        };
        var before = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "nimoca",
            CardNumber = "N-009",
            Note = "メモ",
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 1, 2, 3, 4, 5),
            IsLent = true,
            LastLentAt = new DateTime(2026, 2, 3, 4, 5, 6),
            LastLentStaff = "FFFF000000000002",
            IsRefunded = true,
            RefundedAt = new DateTime(2026, 3, 4, 5, 6, 7),
            StartingPageNumber = 1,
            CarryoverIncomeTotal = 0,
            CarryoverExpenseTotal = 0,
            CarryoverFiscalYear = null,
        };
        var replacement = new CarryoverInfo(7, 45000, 37500, 2025);

        var after = CardManagementService.CreateCarryoverRecoveredSnapshot(before, replacement);

        CarryoverInfo.From(after).Should().Be(replacement);
        var writable = typeof(IcCard).GetProperties().Where(p => p.CanRead && p.CanWrite).ToList();
        writable.Should().Contain(p => p.Name == nameof(IcCard.Note), "走査の対象が空振りしていないこと");
        foreach (var property in writable.Where(p => !carryoverProperties.Contains(p.Name)))
        {
            property.GetValue(after).Should().Be(property.GetValue(before), $"{property.Name} は変更前の値を引き継ぐこと");
        }
    }

    /// <summary>
    /// 他の PC の復旧を模して、サービスを通さずに繰越情報を書き換える
    /// </summary>
    private async Task RecoverDirectlyAsync(CarryoverInfo expected, CarryoverInfo replacement)
    {
        using var scope = await _dbContext.BeginTransactionAsync();
        (await _cardRepository.UpdateCarryoverInfoAsync(TestCardIdm, expected, replacement, scope.Transaction))
            .Should().BeTrue();
        scope.Commit();
    }

    #endregion

    #region ヘルパー

    private CardManagementService CreateService(IOperationLogRepository operationLogRepository)
    {
        var operatorContext = new Mock<ICurrentOperatorContext>();
        operatorContext.SetupGet(c => c.HasSession).Returns(true);
        operatorContext.SetupGet(c => c.CurrentIdm).Returns(OperatorIdm);
        operatorContext.SetupGet(c => c.CurrentName).Returns(OperatorName);

        return new CardManagementService(
            _dbContext,
            _cardRepository,
            new OperationLogger(operationLogRepository, operatorContext.Object),
            NullLogger<CardManagementService>.Instance);
    }

    /// <summary>
    /// 実リポジトリへ委譲する操作ログリポジトリ（tx なし・tx ありの両オーバーロード）
    /// </summary>
    private Mock<IOperationLogRepository> PassThroughOperationLogRepository()
    {
        var mock = new Mock<IOperationLogRepository>();
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Returns<OperationLog>(log => _realOperationLogRepository.InsertAsync(log));
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, tx) => _realOperationLogRepository.InsertAsync(log, tx));
        return mock;
    }

    /// <summary>
    /// tx ありの書き込みだけを失敗させる操作ログリポジトリ。tx なしは実装へ委譲する（旧経路へ戻った実装を
    /// 「書き込めてしまう」側で検出するため）。失敗は <see cref="InvalidOperationException"/> にして
    /// リトライ待機を挟まず 1 回で確定させる（db-write-conventions #1727）。
    /// </summary>
    private IOperationLogRepository FailingOperationLogRepository()
    {
        var mock = PassThroughOperationLogRepository();
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("operation_log への書き込みに失敗（テスト注入）"));
        return mock.Object;
    }

    private static IcCard NewCard(string idm, string cardNumber) => new()
    {
        CardIdm = idm,
        CardType = "はやかけん",
        CardNumber = cardNumber,
        Note = null,
    };

    private static IcCard CopyWith(IcCard source, string? note = null, string? cardNumber = null) => new()
    {
        CardIdm = source.CardIdm,
        CardType = source.CardType,
        CardNumber = cardNumber ?? source.CardNumber,
        Note = note ?? source.Note,
        IsLent = source.IsLent,
        LastLentAt = source.LastLentAt,
        LastLentStaff = source.LastLentStaff,
        IsRefunded = source.IsRefunded,
        RefundedAt = source.RefundedAt,
        StartingPageNumber = source.StartingPageNumber,
        CarryoverIncomeTotal = source.CarryoverIncomeTotal,
        CarryoverExpenseTotal = source.CarryoverExpenseTotal,
        CarryoverFiscalYear = source.CarryoverFiscalYear,
    };

    private async Task SeedCardAsync(
        string idm, string cardNumber, string? note = null, int startingPageNumber = 1, int carryoverIncomeTotal = 0)
    {
        (await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = idm,
            CardType = "はやかけん",
            CardNumber = cardNumber,
            Note = note,
            StartingPageNumber = startingPageNumber,
            CarryoverIncomeTotal = carryoverIncomeTotal,
        })).Should().BeTrue();
    }

    private Task<int> CountLogsAsync(string idm) =>
        CountAsync("SELECT COUNT(*) FROM operation_log WHERE target_table = 'ic_card' AND target_id = @idm", idm);

    private async Task<int> CountAsync(string sql, string idm)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@idm", idm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<List<OperationLog>> ReadLogsAsync(string idm)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = @"SELECT action, operator_idm, operator_name, before_data, after_data
FROM operation_log WHERE target_table = 'ic_card' AND target_id = @idm";
        command.Parameters.AddWithValue("@idm", idm);
        var logs = new List<OperationLog>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(new OperationLog
            {
                Action = reader.GetString(0),
                OperatorIdm = reader.GetString(1),
                OperatorName = reader.GetString(2),
                BeforeData = reader.IsDBNull(3) ? null : reader.GetString(3),
                AfterData = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }
        return logs;
    }

    private static Mock<ICacheService> CreatePassThroughCacheService()
    {
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        return mock;
    }

    #endregion
}
