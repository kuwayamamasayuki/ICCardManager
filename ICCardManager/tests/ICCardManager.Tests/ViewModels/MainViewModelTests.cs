using FluentAssertions;
using ICCardManager.ViewModels;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// MainViewModelの単体テスト
/// </summary>
/// <remarks>
/// <para>
/// MainViewModelはWPF依存（DispatcherTimer）が強いため、
/// コンストラクタ内でDispatcherTimerを直接生成しており、WPFコンテキスト外では
/// インスタンス化できません。
/// </para>
/// <para>
/// このテストファイルでは、テスト可能な部分（AppState列挙型）のみをテストしています。
/// </para>
/// <para>
/// MainViewModelを完全にテスト可能にするには、以下のリファクタリングが必要です:
/// <list type="bullet">
///   <item><description>タイマー機能をITimerインターフェースとして抽象化</description></item>
///   <item><description>コンストラクタ経由でタイマーを注入可能にする</description></item>
///   <item><description>テスト時はモックタイマーを使用</description></item>
/// </list>
/// </para>
/// <para>
/// 状態遷移の仕様については、このファイル末尾の「MainViewModel仕様書」セクションを参照してください。
/// </para>
/// </remarks>
public class MainViewModelTests
{
    #region AppState列挙型テスト

    /// <summary>
    /// AppStateが必要な全ての状態を持つこと
    /// </summary>
    [Fact]
    public void AppState_ShouldHaveAllRequiredStates()
    {
        // Assert
        Enum.GetValues<AppState>().Should().HaveCount(3);
        Enum.IsDefined(typeof(AppState), AppState.WaitingForStaffCard).Should().BeTrue();
        Enum.IsDefined(typeof(AppState), AppState.WaitingForIcCard).Should().BeTrue();
        Enum.IsDefined(typeof(AppState), AppState.Processing).Should().BeTrue();
    }

    /// <summary>
    /// WaitingForStaffCardが0であること（初期状態）
    /// </summary>
    [Fact]
    public void AppState_WaitingForStaffCard_ShouldBeZero()
    {
        // Assert - 初期状態として0が期待される
        ((int)AppState.WaitingForStaffCard).Should().Be(0);
    }

    /// <summary>
    /// AppStateの各状態が異なる値を持つこと
    /// </summary>
    [Fact]
    public void AppState_EachState_ShouldHaveDistinctValue()
    {
        // Arrange
        var states = Enum.GetValues<AppState>();

        // Assert - 全ての状態が一意の値を持つ
        states.Distinct().Should().HaveCount(states.Length);
    }

    /// <summary>
    /// AppStateの状態遷移順序が論理的であること
    /// </summary>
    [Theory]
    [InlineData(AppState.WaitingForStaffCard, 0)]
    [InlineData(AppState.WaitingForIcCard, 1)]
    [InlineData(AppState.Processing, 2)]
    public void AppState_ShouldHaveCorrectOrder(AppState state, int expectedValue)
    {
        // Assert - 状態が期待される順序で定義されている
        ((int)state).Should().Be(expectedValue);
    }

    #endregion
}

/*
================================================================================
MainViewModel 仕様書
================================================================================

このセクションはMainViewModelの動作仕様を文書化したものです。
WPF依存によりユニットテストが困難なため、仕様として記録しています。

--------------------------------------------------------------------------------
1. 状態遷移仕様
--------------------------------------------------------------------------------

1.1 初期状態
    - CurrentState = WaitingForStaffCard
    - StatusMessage = "職員証をタッチしてください"
    - StatusIcon = "👤"
    - StatusBackgroundColor = "#FFFFFF"
    - RemainingSeconds = 0

1.2 職員証タッチ時（WaitingForStaffCard → WaitingForIcCard）
    条件: 有効な職員証IDmが読み取られた場合
    動作:
    - CurrentState が WaitingForIcCard に遷移
    - StatusMessage に職員名が含まれる
    - タイムアウトタイマー（60秒）が開始
    - RemainingSeconds = 60

1.3 ICカードタッチ時（WaitingForIcCard → Processing → WaitingForStaffCard）
    条件: 有効なICカードIDmが読み取られた場合
    動作:
    - カードが未貸出(IsLent=false) → 貸出処理を実行
    - カードが貸出中(IsLent=true) → 返却処理を実行
    - 処理完了後、WaitingForStaffCard に戻る

    貸出時:
    - 背景色 = #FFE0B2（薄いオレンジ）
    - 音 = ピッ（貸出音）
    - アイコン = 🚃→

    返却時:
    - 背景色 = #B3E5FC（薄い水色）
    - 音 = ピピッ（返却音）
    - アイコン = 🏠←

1.4 タイムアウト時（WaitingForIcCard → WaitingForStaffCard）
    条件: 60秒経過
    動作:
    - CurrentState が WaitingForStaffCard に戻る
    - StatusMessage = "職員証をタッチしてください"
    - エラー音が再生される

--------------------------------------------------------------------------------
2. 30秒ルール（再タッチで逆操作）
--------------------------------------------------------------------------------

条件: 同一カードを30秒以内に再タッチ
動作:
- 前回が貸出 → 今回は返却処理を実行
- 前回が返却 → 今回は貸出処理を実行

目的: 誤操作の即時取り消しを可能にする

--------------------------------------------------------------------------------
3. キャンセル機能
--------------------------------------------------------------------------------

3.1 Cancel()メソッド（Escキー）
    - WaitingForIcCard状態の場合: 状態をリセット
    - WaitingForStaffCard状態の場合: 何もしない
    - Processing状態の場合: 何もしない

--------------------------------------------------------------------------------
4. 未登録カード処理
--------------------------------------------------------------------------------

4.1 職員証待ち状態で未登録カードをタッチ
    動作:
    1. カード種別を自動判定（CardTypeDetector使用）
    2. 警告音を再生
    3. 登録確認ダイアログを表示
    4. 「はい」選択 → カード管理画面を開く

4.2 ICカード待ち状態で未登録カードをタッチ
    動作:
    1. 登録確認ダイアログを表示
    2. 処理後、WaitingForStaffCard にリセット

--------------------------------------------------------------------------------
5. 履歴表示
--------------------------------------------------------------------------------

条件: 職員証待ち状態で登録済みICカードをタッチ
動作:
- HistoryDialog（履歴画面）が開く
- 状態は変化しない（WaitingForStaffCardのまま）

--------------------------------------------------------------------------------
6. エラーケース
--------------------------------------------------------------------------------

6.1 ICカード待ち状態で職員証をタッチ
    動作:
    - エラー音が再生される
    - エラーメッセージが表示される
    - 背景色 = #FFEBEE（薄い赤）
    - 状態は変化しない

6.2 処理中にカードをタッチ
    動作:
    - 無視される（何も起きない）

--------------------------------------------------------------------------------
7. 警告チェック（InitializeAsync時）
--------------------------------------------------------------------------------

チェック項目:
1. バス停名未入力の履歴（Summary に "★" が含まれる）
2. 残額が警告閾値未満のカード

結果: WarningMessagesコレクションに警告を追加

--------------------------------------------------------------------------------
8. 定数
--------------------------------------------------------------------------------

- タイムアウト時間: 60秒
- 再タッチ判定時間: 30秒
- 残額警告閾値: 設定画面で変更可能（デフォルト1000円）

================================================================================
*/
