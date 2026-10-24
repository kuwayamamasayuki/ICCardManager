using System;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FluentAssertions;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #2307: データ入出力ダイアログの「既存データはスキップする」が、利用履歴詳細を選んでいる間は
    /// 操作できず、効かない旨が画面に出ること。
    /// </summary>
    /// <remarks>
    /// 利用履歴詳細の取り込みはスキップ指定を受け取らない。以前はチェックを操作でき、変えるとプレビューも破棄されたため、
    /// 効く指定に見えていた。選択は <c>ComboBox.Select(int)</c>（展開と SelectionItem パターン）で行い、座標を介さない。
    /// 対: 利用履歴へ戻すと、チェックボックスは再び操作でき、案内は消える。
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class DataImportSkipExistingUiTests
    {
        /// <summary>インポートのデータ種別の並び（<c>DataExportImportViewModel.ImportDataTypes</c>）での位置</summary>
        private const int LedgersIndex = 2;
        private const int LedgerDetailsIndex = 3;

        [Fact]
        public void 利用履歴詳細を選ぶとスキップ指定が無効になり効かない旨が表示されること()
        {
            using var fixture = AppFixture.Launch();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);
            var dialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenDataExportImportButton,
                TestConstants.DataExportImportDialogName);

            var combo = dialog.FindByNameWithRetry(TestConstants.ImportDataTypeComboBox);
            combo.Should().NotBeNull($"「{TestConstants.ImportDataTypeComboBox}」があること");
            var checkBox = dialog.FindByNameWithRetry(TestConstants.ImportSkipExistingCheckBox);
            checkBox.Should().NotBeNull($"「{TestConstants.ImportSkipExistingCheckBox}」があること");

            // 利用履歴詳細: 操作できず、案内が出る
            var comboBox = combo!.AsComboBox();
            comboBox.Select(LedgerDetailsIndex);
            Retry.WhileTrue(() => checkBox!.IsEnabled, TimeSpan.FromSeconds(5));
            checkBox!.IsEnabled.Should().BeFalse("利用履歴詳細の取り込みはスキップ指定を受け取らないため、操作できないこと");
            dialog.FindByNameWithRetry(TestConstants.ImportSkipExistingNotApplicableText)
                .Should().NotBeNull("指定が効かないことが画面から分かること");

            // 対: 利用履歴へ戻すと操作でき、案内は消える
            comboBox.Select(LedgersIndex);
            Retry.WhileFalse(() => checkBox.IsEnabled, TimeSpan.FromSeconds(5));
            checkBox.IsEnabled.Should().BeTrue("利用履歴の取り込みではスキップ指定が効くこと");
            Retry.WhileTrue(() => dialog.FindByName(TestConstants.ImportSkipExistingNotApplicableText) != null, TimeSpan.FromSeconds(5));
            dialog.FindByName(TestConstants.ImportSkipExistingNotApplicableText)
                .Should().BeNull("効く種別では「効きません」の案内を出さないこと");

            dialog.Close();
        }
    }
}
