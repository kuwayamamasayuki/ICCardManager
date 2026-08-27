using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.Tests.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// <see cref="TransferStationGroupViewModel"/> のテスト（Issue #1905）
/// </summary>
/// <remarks>
/// 保存経路は <see cref="ITransferStationGroupService"/> をモックするため
/// <see cref="SummaryGenerator"/> の静的状態には触れないが、
/// <see cref="TransferStationGroupService.MinimumNamesPerGroup"/> を参照する
/// バリデーション文言の検証を含むため <see cref="SummaryGeneratorCollection"/> には属さない。
/// </remarks>
public class TransferStationGroupViewModelTests
{
    private readonly Mock<ITransferStationGroupService> _groupService = new();
    private readonly Mock<IDialogService> _dialogService = new();

    private TransferStationGroupViewModel CreateViewModel() =>
        new(_groupService.Object, _dialogService.Object);

    private void ArrangeGroups(params string[][] groups) =>
        _groupService
            .Setup(s => s.GetGroupsAsync())
            .ReturnsAsync(groups.Select(g => g.ToList()).ToList());

    private void ArrangeSaveResult(bool result) =>
        _groupService
            .Setup(s => s.SaveGroupsAsync(It.IsAny<IEnumerable<IEnumerable<string>>>()))
            .ReturnsAsync(result);

    private static async Task<TransferStationGroupViewModel> LoadedAsync(
        TransferStationGroupViewModel vm)
    {
        await vm.LoadAsync();
        return vm;
    }

    #region 読み込み

    [Fact]
    public async Task LoadAsync_現在有効なグループを一覧へ読み込むこと()
    {
        // Arrange
        ArrangeGroups(
            new[] { "天神", "西鉄福岡(天神)" },
            new[] { "千早", "西鉄千早" });

        // Act
        var vm = await LoadedAsync(CreateViewModel());

        // Assert
        vm.Groups.Should().HaveCount(2);
        vm.Groups[0].DisplayText.Should().Be("天神、西鉄福岡(天神)");
        vm.Groups[0].NameCount.Should().Be(2);
    }

    #endregion

    #region 追加・編集・削除

    [Fact]
    public async Task SaveAsync_新規追加_一覧へ追加され完了メッセージが残ること()
    {
        // Arrange
        ArrangeGroups(new[] { "天神", "西鉄福岡(天神)" });
        ArrangeSaveResult(true);
        var vm = await LoadedAsync(CreateViewModel());

        vm.New();
        vm.EditingNames = "天神日銀前、天神中央郵便局前";

        // Act
        await vm.SaveAsync();

        // Assert
        vm.Groups.Should().HaveCount(2);
        vm.Groups[1].DisplayText.Should().Be("天神日銀前、天神中央郵便局前");
        vm.IsEditing.Should().BeFalse();

        // Issue #1727 / #1759: CancelEdit() は StatusMessage をクリアするため、
        // 完了メッセージは必ずそのあとに設定されていること
        vm.StatusMessage.Should().Contain("追加しました");
        vm.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_編集_選択を保ったまま一覧が更新されること()
    {
        // Arrange
        ArrangeGroups(new[] { "天神", "西鉄福岡(天神)" });
        ArrangeSaveResult(true);
        var vm = await LoadedAsync(CreateViewModel());

        vm.SelectedGroup = vm.Groups[0];
        var selectedId = vm.Groups[0].Id;
        vm.Edit();
        vm.EditingNames = "天神、西鉄福岡(天神)、天神南";

        // Act
        await vm.SaveAsync();

        // Assert: Issue #1761 — Clear() して詰め直さないので選択が外れない
        vm.Groups.Should().HaveCount(1);
        vm.Groups[0].Id.Should().Be(selectedId);
        vm.Groups[0].DisplayText.Should().Be("天神、西鉄福岡(天神)、天神南");
        vm.SelectedGroup.Should().NotBeNull();
        vm.StatusMessage.Should().Contain("更新しました");
    }

    [Fact]
    public async Task SaveAsync_保存失敗_一覧を変えずエラーを案内すること()
    {
        // Arrange
        ArrangeGroups(new[] { "天神", "西鉄福岡(天神)" });
        ArrangeSaveResult(false);
        var vm = await LoadedAsync(CreateViewModel());

        vm.New();
        vm.EditingNames = "天神日銀前、天神中央郵便局前";

        // Act
        await vm.SaveAsync();

        // Assert
        vm.Groups.Should().HaveCount(1, "保存できていないのに一覧へ反映してはいけない");
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().EndWith("してください。", "行動指示で終わること（error-messages.md）");

        // Issue #1757: エラー表示で入力内容を消さない（指摘された項目だけ直して再保存できるように）
        vm.IsEditing.Should().BeTrue();
        vm.EditingNames.Should().Be("天神日銀前、天神中央郵便局前");
    }

    [Fact]
    public async Task DeleteAsync_確認して削除し完了メッセージが残ること()
    {
        // Arrange
        ArrangeGroups(
            new[] { "天神", "西鉄福岡(天神)" },
            new[] { "千早", "西鉄千早" });
        ArrangeSaveResult(true);
        _dialogService
            .Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(true);

        var vm = await LoadedAsync(CreateViewModel());
        vm.SelectedGroup = vm.Groups[0];

        // Act
        await vm.DeleteAsync();

        // Assert
        vm.Groups.Should().HaveCount(1);
        vm.Groups[0].DisplayText.Should().Be("千早、西鉄千早");
        vm.StatusMessage.Should().Contain("削除しました");
        vm.IsStatusError.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_確認でいいえ_何も書き込まないこと()
    {
        // Arrange
        ArrangeGroups(new[] { "天神", "西鉄福岡(天神)" });
        _dialogService
            .Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(false);

        var vm = await LoadedAsync(CreateViewModel());
        vm.SelectedGroup = vm.Groups[0];

        // Act
        await vm.DeleteAsync();

        // Assert
        vm.Groups.Should().HaveCount(1);
        _groupService.Verify(
            s => s.SaveGroupsAsync(It.IsAny<IEnumerable<IEnumerable<string>>>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_確認中に選択が外れても対象を取り違えないこと()
    {
        // Arrange: Issue #1761 — 確認ダイアログの表示中に Ctrl+クリック等で選択が解除され得る
        ArrangeGroups(
            new[] { "天神", "西鉄福岡(天神)" },
            new[] { "千早", "西鉄千早" });
        ArrangeSaveResult(true);

        var vm = await LoadedAsync(CreateViewModel());
        vm.SelectedGroup = vm.Groups[0];

        _dialogService
            .Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => vm.SelectedGroup = null)
            .Returns(true);

        // Act
        await vm.DeleteAsync();

        // Assert: 選択が外れても、確認で名指しした「天神、西鉄福岡(天神)」だけが消える
        vm.Groups.Should().HaveCount(1);
        vm.Groups[0].DisplayText.Should().Be("千早、西鉄千早");
        vm.StatusMessage.Should().Contain("天神、西鉄福岡(天神)");
    }

    #endregion

    #region 入力の解釈とバリデーション

    [Theory]
    [InlineData("天神日銀前、天神中央郵便局前", 2)]
    [InlineData("天神日銀前,天神中央郵便局前", 2)]
    [InlineData("  天神日銀前 、 天神中央郵便局前  ", 2)]
    [InlineData("天神日銀前、、天神中央郵便局前", 2)]
    [InlineData("天神日銀前、天神日銀前", 1)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    public void ParseNames_区切り文字と空白を正しく扱うこと(string input, int expectedCount)
    {
        TransferStationGroupViewModel.ParseNames(input).Should().HaveCount(expectedCount);
    }

    [Fact]
    public void Validate_未入力_3要素の文言を返すこと()
    {
        var message = TransferStationGroupViewModel.Validate(
            new List<string>(),
            Array.Empty<TransferStationGroupItem>(),
            null);

        message.Should().NotBeNull();
        message.Should().Contain("入力されていません", "何が");
        message.Should().Contain("2つ以上", "なぜ／どうすれば");
        message.Should().EndWith("してください。", "行動指示で終わること");
    }

    [Fact]
    public void Validate_1件だけ_実際の入力値を含む文言を返すこと()
    {
        var message = TransferStationGroupViewModel.Validate(
            new List<string> { "天神日銀前" },
            Array.Empty<TransferStationGroupItem>(),
            null);

        message.Should().NotBeNull();
        message.Should().Contain("天神日銀前", "実際の入力値を含める（error-messages.md）");
        message.Should().EndWith("してください。");
    }

    [Fact]
    public void Validate_他グループと重複_重複した名前と相手のグループを名指しすること()
    {
        var existing = new[] { new TransferStationGroupItem(new[] { "天神", "西鉄福岡(天神)" }) };

        var message = TransferStationGroupViewModel.Validate(
            new List<string> { "天神", "天神南" },
            existing,
            null);

        message.Should().NotBeNull();
        message.Should().Contain("「天神」", "何が");
        message.Should().Contain("天神、西鉄福岡(天神)", "どのグループと重複したか");
        message.Should().EndWith("してください。");
    }

    [Fact]
    public void Validate_自分自身を編集中は重複としないこと()
    {
        // 対のテスト: 重複検出が広すぎると、自分のグループを編集するだけで弾かれる
        var existing = new TransferStationGroupItem(new[] { "天神", "西鉄福岡(天神)" });

        var message = TransferStationGroupViewModel.Validate(
            new List<string> { "天神", "西鉄福岡(天神)", "天神南" },
            new[] { existing },
            existing.Id);

        message.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_バリデーションエラー_保存を呼ばないこと()
    {
        // Arrange
        ArrangeGroups(new[] { "天神", "西鉄福岡(天神)" });
        var vm = await LoadedAsync(CreateViewModel());

        vm.New();
        vm.EditingNames = "天神日銀前";

        // Act
        await vm.SaveAsync();

        // Assert
        _groupService.Verify(
            s => s.SaveGroupsAsync(It.IsAny<IEnumerable<IEnumerable<string>>>()),
            Times.Never);
        vm.IsStatusError.Should().BeTrue();
        vm.IsEditing.Should().BeTrue();
    }

    #endregion

    #region 処理中表示

    [Fact]
    public async Task DeleteAsync_確認ダイアログの表示中は処理中オーバーレイを出さないこと()
    {
        // Issue #1793: 確認ダイアログは職員の判断を待つ設計であり、
        // 背後で回り続ける「処理中」表示はその判断を妨げる。
        // 本 ViewModel は確認を BeginBusy スコープの外で行うことでこれを満たす。
        ArrangeGroups(new[] { "天神", "西鉄福岡(天神)" });
        ArrangeSaveResult(true);

        var vm = await LoadedAsync(CreateViewModel());
        vm.SelectedGroup = vm.Groups[0];

        bool? busyDuringDialog = null;
        _dialogService
            .Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => busyDuringDialog = vm.IsBusy)
            .Returns(true);

        // Act
        await vm.DeleteAsync();

        // Assert
        busyDuringDialog.Should().BeFalse();
        vm.IsBusy.Should().BeFalse("スコープを抜けたら必ず解除される");
    }

    #endregion
}
