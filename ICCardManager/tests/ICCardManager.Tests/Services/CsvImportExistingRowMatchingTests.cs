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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2324: CSV の取り込みで、既存の行との照合の抜けにより台帳・マスタを意図しない形で書き換える経路を塞いだことを固定する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>利用履歴 CSV: 利用履歴 ID が台帳に無い行（削除・統合した行）を新しい履歴として戻さない</item>
/// <item>利用履歴詳細 CSV: 利用履歴 ID が別のカードの履歴を指す行で、そのカードの明細・金額を置き換えない</item>
/// <item>職員・カード CSV: 「削除済み」列が「1」の行を、復元・登録しない</item>
/// </list>
/// いずれも「欠陥を突く側」と「正当な取り込みを塞いでいない側」を対で置く。
/// </remarks>
public class CsvImportExistingRowMatchingTests : IDisposable
{
    private const string CardIdmA = "0123456789ABCDEF";
    private const string CardIdmB = "FEDCBA9876543210";
    private const string StaffIdm = "1111222233334444";
    private const string LedgerCsvHeader = "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考,同行者数";
    private const string DetailCsvHeader = "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<IValidationService> _validationServiceMock = new();
    private readonly Mock<DbContext> _dbContextMock = new();
    private readonly SQLiteConnection _connection;
    private readonly CsvImportService _importService;
    private readonly CsvExportService _exportService;
    private readonly List<Ledger> _insertedLedgers = new();

    public CsvImportExistingRowMatchingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportExistingRowMatchingTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var cards = new List<IcCard>
        {
            new IcCard { CardIdm = CardIdmA, CardType = "はやかけん", CardNumber = "001" },
            new IcCard { CardIdm = CardIdmB, CardType = "nimoca", CardNumber = "002" },
        };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(cards);
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), true))
            .ReturnsAsync((string idm, bool _) => cards.FirstOrDefault(c => c.CardIdm == idm));

        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction?>()))
            .ReturnsAsync((Ledger l, SQLiteTransaction? _) =>
            {
                _insertedLedgers.Add(l);
                return 100 + _insertedLedgers.Count;
            });

        _validationServiceMock.Setup(x => x.ValidateCardIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        _validationServiceMock.Setup(x => x.ValidateStaffIdm(It.IsAny<string>())).Returns(ValidationResult.Success());

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        // 明細 CSV は利用履歴 ID ごとにトランザクションを開くため、呼び出しごとに新しいスコープを返す
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(() => new TransactionScope(new ConnectionLease(_connection, () => { }), _connection.BeginTransaction()));

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _importService = new CsvImportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _validationServiceMock.Object,
            _dbContextMock.Object,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object,
            new OperationLogger(new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>()),
            NullLogger<CsvImportService>.Instance);

        _exportService = new CsvExportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            NullLogger<CsvExportService>.Instance);
    }

    public void Dispose()
    {
        _connection.Dispose();
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

    private async Task<string> WriteCsvAsync(string name, string header, params string[] rows)
    {
        var path = Path.Combine(_testDirectory, name);
        await Task.Run(() => File.WriteAllText(path, string.Join("\r\n", new[] { header }.Concat(rows)), CsvEncoding));
        return path;
    }

    private static void AssertActionable(string message)
    {
        message.Length.Should().BeGreaterThanOrEqualTo(20);
        message.Should().EndWith("ください。");
        message.Should().NotContain(CardIdmA).And.NotContain(CardIdmB, "IDm は画面に出さない（#1986）");
    }

    // ===== 利用履歴 CSV: 利用履歴 ID が台帳に無い行 =====

    /// <summary>
    /// 欠陥を突く側: 削除・統合で台帳から消えた行（ID が台帳に無い）を、昔の CSV の取り込み直しで新しい履歴として戻さない。
    /// スキップ指定の有無に関わらず拒否し、プレビューも同じ文言のエラーを出す（以前はプレビュー「追加」・取り込みで登録）。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportLedgersAsync_IDが台帳に無い行は新しい履歴として登録せず拒否すること(bool skipExisting)
    {
        var path = await WriteCsvAsync("ledger_id_not_found.csv", LedgerCsvHeader,
            "50,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,");

        var preview = await _importService.PreviewLedgersAsync(path, skipExisting);
        var result = await _importService.ImportLedgersAsync(path, skipExisting);

        preview.IsValid.Should().BeFalse();
        preview.NewCount.Should().Be(0, "プレビューで「追加」と表示しない");
        result.Success.Should().BeFalse();
        _insertedLedgers.Should().BeEmpty("削除・統合した行を台帳に戻さない");

        var error = result.Errors.Should().ContainSingle().Subject;
        error.LineNumber.Should().Be(2);
        error.Message.Should().Contain("利用履歴ID 50").And.Contain("台帳にありません")
            .And.Contain("削除").And.Contain("統合").And.Contain("利用履歴IDを空欄");
        AssertActionable(error.Message);
        preview.Errors.Should().ContainSingle().Which.Message.Should().Be(error.Message, "プレビューと取り込みで同じ判定・同じ文言");
    }

    /// <summary>
    /// 対: 同じ内容の行でも、利用履歴 ID を空欄にすれば新しい履歴として登録できる（意図して戻す手段を残す）。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_ID空欄の同じ内容の行は新しい履歴として登録できること()
    {
        var path = await WriteCsvAsync("ledger_id_blank.csv", LedgerCsvHeader,
            ",2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,");

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.NewCount.Should().Be(1);
        result.Success.Should().BeTrue();
        _insertedLedgers.Should().ContainSingle().Which.Summary.Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// 対: ID が台帳に無くても、スキップ指定ありで重複キーが既存の履歴と一致する行は台帳を変えないのでスキップする
    /// （#2307 の LeavesLedgerUnchanged と同じ考え方。プレビューと取り込みの件数も一致する）。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_IDが台帳に無くても重複キーが一致する行はスキップすること()
    {
        var path = await WriteCsvAsync("ledger_id_not_found_dup.csv", LedgerCsvHeader,
            "50,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>
            {
                (CardIdmA, new DateTime(2026, 10, 1), "鉄道（博多～天神）", 0, 260, 9740)
            });

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.SkipCount.Should().Be(1);
        preview.NewCount.Should().Be(0);
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        _insertedLedgers.Should().BeEmpty();
    }

    /// <summary>
    /// 統合の後で、統合前に出力した CSV を取り込み直す形。統合元（ID 2）は台帳に無いので拒否し、
    /// 全か無かの取り込みなので統合先（ID 1）も書き換えない。以前は統合元が別の ID で戻り、統合先と二重計上になった。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_統合前に出力したCSVを取り込み直しても統合元が戻らないこと()
    {
        var path = await WriteCsvAsync("ledger_before_merge.csv", LedgerCsvHeader,
            "1,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,",
            "2,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（天神～博多）,,260,9480,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1,
            CardIdm = CardIdmA,
            Date = new DateTime(2026, 10, 1),
            Summary = "鉄道（博多～天神 往復）",
            Expense = 520,
            Balance = 9480,
            StaffName = "山田太郎"
        });

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.LineNumber.Should().Be(3);
        _insertedLedgers.Should().BeEmpty();
        _ledgerRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never,
            "検証エラーがあれば統合先も書き換えない");
    }

    // ===== 利用履歴詳細 CSV: 利用履歴 ID が別のカードの履歴を指す行 =====

    private void SetupExistingLedgerOfCardA(int ledgerId)
    {
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(ledgerId)).ReturnsAsync(new Ledger
        {
            Id = ledgerId,
            CardIdm = CardIdmA,
            Date = new DateTime(2026, 10, 1),
            Summary = "鉄道（博多～天神）",
            Expense = 260,
            Balance = 9740,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { LedgerId = ledgerId, UseDate = new DateTime(2026, 10, 1, 8, 0, 0), EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 9740 }
            }
        });
        _ledgerRepositoryMock.Setup(x => x.ReplaceDetailsAsync(ledgerId, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(x => x.UpdateSummaryAndAmountsAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
    }

    /// <summary>
    /// 欠陥を突く側: 利用履歴 ID を書き間違えた明細 CSV（ID はカード A の履歴、行のカードIDm はカード B）で、
    /// カード A の履歴の明細・摘要・金額を置き換えない。プレビューも同じ文言のエラーを出す。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_IDが別のカードの履歴を指す行は明細を置き換えず拒否すること()
    {
        SetupExistingLedgerOfCardA(1);
        var path = await WriteCsvAsync("detail_card_mismatch.csv", DetailCsvHeader,
            "1,2026-10-01 08:00:00,FEDCBA9876543210,002,博多,中洲川端,,210,9790,0,0,0,");

        var preview = await _importService.PreviewLedgerDetailsAsync(path);
        var result = await _importService.ImportLedgerDetailsAsync(path);

        preview.IsValid.Should().BeFalse();
        result.Success.Should().BeFalse();
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()),
            Times.Never, "別のカードの履歴の明細を置き換えない");
        _ledgerRepositoryMock.Verify(x => x.UpdateSummaryAndAmountsAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<SQLiteTransaction>()), Times.Never);

        var error = result.Errors.Should().ContainSingle().Subject;
        error.LineNumber.Should().Be(2);
        error.Message.Should().Contain("利用履歴ID 1").And.Contain("「はやかけん 001」").And.Contain("カードIDm列を確認");
        error.Message.Should().NotContain("取り込み先カード", "利用履歴詳細の取り込みに取り込み先カードの指定は無い");
        AssertActionable(error.Message);
        preview.Errors.Should().ContainSingle().Which.Message.Should().Be(error.Message, "プレビューと取り込みで同じ判定・同じ文言");
    }

    /// <summary>
    /// 照合は ID ごとの最初の行だけでなく、すべての行で行う（2 行目だけが別のカードの行も拒否する）。
    /// </summary>
    [Fact]
    public async Task ImportLedgerDetailsAsync_同じIDの2行目だけが別のカードでも拒否すること()
    {
        SetupExistingLedgerOfCardA(1);
        var path = await WriteCsvAsync("detail_card_mismatch_second.csv", DetailCsvHeader,
            "1,2026-10-01 08:00:00,0123456789ABCDEF,001,博多,天神,,260,9740,0,0,0,",
            "1,2026-10-01 18:00:00,FEDCBA9876543210,002,天神,博多,,260,9480,0,0,0,");

        var preview = await _importService.PreviewLedgerDetailsAsync(path);
        var result = await _importService.ImportLedgerDetailsAsync(path);

        preview.Errors.Should().ContainSingle().Which.LineNumber.Should().Be(3);
        result.Errors.Should().ContainSingle().Which.LineNumber.Should().Be(3);
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()),
            Times.Never);
    }

    /// <summary>
    /// 対: 行のカードIDm が ID の履歴のカードと一致する（大文字小文字は問わない）行、カードIDm列が空欄の行
    /// （親の履歴の日付が出力期間の外だと、エクスポートは空欄で出力する）は、従来どおり明細を置き換える。
    /// </summary>
    [Theory]
    [InlineData("0123456789ABCDEF")]
    [InlineData("0123456789abcdef")]
    [InlineData("")]
    public async Task ImportLedgerDetailsAsync_カードが一致する行と空欄の行は従来どおり置き換えること(string rowCardIdm)
    {
        SetupExistingLedgerOfCardA(1);
        var path = await WriteCsvAsync("detail_card_match.csv", DetailCsvHeader,
            $"1,2026-10-01 08:00:00,{rowCardIdm},001,博多,中洲川端,,210,9790,0,0,0,");

        var preview = await _importService.PreviewLedgerDetailsAsync(path);
        var result = await _importService.ImportLedgerDetailsAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.UpdateCount.Should().Be(1);
        result.Success.Should().BeTrue();
        _ledgerRepositoryMock.Verify(x => x.ReplaceDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    // ===== 職員・カード CSV: 「削除済み」列 =====

    /// <summary>
    /// 欠陥を突く側（往復）: 「削除済みデータを含める」で出力したカード CSV をそのまま取り込んでも、削除済みのカードを復元しない。
    /// 対: 同じ CSV の有効なカードの行は従来どおり（全項目一致でスキップ）。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_削除済みを含めて出力したCSVを取り込んでも削除済みのカードを復元しないこと()
    {
        var active = new IcCard { CardIdm = CardIdmA, CardType = "はやかけん", CardNumber = "001" };
        var deleted = new IcCard { CardIdm = CardIdmB, CardType = "nimoca", CardNumber = "002", IsDeleted = true };
        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(new List<IcCard> { active, deleted });
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(CardIdmA, true)).ReturnsAsync(active);
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(CardIdmB, true)).ReturnsAsync(deleted);
        var path = Path.Combine(_testDirectory, "cards_with_deleted.csv");
        (await _exportService.ExportCardsAsync(path, includeDeleted: true)).Success.Should().BeTrue();
        File.ReadAllLines(path, Encoding.UTF8).Should().Contain(l => l.StartsWith(CardIdmB, StringComparison.Ordinal) && l.EndsWith(",1", StringComparison.Ordinal),
            "前提: 削除済みのカードは削除済み列「1」で出力される");

        var preview = await _importService.PreviewCardsAsync(path);
        var result = await _importService.ImportCardsAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.Items.Should().NotContain(i => i.Action == ImportAction.Restore, "削除済みの行を「復元」と表示しない");
        var deletedItem = preview.Items.Should().ContainSingle(i => i.Idm == CardIdmB).Subject;
        deletedItem.Action.Should().Be(ImportAction.Skip);
        deletedItem.Changes.Should().ContainSingle().Which.DisplayText.Should().Contain("削除済み").And.Contain("取り込みません");
        preview.SkipCount.Should().Be(2);
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(2);
        _cardRepositoryMock.Verify(x => x.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _cardRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _cardRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// 欠陥を突く側（往復）: 削除済みを含めて出力した職員 CSV で、削除済みの職員を復元しない（認証できる職員証を増やさない）。
    /// DB に無い職員の削除済みの行も、有効な職員として登録しない。
    /// </summary>
    [Fact]
    public async Task ImportStaffAsync_削除済みを含めて出力したCSVを取り込んでも削除済みの職員を復元も登録もしないこと()
    {
        var deleted = new Staff { StaffIdm = StaffIdm, Name = "山田太郎", Number = "001", IsDeleted = true };
        _staffRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync()).ReturnsAsync(new List<Staff> { deleted });
        var path = Path.Combine(_testDirectory, "staff_with_deleted.csv");
        (await _exportService.ExportStaffAsync(path, includeDeleted: true)).Success.Should().BeTrue();

        foreach (var dbHasDeletedRow in new[] { true, false })
        {
            _staffRepositoryMock.Setup(x => x.GetByIdmAsync(StaffIdm, true)).ReturnsAsync(dbHasDeletedRow ? deleted : null);

            var preview = await _importService.PreviewStaffAsync(path);
            var result = await _importService.ImportStaffAsync(path);

            preview.IsValid.Should().BeTrue();
            preview.Items.Should().ContainSingle().Which.Action.Should().Be(ImportAction.Skip);
            result.Success.Should().BeTrue();
            result.SkippedCount.Should().Be(1);
        }

        _staffRepositoryMock.Verify(x => x.RestoreAsync(It.IsAny<string>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _staffRepositoryMock.Verify(x => x.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    /// <summary>
    /// 対: 削除済み列を「0」（または空欄・列なし）にした行は、従来どおり削除済みの職員・カードを復元する（復元の手段を残す）。
    /// </summary>
    [Theory]
    [InlineData(",0")]
    [InlineData(",")]
    [InlineData("")]
    public async Task 削除済み列が1でない行は従来どおり削除済みの職員とカードを復元すること(string deletedColumn)
    {
        var deletedStaff = new Staff { StaffIdm = StaffIdm, Name = "山田太郎", Number = "001", IsDeleted = true };
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(StaffIdm, It.IsAny<bool>())).ReturnsAsync(deletedStaff);
        _staffRepositoryMock.Setup(x => x.RestoreAsync(StaffIdm, It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _staffRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        var deletedCard = new IcCard { CardIdm = CardIdmB, CardType = "nimoca", CardNumber = "002", IsDeleted = true };
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(CardIdmB, true)).ReturnsAsync(deletedCard);
        _cardRepositoryMock.Setup(x => x.RestoreAsync(CardIdmB, It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        _cardRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        var staffPath = await WriteCsvAsync("staff_restore.csv", "職員IDm,氏名,職員番号,備考,削除済み",
            $"{StaffIdm},山田太郎,001,{deletedColumn}");
        var cardPath = await WriteCsvAsync("card_restore.csv", "カードIDm,カード種別,管理番号,備考,削除済み",
            $"{CardIdmB},nimoca,002,{deletedColumn}");

        (await _importService.PreviewStaffAsync(staffPath)).Items.Should().ContainSingle().Which.Action.Should().Be(ImportAction.Restore);
        (await _importService.PreviewCardsAsync(cardPath)).Items.Should().ContainSingle().Which.Action.Should().Be(ImportAction.Restore);
        (await _importService.ImportStaffAsync(staffPath)).Success.Should().BeTrue();
        (await _importService.ImportCardsAsync(cardPath)).Success.Should().BeTrue();

        _staffRepositoryMock.Verify(x => x.RestoreAsync(StaffIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        _cardRepositoryMock.Verify(x => x.RestoreAsync(CardIdmB, It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    /// <summary>
    /// 削除済み列が「0」「1」・空欄以外の行は、どう扱うか決められないので行エラーにする（プレビュー・取り込みとも）。
    /// </summary>
    [Fact]
    public async Task 削除済み列の値が読み取れない行はエラーにすること()
    {
        var staffPath = await WriteCsvAsync("staff_bad_flag.csv", "職員IDm,氏名,職員番号,備考,削除済み",
            $"{StaffIdm},山田太郎,001,,はい");
        var cardPath = await WriteCsvAsync("card_bad_flag.csv", "カードIDm,カード種別,管理番号,備考,削除済み",
            $"{CardIdmB},nimoca,002,,2");

        var staffPreview = await _importService.PreviewStaffAsync(staffPath);
        var staffResult = await _importService.ImportStaffAsync(staffPath);
        var cardPreview = await _importService.PreviewCardsAsync(cardPath);
        var cardResult = await _importService.ImportCardsAsync(cardPath);

        staffPreview.IsValid.Should().BeFalse();
        cardPreview.IsValid.Should().BeFalse();
        var staffMessage = staffResult.Errors.Should().ContainSingle().Subject.Message;
        staffMessage.Should().Contain("削除済み列").And.Contain("「はい」").And.Contain("「1」").And.Contain("「0」");
        AssertActionable(staffMessage);
        cardResult.Errors.Should().ContainSingle().Which.Message.Should().Contain("「2」");
        _staffRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _cardRepositoryMock.Verify(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }
}
