using System.Windows;
using ICCardManager.Models;

namespace ICCardManager.Views.Dialogs
{
    /// <summary>
    /// 初回起動時の部署選択ダイアログ（Issue #659）
    /// </summary>
    public partial class DepartmentSelectionDialog : Window
    {
        /// <summary>
        /// 選択された部署種別
        /// </summary>
        public DepartmentType SelectedDepartmentType { get; private set; } = DepartmentType.MayorOffice;

        public DepartmentSelectionDialog()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedDepartmentType = EnterpriseAccountRadio.IsChecked == true
                ? DepartmentType.EnterpriseAccount
                : DepartmentType.MayorOffice;

            DialogResult = true;
            Close();
        }
    }
}
