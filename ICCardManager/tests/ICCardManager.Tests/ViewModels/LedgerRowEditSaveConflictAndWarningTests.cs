using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2325: 履歴の行編集の保存の競合・「両方0円」警告の除外・追加モードの残高未入力。
/// </summary>
/// <remarks>
/// 組織設定（<see cref="SummaryGenerator.Configure"/>）を変えるテストを含むため
/// <see cref="SummaryGeneratorCollection"/> に編入し、前後で既定へ戻す。
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerRowEditSaveConflictAndWarningTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";
    private const string ZeroAmountWarningFragment = "受入と払出が両方0円です";

    private readonly Mock<ILedgerRepository> _ledgerRepoMock = new();
    private readonly Mock<IStaffRepository> _staffRepoMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepoMock = new();

    /// <summary>既定では ShowWarningConfirmation は false（＝「いいえ」）を返す</summary>
    private readonly Mock<IDialogService> _dialogServiceMock = new();

    private readonly DbContext _dbContext;
    private readonly LedgerRowEditViewModel _viewModel;

    public LedgerRowEditSaveConflictAndWarningTests()
    {
        SummaryGenerator.ResetToDefaults();
        _dbContext = TestDbContextFactory.Create();
        _staffRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Staff>());
        _viewModel = new LedgerRowEditViewModel(
            _ledgerRepoMock.Object,
            _staffRepoMock.Object,
            new OperationLogger(_operationLogRepoMock.Object, Mock.Of<ICurrentOperatorContext>()),
            _dbContext,
            _dialogServiceMock.Object);
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Ledger CreateLedger(string summary = "鉄道（天神～博多）", int income = 0, int expense = 210, bool isLentRecord = false) => new()
    {
        Id = 7,
        CardIdm = TestCardIdm,
        Date = new DateTime(2026, 1, 10),
        Summary = summary,
        Income = income,
        Expense = expense,
        Balance = 2300,
        IsLentRecord = isLentRecord,
    };

    private static LedgerDto ToDto(Ledger ledger) => new()
    {
        Id = ledger.Id,
        CardIdm = ledger.CardIdm,
        Date = ledger.Date,
        DateDisplay = "R8.1.10",
        Summary = ledger.Summary,
        Income = ledger.Income,
        Expense = ledger.Expense,
        Balance = ledger.Balance,
        IsLentRecord = ledger.IsLentRecord,
    };

    private const string ExpectedTarget = "履歴「R8.1.10 鉄道（天神～博多）」";

    private static List<LedgerDto> CreateListedLedgers() => new()
    {
        new LedgerDto { Id = 1, CardIdm = TestCardIdm, Date = new DateTime(2026, 1, 10), DateDisplay = "R8.1.10", Summary = "鉄道（天神～博多）", Expense = 210, Balance = 2300 },
        new LedgerDto { Id = 2, CardIdm = TestCardIdm, Date = new DateTime(2026, 1, 11), DateDisplay = "R8.1.11", Summary = "鉄道（博多～天神）", Expense = 210, Balance = 2090 },
    };

    #region 保存の競合（影響行数 0）

    [Fact]
    public async Task 保存で更新が0行なら_競合の文言で案内し_閉じたら一覧の再読込を求めること()
    {
        var ledger = CreateLedger();
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(ledger.Id)).ReturnsAsync(() => CreateLedger());
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2510);

        _viewModel.Note = "備考を追加";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.StatusMessage.Should().Be(
            ConcurrencyConflictMessage.ForUpdateReloadOnClose(ExpectedTarget, "履歴一覧"));
        _viewModel.NeedsHistoryReload.Should().BeTrue("閉じたら呼び出し元が履歴一覧を読み直す");
        _viewModel.CanSave.Should().BeFalse("同じ行は何度保存しても失敗する");
        _viewModel.CanClose(() => throw new InvalidOperationException("破棄の確認を出してはいけない"))
            .Should().BeTrue("保存できない入力を未保存の変更として残さない");
    }

    [Fact]
    public async Task 保存直前の読み直しで行が無いときも_同じ競合の案内になること()
    {
        var ledger = CreateLedger(summary: "（貸出中）", expense: 0, isLentRecord: true);
        _ledgerRepoMock.SetupSequence(r => r.GetByIdAsync(ledger.Id))
            .ReturnsAsync(CreateLedger(summary: "（貸出中）", expense: 0, isLentRecord: true))
            .ReturnsAsync((Ledger?)null); // 開いている間に返却で貸出中レコードが消えた
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2300);

        _viewModel.Note = "備考";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.Should().Be(
            ConcurrencyConflictMessage.ForUpdateReloadOnClose("履歴「R8.1.10 （貸出中）」", "履歴一覧"));
        _viewModel.NeedsHistoryReload.Should().BeTrue();
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
    }

    [Fact]
    public async Task 競合の後に入力を直しても保存は無効のままであること()
    {
        var ledger = CreateLedger();
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(ledger.Id)).ReturnsAsync(() => CreateLedger());
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2510);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.Expense = 220; // Validate が走る

        _viewModel.CanSave.Should().BeFalse();
        _viewModel.CanClose(() => throw new InvalidOperationException("破棄の確認を出してはいけない")).Should().BeTrue();
    }

    [Fact]
    public async Task 競合の後は次へと戻るを押せないこと()
    {
        var ledger = CreateLedger();
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(ledger.Id)).ReturnsAsync(() => CreateLedger());
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(false);
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2510);
        _viewModel.SkipToNextCommand.CanExecute(null).Should().BeTrue("競合の前は押せる（対）");
        _viewModel.BackCommand.CanExecute(null).Should().BeTrue();

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.SkipToNextCommand.CanExecute(null).Should().BeFalse("閉じると一覧を読み直し、隣の行へは進まない");
        _viewModel.BackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task バス停名の同期の競合でも_閉じたら一覧の再読込を求め_保存は塞がないこと()
    {
        var ledger = CreateLedger(summary: "バス（天神～博多）");
        ledger.Details = new List<LedgerDetail> { new() { LedgerId = 7, SequenceNumber = 1, IsBus = true, BusStops = "天神～博多" } };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(ledger.Id)).ReturnsAsync(() =>
        {
            var l = CreateLedger(summary: "バス（天神～博多）");
            l.Details = new List<LedgerDetail> { new() { LedgerId = 7, SequenceNumber = 1, IsBus = true, BusStops = "天神～博多" } };
            return l;
        });
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        // 明細の id が振り直されている（実 DB に該当する明細が無い）ので、同期の UPDATE は 0 行になる
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2510);

        _viewModel.Summary = "バス（天神～博多駅前）";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.StatusMessage.Should().Be(LedgerRowEditViewModel.BusStopConflictMessage);
        _viewModel.StatusMessage.Should().Contain("この画面を閉じると履歴一覧を再読み込みします");
        _viewModel.NeedsHistoryReload.Should().BeTrue();
        _viewModel.CanSave.Should().BeTrue("行は残っているので、読み直した明細で保存し直せる");
        _viewModel.SkipToNextCommand.CanExecute(null).Should().BeFalse("閉じると一覧を読み直し、隣の行へは進まない");
        _viewModel.BackCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// バス停名の競合の後に保存し直して成功したら、競合の印を下ろす（コードレビュー 2 回目）。
    /// 残すと呼び出し元が「保存して次へ」を競合の再読込として扱い、次の行を開かない。
    /// </summary>
    [Fact]
    public async Task バス停名の同期の競合の後に保存し直して成功したら_再読込の要求を下ろし保存して次へへ進むこと()
    {
        var ledger = CreateLedger(summary: "バス（天神～博多）");
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(ledger.Id)).ReturnsAsync(() =>
        {
            var l = CreateLedger(summary: "バス（天神～博多）");
            l.Details = new List<LedgerDetail> { new() { LedgerId = 7, SequenceNumber = 1, IsBus = true, BusStops = "天神～博多" } };
            return l;
        });
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2510);
        _viewModel.Summary = "バス（天神～博多駅前）";
        await _viewModel.SaveAndEditNextCommand.ExecuteAsync(null);
        _viewModel.NeedsHistoryReload.Should().BeTrue("前提: 1 回目は競合");

        // バス停名を元に戻して（同期が要らない）保存し直す
        _viewModel.Summary = "バス（天神～博多）";
        _viewModel.Note = "備考";
        await _viewModel.SaveAndEditNextCommand.ExecuteAsync(null);

        _viewModel.IsSaveAndEditNextRequested.Should().BeTrue();
        _viewModel.NeedsHistoryReload.Should().BeFalse("保存できたので競合の再読込ではない");
        _viewModel.SkipToNextCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task 保存できたときは再読込の要求を立てないこと()
    {
        var ledger = CreateLedger();
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(ledger.Id)).ReturnsAsync(() => CreateLedger());
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(true);
        await _viewModel.InitializeForEditAsync(ToDto(ledger), previousBalance: 2510);

        _viewModel.Note = "備考を追加";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeTrue();
        _viewModel.NeedsHistoryReload.Should().BeFalse();
        _viewModel.StatusMessage.Should().BeEmpty();
    }

    [Fact]
    public void 閉じたあと再読込する経路の文言は_再読込済みと述べないこと()
    {
        var message = ConcurrencyConflictMessage.ForUpdateReloadOnClose("履歴「R8.1.10 鉄道」", "履歴一覧");

        message.Should().Contain("更新できませんでした").And.Contain("削除された可能性があります");
        message.Should().Contain("閉じると履歴一覧を再読み込みします");
        message.Should().NotContain("再読み込みしましたので", "閉じる前の時点では読み直していない");
    }

    [Fact]
    public void 履歴パネルは_行編集の競合で閉じたら一覧を読み直すこと()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(),
            "ViewModels", "HistoryPanel", "HistoryPanelViewModel.Edit.cs");
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

        // 「NeedsHistoryReload を見る分岐」と、その分岐の中で一覧を読み直すことを対で表明する
        Regex.IsMatch(code,
                @"capturedEditDialog\?\.NeedsHistoryReload\s*==\s*true\s*\)\s*\{\s*await\s+LoadHistoryLedgersAsync\(\)",
                RegexOptions.Singleline)
            .Should().BeTrue("競合の案内は「閉じると履歴一覧を再読み込みします」と述べている");

        // 保存できたとき（dialogResult == true）は、競合の再読込より保存の分岐を優先する（「保存して次へ」へ進む）
        Regex.IsMatch(code, @"dialogResult\s*!=\s*true\s*&&\s*capturedEditDialog\?\.NeedsHistoryReload\s*==\s*true")
            .Should().BeTrue("競合の後に保存し直して成功した場合に、再読込の分岐が保存の分岐を奪わない");
    }

    #endregion

    #region 「両方0円」警告の除外は組織設定に追従する

    private async Task OpenZeroAmountRowAsync(string summary, bool isLentRecord = false, params bool[] detailIsPointRedemption)
    {
        Ledger Create()
        {
            var l = CreateLedger(summary: summary, expense: 0, isLentRecord: isLentRecord);
            l.Details = detailIsPointRedemption
                .Select((p, i) => new LedgerDetail { LedgerId = l.Id, SequenceNumber = i + 1, IsPointRedemption = p })
                .ToList();
            return l;
        }

        _ledgerRepoMock.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(() => Create());
        await _viewModel.InitializeForEditAsync(ToDto(Create()), previousBalance: 2300);
    }

    [Fact]
    public async Task ポイント還元の文言を設定で変えても_その文言の行に両方0円の警告を出さないこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.PointRedemption = "ポイント充当";
        SummaryGenerator.Configure(options);

        await OpenZeroAmountRowAsync("ポイント充当");

        _viewModel.WarningMessage.Should().NotContain(ZeroAmountWarningFragment);
    }

    /// <summary>
    /// 文言を変える前に保存された行（旧文言）を、現在の設定値だけで判定しない（#2044。コードレビューで検出）。
    /// 明細の無い旧文言の行は、設定の既定値の文言で除外する。
    /// </summary>
    [Fact]
    public async Task 設定を変える前に保存された既定の文言のポイント還元には_両方0円の警告を出さないこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.PointRedemption = "ポイント充当";
        SummaryGenerator.Configure(options);

        await OpenZeroAmountRowAsync("ポイント還元");

        _viewModel.WarningMessage.Should().NotContain(ZeroAmountWarningFragment);
    }

    [Fact]
    public async Task 明細がすべてポイント還元の行は_摘要の文言に依らず両方0円の警告を出さないこと()
    {
        await OpenZeroAmountRowAsync("ポイント利用分", false, true, true);

        _viewModel.WarningMessage.Should().NotContain(ZeroAmountWarningFragment);
    }

    [Fact]
    public async Task 明細にポイント還元以外が混ざる行は_摘要が還元でなければ両方0円の警告を出すこと()
    {
        await OpenZeroAmountRowAsync("ポイント利用分", false, true, false);

        _viewModel.WarningMessage.Should().Contain(ZeroAmountWarningFragment);
    }

    [Fact]
    public async Task 前年度繰越の文言を設定で変えても_その行に両方0円の警告を出さないこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromPreviousYear = "前年より持越";
        SummaryGenerator.Configure(options);

        await OpenZeroAmountRowAsync("前年より持越");

        _viewModel.WarningMessage.Should().NotContain(ZeroAmountWarningFragment);
    }

    [Fact]
    public async Task 貸出中レコードには_両方0円の警告を出さないこと()
    {
        await OpenZeroAmountRowAsync("（貸出中）", isLentRecord: true);

        _viewModel.WarningMessage.Should().NotContain(ZeroAmountWarningFragment);
    }

    [Fact]
    public async Task 通常の利用の行が両方0円なら_警告を出すこと()
    {
        await OpenZeroAmountRowAsync("鉄道（天神～博多）");

        _viewModel.WarningMessage.Should().Contain(ZeroAmountWarningFragment);
    }

    /// <summary>
    /// 摘要をリテラルと比べる形（<c>Summary == "…"</c> / <c>Summary.Contains("…")</c> 等）が本体に増えないこと。
    /// </summary>
    /// <remarks>
    /// 摘要の文言は組織設定で変えられる（#1818）。経路ごとのテストは経路の追加に追随できないため
    /// 静的検査を対で置く（#1764）。例外は組織設定に無い固定の文言「新規購入」を <c>Models/Ledger.cs</c>
    /// （<c>IsCarryover</c> / <c>IsInitialRecordSummary</c>）で比べることだけ。
    /// <para>
    /// 限界: 検出は 1 行の中の字句の形による。摘要を別名の変数（<c>var s = ledger.Summary;</c>）へ移してから比べる形、
    /// 比較が複数行にまたがる形、<c>switch</c> の <c>case "…":</c>・パターン（<c>is "…"</c>）・<c>Regex</c> での照合は検出しない。
    /// </para>
    /// </remarks>
    [Fact]
    public void 本体ソースが摘要をリテラルと比べていないこと()
    {
        var sourceRoot = TestPaths.GetProductionSourceRoot();
        var files = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Replace('\\', '/').Contains("/obj/") && !p.Replace('\\', '/').Contains("/bin/"))
            .ToList();
        files.Should().HaveCountGreaterThan(100, "走査対象が縮んで空振りしないこと");

        var violations = new List<string>();
        foreach (var path in files)
        {
            var lines = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var literal in FindSummaryLiteralComparisons(lines[i]))
                {
                    if (!(literal == "新規購入" && IsAllowedFixedSummaryFile(sourceRoot, path)))
                    {
                        violations.Add($"{Path.GetFileName(path)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "摘要の文言は組織設定から導出した判定（SummaryGenerator.Get…Summary / Ledger.IsInitialRecordSummary 等）で比べること");
    }

    private static bool IsAllowedFixedSummaryFile(string sourceRoot, string path) =>
        string.Equals(
            path.Substring(sourceRoot.Length).TrimStart('\\', '/').Replace('\\', '/'),
            "Models/Ledger.cs",
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void 新規購入の例外はLedgerだけに効くこと()
    {
        var root = TestPaths.GetProductionSourceRoot();
        IsAllowedFixedSummaryFile(root, Path.Combine(root, "Models", "Ledger.cs")).Should().BeTrue();
        IsAllowedFixedSummaryFile(root, Path.Combine(root, "ViewModels", "LedgerRowEditViewModel.cs")).Should().BeFalse();
    }

    [Fact]
    public void 行編集は_ポイント還元と導入行の判定を設定由来の判定へ委譲していること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "ViewModels", "LedgerRowEditViewModel.cs");
        var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));

        code.Should().Contain("SummaryGenerator.GetPointRedemptionSummary()");
        code.Should().Contain("Ledger.IsInitialRecordSummary(Summary)");
    }

    [Theory]
    [InlineData("var x = Summary == \"ポイント還元\";", "ポイント還元")]
    [InlineData("if (Summary.Contains(\"繰越\"))", "繰越")]
    [InlineData("if (ledger.Summary != \"（貸出中）\")", "（貸出中）")]
    [InlineData("summary?.StartsWith(\"鉄道\")", "鉄道")]
    [InlineData("if (\"ポイント還元\" == Summary)", "ポイント還元")]
    [InlineData("if (newSummary == \"（貸出中）\")", "（貸出中）")]
    [InlineData("string.Equals(ledger.Summary, \"繰越\")", "繰越")]
    [InlineData("summary.IndexOf(\"繰越\") >= 0", "繰越")]
    public void 摘要のリテラル比較を検出すること(string line, string expected)
    {
        FindSummaryLiteralComparisons(line).Should().Equal(expected);
    }

    [Theory]
    [InlineData("var x = Summary == SummaryGenerator.GetPointRedemptionSummary();")]
    [InlineData("ValidationMessage = \"摘要が空です。\";")]
    [InlineData("var summaryText = \"鉄道\";")]
    [InlineData("SummaryText == \"x\"")]
    [InlineData("if (\"x\" == SummaryText)")]
    [InlineData("string.Equals(summaryText, \"x\")")]
    public void 摘要のリテラル比較でない形は検出しないこと(string line)
    {
        FindSummaryLiteralComparisons(line).Should().BeEmpty();
    }

    /// <summary>
    /// 摘要（名前が <c>Summary</c> / <c>summary</c> で終わる識別子）をリテラルと比べる形。
    /// ①<c>X == "…"</c>・<c>X.Contains("…")</c> 等（StartsWith / EndsWith / Equals / IndexOf） ②<c>"…" == X</c> ③<c>string.Equals(X, "…")</c>
    /// </summary>
    private static readonly Regex[] SummaryLiteralComparisons =
    {
        new(@"\b\w*[Ss]ummary\??(?:\s*(?:==|!=)\s*|\.(?:Contains|StartsWith|EndsWith|Equals|IndexOf)\(\s*)""([^""]*)""", RegexOptions.Compiled),
        new(@"""([^""]*)""\s*(?:==|!=)\s*[\w.?]*\b\w*[Ss]ummary\b(?!\s*\()", RegexOptions.Compiled),
        new(@"\b[Ss]tring\.Equals\(\s*[\w.?]*\b\w*[Ss]ummary\s*,\s*""([^""]*)""", RegexOptions.Compiled),
    };

    private static IEnumerable<string> FindSummaryLiteralComparisons(string line) =>
        SummaryLiteralComparisons
            .SelectMany(r => r.Matches(line).Cast<System.Text.RegularExpressions.Match>())
            .Select(m => m.Groups[1].Value)
            .ToList();

    #endregion

    #region 追加モード: 自動計算が使えなくなったときの残高

    private async Task InitAddAtTopAsync()
    {
        await _viewModel.InitializeForAddAsync(TestCardIdm, CreateListedLedgers(), historyStartsAtCardBeginning: false);
        _viewModel.Summary = "鉄道（六本松～天神）";
        _viewModel.Expense = 200;
        _viewModel.Balance.Should().Be(2090 - 200, "末尾では直前行から自動計算される");
        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.InsertIndex.Should().Be(0);
        _viewModel.CanAutoBalance.Should().BeFalse();
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(100);
    }

    [Fact]
    public async Task 先頭へ動かして自動計算が使えなくなったら_前の位置の計算値を残さず未入力の警告を出すこと()
    {
        await InitAddAtTopAsync();

        _viewModel.IsAutoBalance.Should().BeFalse();
        _viewModel.Balance.Should().Be(0, "前の位置で計算した残高を今の位置の値として見せない");
        _viewModel.WarningMessage.Should().Contain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
    }

    [Fact]
    public async Task 残高が未入力のまま保存しようとしたら確認し_いいえなら書き込まないこと()
    {
        await InitAddAtTopAsync();
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "残高の確認")).Returns(false);

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(It.IsAny<string>(), "残高の確認"), Times.Once);
        _ledgerRepoMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Never);
        _viewModel.IsSaved.Should().BeFalse();
    }

    [Fact]
    public async Task 残高が未入力でも確認ではいを選べば保存できること()
    {
        await InitAddAtTopAsync();
        _dialogServiceMock.Setup(d => d.ShowWarningConfirmation(It.IsAny<string>(), "残高の確認")).Returns(true);

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _ledgerRepoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l => l.Balance == 0), It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.IsSaved.Should().BeTrue();
    }

    [Fact]
    public async Task 残高を入力すれば_警告も確認も出さずに保存すること()
    {
        await InitAddAtTopAsync();

        _viewModel.Balance = 2510;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.WarningMessage.Should().NotContain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _ledgerRepoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l => l.Balance == 2510), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    [Fact]
    public async Task 自動計算が使える位置へ戻して自動計算をONにすれば_未入力の扱いが解けること()
    {
        await InitAddAtTopAsync();

        _viewModel.MoveInsertPositionDownCommand.Execute(null);
        _viewModel.IsAutoBalance = true;

        _viewModel.Balance.Should().Be(2300 - 200);
        _viewModel.WarningMessage.Should().NotContain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
    }

    [Fact]
    public async Task 未入力のまま自動計算が使える位置へ戻しただけで_自動計算がONに戻り警告も確認も出さないこと()
    {
        await InitAddAtTopAsync();

        _viewModel.MoveInsertPositionDownCommand.Execute(null);

        _viewModel.IsAutoBalance.Should().BeTrue("起点が分かる位置で「自動計算できません」と案内しない");
        _viewModel.Balance.Should().Be(2300 - 200);
        _viewModel.WarningMessage.Should().NotContain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
        await _viewModel.SaveCommand.ExecuteAsync(null);
        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);

        // もう一度先頭へ動かせば、また未入力へ戻る（戻した時点の 0 を「復元する値」として扱わない）
        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.Balance.Should().Be(0);
        _viewModel.WarningMessage.Should().Contain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
    }

    [Fact]
    public async Task 残高を入力してから使える位置へ戻しても_入力した残高を残すこと()
    {
        await InitAddAtTopAsync();
        _viewModel.Balance = 2510;

        _viewModel.MoveInsertPositionDownCommand.Execute(null);

        _viewModel.IsAutoBalance.Should().BeFalse("利用者が入力した値を自動計算で上書きしない");
        _viewModel.Balance.Should().Be(2510);
    }

    [Fact]
    public async Task OFFにして入力した残高からONにしてから先頭へ動かすと_入力した残高を復元すること()
    {
        await _viewModel.InitializeForAddAsync(TestCardIdm, CreateListedLedgers(), historyStartsAtCardBeginning: false);
        _viewModel.Summary = "鉄道（六本松～天神）";
        _viewModel.Expense = 200;
        _viewModel.IsAutoBalance = false;
        _viewModel.Balance = 1234;
        _viewModel.IsAutoBalance = true;
        _viewModel.Balance.Should().Be(1890);

        _viewModel.MoveInsertPositionUpCommand.Execute(null);
        _viewModel.MoveInsertPositionUpCommand.Execute(null);

        _viewModel.IsAutoBalance.Should().BeFalse();
        _viewModel.Balance.Should().Be(1234, "OFF に戻したときは ON にする直前の残高を復元する（§3.12）");
        _viewModel.WarningMessage.Should().NotContain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
    }

    [Fact]
    public async Task 自動計算が使える位置での保存では確認を出さないこと()
    {
        await _viewModel.InitializeForAddAsync(TestCardIdm, CreateListedLedgers(), historyStartsAtCardBeginning: false);
        _viewModel.Summary = "鉄道（六本松～天神）";
        _viewModel.Expense = 200;
        _ledgerRepoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>())).ReturnsAsync(100);

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _dialogServiceMock.Verify(d => d.ShowWarningConfirmation(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _ledgerRepoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(l => l.Balance == 1890), It.IsAny<SQLiteTransaction>()), Times.Once);
    }

    [Fact]
    public async Task 利用者が自分で自動計算をOFFにしたときは_計算値を残すこと()
    {
        await _viewModel.InitializeForAddAsync(TestCardIdm, CreateListedLedgers(), historyStartsAtCardBeginning: false);
        _viewModel.Expense = 200;

        _viewModel.IsAutoBalance = false;

        _viewModel.Balance.Should().Be(1890, "手で微調整するための OFF で値を消さない");
        _viewModel.WarningMessage.Should().NotContain(LedgerRowEditViewModel.AddBalanceNotEnteredWarning);
    }

    #endregion
}
