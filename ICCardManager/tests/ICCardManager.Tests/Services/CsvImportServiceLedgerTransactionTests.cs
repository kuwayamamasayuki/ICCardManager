using System;
using System.Collections.Generic;
using System.Data.SQLite;
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
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1745: 履歴CSVインポートの原子性（全か無か）のテスト
/// </summary>
/// <remarks>
/// <para>
/// カード（<c>ImportCardsInternalAsync</c>）／職員（<c>ImportStaffInternalAsync</c>）のインポートは
/// <c>BeginTransactionAsync</c> で囲まれているのに、利用履歴だけがトランザクションを持たず
/// <c>InsertAsync(Ledger)</c> / <c>UpdateAsync(Ledger)</c> の tx なしオーバーロードを使っていた。
/// tx が null の経路は <c>LeaseConnectionAsync()</c> で接続を借りて autocommit で書くため、
/// N 行目で失敗しても 1..N-1 行は台帳に残る。事前の
/// <c>ValidateBalanceConsistencyForLedgers</c> は「全行が入る前提」で残高チェーンを検証しており、
/// 途中で切れた状態は誰にも検証されないまま確定してしまう。
/// </para>
/// <para>
/// ロールバックが効いたかは「モックが呼ばれたか」では観測できないため、実
/// <see cref="LedgerRepository"/> へ委譲しつつ特定回だけ失敗を注入し、実際の行数を数える
/// （<c>LendingServiceHistoryImportTests</c> と同じ手法。development-conventions.md 参照）。
/// </para>
/// </remarks>
public class CsvImportServiceLedgerTransactionTests : IDisposable
{
    private const string TestCardIdm = "0123456789ABCDEF";

    /// <summary>UTF-8 with BOM（Excel 対応）。<c>ReadCsvFileAsync</c> の文字コード判別を通すため。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _realLedgerRepository;

    public CsvImportServiceLedgerTransactionTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportLedgerTx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _dbContext = TestDbContextFactory.Create();
        _realLedgerRepository = new LedgerRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region ヘルパー

    /// <summary>
    /// Issue #1955: 摘要の再生成が参照する部署種別の供給元（既定 ＝ 市長事務部局）。
    /// </summary>
    private static ISettingsRepository CreateSettingsRepository()
    {
        var mock = new Mock<ISettingsRepository>();
        mock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        return mock.Object;
    }

    /// <summary>INSERT の呼び出し回数を数えるカウンター</summary>
    private sealed class InsertCounter
    {
        public int Count;
    }

    /// <summary>
    /// 実 <see cref="LedgerRepository"/> に委譲する <see cref="CsvImportService"/> を組み立てる。
    /// </summary>
    /// <param name="counter">INSERT の呼び出し回数</param>
    /// <param name="insertBehavior">
    /// INSERT の差し替え。引数は（何回目の呼び出しか, 対象 Ledger, トランザクション。
    /// tx なし経路では null）。null なら実リポジトリへそのまま委譲する。
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>tx なしオーバーロードも実リポジトリへ委譲する</b>のが要点。未設定のままにすると
    /// Moq の既定値 0 が返って DB へ 1 行も書かれず、「ロールバックされたので 0 行」という
    /// アサーションが<b>修正前のコードでも空振りで通ってしまう</b>
    /// （testing.md「通るが目的を果たさないテスト」）。実際に autocommit で書かせておけば、
    /// tx なし経路へ退行した瞬間に「先に成功した行が残る」形でテストが落ちる。
    /// </para>
    /// </remarks>
    private (CsvImportService Service, Mock<ILedgerRepository> LedgerRepositoryMock) CreateServiceOverRealRepository(
        InsertCounter counter,
        Func<int, Ledger, SQLiteTransaction, Task<int>> insertBehavior = null)
    {
        var ledgerRepositoryMock = new Mock<ILedgerRepository>();

        // 読み取り系は実 DB の状態を返さないと残高チェーン検証・重複判定が成立しない
        ledgerRepositoryMock.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .Returns<int>(id => _realLedgerRepository.GetByIdAsync(id));
        ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns<string, DateTime>((idm, beforeDate) => _realLedgerRepository.GetLatestBeforeDateAsync(idm, beforeDate));
        ledgerRepositoryMock.Setup(r => r.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .Returns<IEnumerable<string>>(idms => _realLedgerRepository.GetExistingLedgerKeysAsync(idms));

        Task<int> RunInsert(int callNumber, Ledger ledger, SQLiteTransaction transaction) =>
            insertBehavior != null
                ? insertBehavior(callNumber, ledger, transaction)
                : transaction != null
                    ? _realLedgerRepository.InsertAsync(ledger, transaction)
                    : _realLedgerRepository.InsertAsync(ledger);

        ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Returns<Ledger, SQLiteTransaction>((ledger, transaction) =>
            {
                counter.Count++;
                return RunInsert(counter.Count, ledger, transaction);
            });
        ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(ledger =>
            {
                counter.Count++;
                return RunInsert(counter.Count, ledger, null);
            });
        ledgerRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Returns<Ledger, SQLiteTransaction>((ledger, transaction) =>
                _realLedgerRepository.UpdateAsync(ledger, transaction));
        ledgerRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(ledger => _realLedgerRepository.UpdateAsync(ledger));

        var cardRepositoryMock = new Mock<ICardRepository>();
        cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(new List<IcCard>
            {
                new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "001" }
            });

        var service = new CsvImportService(
            cardRepositoryMock.Object,
            new Mock<IStaffRepository>().Object,
            ledgerRepositoryMock.Object,
            new Mock<IValidationService>().Object,
            _dbContext,
            new Mock<ICacheService>().Object,
            CreateSettingsRepository(),
            NullLogger<CsvImportService>.Instance);

        return (service, ledgerRepositoryMock);
    }

    /// <summary>
    /// 書き込みが tx なしオーバーロード（autocommit 経路）を一度も通っていないことを表明する。
    /// </summary>
    /// <remarks>
    /// Issue #1575: 入れ子で <c>BeginTransactionAsync</c> を開くと自己デッドロックするため、
    /// スコープ内の書き込みは必ず <c>scope.Transaction</c> を引き渡す（development-conventions.md の「①」経路）。
    /// </remarks>
    private static void VerifyNoAutocommitWrites(Mock<ILedgerRepository> ledgerRepositoryMock)
    {
        ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never,
            "トランザクションを引き渡さない登録は autocommit で確定してしまう");
        ledgerRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never,
            "トランザクションを引き渡さない更新は autocommit で確定してしまう");
    }

    private async Task SeedCardAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText =
            "INSERT INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>既存の台帳行を 1 件投入し、その id を返す</summary>
    private async Task<int> SeedLedgerAsync(DateTime date, string summary, int income, int expense, int balance)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = @"INSERT INTO ledger (card_idm, date, summary, income, expense, balance)
VALUES (@idm, @date, @summary, @income, @expense, @balance);
SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        command.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd HH:mm:ss"));
        command.Parameters.AddWithValue("@summary", summary);
        command.Parameters.AddWithValue("@income", income);
        command.Parameters.AddWithValue("@expense", expense);
        command.Parameters.AddWithValue("@balance", balance);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<int> CountLedgerRowsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ledger WHERE card_idm = @idm";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<(int Income, int Balance)> GetLedgerAmountsAsync(int ledgerId)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT income, balance FROM ledger WHERE id = @id";
        command.Parameters.AddWithValue("@id", ledgerId);
        using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>
    /// Issue #1906: 同行者数列を持つ CSV を取り込むと companion_count に保存され、列を持たない旧形式は 0 のまま取り込めること
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_同行者数列_companion_countへ保存されること()
    {
        await SeedCardAsync();
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考,同行者数
2025-01-10 00:00:00," + TestCardIdm + @",001,役務費によりチャージ,10000,,10000,,,
2025-01-11 00:00:00," + TestCardIdm + @",001,鉄道（天神～博多）,,210,9790,博多 花子,,2";
        var filePath = Path.Combine(_testDirectory, "ledgers_companion.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));
        var (service, _) = CreateServiceOverRealRepository(new InsertCounter());

        var result = await service.ImportLedgersAsync(filePath);

        result.Success.Should().BeTrue();
        var rows = (await _realLedgerRepository.GetByDateRangeAsync(TestCardIdm, new DateTime(2025, 1, 1), new DateTime(2025, 1, 31))).OrderBy(l => l.Date).ToList();
        rows.Should().HaveCount(2);
        rows[0].CompanionCount.Should().Be(0);
        rows[1].CompanionCount.Should().Be(2);
        rows[1].StaffName.Should().Be("博多 花子", "staff_name には「外N名」を書き込まない");
    }

    /// <summary>
    /// Issue #1906 / #1808: 同行者数列を持たない旧形式の CSV を取り込み直しても、既存の同行者数が 0 で上書きされないこと
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_同行者数列が無い旧形式CSV_既存の同行者数を消さないこと()
    {
        await SeedCardAsync();
        var seeded = await _realLedgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2025, 1, 11),
            Summary = "鉄道（天神～博多）",
            Expense = 210,
            Balance = 9790,
            StaffName = "博多 花子",
            CompanionCount = 2
        });

        // 旧形式（同行者数列なし）。ID 列ありで既存行を更新する形にする
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
" + seeded + @",2025-01-11 00:00:00," + TestCardIdm + @",001,鉄道（天神～博多・修正）,,210,9790,博多 花子,";
        var filePath = Path.Combine(_testDirectory, "ledgers_legacy_format.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));
        var (service, _) = CreateServiceOverRealRepository(new InsertCounter());

        var result = await service.ImportLedgersAsync(filePath);

        result.Success.Should().BeTrue();
        var updated = await _realLedgerRepository.GetByIdAsync(seeded);
        updated!.Summary.Should().Be("鉄道（天神～博多・修正）", "摘要の編集は反映される");
        updated.CompanionCount.Should().Be(2, "列が無い CSV は「同行者数の指定なし」であり、0 で上書きしてはならない");
    }

    /// <summary>ID列なし・残高チェーンが整合した3行の履歴CSVを書き出す</summary>
    private async Task<string> WriteThreeRowCsvAsync(string fileName)
    {
        var csvContent = @"日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
2025-01-10 00:00:00," + TestCardIdm + @",001,役務費によりチャージ,10000,,10000,,
2025-01-11 00:00:00," + TestCardIdm + @",001,鉄道（天神～博多）,,210,9790,,
2025-01-12 00:00:00," + TestCardIdm + @",001,鉄道（博多～天神）,,210,9580,,";

        var filePath = Path.Combine(_testDirectory, fileName);
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));
        return filePath;
    }

    #endregion

    /// <summary>
    /// 途中行の登録が SQLite エラーで失敗した場合、それ以前に成功した行も残らないこと。
    /// </summary>
    /// <remarks>
    /// Issue #1745 の中核。共有モードで他 PC のバックアップ処理と競合して SQLITE_BUSY が
    /// 発生する状況を模した。従来は tx なしの autocommit だったため 1 行目だけが確定し、
    /// 残高チェーンが検証されないまま途中で切れた状態が残っていた。
    /// </remarks>
    [Fact]
    public async Task ImportLedgersAsync_途中行がSQLiteエラー_それ以前の行も含めて1行も残らないこと()
    {
        // Arrange
        await SeedCardAsync();
        var filePath = await WriteThreeRowCsvAsync("ledgers_busy_midway.csv");
        var counter = new InsertCounter();
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(counter, (callNumber, ledger, transaction) =>
            callNumber == 2
                ? throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked")
                : _realLedgerRepository.InsertAsync(ledger, transaction));

        // Act
        var result = await service.ImportLedgersAsync(filePath);

        // Assert
        counter.Count.Should().BeGreaterOrEqualTo(2, "1行目の登録までは進んでいること（＝失敗前に書き込みが起きている）");
        (await CountLedgerRowsAsync()).Should().Be(0,
            "全行が入らなかった以上、先に成功した行だけを台帳へ残してはならない");
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        VerifyNoAutocommitWrites(ledgerRepositoryMock);
    }

    /// <summary>
    /// SQLite エラーの文言が生のままユーザーへ出ないこと（Issue #1614）。
    /// </summary>
    /// <remarks>
    /// 従来は行ごとの catch が <c>$"…エラーが発生しました: {ex.Message}"</c> を
    /// エラー一覧へ積んでいたため、英語の SQLite メッセージが画面に出ていた。
    /// トランザクション化に伴い <c>DatabaseException</c> へラップし、
    /// <c>ToUserFacingErrorMessage</c> が整備済みの文言へ変換する。
    /// </remarks>
    [Fact]
    public async Task ImportLedgersAsync_SQLiteエラー_生の例外メッセージをUIへ出さないこと()
    {
        // Arrange
        await SeedCardAsync();
        var filePath = await WriteThreeRowCsvAsync("ledgers_busy_message.csv");
        var counter = new InsertCounter();
        var (service, _) = CreateServiceOverRealRepository(counter, (callNumber, ledger, transaction) =>
            callNumber == 2
                ? throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked")
                : _realLedgerRepository.InsertAsync(ledger, transaction));

        // Act
        var result = await service.ImportLedgersAsync(filePath);

        // Assert
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace("失敗の理由が呼び出し元へ伝わること");
        result.ErrorMessage.Should().NotContain("database is locked", "生の例外メッセージを UI へ出さない（Issue #1614）");
        result.ErrorMessage.Should().NotContain("予期しないエラー", "対応表で変換されず default 分岐へ落ちていないこと");
        result.ErrorMessage.Should().EndWith("ください。", "行動指示で終わること（error-messages.md）");
    }

    /// <summary>
    /// リポジトリが例外ではなく失敗値（0）を返した場合も、全行ロールバックされること。
    /// </summary>
    /// <remarks>
    /// 例外を投げない失敗（影響行数 0 等）も errors へ積まれるため、
    /// カード／職員インポートと同じく <c>errors.Count &gt; 0</c> で Rollback する。
    /// </remarks>
    [Fact]
    public async Task ImportLedgersAsync_登録が失敗値を返す_全行ロールバックされること()
    {
        // Arrange
        await SeedCardAsync();
        var filePath = await WriteThreeRowCsvAsync("ledgers_insert_returns_zero.csv");
        var counter = new InsertCounter();
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(counter, (callNumber, ledger, transaction) =>
            callNumber == 2
                ? Task.FromResult(0)
                : _realLedgerRepository.InsertAsync(ledger, transaction));

        // Act
        var result = await service.ImportLedgersAsync(filePath);

        // Assert
        counter.Count.Should().Be(3, "例外ではないため残りの行も試行されること");
        (await CountLedgerRowsAsync()).Should().Be(0, "1件でも失敗したら全件戻すこと");
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0, "巻き戻した以上、登録件数を残してはならない");
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().ContainSingle(e => e.LineNumber == 3,
            "失敗した行を行番号付きで示すこと（CSV 3行目＝2件目の登録）");
        VerifyNoAutocommitWrites(ledgerRepositoryMock);
    }

    /// <summary>
    /// 更新が成功したあとに登録が失敗した場合、更新も巻き戻ること。
    /// </summary>
    /// <remarks>
    /// 更新と登録が同一トランザクションに載っていることの確認。従来は tx なしのため
    /// 更新だけが確定し、CSV の一部だけを反映した台帳が残っていた。
    /// </remarks>
    [Fact]
    public async Task ImportLedgersAsync_更新成功後に登録失敗_更新も巻き戻ること()
    {
        // Arrange
        await SeedCardAsync();
        var existingId = await SeedLedgerAsync(
            new DateTime(2025, 1, 10), "役務費によりチャージ", income: 10000, expense: 0, balance: 10000);

        // 1行目: 既存 id を金額変更（更新）／2行目: 新規登録
        var csvContent = @"ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
" + existingId + @",2025-01-10 00:00:00," + TestCardIdm + @",001,役務費によりチャージ,12000,,12000,,
,2025-01-11 00:00:00," + TestCardIdm + @",001,鉄道（天神～博多）,,210,11790,,";

        var filePath = Path.Combine(_testDirectory, "ledgers_update_then_insert_fails.csv");
        await Task.Run(() => File.WriteAllText(filePath, csvContent, CsvEncoding));

        var counter = new InsertCounter();
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(counter, (_, __, ___) =>
            throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked"));

        // Act
        var result = await service.ImportLedgersAsync(filePath);

        // Assert
        counter.Count.Should().Be(1, "更新のあとに登録が試行されていること");
        (await CountLedgerRowsAsync()).Should().Be(1, "新規行が残っていないこと");
        var amounts = await GetLedgerAmountsAsync(existingId);
        amounts.Income.Should().Be(10000, "更新が巻き戻り、CSV の 12000 円が反映されていないこと");
        amounts.Balance.Should().Be(10000);
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        VerifyNoAutocommitWrites(ledgerRepositoryMock);
    }

    /// <summary>
    /// ロールバック自体が失敗しても、本来の失敗要因の文言が返ること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>catch</c> の中で素の <c>scope.Rollback()</c> を呼ぶと、ロールバックの二次例外が
    /// <b>本来の失敗要因を置き換えて</b> 抜けてしまい、その catch に書いた <c>LogError</c> も
    /// <c>DatabaseException</c> へのラップも実行されない。結果、
    /// <c>ToUserFacingErrorMessage</c> の <c>default</c> 分岐に落ちて
    /// 「予期しないエラーが発生しました: No transaction is active on this connection」という
    /// 生の英語メッセージが UI へ出る（Issue #1614 違反）。
    /// </para>
    /// <para>
    /// ロールバックが失敗する状況は実在する。<c>COMMIT</c> が SQLITE_BUSY 等で失敗すると
    /// SQLite 側は既に自動ロールバック済みで <c>SQLiteTransaction</c> が無効化されており、
    /// 続けて <c>Rollback()</c> を呼ぶと <see cref="InvalidOperationException"/> になる。
    /// 共有フォルダーモードの接続断でも同様。ここでは同じ状態
    /// （＝トランザクションが既に巻き戻っている）を、書き込み失敗の直前に
    /// テスト側から <c>Rollback()</c> して再現する。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ImportLedgersAsync_ロールバックも失敗_本来の失敗要因の文言が返ること()
    {
        // Arrange
        await SeedCardAsync();
        var filePath = await WriteThreeRowCsvAsync("ledgers_rollback_also_fails.csv");
        var counter = new InsertCounter();
        var (service, _) = CreateServiceOverRealRepository(counter, (callNumber, ledger, transaction) =>
        {
            if (callNumber != 2)
            {
                return _realLedgerRepository.InsertAsync(ledger, transaction);
            }

            // トランザクションを先に巻き戻しておく。以降 SUT 側の Rollback() は
            // InvalidOperationException になる（COMMIT 失敗後・接続断後と同じ状態）
            transaction.Rollback();
            throw new SQLiteException(SQLiteErrorCode.Busy, "database is locked");
        });

        // Act
        var result = await service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeFalse();
        (await CountLedgerRowsAsync()).Should().Be(0, "巻き戻っている以上、1行も残らないこと");
        result.ErrorMessage.Should().NotContain("予期しないエラー",
            "ロールバックの二次例外が本来の失敗要因を置き換えて default 分岐へ落ちていないこと");
        result.ErrorMessage.Should().NotContain("No transaction is active",
            "ロールバック失敗の生の英語メッセージを UI へ出さない（Issue #1614）");
        result.ErrorMessage.Should().NotContain("database is locked",
            "本来の失敗要因も生のままは出さない（Issue #1614）");
        result.ErrorMessage.Should().EndWith("ください。", "行動指示で終わること（error-messages.md）");
    }

    /// <summary>
    /// すべて成功した場合はコミットされ、全行が台帳へ確定すること。
    /// </summary>
    /// <remarks>
    /// 失敗系だけを固定すると「常にロールバックする」実装へ退化しても検出できないため、
    /// 成功パスを併せて固定する。
    /// </remarks>
    [Fact]
    public async Task ImportLedgersAsync_全行成功_まとめてコミットされること()
    {
        // Arrange
        await SeedCardAsync();
        var filePath = await WriteThreeRowCsvAsync("ledgers_all_success.csv");
        var counter = new InsertCounter();
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(counter);

        // Act
        var result = await service.ImportLedgersAsync(filePath);

        // Assert
        result.Success.Should().BeTrue();
        result.ImportedCount.Should().Be(3);
        result.ErrorCount.Should().Be(0);
        (await CountLedgerRowsAsync()).Should().Be(3, "コミット後は接続を跨いで参照できること");
        VerifyNoAutocommitWrites(ledgerRepositoryMock);
    }
}
