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

    #region Issue #634: OnRequestSplitMode テスト

    [Fact]
    public void OnRequestSplitMode_TwoGroups_CallbackIsInvoked()
    {
        // Arrange: 2グループに分割してOnRequestSplitModeを設定
        AddItems(2);
        _viewModel.ToggleDividerAt(0);

        // Items[0].GroupId と Items[1].GroupId が異なる（2グループ）ことを確認
        var distinctGroups = _viewModel.Items
            .Where(i => i.GroupId.HasValue)
            .Select(i => i.GroupId!.Value)
            .Distinct()
            .Count();
        distinctGroups.Should().Be(2, "2つのグループがある");

        // OnRequestSplitModeが呼ばれることを検証
        bool callbackInvoked = false;
        _viewModel.OnRequestSplitMode = () =>
        {
            callbackInvoked = true;
            return SplitSaveMode.Cancel; // テストではキャンセル
        };

        // Act: SaveCommandを実行（_ledgerが未初期化のため内部でSaveAsyncが動く前にHasChangesチェックを通過する必要あり）
        // HasChangesはToggleDividerAtで既にtrueになっている
        _viewModel.SaveCommand.Execute(null);

        // Assert
        callbackInvoked.Should().BeTrue("2グループある場合にOnRequestSplitModeが呼ばれる");
    }

    [Fact]
    public void OnRequestSplitMode_SingleGroup_NotInvoked()
    {
        // Arrange: 分割線なし（1グループ）
        AddItems(2);
        // 分割線を入れない → GroupIdはすべてnull

        bool callbackInvoked = false;
        _viewModel.OnRequestSplitMode = () =>
        {
            callbackInvoked = true;
            return SplitSaveMode.Cancel;
        };

        // HasChangesをtrueにするために一度分割してから統合
        _viewModel.ToggleDividerAt(0);
        _viewModel.ToggleDividerAt(0); // 元に戻す（1グループ）
        // HasChangesはまだtrueのはず

        // Act
        _viewModel.SaveCommand.Execute(null);

        // Assert
        callbackInvoked.Should().BeFalse("1グループの場合はOnRequestSplitModeが呼ばれない");
    }

    #endregion
}
