using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerEditViewModelの単体テスト
/// Issue #636: 利用者を空欄にできる機能のテスト
/// </summary>
public class LedgerEditViewModelTests
{
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly Mock<IStaffRepository> _staffRepoMock;
    private readonly Mock<IOperationLogRepository> _operationLogRepoMock;
    private readonly OperationLogger _operationLogger;
    private readonly LedgerEditViewModel _viewModel;

    private readonly Staff _staffA = new Staff { StaffIdm = "AAAA000000000001", Name = "田中太郎" };
    private readonly Staff _staffB = new Staff { StaffIdm = "BBBB000000000002", Name = "山田花子" };

    public LedgerEditViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _staffRepoMock = new Mock<IStaffRepository>();
        _operationLogRepoMock = new Mock<IOperationLogRepository>();
        _operationLogger = new OperationLogger(
            _operationLogRepoMock.Object,
            _staffRepoMock.Object);

        _viewModel = new LedgerEditViewModel(
            _ledgerRepoMock.Object,
            _staffRepoMock.Object,
            _operationLogger);
    }

    /// <summary>
    /// テスト用にViewModelを初期化
    /// </summary>
    private async Task InitializeViewModelAsync(string? lenderIdm = null, string? staffName = null)
    {
        var ledger = new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            LenderIdm = lenderIdm,
            StaffName = staffName,
            Date = DateTime.Now,
            Summary = "鉄道（博多～天神）",
            Income = 0,
            Expense = 260,
            Balance = 740,
            Note = "テスト"
        };

        _ledgerRepoMock
            .Setup(r => r.GetByIdAsync(1))
            .ReturnsAsync(ledger);

        _staffRepoMock
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Staff> { _staffA, _staffB });

        // UpdateAsyncが呼ばれたら成功を返す
        _ledgerRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .ReturnsAsync(true);

        var dto = new LedgerDto
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            StaffName = staffName,
            Summary = "鉄道（博多～天神）",
            Note = "テスト",
            DateDisplay = "R8.2.10",
            Income = 0,
            Expense = 260,
            Balance = 740
        };

        await _viewModel.InitializeAsync(dto);
    }

    #region ClearStaffコマンドのテスト

    [Fact]
    public async Task ClearStaff_SetsSelectedStaffToNull()
    {
        // Arrange
        await InitializeViewModelAsync(_staffA.StaffIdm, _staffA.Name);
        _viewModel.SelectedStaff.Should().NotBeNull();

        // Act
        _viewModel.ClearStaffCommand.Execute(null);

        // Assert
        _viewModel.SelectedStaff.Should().BeNull();
    }

    [Fact]
    public async Task ClearStaff_WhenAlreadyNull_RemainsNull()
    {
        // Arrange
        await InitializeViewModelAsync(null, null);

        // Act
        _viewModel.ClearStaffCommand.Execute(null);

        // Assert
        _viewModel.SelectedStaff.Should().BeNull();
    }

    #endregion

    #region 保存時の利用者クリアのテスト

    [Fact]
    public async Task Save_ClearStaff_SetsLenderIdmAndStaffNameToNull()
    {
        // Arrange
        await InitializeViewModelAsync(_staffA.StaffIdm, _staffA.Name);
        _viewModel.ClearStaffCommand.Execute(null); // 利用者をクリア

        Ledger? savedLedger = null;
        _ledgerRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => savedLedger = l)
            .ReturnsAsync(true);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue();
        savedLedger.Should().NotBeNull();
        savedLedger!.LenderIdm.Should().BeNull("利用者をクリアしたのでLenderIdmはnull");
        savedLedger.StaffName.Should().BeNull("利用者をクリアしたのでStaffNameはnull");
    }

    [Fact]
    public async Task Save_ChangeStaffToAnother_UpdatesLenderIdmAndStaffName()
    {
        // Arrange
        await InitializeViewModelAsync(_staffA.StaffIdm, _staffA.Name);
        _viewModel.SelectedStaff = _staffB; // 別の職員に変更

        Ledger? savedLedger = null;
        _ledgerRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => savedLedger = l)
            .ReturnsAsync(true);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue();
        savedLedger.Should().NotBeNull();
        savedLedger!.LenderIdm.Should().Be(_staffB.StaffIdm);
        savedLedger.StaffName.Should().Be(_staffB.Name);
    }

    [Fact]
    public async Task Save_NoChanges_DoesNotCallUpdate()
    {
        // Arrange
        await InitializeViewModelAsync(_staffA.StaffIdm, _staffA.Name);
        // 何も変更しない

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue("変更なしでも保存成功扱い");
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never,
            "変更がない場合はUpdateAsyncが呼ばれない");
    }

    [Fact]
    public async Task Save_ClearStaffFromNull_DoesNotCallUpdate()
    {
        // Arrange: 元々利用者がnullの場合
        await InitializeViewModelAsync(null, null);
        _viewModel.ClearStaffCommand.Execute(null); // すでにnullなので変更なし

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.IsSaved.Should().BeTrue("変更なしでも保存成功扱い");
        _ledgerRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Ledger>()), Times.Never,
            "元々nullで変更がないのでUpdateAsyncが呼ばれない");
    }

    #endregion
}
