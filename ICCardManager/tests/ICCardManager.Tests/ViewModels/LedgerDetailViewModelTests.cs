using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerDetailViewModelの単体テスト
/// Issue #633: 分割操作でGroupIdが正しく設定されることを検証
/// </summary>
public class LedgerDetailViewModelTests
{
    private readonly LedgerDetailViewModel _viewModel;

    public LedgerDetailViewModelTests()
    {
        var ledgerRepoMock = new Mock<ILedgerRepository>();
        var summaryGenerator = new SummaryGenerator();
        var operationLogRepoMock = new Mock<IOperationLogRepository>();
        var staffRepoMock = new Mock<IStaffRepository>();
        var operationLogger = new OperationLogger(
            operationLogRepoMock.Object,
            staffRepoMock.Object);
        var splitServiceLogger = NullLogger<LedgerSplitService>.Instance;
        var ledgerSplitService = new LedgerSplitService(
            ledgerRepoMock.Object,
            summaryGenerator,
            operationLogger,
            splitServiceLogger);
        var logger = NullLogger<LedgerDetailViewModel>.Instance;

        _viewModel = new LedgerDetailViewModel(
            ledgerRepoMock.Object,
            summaryGenerator,
            operationLogger,
            ledgerSplitService,
            logger);
    }

    /// <summary>
    /// テスト用にItemsを直接追加するヘルパー
    /// </summary>
    private void AddItems(int count)
    {
        _viewModel.Items.Clear();
        for (int i = 0; i < count; i++)
        {
            var detail = new LedgerDetail
            {
                EntryStation = $"駅{i * 2 + 1}",
                ExitStation = $"駅{i * 2 + 2}",
                UseDate = new DateTime(2026, 2, 10, 10 + i, 0, 0),
                Balance = 1000 - (i * 260),
                Amount = 260,
                SequenceNumber = i + 1
            };
            _viewModel.Items.Add(new LedgerDetailItemViewModel(detail, i));
        }
    }

    #region ToggleDividerAt テスト

    [Fact]
    public void ToggleDividerAt_TwoItems_BothGetGroupId()
    {
        // Arrange
        AddItems(2);

        // Act: 1番目のアイテムの下に分割線を挿入
        _viewModel.ToggleDividerAt(0);

        // Assert: 分割線があるため、両方にGroupIdが設定される
        _viewModel.Items[0].GroupId.Should().NotBeNull("分割線がある場合、単独アイテムにもGroupIdが付与される");
        _viewModel.Items[1].GroupId.Should().NotBeNull("分割線がある場合、単独アイテムにもGroupIdが付与される");
        _viewModel.Items[0].GroupId.Should().NotBe(_viewModel.Items[1].GroupId,
            "分割されたアイテムは異なるGroupIdを持つ");
    }

    [Fact]
    public void ToggleDividerAt_ThreeItems_SplitAfterFirst_CorrectGroupIds()
    {
        // Arrange
        AddItems(3);

        // Act: 1番目のアイテムの下に分割線を挿入
        _viewModel.ToggleDividerAt(0);

        // Assert
        // Item 0: GroupId=1（単独グループ）
        _viewModel.Items[0].GroupId.Should().Be(1, "1番目のアイテムは独立したグループ");
        // Item 1, 2: GroupId=2（同じグループ）
        _viewModel.Items[1].GroupId.Should().Be(2, "2番目と3番目は同じグループ");
        _viewModel.Items[2].GroupId.Should().Be(2, "2番目と3番目は同じグループ");
    }

    [Fact]
    public void ToggleDividerAt_Toggle_RemovesDivider_ClearsGroupIds()
    {
        // Arrange
        AddItems(2);
        _viewModel.ToggleDividerAt(0); // 分割線を挿入

        // Act: もう一度トグルして分割線を削除
        _viewModel.ToggleDividerAt(0);

        // Assert: 分割線がなくなったのでGroupIdはnull（自動検出モード）
        _viewModel.Items[0].GroupId.Should().BeNull("分割線なしではGroupIdはnull");
        _viewModel.Items[1].GroupId.Should().BeNull("分割線なしではGroupIdはnull");
    }

    #endregion

    #region SplitAll テスト

    [Fact]
    public void SplitAll_ThreeItems_AllGetUniqueGroupIds()
    {
        // Arrange
        AddItems(3);

        // Act
        _viewModel.SplitAllCommand.Execute(null);

        // Assert: 全アイテムにGroupIdが付与される
        _viewModel.Items[0].GroupId.Should().Be(1);
        _viewModel.Items[1].GroupId.Should().Be(2);
        _viewModel.Items[2].GroupId.Should().Be(3);
    }

    [Fact]
    public void SplitAll_TwoItems_BothGetDistinctGroupIds()
    {
        // Arrange
        AddItems(2);

        // Act
        _viewModel.SplitAllCommand.Execute(null);

        // Assert
        _viewModel.Items[0].GroupId.Should().NotBeNull();
        _viewModel.Items[1].GroupId.Should().NotBeNull();
        _viewModel.Items[0].GroupId.Should().NotBe(_viewModel.Items[1].GroupId);
    }

    #endregion

    #region MergeAll テスト

    [Fact]
    public void MergeAll_AfterSplit_ClearsAllGroupIds()
    {
        // Arrange
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        // すべてにGroupIdが設定されていることを確認
        _viewModel.Items.All(i => i.GroupId.HasValue).Should().BeTrue();

        // Act: すべてを統合
        _viewModel.MergeAllCommand.Execute(null);

        // Assert: 分割線がないのでGroupIdはすべてnull（自動検出モード）
        _viewModel.Items[0].GroupId.Should().BeNull("統合後はGroupIdがクリアされる");
        _viewModel.Items[1].GroupId.Should().BeNull("統合後はGroupIdがクリアされる");
        _viewModel.Items[2].GroupId.Should().BeNull("統合後はGroupIdがクリアされる");
    }

    #endregion

    #region HasChanges テスト

    [Fact]
    public void ToggleDividerAt_SetsHasChanges()
    {
        // Arrange
        AddItems(2);
        _viewModel.HasChanges.Should().BeFalse();

        // Act
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
    }

    #endregion

    #region Issue #634: HasMultipleGroups テスト

    [Fact]
    public void ToggleDividerAt_TwoItems_HasMultipleGroupsIsTrue()
    {
        // Arrange
        AddItems(2);
        _viewModel.HasMultipleGroups.Should().BeFalse("初期状態ではfalse");

        // Act: 分割線を挿入して2グループにする
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.HasMultipleGroups.Should().BeTrue("2グループある場合はtrue");
    }

    [Fact]
    public void MergeAll_HasMultipleGroupsIsFalse()
    {
        // Arrange: まず分割してMultipleGroupsをtrueにする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasMultipleGroups.Should().BeTrue();

        // Act: すべて統合
        _viewModel.MergeAllCommand.Execute(null);

        // Assert
        _viewModel.HasMultipleGroups.Should().BeFalse("統合後はfalse");
    }

    [Fact]
    public void ToggleDividerAt_RemoveDivider_HasMultipleGroupsReturnsFalse()
    {
        // Arrange
        AddItems(2);
        _viewModel.ToggleDividerAt(0);
        _viewModel.HasMultipleGroups.Should().BeTrue();

        // Act: 分割線を削除
        _viewModel.ToggleDividerAt(0);

        // Assert
        _viewModel.HasMultipleGroups.Should().BeFalse("分割線を外すとfalseに戻る");
    }

    #endregion

    #region Issue #635: 行選択テスト

    [Fact]
    public void SelectItem_SetsSelectedItemAndDeselectsPrevious()
    {
        // Arrange
        AddItems(3);

        // Act: 1番目を選択
        _viewModel.SelectItemCommand.Execute(_viewModel.Items[0]);

        // Assert
        _viewModel.SelectedItem.Should().Be(_viewModel.Items[0]);
        _viewModel.HasSelectedItem.Should().BeTrue();
        _viewModel.Items[0].IsSelected.Should().BeTrue();

        // Act: 2番目を選択
        _viewModel.SelectItemCommand.Execute(_viewModel.Items[1]);

        // Assert: 前の選択が解除され、新しい選択が設定される
        _viewModel.SelectedItem.Should().Be(_viewModel.Items[1]);
        _viewModel.Items[0].IsSelected.Should().BeFalse("前の選択が解除される");
        _viewModel.Items[1].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectItem_Null_ClearsSelection()
    {
        // Arrange
        AddItems(2);
        _viewModel.SelectItemCommand.Execute(_viewModel.Items[0]);

        // Act
        _viewModel.SelectItemCommand.Execute(null);

        // Assert
        _viewModel.SelectedItem.Should().BeNull();
        _viewModel.HasSelectedItem.Should().BeFalse();
        _viewModel.Items[0].IsSelected.Should().BeFalse();
    }

    #endregion

    #region Issue #635: 行削除テスト

    [Fact]
    public void DeleteRow_RemovesAndReindexes()
    {
        // Arrange
        AddItems(3);
        _viewModel.SelectItemCommand.Execute(_viewModel.Items[1]); // 2番目を選択
        _viewModel.OnRequestDeleteConfirmation = _ => true; // 常にYes

        // Act
        _viewModel.DeleteRowCommand.Execute(null);

        // Assert
        _viewModel.Items.Count.Should().Be(2);
        _viewModel.Items[0].Index.Should().Be(0, "インデックスが振り直される");
        _viewModel.Items[1].Index.Should().Be(1, "インデックスが振り直される");
    }

    [Fact]
    public void DeleteRow_SetsHasChanges()
    {
        // Arrange
        AddItems(2);
        _viewModel.HasChanges = false;
        _viewModel.SelectItemCommand.Execute(_viewModel.Items[0]);
        _viewModel.OnRequestDeleteConfirmation = _ => true;

        // Act
        _viewModel.DeleteRowCommand.Execute(null);

        // Assert
        _viewModel.HasChanges.Should().BeTrue();
    }

    [Fact]
    public void DeleteRow_CancelledByUser_DoesNotRemove()
    {
        // Arrange
        AddItems(3);
        _viewModel.SelectItemCommand.Execute(_viewModel.Items[0]);
        _viewModel.OnRequestDeleteConfirmation = _ => false; // No

        // Act
        _viewModel.DeleteRowCommand.Execute(null);

        // Assert
        _viewModel.Items.Count.Should().Be(3, "キャンセルされたので削除されない");
    }

    #endregion
}
