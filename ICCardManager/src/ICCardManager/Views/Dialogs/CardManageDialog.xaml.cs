using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// カード管理ダイアログ
/// </summary>
public partial class CardManageDialog : Window
{
    private readonly CardManageViewModel _viewModel;

    public CardManageDialog(CardManageViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += async (s, e) => await _viewModel.InitializeAsync();
        Closed += (s, e) => _viewModel.Cleanup();
    }

    /// <summary>
    /// 完了ボタンクリック
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
