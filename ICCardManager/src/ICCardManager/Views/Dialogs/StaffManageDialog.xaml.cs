using System.Windows;
using ICCardManager.ViewModels;

namespace ICCardManager.Views.Dialogs;

/// <summary>
/// 職員管理ダイアログ
/// </summary>
public partial class StaffManageDialog : Window
{
    private readonly StaffManageViewModel _viewModel;

    public StaffManageDialog(StaffManageViewModel viewModel)
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
