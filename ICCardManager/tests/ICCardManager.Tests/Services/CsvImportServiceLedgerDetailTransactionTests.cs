using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
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
/// Issue #2155: 利用履歴詳細（明細）CSV の取り込みで、明細の置換と親の履歴（摘要・金額）の更新が
/// 1 つのトランザクションで確定することのテスト。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は <c>ReplaceDetailsAsync</c> が自前のトランザクションで確定し、親の <c>UpdateAsync</c> が
/// 別のトランザクションだった。後者が失敗すると「明細は差し替わったが、親の摘要・金額は旧値」という
/// 食い違いが 6 年保存の台帳に残り、しかも再インポートは明細が一致するため「変更なし」でスキップされた。
/// </para>
/// <para>
/// 巻き戻ったかは「モックが呼ばれたか」では観測できないため、実 <see cref="LedgerRepository"/> へ
/// 委譲しつつ特定の呼び出しだけ失敗を注入し、<b>DB に実際に残った明細と親の値</b>を読んで表明する
/// （<see cref="CsvImportServiceLedgerTransactionTests"/> と同じ手法）。
/// </para>
/// <para>
/// <b>tx なしオーバーロードも実リポジトリへ委譲し、失敗の注入も両方のオーバーロードに掛ける</b>。
/// そうしないと、tx なし経路（＝旧実装の形）へ退行したとき、失敗が注入されずに成功してしまい
/// 「巻き戻った」ことを確かめるテストが意味を失う。
/// </para>
/// </remarks>
public class CsvImportServiceLedgerDetailTransactionTests : IDisposable
{
    private const string TestCardIdm = "0123456789ABCDEF";

    /// <summary>UTF-8 with BOM（<c>ReadCsvFileAsync</c> の文字コード判別を通すため）。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private const string Header =
        "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

    private static readonly DateTime UseDate = new DateTime(2024, 1, 15, 10, 30, 0);

    private const string OriginalSummary = "鉄道（博多～天神）";

    private readonly string _testDirectory;
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _realLedgerRepository;

    public CsvImportServiceLedgerDetailTransactionTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportDetailTx_{Guid.NewGuid():N}");
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
    /// 親の更新（<c>UpdateAsync</c>）の差し替え。引数は（対象 Ledger, トランザクション。tx なし経路では null）。
    /// </summary>
    private delegate Task<bool> UpdateBehavior(Ledger ledger, SQLiteTransaction? transaction);

    /// <summary>
    /// 実 <see cref="LedgerRepository"/> に委譲する <see cref="CsvImportService"/> を組み立てる。
    /// </summary>
    /// <param name="updateBehavior">
    /// 親の更新の差し替え。null なら実リポジトリへそのまま委譲する。
    /// tx あり・なしの両方のオーバーロードに同じ差し替えを掛ける（クラスの remarks を参照）。
    /// </param>
    /// <param name="replaceBehavior">
    /// 明細の置換の差し替え。null なら実リポジトリへそのまま委譲する（両方のオーバーロード）。
    /// </param>
    /// <param name="receivedTransactions">
    /// 置換・更新の tx ありオーバーロードが受け取った tx を記録する（null のまま渡す退行を検出するため。testing.md #2103）。
    /// </param>
    private (CsvImportService Service, Mock<ILedgerRepository> LedgerRepositoryMock) CreateServiceOverRealRepository(
        UpdateBehavior updateBehavior = null,
        UpdateBehavior replaceBehavior = null,
        List<SQLiteTransaction> receivedTransactions = null)
    {
        var ledgerRepositoryMock = new Mock<ILedgerRepository>();

        async Task<bool> RunReplace(int id, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction)
        {
            var replaced = transaction != null
                ? await _realLedgerRepository.ReplaceDetailsAsync(id, details, transaction)
                : await _realLedgerRepository.ReplaceDetailsAsync(id, details);
            // 差し替えは実際の置換を行ったうえで戻り値だけを変える（置換が DB に届いていなければ
            // 巻き戻ったかを観測できない。testing.md「ロールバックは書き込みが残らないことで観測する」）
            return replaceBehavior != null
                ? await replaceBehavior(new Ledger { Id = id }, transaction)
                : replaced;
        }

        ledgerRepositoryMock.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
            .Returns<int>(id => _realLedgerRepository.GetByIdAsync(id));

        ledgerRepositoryMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()))
            .Returns<int, IEnumerable<LedgerDetail>, SQLiteTransaction>((id, details, transaction) =>
            {
                receivedTransactions?.Add(transaction);
                return RunReplace(id, details, transaction);
            });
        ledgerRepositoryMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Returns<int, IEnumerable<LedgerDetail>>((id, details) => RunReplace(id, details, null));

        Task<bool> RunUpdate(Ledger ledger, SQLiteTransaction transaction) =>
            updateBehavior != null
                ? updateBehavior(ledger, transaction)
                : transaction != null
                    ? _realLedgerRepository.UpdateAsync(ledger, transaction)
                    : _realLedgerRepository.UpdateAsync(ledger);

        ledgerRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Returns<Ledger, SQLiteTransaction>((ledger, transaction) =>
            {
                receivedTransactions?.Add(transaction);
                return RunUpdate(ledger, transaction);
            });
        ledgerRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(ledger => RunUpdate(ledger, null));

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        var service = new CsvImportService(
            new Mock<ICardRepository>().Object,
            new Mock<IStaffRepository>().Object,
            ledgerRepositoryMock.Object,
            new Mock<IValidationService>().Object,
            _dbContext,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object,
            NullLogger<CsvImportService>.Instance);

        return (service, ledgerRepositoryMock);
    }

    /// <summary>
    /// 既存の履歴（博多→天神 260 円、残額 9,740 円）を 1 件投入し、その id を返す。
    /// </summary>
    private async Task<int> SeedLedgerWithDetailAsync(DateTime date)
    {
        using (var lease = await _dbContext.LeaseConnectionAsync())
        using (var command = lease.Connection.CreateCommand())
        {
            command.CommandText =
                "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')";
            command.Parameters.AddWithValue("@idm", TestCardIdm);
            await command.ExecuteNonQueryAsync();
        }

        var ledgerId = await _realLedgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date.Date,
            Summary = OriginalSummary,
            Income = 0,
            Expense = 260,
            Balance = 9740
        });
        var inserted = await _realLedgerRepository.InsertDetailsAsync(ledgerId, new[]
        {
            new LedgerDetail
            {
                LedgerId = ledgerId,
                UseDate = date,
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                Balance = 9740
            }
        });
        inserted.Should().BeTrue("前提: 既存の明細を投入できていること");
        return ledgerId;
    }

    /// <summary>既存の明細を「博多→中洲川端 300 円、残額 9,700 円」へ変える CSV 行。</summary>
    private static string ChangedDetailLine(int ledgerId, DateTime date) =>
        $"{ledgerId},{FormatUseDate(date)},{TestCardIdm},001,博多,中洲川端,,300,9700,0,0,0,";

    private static string FormatUseDate(DateTime date) =>
        date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(_testDirectory, $"details_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, Header + "\n" + string.Join("\n", lines) + "\n", CsvEncoding);
        return path;
    }

    /// <summary>DB に残っている明細と親の値が、投入したときのままであることを表明する。</summary>
    private async Task AssertUnchangedAsync(int ledgerId, string because)
    {
        var ledger = await _realLedgerRepository.GetByIdAsync(ledgerId);
        ledger.Should().NotBeNull();
        var detail = ledger!.Details.Should().ContainSingle(because).Subject;
        detail.ExitStation.Should().Be("天神", because);
        detail.Amount.Should().Be(260, because);
        ledger.Summary.Should().Be(OriginalSummary, because);
        ledger.Expense.Should().Be(260, because);
        ledger.Balance.Should().Be(9740, because);
    }

    #endregion

    /// <summary>
    /// 欠陥を突く側: 明細の置換のあとで親の更新が例外になったら、明細も旧値のまま（巻き戻る）こと。
    /// </summary>
    /// <remarks>
    /// 旧実装では明細（博多→中洲川端）だけが確定し、親の摘要・金額は「博多～天神／260 円」のまま残った。
    /// </remarks>
    [Fact]
    public async Task 親の更新が例外_明細の置換も巻き戻ること()
    {
        // Arrange
        var ledgerId = await SeedLedgerWithDetailAsync(UseDate);
        var csvPath = WriteCsv(ChangedDetailLine(ledgerId, UseDate));
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(
            (_, __) => throw new InvalidOperationException("injected parent update failure"));

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        ledgerRepositoryMock.Verify(r => r.ReplaceDetailsAsync(
                ledgerId, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()), Times.Once,
            "前提: 明細の置換を実行したあとで親の更新が失敗していること");
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        var error = result.Errors.Should().ContainSingle().Subject;
        error.LineNumber.Should().Be(2);
        error.Message.Should().Contain("変更されていません");
        await AssertUnchangedAsync(ledgerId, "明細の置換と親の更新は 1 つのトランザクションで巻き戻る（Issue #2155）");
    }

    /// <summary>
    /// 欠陥を突く側: 親の更新が 0 行（他 PC が履歴を削除した競合）を返したときも、明細の置換を巻き戻すこと。
    /// </summary>
    /// <remarks>
    /// 実 DB で 0 行を起こすには置換と更新の間に行を消す必要があり、同一 tx の内側では起こせないため、
    /// 戻り値 <c>false</c> を注入する。旧実装はこの分岐でも明細だけを確定させていた。
    /// </remarks>
    [Fact]
    public async Task 親の更新が0行_明細の置換も巻き戻り競合として報告すること()
    {
        // Arrange
        var ledgerId = await SeedLedgerWithDetailAsync(UseDate);
        var csvPath = WriteCsv(ChangedDetailLine(ledgerId, UseDate));
        var (service, _) = CreateServiceOverRealRepository((_, __) => Task.FromResult(false));

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("削除された可能性");
        await AssertUnchangedAsync(ledgerId, "0 行の競合でも置換を確定させない（Issue #2155）");
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 通常の取り込みでは、明細と親の摘要・金額がともに更新されて確定すること。
    /// </summary>
    /// <remarks>
    /// 対の表明。これが無いと「常にロールバックする」実装でも上の 2 件は緑になる。
    /// 書き込みが tx なしオーバーロード（autocommit）を通っていないことも併せて固定する。
    /// </remarks>
    [Fact]
    public async Task 通常の取り込み_明細と親の摘要と金額がともに更新されること()
    {
        // Arrange
        var ledgerId = await SeedLedgerWithDetailAsync(UseDate);
        var csvPath = WriteCsv(ChangedDetailLine(ledgerId, UseDate));
        var receivedTransactions = new List<SQLiteTransaction>();
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(
            receivedTransactions: receivedTransactions);

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        result.ImportedCount.Should().Be(1);

        // 置換と更新が「同じ非 null の tx」を受け取ったこと。null を渡しても同じ接続上の暗黙参加（②）で
        // 原子性は保たれてしまうため、DB の状態だけでは ① の明示的な受け渡しを検査できない（Issue #1737）
        receivedTransactions.Should().HaveCount(2, "置換と親の更新の 2 回");
        receivedTransactions.Should().NotContainNulls();
        receivedTransactions[1].Should().BeSameAs(receivedTransactions[0],
            "明細の置換と親の更新は同じトランザクションで行う");

        var ledger = await _realLedgerRepository.GetByIdAsync(ledgerId);
        var detail = ledger!.Details.Should().ContainSingle().Subject;
        detail.ExitStation.Should().Be("中洲川端");
        detail.Amount.Should().Be(300);
        ledger.Summary.Should().Be("鉄道（博多～中洲川端）");
        ledger.Expense.Should().Be(300);
        ledger.Balance.Should().Be(9700);

        ledgerRepositoryMock.Verify(r => r.ReplaceDetailsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()), Times.Never,
            "トランザクションを引き渡さない置換は自前の tx で確定してしまう");
        ledgerRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never,
            "トランザクションを引き渡さない更新は autocommit で確定してしまう");
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 既存と同一の明細はスキップされ、何も書き込まないこと。
    /// </summary>
    [Fact]
    public async Task 変更のない明細_スキップされ書き込みを行わないこと()
    {
        // Arrange
        var ledgerId = await SeedLedgerWithDetailAsync(UseDate);
        var csvPath = WriteCsv($"{ledgerId},{FormatUseDate(UseDate)},{TestCardIdm},001,博多,天神,,260,9740,0,0,0,");
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository();

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        result.ImportedCount.Should().Be(0);
        result.SkippedCount.Should().Be(1);
        ledgerRepositoryMock.Verify(r => r.ReplaceDetailsAsync(
                It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        ledgerRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        await AssertUnchangedAsync(ledgerId, "変更が無ければ書き込まない");
    }

    /// <summary>
    /// 明細の INSERT が件数不足（<c>ReplaceDetailsAsync</c> が <c>false</c>）のとき、置換を巻き戻し、
    /// 親も更新せず、3 要素の文言で報告すること。
    /// </summary>
    /// <remarks>
    /// 差し替えは実際の置換を DB へ書いたうえで <c>false</c> を返す。巻き戻さない実装では置換された明細が残る。
    /// </remarks>
    [Fact]
    public async Task 明細の置換がfalse_置換を巻き戻し親を更新せず報告すること()
    {
        // Arrange
        var ledgerId = await SeedLedgerWithDetailAsync(UseDate);
        var csvPath = WriteCsv(ChangedDetailLine(ledgerId, UseDate));
        var (service, ledgerRepositoryMock) = CreateServiceOverRealRepository(
            replaceBehavior: (_, __) => Task.FromResult(false));

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        var message = result.Errors.Should().ContainSingle().Subject.Message;
        message.Should().Contain($"利用履歴ID {ledgerId}", "何が");
        message.Should().Contain("変更されていません", "なぜ／状態");
        message.Should().MatchRegex("してください。$", "どうすれば: 行動指示で終わる");
        ledgerRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never,
            "置換に失敗したら親を更新しない");
        await AssertUnchangedAsync(ledgerId, "置換の件数不足でも途中まで書いた明細を確定させない");
    }

    /// <summary>
    /// トランザクションの単位は利用履歴 ID ごと: 1 件が失敗しても、他の履歴の取り込みは確定すること。
    /// </summary>
    /// <remarks>
    /// ループ全体を 1 つのトランザクションにすると、失敗した履歴と無関係な履歴まで巻き戻る。
    /// 従来どおり「失敗した履歴だけを行番号付きのエラーとして報告し、他は取り込む」形を固定する。
    /// </remarks>
    [Fact]
    public async Task 複数の履歴で1件だけ失敗_失敗した履歴だけが巻き戻り他は確定すること()
    {
        // Arrange
        var failingDate = UseDate;
        var succeedingDate = UseDate.AddDays(1);
        var failingId = await SeedLedgerWithDetailAsync(failingDate);
        var succeedingId = await SeedLedgerWithDetailAsync(succeedingDate);
        var csvPath = WriteCsv(
            ChangedDetailLine(failingId, failingDate),
            ChangedDetailLine(succeedingId, succeedingDate));
        var (service, _) = CreateServiceOverRealRepository((ledger, transaction) =>
            ledger.Id == failingId
                ? throw new InvalidOperationException("injected parent update failure")
                : transaction != null
                    ? _realLedgerRepository.UpdateAsync(ledger, transaction)
                    : _realLedgerRepository.UpdateAsync(ledger));

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(1, "成功した履歴の明細は取り込み件数に数える");
        result.Errors.Should().ContainSingle().Which.LineNumber.Should().Be(2);

        await AssertUnchangedAsync(failingId, "失敗した履歴は巻き戻る");
        var succeeded = await _realLedgerRepository.GetByIdAsync(succeedingId);
        succeeded!.Details.Should().ContainSingle().Which.ExitStation.Should().Be("中洲川端",
            "他の履歴の取り込みは確定している");
        succeeded.Summary.Should().Be("鉄道（博多～中洲川端）");
    }
}
