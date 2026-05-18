using FluentAssertions;
using ICCardManager.Views.Dialogs;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1548 / #1507: <see cref="OperationLogDialog"/> の LiveRegion 発火対応のテスト。
/// プロパティ名 → 対象 TextBlock 名 のマッピング純粋関数を検証する。
/// 実際の RaiseAutomationEvent 発火は WPF UI スレッドが必要なため、
/// スクリーンリーダー実機読み上げ確認はユーザー手動で実施する（設計書 §5.4 参照）。
/// </summary>
public class OperationLogDialogLiveRegionTests
{
    [Theory]
    [InlineData("PageInfo", false, "PageInfoText")]
    // Issue #1548/#1507: CurrentPage / TotalPages 単体ではなく派生プロパティ PageNumberDisplay 経由で
    // CurrentPageNumberText の読み上げを発火するように変更（Run 構成 → 単一 Text バインドへの移行に対応）。
    [InlineData("PageNumberDisplay", false, "CurrentPageNumberText")]
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
    // Issue #1548/#1507: CurrentPage / TotalPages 単体は派生プロパティ PageNumberDisplay 経由に集約されたため、
    // 直接マッピングからは外れた（ViewModel 側の [NotifyPropertyChangedFor] が PageNumberDisplay の通知を伝搬する）。
    [InlineData("CurrentPage")]
    [InlineData("TotalPages")]
    [InlineData("UnknownProperty")]
    [InlineData("")]
    [InlineData(null)]
    public void GetTargetElementName_対象外プロパティの場合_nullを返すこと(string? propertyName)
    {
        var result = OperationLogDialog.GetTargetElementName(propertyName, isBusy: false);
        result.Should().BeNull();
    }
}
