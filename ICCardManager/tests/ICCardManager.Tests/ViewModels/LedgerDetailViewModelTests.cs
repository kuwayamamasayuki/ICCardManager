using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Tests.Data;
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
using System.Threading.Tasks;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// LedgerDetailViewModelの単体テスト
/// Issue #633: 分割操作でGroupIdが正しく設定されることを検証
/// Issue #1134: パンくず表示を検証
/// </summary>
public class LedgerDetailViewModelTests : IDisposable
{
    private readonly LedgerDetailViewModel _viewModel;
    private readonly Mock<ILedgerRepository> _ledgerRepoMock;
    private readonly DbContext _dbContext;
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock;

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    public LedgerDetailViewModelTests()
    {
        _ledgerRepoMock = new Mock<ILedgerRepository>();
        _dbContext = TestDbContextFactory.Create();
        var summaryGenerator = new SummaryGenerator();
        var operationLogRepoMock = new Mock<IOperationLogRepository>();
        var staffRepoMock = new Mock<IStaffRepository>();
        var operationLogger = new OperationLogger(
            operationLogRepoMock.Object,
            Mock.Of<ICurrentOperatorContext>());
        var splitServiceLogger = NullLogger<LedgerSplitService>.Instance;
        var ledgerSplitService = new LedgerSplitService(
            _ledgerRepoMock.Object,
            summaryGenerator,
            operationLogger,
            _dbContext,
            splitServiceLogger);
        var logger = NullLogger<LedgerDetailViewModel>.Instance;
        _staffAuthServiceMock = new Mock<IStaffAuthService>();

        _viewModel = new LedgerDetailViewModel(
            _ledgerRepoMock.Object,
            summaryGenerator,
            operationLogger,
            ledgerSplitService,
            _dbContext,
            _staffAuthServiceMock.Object,
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

    #region Issue #1134: パンくず表示テスト

    [Fact]
    public async Task InitializeAsync_カード名ありの場合パンくずが設定されること()
    {
        // Arrange
        var testLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 3, 15),
            Summary = "鉄道（博多駅～天神駅）",
            Balance = 500,
            Details = new List<LedgerDetail>
            {
                new LedgerDetail { SequenceNumber = 1, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 500 }
            }
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(testLedger);

        // Act
        await _viewModel.InitializeAsync(1, cardName: "nimoca N-002");

        // Assert
        _viewModel.BreadcrumbText.Should().Be("nimoca N-002 > 履歴詳細");
    }

    [Fact]
    public async Task InitializeAsync_カード名なしの場合パンくずがデフォルト設定されること()
    {
        // Arrange
        var testLedger = new Ledger
        {
            Id = 1,
            CardIdm = "0102030405060708",
            Date = new DateTime(2026, 3, 15),
            Summary = "テスト",
            Balance = 500,
            Details = new List<LedgerDetail>()
        };
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(testLedger);

        // Act
        await _viewModel.InitializeAsync(1);

        // Assert
        _viewModel.BreadcrumbText.Should().Be("履歴詳細");
    }

    #endregion

    #region 履歴分割の職員認証ゲート（SEQ-AUTH-01）

    [Fact]
    public async Task SaveWithFullSplit_認証キャンセル時_分割せず中止メッセージを表示する()
    {
        // Arrange: 3項目を個別グループに分割し、変更あり状態にする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasChanges.Should().BeTrue();
        // _staffAuthServiceMock は未設定 → RequestAuthenticationAsync は既定で null（=認証キャンセル）を返す

        // Act
        await _viewModel.SaveWithFullSplitCommand.ExecuteAsync(null);

        // Assert: 認証を要求し、キャンセルされたため分割を実行しない
        _staffAuthServiceMock.Verify(
            s => s.RequestAuthenticationAsync("履歴の分割"), Times.Once);
        _viewModel.HasChanges.Should().BeTrue(
            "認証キャンセル時は分割を実行しないため変更フラグは保持される");
        _viewModel.StatusMessage.Should().Be("認証がキャンセルされたため分割を中止しました");
    }

    #endregion

    #region ダイアログクローズガード（Issue #1743）

    [Fact]
    public void CanClose_変更がなければ確認を求めずに閉じられる()
    {
        // Arrange
        var confirmCalls = 0;
        _viewModel.HasChanges.Should().BeFalse("前提: 初期状態は未保存の変更なし");

        // Act
        var canClose = _viewModel.CanClose(() => { confirmCalls++; return false; });

        // Assert
        canClose.Should().BeTrue("未保存の変更が無ければ破棄確認なしで閉じられるべき");
        confirmCalls.Should().Be(0, "変更が無いのに破棄確認を出すと操作の妨げになる");
    }

    [Fact]
    public void CanClose_変更ありで破棄を承諾すると閉じられる()
    {
        // Arrange: 分割操作で未保存の変更がある状態にする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasChanges.Should().BeTrue("前提: 分割操作で未保存の変更がある状態");
        var confirmCalls = 0;

        // Act
        var canClose = _viewModel.CanClose(() => { confirmCalls++; return true; });

        // Assert
        canClose.Should().BeTrue("破棄を承諾したら閉じられるべき");
        confirmCalls.Should().Be(1, "未保存の変更があるときは必ず破棄確認を通す");
    }

    [Fact]
    public void CanClose_変更ありで破棄を拒否すると閉じられない()
    {
        // Arrange: 分割操作で未保存の変更がある状態にする
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);
        _viewModel.HasChanges.Should().BeTrue("前提: 分割操作で未保存の変更がある状態");

        // Act
        var canClose = _viewModel.CanClose(() => false);

        // Assert
        canClose.Should().BeFalse("「いいえ」を選んだら編集内容を保持したままダイアログに留まるべき");
        _viewModel.HasChanges.Should().BeTrue("拒否しても編集内容（変更フラグ）は失われない");
    }

    [Fact]
    public async Task CanClose_保存トランザクション実行中は確認を求めず閉じられない()
    {
        // Arrange: 実際に保存を走らせ、ReplaceDetailsAsync の中で止めて「保存中」を再現する
        //（IsBusy を直接立てると本番で成立しない状態を検証してしまう）
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        var replaceEntered = new TaskCompletionSource<bool>();
        var releaseReplace = new TaskCompletionSource<bool>();
        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Returns(async () =>
            {
                replaceEntered.TrySetResult(true);
                await releaseReplace.Task;
                return true;
            });
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(true);

        var saveTask = _viewModel.SaveCommand.ExecuteAsync(null);
        await WaitForAsync(replaceEntered, "ReplaceDetailsAsync の開始");
        _viewModel.IsBusy.Should().BeTrue("前提: 保存トランザクションが実行中");

        try
        {
            var confirmCalls = 0;

            // Act: 保存中に ✕ / Alt+F4 / Escape で閉じようとする
            var canClose = _viewModel.CanClose(() => { confirmCalls++; return true; });

            // Assert: DB コミット中にウィンドウが閉じると、保存は成功しているのに
            // 呼び出し元が WasSaved=false を見て履歴一覧を再読込しない
            canClose.Should().BeFalse("保存トランザクションの実行中は閉じられないこと");
            confirmCalls.Should().Be(0,
                "保存中の変更は破棄されるわけではないため、破棄確認を出すのは事実に反する");
        }
        finally
        {
            releaseReplace.TrySetResult(true);
            await saveTask;
        }
    }

    [Fact]
    public async Task SaveAsync_摘要更新のみ競合したとき_明細は保存済みとして扱う()
    {
        // Arrange: 明細の置換は成功、摘要 UPDATE だけが競合で 0 行（共有モードで他 PC が変更）
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);
        _ledgerRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Ledger>())).ReturnsAsync(false);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert: 明細の GroupId は別トランザクションで既にコミット済みのため、
        // 「未保存の変更がある」と表示して破棄確認を出すのは事実に反する
        _viewModel.HasChanges.Should().BeFalse(
            "明細は DB へ確定済みで、閉じても破棄されるものは無い");
        _viewModel.HasPersistedChanges.Should().BeTrue(
            "呼び出し元が履歴一覧を再読込しないと、画面の旧グループと DB の新 GroupId が食い違う");
        _viewModel.StatusMessage.Should().Contain("摘要を更新できませんでした",
            "摘要だけ更新できなかったことは利用者へ伝える");
    }

    [Fact]
    public async Task SaveAsync_保存前はHasPersistedChangesが立たない()
    {
        // Arrange: 明細の置換自体が失敗した場合は何もコミットされていない
        await InitializeWithTestLedgerAsync();
        AddItems(3);
        _viewModel.SplitAllCommand.Execute(null);

        _ledgerRepoMock
            .Setup(r => r.ReplaceDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(false);

        // Act
        await _viewModel.SaveCommand.ExecuteAsync(null);

        // Assert
        _viewModel.HasPersistedChanges.Should().BeFalse(
            "1 行もコミットされていないのに再読込を促すと、成功したように見える");
        _viewModel.HasChanges.Should().BeTrue("保存できていないので変更は保持される");
    }

    /// <summary>
    /// SaveAsync を走らせるための最小の台帳で ViewModel を初期化する。
    /// </summary>
    private async Task InitializeWithTestLedgerAsync()
    {
        _ledgerRepoMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1,
            CardIdm = "0123456789ABCDEF",
            Date = new DateTime(2026, 2, 10),
            Summary = "テスト",
            Balance = 500,
            Details = new List<LedgerDetail>()
        });

        await _viewModel.InitializeAsync(1);
    }

    /// <summary>
    /// 非同期処理が所定の地点へ到達するのを待つ（固定時間の待機はマシン速度で不安定になる）。
    /// </summary>
    private static async Task WaitForAsync(TaskCompletionSource<bool> signal, string what)
    {
        var completed = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(signal.Task, $"{what} が 5 秒以内に到達すること");
    }

    [Fact]
    public void RequestCloseCommand_OnCloseRequestedコールバックを呼ぶ()
    {
        // Arrange
        var closeRequests = 0;
        _viewModel.OnCloseRequested = () => closeRequests++;

        // Act: Escape キーの KeyBinding から実行される経路
        _viewModel.RequestCloseCommand.Execute(null);

        // Assert: View 側で OnCloseRequested = Close を設定するため、
        // この経路が Window.Close() → OnClosing の破棄確認へ届く
        closeRequests.Should().Be(1, "Escape キーからウィンドウの Close() へ届く唯一の経路");
    }

    #endregion
}
