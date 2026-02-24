using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using FluentAssertions;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// ViewModelBaseの単体テスト
/// </summary>
public class ViewModelBaseTests
{
    /// <summary>
    /// テスト用のViewModelBase具象クラス
    /// protectedメソッドを公開してテスト可能にする
    /// BusyScopeはprotectedクラスのため、ラッパーメソッドで間接的に操作する
    /// </summary>
    private class TestViewModel : ViewModelBase
    {
        private BusyScope _currentScope;

        public new void SetBusy(bool isBusy, string message = null)
            => base.SetBusy(isBusy, message);

        public new void ResetProgress()
            => base.ResetProgress();

        public new void SetProgress(double value, double max, string message = null)
            => base.SetProgress(value, max, message);

        public new IDisposable BeginBusy(string message = null)
            => base.BeginBusy(message);

        /// <summary>
        /// BeginCancellableBusyのラッパー（BusyScopeをフィールドに保持）
        /// </summary>
        public IDisposable StartCancellableBusy(string message = null)
        {
            _currentScope = base.BeginCancellableBusy(message);
            return _currentScope;
        }

        /// <summary>
        /// 現在のスコープのCancellationTokenを取得
        /// </summary>
        public CancellationToken GetScopeCancellationToken()
            => _currentScope?.CancellationToken ?? CancellationToken.None;

        /// <summary>
        /// 現在のスコープでThrowIfCancellationRequestedを呼び出す
        /// </summary>
        public void ScopeThrowIfCancellationRequested()
            => _currentScope?.ThrowIfCancellationRequested();

        /// <summary>
        /// 現在のスコープでReportProgressを呼び出す
        /// </summary>
        public void ScopeReportProgress(double value, double max, string message = null)
            => _currentScope?.ReportProgress(value, max, message);
    }

    private readonly TestViewModel _viewModel;

    public ViewModelBaseTests()
    {
        _viewModel = new TestViewModel();
    }

    #region 初期状態テスト

    [Fact]
    public void 初期状態でIsBusyがfalseであること()
    {
        _viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void 初期状態でBusyMessageがnullであること()
    {
        _viewModel.BusyMessage.Should().BeNull();
    }

    [Fact]
    public void 初期状態でIsIndeterminateがtrueであること()
    {
        _viewModel.IsIndeterminate.Should().BeTrue();
    }

    [Fact]
    public void 初期状態でProgressValueが0であること()
    {
        _viewModel.ProgressValue.Should().Be(0);
    }

    [Fact]
    public void 初期状態でProgressMaxが100であること()
    {
        _viewModel.ProgressMax.Should().Be(100);
    }

    [Fact]
    public void 初期状態でCanCancelがfalseであること()
    {
        _viewModel.CanCancel.Should().BeFalse();
    }

    [Fact]
    public void 初期状態でIsCancellationRequestedがfalseであること()
    {
        _viewModel.IsCancellationRequested.Should().BeFalse();
    }

    #endregion

    #region SetBusy テスト

    [Fact]
    public void SetBusy_trueを設定するとIsBusyがtrueになること()
    {
        // Act
        _viewModel.SetBusy(true, "処理中...");

        // Assert
        _viewModel.IsBusy.Should().BeTrue();
        _viewModel.BusyMessage.Should().Be("処理中...");
    }

    [Fact]
    public void SetBusy_falseを設定するとIsBusyがfalseになりプログレスがリセットされること()
    {
        // Arrange - まずプログレスを設定
        _viewModel.SetBusy(true, "処理中...");
        _viewModel.SetProgress(50, 200, "半分完了");

        // Act
        _viewModel.SetBusy(false);

        // Assert
        _viewModel.IsBusy.Should().BeFalse();
        _viewModel.BusyMessage.Should().BeNull();
        _viewModel.ProgressValue.Should().Be(0);
        _viewModel.ProgressMax.Should().Be(100);
        _viewModel.IsIndeterminate.Should().BeTrue();
        _viewModel.CanCancel.Should().BeFalse();
    }

    [Fact]
    public void SetBusy_メッセージなしの場合BusyMessageがnullになること()
    {
        // Act
        _viewModel.SetBusy(true);

        // Assert
        _viewModel.IsBusy.Should().BeTrue();
        _viewModel.BusyMessage.Should().BeNull();
    }

    [Fact]
    public void SetBusy_PropertyChangedが発火すること()
    {
        // Arrange
        var changedProperties = new List<string>();
        _viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Act
        _viewModel.SetBusy(true, "テスト");

        // Assert
        changedProperties.Should().Contain("IsBusy");
        changedProperties.Should().Contain("BusyMessage");
    }

    #endregion

    #region SetProgress テスト

    [Fact]
    public void SetProgress_値を設定するとIsIndeterminateがfalseになること()
    {
        // Act
        _viewModel.SetProgress(30, 100);

        // Assert
        _viewModel.IsIndeterminate.Should().BeFalse();
        _viewModel.ProgressValue.Should().Be(30);
        _viewModel.ProgressMax.Should().Be(100);
    }

    [Fact]
    public void SetProgress_メッセージ付きでBusyMessageが更新されること()
    {
        // Act
        _viewModel.SetProgress(5, 10, "5/10 完了");

        // Assert
        _viewModel.BusyMessage.Should().Be("5/10 完了");
        _viewModel.ProgressValue.Should().Be(5);
        _viewModel.ProgressMax.Should().Be(10);
    }

    [Fact]
    public void SetProgress_メッセージなしの場合BusyMessageが変わらないこと()
    {
        // Arrange
        _viewModel.SetBusy(true, "初期メッセージ");

        // Act
        _viewModel.SetProgress(50, 100);

        // Assert
        _viewModel.BusyMessage.Should().Be("初期メッセージ");
    }

    #endregion

    #region ResetProgress テスト

    [Fact]
    public void ResetProgress_全てのプログレス値がデフォルトに戻ること()
    {
        // Arrange
        _viewModel.SetProgress(75, 200, "進行中");

        // Act
        _viewModel.ResetProgress();

        // Assert
        _viewModel.ProgressValue.Should().Be(0);
        _viewModel.ProgressMax.Should().Be(100);
        _viewModel.IsIndeterminate.Should().BeTrue();
        _viewModel.CanCancel.Should().BeFalse();
    }

    #endregion

    #region BeginBusy テスト

    [Fact]
    public void BeginBusy_スコープ開始でIsBusyがtrueになること()
    {
        // Act
        using var scope = _viewModel.BeginBusy("読み込み中...");

        // Assert
        _viewModel.IsBusy.Should().BeTrue();
        _viewModel.BusyMessage.Should().Be("読み込み中...");
    }

    [Fact]
    public void BeginBusy_スコープ終了でIsBusyがfalseに戻ること()
    {
        // Act
        var scope = _viewModel.BeginBusy("処理中...");
        _viewModel.IsBusy.Should().BeTrue();

        scope.Dispose();

        // Assert
        _viewModel.IsBusy.Should().BeFalse();
        _viewModel.BusyMessage.Should().BeNull();
    }

    [Fact]
    public void BeginBusy_メッセージなしでも動作すること()
    {
        // Act
        using var scope = _viewModel.BeginBusy();

        // Assert
        _viewModel.IsBusy.Should().BeTrue();
        _viewModel.BusyMessage.Should().BeNull();
    }

    [Fact]
    public void BeginBusy_CanCancelがfalseであること()
    {
        // Act
        using var scope = _viewModel.BeginBusy("処理中...");

        // Assert
        _viewModel.CanCancel.Should().BeFalse();
    }

    [Fact]
    public void BeginBusy_複数回Disposeしてもエラーにならないこと()
    {
        // Act
        var scope = _viewModel.BeginBusy("処理中...");
        scope.Dispose();
        scope.Dispose(); // 二重Dispose

        // Assert - 例外が発生しないこと
        _viewModel.IsBusy.Should().BeFalse();
    }

    #endregion

    #region BeginCancellableBusy テスト

    [Fact]
    public void BeginCancellableBusy_CanCancelがtrueになること()
    {
        // Act
        using var scope = _viewModel.StartCancellableBusy("キャンセル可能な処理");

        // Assert
        _viewModel.IsBusy.Should().BeTrue();
        _viewModel.CanCancel.Should().BeTrue();
        _viewModel.BusyMessage.Should().Be("キャンセル可能な処理");
    }

    [Fact]
    public void BeginCancellableBusy_CancellationTokenが有効であること()
    {
        // Act
        using var scope = _viewModel.StartCancellableBusy("処理中");

        // Assert
        var token = _viewModel.GetScopeCancellationToken();
        token.Should().NotBe(CancellationToken.None);
        token.CanBeCanceled.Should().BeTrue();
        token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void BeginCancellableBusy_スコープ終了でキャンセル状態がリセットされること()
    {
        // Act
        var scope = _viewModel.StartCancellableBusy("処理中");
        scope.Dispose();

        // Assert
        _viewModel.IsBusy.Should().BeFalse();
        _viewModel.CanCancel.Should().BeFalse();
    }

    #endregion

    #region CancelOperation テスト

    [Fact]
    public void CancelOperation_キャンセル可能なスコープ内でIsCancellationRequestedがtrueになること()
    {
        // Arrange
        using var scope = _viewModel.StartCancellableBusy("処理中");

        // Act
        _viewModel.CancelOperation();

        // Assert
        _viewModel.IsCancellationRequested.Should().BeTrue();
        _viewModel.BusyMessage.Should().Be("キャンセル中...");
    }

    [Fact]
    public void CancelOperation_CancellationTokenにキャンセルが伝播すること()
    {
        // Arrange
        using var scope = _viewModel.StartCancellableBusy("処理中");

        // Act
        _viewModel.CancelOperation();

        // Assert
        var token = _viewModel.GetScopeCancellationToken();
        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void CancelOperation_キャンセル不可スコープでは何も起きないこと()
    {
        // Arrange - BeginBusy（キャンセル不可）
        using var scope = _viewModel.BeginBusy("処理中...");

        // Act - エラーにならないこと
        _viewModel.CancelOperation();

        // Assert
        _viewModel.IsCancellationRequested.Should().BeFalse();
        _viewModel.BusyMessage.Should().Be("処理中...");
    }

    [Fact]
    public void CancelOperation_スコープ外では何も起きないこと()
    {
        // Act - CancellationTokenSourceがnullの状態
        _viewModel.CancelOperation();

        // Assert
        _viewModel.IsCancellationRequested.Should().BeFalse();
    }

    #endregion

    #region BusyScope.ReportProgress テスト

    [Fact]
    public void BusyScope_ReportProgress_プログレスが更新されること()
    {
        // Arrange
        using var scope = _viewModel.StartCancellableBusy("処理中");

        // Act
        _viewModel.ScopeReportProgress(50, 100, "50%完了");

        // Assert
        _viewModel.ProgressValue.Should().Be(50);
        _viewModel.ProgressMax.Should().Be(100);
        _viewModel.IsIndeterminate.Should().BeFalse();
        _viewModel.BusyMessage.Should().Be("50%完了");
    }

    [Fact]
    public void BusyScope_ReportProgress_メッセージなしで値のみ更新されること()
    {
        // Arrange
        using var scope = _viewModel.StartCancellableBusy("初期メッセージ");

        // Act
        _viewModel.ScopeReportProgress(3, 10);

        // Assert
        _viewModel.ProgressValue.Should().Be(3);
        _viewModel.ProgressMax.Should().Be(10);
        _viewModel.BusyMessage.Should().Be("初期メッセージ");
    }

    #endregion

    #region BusyScope.ThrowIfCancellationRequested テスト

    [Fact]
    public void BusyScope_ThrowIfCancellationRequested_キャンセル未要求時は例外が発生しないこと()
    {
        // Arrange
        using var scope = _viewModel.StartCancellableBusy("処理中");

        // Act & Assert - 例外が発生しないこと
        var action = () => _viewModel.ScopeThrowIfCancellationRequested();
        action.Should().NotThrow();
    }

    [Fact]
    public void BusyScope_ThrowIfCancellationRequested_キャンセル要求後にOperationCanceledExceptionが発生すること()
    {
        // Arrange
        using var scope = _viewModel.StartCancellableBusy("処理中");
        _viewModel.CancelOperation();

        // Act & Assert
        var action = () => _viewModel.ScopeThrowIfCancellationRequested();
        action.Should().Throw<OperationCanceledException>();
    }

    #endregion

    #region BeginBusy CancellationToken テスト

    [Fact]
    public void BeginBusy_キャンセル不可スコープではCancelOperationが無視されること()
    {
        // Arrange - キャンセル不可のBeginBusy
        using var scope = _viewModel.BeginBusy("処理中");

        // Act
        _viewModel.CancelOperation();

        // Assert - CancellationTokenSourceがnullなので何も起きない
        _viewModel.IsCancellationRequested.Should().BeFalse();
        _viewModel.CanCancel.Should().BeFalse();
    }

    #endregion
}
