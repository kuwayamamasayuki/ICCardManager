using FluentAssertions;
using ICCardManager.Views.Dialogs;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1548: <see cref="OperationLogDialog"/> の LiveRegion 発火対応のテスト。
/// プロパティ名 → 対象 TextBlock 名 のマッピング純粋関数を検証する。
/// 実際の RaiseAutomationEvent 発火は WPF UI スレッドが必要なため、
/// スクリーンリーダー実機読み上げ確認はユーザー手動で実施する（設計書 §5.4 参照）。
/// </summary>
public class OperationLogDialogLiveRegionTests
{
    [Theory]
    [InlineData("PageInfo", false, "PageInfoText")]
    [InlineData("CurrentPage", false, "CurrentPageNumberText")]
    [InlineData("TotalPages", false, "CurrentPageNumberText")]
    [InlineData("StatusMessage", false, "StatusMessageText")]
    [InlineData("BusyMessage", false, "ProcessingOverlayText")]
    public void GetTargetElementName_対象プロパティ変化時に_対応するTextBlock名を返すこと(
        string propertyName, bool isBusy, string expectedTargetName)
    {
        var result = OperationLogDialog.GetTargetElementName(propertyName, isBusy);
        result.Should().Be(expectedTargetName);
    }

    [Fact]
    public void GetTargetElementName_IsBusyがtrueへ変化時_ProcessingOverlayTextを返すこと()
    {
        var result = OperationLogDialog.GetTargetElementName("IsBusy", isBusy: true);
        result.Should().Be("ProcessingOverlayText");
    }

    [Fact]
    public void GetTargetElementName_IsBusyがfalseへ変化時_nullを返すこと()
    {
        // IsBusy=false への遷移はオーバーレイ非表示なので通知不要
        var result = OperationLogDialog.GetTargetElementName("IsBusy", isBusy: false);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("UnknownProperty")]
    [InlineData("")]
    [InlineData(null)]
    public void GetTargetElementName_対象外プロパティの場合_nullを返すこと(string? propertyName)
    {
        var result = OperationLogDialog.GetTargetElementName(propertyName, isBusy: false);
        result.Should().BeNull();
    }
}
