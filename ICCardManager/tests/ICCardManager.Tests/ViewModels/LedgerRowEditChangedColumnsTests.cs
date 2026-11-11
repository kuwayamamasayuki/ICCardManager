#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2375: 履歴の行編集は、開いたときの値から変えた列だけを書く。
/// </summary>
/// <remarks>
/// <para>
/// 実 SQLite（<see cref="LedgerRepository"/>）で、ダイアログを開いた後に「別の PC の変更」を SQL で直接書き込み、
/// 保存後の DB の値を読み返して表明する。モックのリポジトリでは、全列を書く実装でも渡す値が DB と同じなら区別できない。
/// </para>
/// <para>
/// 欠陥を突く側（開いた後に他 PC が直した列を巻き戻さない）と、正当な動作を塞いでいない側
/// （選び直した利用者・変えた列は書く／他 PC の変更が無ければ従来どおり保存できる／派生の変更も書く）を対で置く。
/// </para>
/// </remarks>
public sealed class LedgerRowEditChangedColumnsTests : IDisposable
{
    private const string TestCardIdm = "0102030405060708";

    private readonly Staff _staffX = new() { StaffIdm = "1111111111111111", Name = "博多 花子", Number = "001" };
    private readonly Staff _staffY = new() { StaffIdm = "2222222222222222", Name = "天神 太郎", Number = "002" };

    private readonly Mock<IStaffRepository> _staffRepoMock = new();
    private readonly Mock<IOperationLogRepository> _operationLogRepoMock = new();
    private readonly List<OperationLog> _operationLogs = new();
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly LedgerRowEditViewModel _viewModel;

    public LedgerRowEditChangedColumnsTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _ledgerRepository = new LedgerRepository(_dbContext);
        _staffRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(() => new List<Staff> { _staffX, _staffY });
        _operationLogRepoMock
            .Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Callback<OperationLog, SQLiteTransaction>((log, _) => _operationLogs.Add(log))
            .ReturnsAsync(1);
        _viewModel = new LedgerRowEditViewModel(
            _ledgerRepository,
            _staffRepoMock.Object,
            new OperationLogger(_operationLogRepoMock.Object, Mock.Of<ICurrentOperatorContext>()),
            _dbContext,
            Mock.Of<IDialogService>());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>利用者 X・備考ありの行を投入し、行編集を開く</summary>
    private async Task<int> SeedAndOpenAsync(
        string? lenderIdm = "1111111111111111", string? staffName = "博多 花子", string? note = "元の備考",
        int balance = 2300, int? previousBalance = null)
    {
        Execute(
            "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001');" +
            "INSERT OR IGNORE INTO staff (staff_idm, name, number) VALUES ('1111111111111111', '博多 花子', '001'), ('2222222222222222', '天神 太郎', '002');",
            ("@idm", TestCardIdm));
        var id = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            LenderIdm = lenderIdm,
            StaffName = staffName,
            Date = new DateTime(2026, 4, 17),
            Summary = "鉄道（博多～天神）",
            Expense = 210,
            Balance = balance,
            Note = note,
            CompanionCount = 1,
        });

        await _viewModel.InitializeForEditAsync(new LedgerDto
        {
            Id = id,
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 4, 17),
            DateDisplay = "R8.4.17",
            Summary = "鉄道（博多～天神）",
            Expense = 210,
            Balance = balance,
            StaffName = staffName,
        }, previousBalance);
        return id;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var lease = _dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>別の PC が利用者を職員 Y へ直した（ダイアログを開いた後）</summary>
    private void OtherPcChangesStaffToY(int id) =>
        Execute("UPDATE ledger SET lender_idm = @idm, staff_name = @name WHERE id = @id",
            ("@idm", _staffY.StaffIdm), ("@name", _staffY.Name), ("@id", id));

    private async Task<Ledger> ReadAsync(int id) => (await _ledgerRepository.GetByIdAsync(id))!;

    #region 欠陥を突く側: 開いた後に他 PC が直した列を巻き戻さない

    [Fact]
    public async Task 開いた後に他PCが利用者を直しても_備考だけ変えた保存は利用者を書き戻さないこと()
    {
        var id = await SeedAndOpenAsync();
        _viewModel.SelectedStaff.Should().BeSameAs(_staffX, "前提: 在籍職員 X の行として開いた");
        OtherPcChangesStaffToY(id);

        _viewModel.Note = "備考だけ直した";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeTrue();
        var stored = await ReadAsync(id);
        stored.Note.Should().Be("備考だけ直した");
        stored.LenderIdm.Should().Be(_staffY.StaffIdm, "利用者に触れていない保存は、他の PC が直した利用者を開いたときの職員へ戻さない");
        stored.StaffName.Should().Be(_staffY.Name);
    }

    [Fact]
    public async Task 利用者を巻き戻さない保存の監査ログに_起きていない利用者の変更が載らないこと()
    {
        var id = await SeedAndOpenAsync();
        OtherPcChangesStaffToY(id);

        _viewModel.Note = "備考だけ直した";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _operationLogs.Should().ContainSingle();
        _operationLogs[0].BeforeData.Should().Contain(_staffY.Name, "変更前は保存直前に読み直した行（他 PC の変更の後）");
        _operationLogs[0].AfterData.Should().Contain(_staffY.Name).And.NotContain(_staffX.Name,
            "この保存は利用者を変えていないので「Y → X」を記録しない");
    }

    [Fact]
    public async Task 開いた後に他PCが行編集の列をすべて直しても_備考だけ変えた保存は他の列を書き戻さないこと()
    {
        var id = await SeedAndOpenAsync();
        Execute(
            "UPDATE ledger SET lender_idm = @idm, staff_name = @name, date = '2026-04-18 00:00:00', summary = '鉄道（天神～博多）', " +
            "income = 100, expense = 300, balance = 2100, companion_count = 3 WHERE id = @id",
            ("@idm", _staffY.StaffIdm), ("@name", _staffY.Name), ("@id", id));

        _viewModel.Note = "備考だけ直した";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        var stored = await ReadAsync(id);
        stored.Note.Should().Be("備考だけ直した");
        stored.LenderIdm.Should().Be(_staffY.StaffIdm);
        stored.Date.Should().Be(new DateTime(2026, 4, 18));
        stored.Summary.Should().Be("鉄道（天神～博多）");
        stored.Income.Should().Be(100);
        stored.Expense.Should().Be(300);
        stored.Balance.Should().Be(2100);
        stored.CompanionCount.Should().Be(3);
    }

    [Fact]
    public async Task 開いた後に他PCが備考を直しても_金額だけ変えた保存は備考を書き戻さないこと()
    {
        var id = await SeedAndOpenAsync();
        Execute("UPDATE ledger SET note = '他のPCの備考' WHERE id = @id", ("@id", id));

        _viewModel.Expense = 230;
        _viewModel.Balance = 2280;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        var stored = await ReadAsync(id);
        stored.Expense.Should().Be(230);
        stored.Balance.Should().Be(2280);
        stored.Note.Should().Be("他のPCの備考");
        stored.LenderIdm.Should().Be(_staffX.StaffIdm);
    }

    #endregion

    #region 正当な動作を塞いでいない側

    [Fact]
    public async Task 利用者を選び直して保存すると新しい職員が書かれること()
    {
        var id = await SeedAndOpenAsync();

        _viewModel.SelectedStaff = _staffY;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        var stored = await ReadAsync(id);
        stored.LenderIdm.Should().Be(_staffY.StaffIdm);
        stored.StaffName.Should().Be(_staffY.Name);
        stored.Note.Should().Be("元の備考");
    }

    [Fact]
    public async Task 利用者をクリアして保存すると利用者が消えること()
    {
        var id = await SeedAndOpenAsync();

        _viewModel.ClearStaffCommand.Execute(null);
        await _viewModel.SaveCommand.ExecuteAsync(null);

        var stored = await ReadAsync(id);
        stored.LenderIdm.Should().BeNull();
        stored.StaffName.Should().BeNull();
    }

    [Fact]
    public async Task 他PCの変更が無ければ従来どおり変えた列が保存されること()
    {
        var id = await SeedAndOpenAsync();

        _viewModel.EditDate = new DateTime(2026, 4, 16);
        _viewModel.Summary = "鉄道（博多～天神 往復）";
        _viewModel.Expense = 420;
        _viewModel.Balance = 2090;
        _viewModel.Note = "直した備考";
        _viewModel.CompanionCount = 2;
        _viewModel.SelectedStaff = _staffY;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeTrue();
        var stored = await ReadAsync(id);
        stored.Date.Should().Be(new DateTime(2026, 4, 16));
        stored.Summary.Should().Be("鉄道（博多～天神 往復）");
        stored.Expense.Should().Be(420);
        stored.Balance.Should().Be(2090);
        stored.Note.Should().Be("直した備考");
        stored.CompanionCount.Should().Be(2);
        stored.LenderIdm.Should().Be(_staffY.StaffIdm);
        _operationLogs.Should().ContainSingle("変えた保存は従来どおり監査ログに残る");
    }

    [Fact]
    public async Task 何も変えずに保存すると何も書かず監査ログも残さずに閉じること()
    {
        var id = await SeedAndOpenAsync();
        OtherPcChangesStaffToY(id);

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeTrue("保存と同じく閉じる");
        _operationLogs.Should().BeEmpty("変更の無い記録を 6 年保存の監査ログに残さない");
        var stored = await ReadAsync(id);
        stored.LenderIdm.Should().Be(_staffY.StaffIdm, "他の PC の変更を開いたときの値へ戻さない");
        stored.Note.Should().Be("元の備考");
    }

    [Fact]
    public async Task 何も変えずに保存しても行が無ければ競合として案内すること()
    {
        // Issue #2325: 行が残っているかは保存直前の読み直しで確かめる（書かない保存でも同じ案内）
        var id = await SeedAndOpenAsync();
        Execute("DELETE FROM ledger WHERE id = @id", ("@id", id));

        await _viewModel.SaveCommand.ExecuteAsync(null);

        _viewModel.IsSaved.Should().BeFalse();
        _viewModel.NeedsHistoryReload.Should().BeTrue();
        _viewModel.CanSave.Should().BeFalse();
    }

    [Fact]
    public async Task 変えた後に元へ戻した列は書かないこと()
    {
        var id = await SeedAndOpenAsync();
        OtherPcChangesStaffToY(id);

        _viewModel.SelectedStaff = _staffY;
        _viewModel.SelectedStaff = _staffX; // 開いたときの職員へ戻した
        _viewModel.Note = "備考だけ直した";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        (await ReadAsync(id)).LenderIdm.Should().Be(_staffY.StaffIdm);
    }

    [Fact]
    public async Task 備考が空の行で他の列を変えても_備考を空文字で書かないこと()
    {
        // 画面は null の備考を空文字で表示する。null と空文字を同じ「未入力」として比べる
        var id = await SeedAndOpenAsync(note: null);

        _viewModel.CompanionCount = 0;
        await _viewModel.SaveCommand.ExecuteAsync(null);

        var stored = await ReadAsync(id);
        stored.CompanionCount.Should().Be(0);
        stored.Note.Should().BeNull();
    }

    #endregion

    #region 派生の変更（画面が自分で変えた値）も「変えた列」として書く

    [Fact]
    public async Task 自動計算で再計算した残額は_残額を直接変えていなくても書くこと()
    {
        // 直前の残額 2510 − 払出 210 = 2300 だが、行の残額は 2000（食い違っている）
        var id = await SeedAndOpenAsync(balance: 2000, previousBalance: 2510);

        _viewModel.IsAutoBalance = true;
        _viewModel.Balance.Should().Be(2300, "前提: 自動計算が残額を再計算した");
        await _viewModel.SaveCommand.ExecuteAsync(null);

        (await ReadAsync(id)).Balance.Should().Be(2300);
    }

    [Fact]
    public async Task 開いたときに氏名で職員へ紐づけた行は_利用者に触れなくても選択中の職員のIDmで書くこと()
    {
        // Issue #1303: 貸出者 IDm の無い行は氏名で在籍職員へ紐づける。従来どおり保存で IDm を補う（派生の変更）
        var id = await SeedAndOpenAsync(lenderIdm: null, staffName: "博多 花子");
        _viewModel.SelectedStaff.Should().BeSameAs(_staffX, "前提: 氏名で職員 X へ紐づいた");

        _viewModel.Note = "備考だけ直した";
        await _viewModel.SaveCommand.ExecuteAsync(null);

        var stored = await ReadAsync(id);
        stored.LenderIdm.Should().Be(_staffX.StaffIdm);
        stored.StaffName.Should().Be(_staffX.Name);
    }

    #endregion
}
