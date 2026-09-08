using System;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// <see cref="TestConstants"/> の定数が「XAML の <c>AutomationProperties.Name</c> と
    /// 完全一致すること」を宣言するマーカー（Issue #2018）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// UI テストは CI（<c>Category!=UI</c>）で実行されないため、定数と XAML のずれは
    /// 誰かが Windows で UI テストを流すまで検出されない。実際 <c>StaffManageDeleteButtonName</c> は
    /// 追加当初（#1500）から <c>"削除"</c>（＝ <c>Content</c> の文字列）のままで、
    /// <c>AutomationProperties.Name="職員削除"</c> とは一度も一致していなかった。
    /// <c>ByName("削除")</c> はボタン内側の Text 要素を掴み、Text は Invoke パターンを持たないため
    /// <c>PatternNotSupportedException</c> になる。
    /// </para>
    /// <para>
    /// このマーカーは実行時には読まれない。CI で走る
    /// <c>ICCardManager.Tests.Views.UiTestAutomationNameConventionTests</c> が
    /// 本ファイルと <c>TestConstants.cs</c> を<b>ソーステキストとして</b>走査し、
    /// マーカーの付いた定数の値が実際の XAML に存在することを検証する
    /// （UITests プロジェクトは net48 / FlaUI 依存で、単体テストから参照したくないため）。
    /// </para>
    /// <para>
    /// <b>マーカーの省略は許されない。</b> <c>AutomationProperties</c> と対応しない定数には
    /// <see cref="NotUiaNameAttribute"/> を付けて「対応しないこと」を明示する。
    /// 省略を許すと、新しい定数を足した人がマーカーを付け忘れたときに検査が静かに素通りし、
    /// #2018 と同じ形（誰も気付かないまま UI テストが壊れている）が再発する。
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class UiaNameAttribute : Attribute
    {
    }

    /// <summary>
    /// 定数が XAML のいずれかの <c>AutomationProperties.Name</c> の<b>前方一致</b>であることを
    /// 宣言するマーカー（Issue #2018）。
    /// </summary>
    /// <remarks>
    /// WPF の <c>Window</c> は <c>AutomationProperties.Name</c> が付いていればそちらが UIA Name になり、
    /// 無ければ <c>Title</c> が使われる。メインウィンドウは両方を持ち、テスト側は
    /// 共通プレフィックスで前方一致させているため、完全一致では検査できない。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class UiaNamePrefixAttribute : Attribute
    {
    }

    /// <summary>
    /// 定数が XAML の <c>AutomationProperties.HelpText</c> と完全一致することを宣言するマーカー
    /// （Issue #2018）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class UiaHelpTextAttribute : Attribute
    {
    }

    /// <summary>
    /// 定数が <c>AutomationProperties</c> のいずれとも対応しないことを宣言するマーカー
    /// （Issue #2018）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TextBlock</c> の本文で要素を探す値（<c>TextBlock</c> は UIA Name が Text へフォールバックする）や、
    /// そもそも要素名ではない値がこれにあたる。<b>なぜ対応しないのか</b>を必ず XML doc か
    /// 直前のコメントに書くこと。
    /// </para>
    /// <para>
    /// マーカーを省略できるようにすると、付け忘れと「意図的に対象外」が区別できず、
    /// 静的検査が fail-open になる。付け忘れを赤にするために、明示のマーカーを用意している。
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    internal sealed class NotUiaNameAttribute : Attribute
    {
    }
}
