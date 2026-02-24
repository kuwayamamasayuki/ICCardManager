using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;


namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// PrintPreviewViewModelの単体テスト
/// </summary>
/// <remarks>
/// FlowDocumentやPrintService等のWPF依存部分は除外し、
/// ズーム・ページナビゲーション・計算ロジックなど純粋なロジックをテストする。
/// System.Printing.PageOrientationはテストプロジェクトで参照困難なため、
/// SelectedOrientationプロパティの直接操作は避け、関連するロジックのみテストする。
/// </remarks>
public class PrintPreviewViewModelTests
{
    private readonly PrintPreviewViewModel _viewModel;

    public PrintPreviewViewModelTests()
    {
        // PrintServiceのコンストラクタにはIReportDataBuilderが必要
        var reportDataBuilderMock = new Mock<IReportDataBuilder>();
        var printService = new PrintService(reportDataBuilderMock.Object);
        _viewModel = new PrintPreviewViewModel(printService);
    }

    #region 初期状態

    [Fact]
    public void Constructor_初期ズームレベルが100であること()
    {
        _viewModel.ZoomLevel.Should().Be(100);
    }

    [Fact]
    public void Constructor_初期ページが1であること()
    {
        _viewModel.CurrentPage.Should().Be(1);
        _viewModel.TotalPages.Should().Be(1);
    }

    #endregion

    #region GetOrientationDisplayName

    [Fact]
    public void GetOrientationDisplayName_横向きの場合に正しい表示名を返すこと()
    {
        var result = PrintPreviewViewModel.GetOrientationDisplayName(PageOrientation.Landscape);
        result.Should().Be("横向き（A4横）");
    }

    [Fact]
    public void GetOrientationDisplayName_縦向きの場合に正しい表示名を返すこと()
    {
        var result = PrintPreviewViewModel.GetOrientationDisplayName(PageOrientation.Portrait);
        result.Should().Be("縦向き（A4縦）");
    }

    #endregion

    #region ページ表示テキスト

    [Fact]
    public void PageDisplayText_正しいフォーマットで表示されること()
    {
        // Arrange
        _viewModel.UpdatePageCount(5, 3);

        // Assert
        _viewModel.PageDisplayText.Should().Be("3 / 5 ページ");
    }

    [Fact]
    public void PageDisplayText_TotalPagesが0の場合でも1に正規化されること()
    {
        // Arrange: TotalPages=0は内部で1に変換される
        _viewModel.UpdatePageCount(0, 0);

        // Assert: TotalPages > 0 なのでページ表示テキスト
        _viewModel.TotalPages.Should().Be(1);
        _viewModel.PageDisplayText.Should().Be("1 / 1 ページ");
    }

    #endregion

    #region IsFirstPage / IsLastPage

    [Fact]
    public void IsFirstPage_1ページ目の場合にtrueであること()
    {
        _viewModel.UpdatePageCount(5, 1);
        _viewModel.IsFirstPage.Should().BeTrue();
    }

    [Fact]
    public void IsFirstPage_2ページ目以降の場合にfalseであること()
    {
        _viewModel.UpdatePageCount(5, 2);
        _viewModel.IsFirstPage.Should().BeFalse();
    }

    [Fact]
    public void IsLastPage_最終ページの場合にtrueであること()
    {
        _viewModel.UpdatePageCount(5, 5);
        _viewModel.IsLastPage.Should().BeTrue();
    }

    [Fact]
    public void IsLastPage_最終ページでない場合にfalseであること()
    {
        _viewModel.UpdatePageCount(5, 3);
        _viewModel.IsLastPage.Should().BeFalse();
    }

    #endregion

    #region UpdatePageCount

    [Fact]
    public void UpdatePageCount_CurrentPageがTotalPagesを超えないようにクランプされること()
    {
        // Act: 3ページ中のページ5を指定
        _viewModel.UpdatePageCount(3, 5);

        // Assert: ページ3にクランプ
        _viewModel.CurrentPage.Should().Be(3);
        _viewModel.TotalPages.Should().Be(3);
    }

    [Fact]
    public void UpdatePageCount_0以下のページが1にクランプされること()
    {
        // Act
        _viewModel.UpdatePageCount(5, 0);

        // Assert
        _viewModel.CurrentPage.Should().Be(1);
    }

    [Fact]
    public void UpdatePageCount_TotalPagesが0の場合に1に設定されること()
    {
        // Act
        _viewModel.UpdatePageCount(0, 1);

        // Assert
        _viewModel.TotalPages.Should().Be(1);
    }

    #endregion

    #region ズーム

    [Fact]
    public void ZoomIn_次のズームレベルに増加すること()
    {
        // Arrange: 100%からスタート
        _viewModel.ZoomLevel.Should().Be(100);

        // Act
        _viewModel.ZoomInCommand.Execute(null);

        // Assert: 100 → 125
        _viewModel.ZoomLevel.Should().Be(125);
    }

    [Fact]
    public void ZoomOut_前のズームレベルに減少すること()
    {
        // Arrange: 100%からスタート
        _viewModel.ZoomLevel.Should().Be(100);

        // Act
        _viewModel.ZoomOutCommand.Execute(null);

        // Assert: 100 → 75
        _viewModel.ZoomLevel.Should().Be(75);
    }

    [Fact]
    public void ZoomIn_最大ズームレベルで変化しないこと()
    {
        // Arrange
        _viewModel.ZoomLevel = 200; // 最大値

        // Act
        _viewModel.ZoomInCommand.Execute(null);

        // Assert
        _viewModel.ZoomLevel.Should().Be(200);
    }

    [Fact]
    public void ZoomOut_最小ズームレベルで変化しないこと()
    {
        // Arrange
        _viewModel.ZoomLevel = 50; // 最小値

        // Act
        _viewModel.ZoomOutCommand.Execute(null);

        // Assert
        _viewModel.ZoomLevel.Should().Be(50);
    }

    [Fact]
    public void ResetZoom_100パーセントに戻ること()
    {
        // Arrange
        _viewModel.ZoomLevel = 150;

        // Act
        _viewModel.ResetZoomCommand.Execute(null);

        // Assert
        _viewModel.ZoomLevel.Should().Be(100);
    }

    [Fact]
    public void EffectiveZoom_ZoomLevelとContentScaleの積であること()
    {
        // Arrange
        _viewModel.ZoomLevel = 150;
        // ContentScaleはデフォルト1.0

        // Assert
        _viewModel.EffectiveZoom.Should().Be(150.0);
    }

    [Fact]
    public void ZoomLevels_正しい選択肢が提供されること()
    {
        _viewModel.ZoomLevels.Should().BeEquivalentTo(
            new double[] { 50, 75, 100, 125, 150, 200 });
    }

    [Fact]
    public void ZoomIn_リストにない値からの場合にZoomLevels先頭に移動すること()
    {
        // Arrange: リストにない値を設定
        _viewModel.ZoomLevel = 110;

        // Act
        _viewModel.ZoomInCommand.Execute(null);

        // Assert:
        // 注: Array.IndexOf == -1 の場合、-1 < Length - 1 (= 5) は常にtrueなので
        // ZoomLevels[-1 + 1] = ZoomLevels[0] = 50 が返る
        // （else if の「次に大きい値を選択」ロジックは到達不能コード）
        _viewModel.ZoomLevel.Should().Be(50);
    }

    [Fact]
    public void ZoomOut_リストにない値からの場合に次に小さい値が選択されること()
    {
        // Arrange: リストにない値を設定
        _viewModel.ZoomLevel = 110;

        // Act
        _viewModel.ZoomOutCommand.Execute(null);

        // Assert: 110より小さい最後のレベル = 100
        _viewModel.ZoomLevel.Should().Be(100);
    }

    #endregion

    #region ページナビゲーションイベント

    [Fact]
    public void NextPageCommand_イベントが発火されること()
    {
        // Arrange
        _viewModel.UpdatePageCount(3, 1);
        var eventFired = false;
        _viewModel.NavigateNextRequested += () => eventFired = true;

        // Act
        _viewModel.NextPageCommand.Execute(null);

        // Assert
        eventFired.Should().BeTrue();
    }

    [Fact]
    public void PreviousPageCommand_イベントが発火されること()
    {
        // Arrange
        _viewModel.UpdatePageCount(3, 2);
        var eventFired = false;
        _viewModel.NavigatePreviousRequested += () => eventFired = true;

        // Act
        _viewModel.PreviousPageCommand.Execute(null);

        // Assert
        eventFired.Should().BeTrue();
    }

    [Fact]
    public void FirstPageCommand_イベントが発火されること()
    {
        // Arrange
        _viewModel.UpdatePageCount(3, 3);
        var eventFired = false;
        _viewModel.NavigateFirstRequested += () => eventFired = true;

        // Act
        _viewModel.FirstPageCommand.Execute(null);

        // Assert
        eventFired.Should().BeTrue();
    }

    [Fact]
    public void LastPageCommand_イベントが発火されること()
    {
        // Arrange
        _viewModel.UpdatePageCount(3, 1);
        var eventFired = false;
        _viewModel.NavigateLastRequested += () => eventFired = true;

        // Act
        _viewModel.LastPageCommand.Execute(null);

        // Assert
        eventFired.Should().BeTrue();
    }

    #endregion

    #region 印刷（Documentなし）

    [Fact]
    public void PrintCommand_ドキュメントがnullの場合にエラーメッセージが表示されること()
    {
        // Act
        _viewModel.PrintCommand.Execute(null);

        // Assert
        _viewModel.StatusMessage.Should().Be("印刷するドキュメントがありません");
    }

    #endregion
}
