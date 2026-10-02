using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FluentAssertions;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// アプリが出す Win32 の MessageBox（確認ダイアログ）を待ち、ボタンで答える（Issue #2190）。
    /// </summary>
    /// <remarks>
    /// 本体の確認はすべて <c>DialogService.ShowConfirmation</c> 等を経て Win32 の MessageBox になる（UT-096）。
    /// UIA ツリー上の位置は一定しない（実測ではトップレベルではなくメイン画面の ModalWindows に現れた）ので、
    /// 探し方は <see cref="DialogLocator"/> の 1 か所に寄せ、Win32 の MessageBox であること（クラス名）は別に表明する。
    /// </remarks>
    internal static class MessageBoxOperations
    {
        /// <summary>Win32 の MessageBox のウィンドウクラス名。</summary>
        public const string ClassName = "#32770";

        /// <summary>「はい」ボタンの名前の先頭（日本語の MessageBox では「はい(Y)」）。</summary>
        public const string YesPrefix = "はい";

        /// <summary>「いいえ」ボタンの名前の先頭（日本語の MessageBox では「いいえ(N)」）。</summary>
        public const string NoPrefix = "いいえ";

        /// <summary>タイトルで MessageBox を待つ。</summary>
        public static Window WaitFor(AppFixture fixture, Window opener, string title)
        {
            Window found;
            try
            {
                found = DialogLocator.WaitForNestedDialog(
                    fixture, opener, title, TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"{ex.Message} 見えていたウィンドウ: {DialogLocator.DescribeOpenWindows(fixture)}", ex);
            }

            found.ClassName.Should().Be(ClassName, $"「{title}」は Win32 の MessageBox であること");
            return found;
        }

        /// <summary>名前が <paramref name="buttonPrefix"/> で始まるボタンを押す（「はい(Y)」の「(Y)」は言語設定で変わり得るので前方一致）。</summary>
        public static void Answer(Window messageBox, string buttonPrefix)
        {
            var button = Retry.WhileNull(
                () => messageBox.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => b.Name.StartsWith(buttonPrefix, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Result;
            button.Should().NotBeNull($"MessageBox「{messageBox.Name}」に「{buttonPrefix}」ボタンがあること");
            button!.AsButton().Invoke();
        }
    }
}
