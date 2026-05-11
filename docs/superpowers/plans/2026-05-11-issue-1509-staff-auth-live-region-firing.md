# Issue #1509: 職員証認証ダイアログ LiveRegion 即時通知発火 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `StaffAuthDialog` の `StatusText` LiveRegion を NVDA/Narrator に対し確実に発火させ、認証成功/失敗/タイムアウト時のメッセージが即時読み上げられるようにする。

**Architecture:** `StatusBorder` を常時可視化して AutomationTree 上に常駐させ、`ShowStatus()` 内で `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を明示発火する。色値リテラルは `AccessibilityStyles.xaml` の DynamicResource 化（Issue #1392 同時解消）。認証成功時は新たに `ShowStatus("認証に成功しました（職員名）")` を一瞬表示してから `Close()`。

**Tech Stack:** WPF (.NET Framework 4.8), C# 10, MVVM Toolkit, xUnit + FluentAssertions, FlaUI 5.0.0 (UI Automation), AccessibilityStyles.xaml (WCAG 2.1 AA 準拠ブラシ)

**Spec:** `docs/superpowers/specs/2026-05-11-issue-1509-staff-auth-live-region-design.md`

**ブランチ:** `fix/issue-1509-live-region-firing`（既に main から作成済み）

---

## File Structure

### Modify
- `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml` — `StatusBorder` を常時可視化（Visibility="Collapsed" 除去 + Background="Transparent" + BorderThickness="0" + MinHeight="44"）
- `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml.cs` — `ShowStatus()` 刷新（DynamicResource + RaiseAutomationEvent）、`OnCardRead` 成功パスに `ShowStatus` 追加、`CloseAfterDelay` ヘルパ抽出、必要な using 追加
- `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs` — 静的検査 4 件追加
- `ICCardManager/CHANGELOG.md` — PR #1500 の誤主張を訂正、Issue #1509 セクション追加
- `docs/design/03_画面設計書.md` — §5.7 に StatusBorder 常時可視化と LiveRegionChanged 明示発火の設計記述追加
- `docs/design/07_テスト設計書.md` — §2.45 拡張、新規 UT-058b 追加

### Create
- `ICCardManager/tests/ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs` — FlaUI による UI Automation 統合テスト 3 件

---

## Task 1: 静的検査テスト 4 件を追加（TDD Red）

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs`

**Background:** 既存の `DialogAutomationPropertiesCoverageTests` には XAML 属性の存在検証はあるが、(a) `StatusBorder` の初期 Visibility、(b) code-behind での `RaiseAutomationEvent` 呼び出し、(c) 成功パスでの `ShowStatus` 呼び出し、(d) 色値リテラル不使用 は未検証。本 Issue の修正をすり抜けさせないために 4 件追加する。

- [ ] **Step 1.1: 既存テストファイルの末尾に 4 件のテストを追加**

ファイル末尾の `private static string ResolveDialogsDirectory()` メソッドより前に以下を挿入する：

```csharp
    /// <summary>
    /// Issue #1509: StatusBorder の初期 Visibility が Collapsed であってはならない。
    /// Collapsed → Visible 遷移は WPF UI Automation の LiveRegionChanged を発火させないため、
    /// 常時可視化して AutomationTree 上に常駐させる必要がある。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_StatusBorder_should_not_be_initially_collapsed()
    {
        var xaml = ReadDialog("StaffAuthDialog.xaml");

        Regex.IsMatch(xaml,
            @"x:Name=""StatusBorder""[\s\S]*?Visibility=""Collapsed""")
            .Should().BeFalse(
            "StaffAuthDialog: StatusBorder の初期 Visibility が Collapsed だと " +
            "AutomationTree から除外され、Text 更新時に LiveRegionChanged が発火しない（Issue #1509）。" +
            "Background=\"Transparent\" + BorderThickness=\"0\" で常時可視化すること。");
    }

    /// <summary>
    /// Issue #1509: ShowStatus 内で UIElementAutomationPeer.RaiseAutomationEvent を
    /// 明示呼び出ししないと、Text 代入だけでは LiveRegionChanged が確実に発火しない。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_code_behind_should_raise_LiveRegionChanged()
    {
        var codeBehind = ReadCodeBehind("StaffAuthDialog.xaml.cs");

        codeBehind.Should().Contain(
            "RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)",
            "StaffAuthDialog: Text 更新だけでは LiveRegionChanged が確実に発火しないため、" +
            "ShowStatus 内で UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged) を明示呼び出しすること（Issue #1509）。");
    }

    /// <summary>
    /// Issue #1509: 認証成功時にも ShowStatus でステータス表示し、
    /// スクリーンリーダーに認証成功を通知する。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_authentication_success_path_should_call_ShowStatus()
    {
        var codeBehind = ReadCodeBehind("StaffAuthDialog.xaml.cs");

        // OnCardRead の if (staff != null) { ... } ブロック内に ShowStatus 呼び出しが必要。
        // 「認証に成功」というメッセージリテラルで成功パスを識別する。
        Regex.IsMatch(codeBehind, @"ShowStatus\(\$?""認証に成功")
            .Should().BeTrue(
            "StaffAuthDialog: 認証成功時にも ShowStatus(\"認証に成功しました...\") を呼び出して " +
            "スクリーンリーダー利用者に成功を通知すること（Issue #1509）。");
    }

    /// <summary>
    /// Issue #1509/Issue #1392: ShowStatus メソッド本体に色値リテラル（#RRGGBB）を
    /// 直接記述してはならない。AccessibilityStyles.xaml のブラシキーを
    /// DynamicResource / FindResource 経由で参照すること。
    /// </summary>
    [Fact]
    public void StaffAuthDialog_ShowStatus_should_use_DynamicResource_for_colors()
    {
        var codeBehind = ReadCodeBehind("StaffAuthDialog.xaml.cs");

        // ShowStatus メソッド本体を抽出
        var showStatusBody = ExtractMethodBody(codeBehind, "ShowStatus");

        Regex.IsMatch(showStatusBody, @"0x[0-9A-Fa-f]{2}")
            .Should().BeFalse(
            "StaffAuthDialog.ShowStatus: 色値リテラル（0xFF, 0xEB 等）は AccessibilityStyles.xaml の " +
            "ブラシキー（ErrorBackgroundBrush / SuccessBackgroundBrush 等）を FindResource 経由で参照すること（Issue #1392）。" +
            "現在のメソッド本体: " + showStatusBody);
    }

    /// <summary>
    /// code-behind ファイルを読み込む。Views/Dialogs/ 配下を想定。
    /// </summary>
    private static string ReadCodeBehind(string fileName)
    {
        var path = Path.Combine(DialogsDirectory, fileName);
        File.Exists(path).Should().BeTrue($"code-behind {fileName} が存在すべき");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// 指定メソッドの本体（{ ... } の中身）をテキストから抽出する。
    /// ネストブロックは数えず、最初の閉じカッコまでを取る。
    /// </summary>
    private static string ExtractMethodBody(string code, string methodName)
    {
        var pattern = $@"\b{Regex.Escape(methodName)}\s*\([^)]*\)\s*(?::\s*base\([^)]*\))?\s*\{{";
        var startMatch = Regex.Match(code, pattern);
        if (!startMatch.Success)
        {
            throw new InvalidOperationException($"メソッド {methodName} が見つかりません");
        }

        var start = startMatch.Index + startMatch.Length;
        var depth = 1;
        var pos = start;
        while (pos < code.Length && depth > 0)
        {
            if (code[pos] == '{') depth++;
            else if (code[pos] == '}') depth--;
            pos++;
        }
        return code.Substring(start, pos - start - 1);
    }
```

- [ ] **Step 1.2: テスト実行して 4 件全て失敗することを確認（RED）**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~DialogAutomationPropertiesCoverageTests"`

Expected: 4 件失敗
- `StaffAuthDialog_StatusBorder_should_not_be_initially_collapsed` — FAIL: 現在 `Visibility="Collapsed"` あり
- `StaffAuthDialog_code_behind_should_raise_LiveRegionChanged` — FAIL: `RaiseAutomationEvent` 未呼び出し
- `StaffAuthDialog_authentication_success_path_should_call_ShowStatus` — FAIL: 成功時 ShowStatus なし
- `StaffAuthDialog_ShowStatus_should_use_DynamicResource_for_colors` — FAIL: 色値リテラルあり

- [ ] **Step 1.3: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Views/Dialogs/DialogAutomationPropertiesCoverageTests.cs
git commit -m "$(cat <<'EOF'
test: Issue #1509 の回帰防止用静的検査テストを追加（RED）

DialogAutomationPropertiesCoverageTests に以下 4 件を追加:
- StatusBorder が初期 Collapsed でないこと
- code-behind に RaiseAutomationEvent(LiveRegionChanged) が含まれること
- 認証成功パスに ShowStatus 呼び出しが含まれること
- ShowStatus メソッド本体に色値リテラルが含まれないこと

現状の StaffAuthDialog では 4 件全て失敗（TDD Red）。
後続コミットで XAML / code-behind を修正して Green にする。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: StaffAuthDialog.xaml を修正

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml:60-73`

**Background:** `StatusBorder` の初期 `Visibility="Collapsed"` を撤廃し常時可視化する。視覚的中立性を保つため初期は `Background="Transparent"` + `BorderThickness="0"` + `MinHeight="44"`（WCAG 2.1 SC 2.5.5 ターゲットサイズ準拠 & レイアウトジャンプ防止）。

- [ ] **Step 2.1: StatusBorder と StatusText の XAML を差し替え**

`StaffAuthDialog.xaml:60-73` 周辺の `<!-- ステータスメッセージ -->` セクションを以下に置き換える：

```xml
        <!-- ステータスメッセージ -->
        <!-- Issue #1509: StatusBorder は常時可視化して AutomationTree 上に常駐させる。
             Collapsed → Visible 遷移では LiveRegionChanged が発火しないため、
             Text 更新時に code-behind から AutomationPeer.RaiseAutomationEvent を明示発火する。
             初期は背景透明・枠なしで視覚的に中立、Text 設定時に背景色・枠を切り替える。 -->
        <Border x:Name="StatusBorder" Grid.Row="2" CornerRadius="4" Padding="12" Margin="0,0,0,15"
                Background="Transparent" BorderThickness="0" MinHeight="44">
            <!-- Issue #1468/#1509: 動的更新される TextBlock。Name を付けず Text 自体を読み上げ対象にし、
                 LiveSetting=Assertive でステータス変化（成功・失敗）を即時通知する。 -->
            <TextBlock x:Name="StatusText"
                       Text=""
                       FontSize="{DynamicResource BaseFontSize}"
                       TextWrapping="Wrap"
                       HorizontalAlignment="Center"
                       VerticalAlignment="Center"
                       AutomationProperties.HelpText="認証処理の現在の状態（成功・失敗・進行中など）"
                       AutomationProperties.LiveSetting="Assertive"/>
        </Border>
```

主な変更点：
- `Background="{DynamicResource LendingBackgroundBrush}" Visibility="Collapsed"` を削除
- 代わりに `Background="Transparent" BorderThickness="0" MinHeight="44"` を追加
- `StatusText` の `Foreground` 属性を削除（code-behind から DynamicResource で設定）
- `StatusText` に `VerticalAlignment="Center"` 追加（空 Border での text 中央配置）

- [ ] **Step 2.2: 該当する静的検査 1 件が GREEN になることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName=ICCardManager.Tests.Views.Dialogs.DialogAutomationPropertiesCoverageTests.StaffAuthDialog_StatusBorder_should_not_be_initially_collapsed"`

Expected: PASS

- [ ] **Step 2.3: 既存 XAML 静的検査が回帰していないか確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~DialogAutomationPropertiesCoverageTests.StaffAuthDialog"`

Expected: `StatusBorder_should_not_be_initially_collapsed` と `status_text_should_have_assertive_live_setting` と `timeout_text_should_have_polite_live_setting` と `Dynamic_text_blocks_should_not_have_AutomationProperties_Name` が PASS。残り 3 件（code-behind 関連）は FAIL のまま。

- [ ] **Step 2.4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml
git commit -m "$(cat <<'EOF'
fix(xaml): Issue #1509 StatusBorder を常時可視化して AutomationTree に常駐させる

Collapsed → Visible 遷移は WPF UI Automation の LiveRegionChanged を発火させないため、
StatusBorder の初期 Visibility="Collapsed" を撤廃し、Background="Transparent" +
BorderThickness="0" + MinHeight="44" で視覚的に中立な状態で常時可視化する。
Text 設定時に code-behind から背景色・枠を切り替える設計とし、後続コミットで対応。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: ShowStatus() を DynamicResource 化 + RaiseAutomationEvent 追加

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml.cs:180-199` (`ShowStatus` メソッド)
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml.cs:1-12` (using 文)

**Background:** `ShowStatus()` 内の色値リテラル直接指定（Issue #1392 違反）を `AccessibilityStyles.xaml` の DynamicResource 化。同時に `UIElementAutomationPeer.RaiseAutomationEvent(LiveRegionChanged)` を明示呼び出してスクリーンリーダー通知を発火させる。

- [ ] **Step 3.1: using 文を追加**

`StaffAuthDialog.xaml.cs:1-12` のファイル先頭の using 文ブロック末尾に以下 2 行を追加：

```csharp
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
```

（`System.Windows.Media` は既存の `SolidColorBrush` 利用で既に解決可能だが、明示する）

- [ ] **Step 3.2: ShowStatus メソッドを差し替え**

`StaffAuthDialog.xaml.cs:180-199` の `ShowStatus` メソッド全体を以下に差し替える：

```csharp
        /// <summary>
        /// ステータスメッセージを表示し、スクリーンリーダーに LiveRegion 通知を発火する。
        /// </summary>
        /// <remarks>
        /// Issue #1509: Text 更新だけでは LiveRegionChanged が確実に発火しないため、
        /// UIElementAutomationPeer.RaiseAutomationEvent で明示的に通知する。
        /// Issue #1392: 色値は AccessibilityStyles.xaml のブラシキーを FindResource で参照する
        /// （色値の Single Source of Truth）。
        /// </remarks>
        private void ShowStatus(string message, bool isError)
        {
            StatusText.Text = message;

            // 視覚的状態の切り替え（DynamicResource ブラシ参照、Issue #1392）
            var bgKey = isError ? "ErrorBackgroundBrush" : "SuccessBackgroundBrush";
            var fgKey = isError ? "ErrorForegroundBrush" : "SuccessForegroundBrush";
            var borderKey = isError ? "ErrorBorderBrush" : "SuccessBorderBrush";

            StatusBorder.Background = (Brush)Application.Current.FindResource(bgKey);
            StatusBorder.BorderBrush = (Brush)Application.Current.FindResource(borderKey);
            StatusBorder.BorderThickness = new Thickness(1);
            StatusText.Foreground = (Brush)Application.Current.FindResource(fgKey);

            // スクリーンリーダーへの即時通知（Issue #1509）
            var peer = UIElementAutomationPeer.FromElement(StatusText)
                       ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
```

主な変更：
- 色値 `Color.FromRgb(0xFF, 0xEB, 0xEE)` 等を全て削除し `FindResource` 経由に統一
- `StatusBorder.Background` も `FindResource` 経由（既存は XAML 上で DynamicResource だったが、Task 2 で `Transparent` に変更したため code-behind から設定する）
- `BorderBrush` + `BorderThickness=1` を追加して視覚的境界を明示
- 末尾に `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を追加

- [ ] **Step 3.3: ビルドが通ることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj`

Expected: ビルド成功、警告ゼロ

- [ ] **Step 3.4: 2 件の静的検査が GREEN になることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName=ICCardManager.Tests.Views.Dialogs.DialogAutomationPropertiesCoverageTests.StaffAuthDialog_code_behind_should_raise_LiveRegionChanged" --filter "FullyQualifiedName=ICCardManager.Tests.Views.Dialogs.DialogAutomationPropertiesCoverageTests.StaffAuthDialog_ShowStatus_should_use_DynamicResource_for_colors"`

注: dotnet test の `--filter` は 1 個だけ受け付けるので個別実行する：

```bash
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~StaffAuthDialog_code_behind_should_raise_LiveRegionChanged"
"$DOTNET" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~StaffAuthDialog_ShowStatus_should_use_DynamicResource_for_colors"
```

Expected: 両方 PASS

- [ ] **Step 3.5: コミット**

```bash
git add ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml.cs
git commit -m "$(cat <<'EOF'
fix: Issue #1509 ShowStatus() で LiveRegionChanged を明示発火 + 色値を DynamicResource 化

- StatusBorder.Background / BorderBrush / BorderThickness と StatusText.Foreground を
  AccessibilityStyles.xaml のブラシキー（ErrorBackgroundBrush / SuccessBackgroundBrush 等）
  に FindResource 経由で統一（Issue #1392 同時解消）。
- 末尾で UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)
  を明示呼び出ししてスクリーンリーダー通知を発火（Issue #1509 本体）。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: 認証成功パスに ShowStatus 追加 + CloseAfterDelay ヘルパ抽出

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml.cs:114-178` (`OnTimeoutTick` と `OnCardRead`)

**Background:** 認証成功時も `ShowStatus("認証に成功しました（職員名）", false)` を一瞬表示してから閉じる。タイムアウト失敗側の「1 秒後に Close」ロジックと共通化するため `CloseAfterDelay` ヘルパを抽出する。

- [ ] **Step 4.1: OnTimeoutTick を CloseAfterDelay 利用に書き換える**

`StaffAuthDialog.xaml.cs:114-143` の `OnTimeoutTick` メソッドのうち、タイムアウト分岐内の `DispatcherTimer` ブロックを以下に差し替える：

変更前（126-142行目相当）：
```csharp
            if (_remainingSeconds <= 0)
            {
                // タイムアウト
                _timeoutTimer.Stop();
                _soundPlayer.Play(SoundType.Warning);
                ShowStatus("認証がタイムアウトしました", isError: true);

                // 少し待ってから閉じる
                var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                closeTimer.Tick += (s, args) =>
                {
                    closeTimer.Stop();
                    DialogResult = false;
                    Close();
                };
                closeTimer.Start();
            }
```

変更後：
```csharp
            if (_remainingSeconds <= 0)
            {
                // タイムアウト
                _timeoutTimer.Stop();
                _soundPlayer.Play(SoundType.Warning);
                ShowStatus("認証がタイムアウトしました", isError: true);

                // 少し待ってから閉じる（Issue #1509: CloseAfterDelay に集約）
                CloseAfterDelay(TimeSpan.FromSeconds(1), dialogResult: false);
            }
```

- [ ] **Step 4.2: OnCardRead の認証成功パスに ShowStatus 追加**

`StaffAuthDialog.xaml.cs:145-178` の `OnCardRead` メソッドのうち、`if (staff != null)` ブロックを以下に差し替える：

変更前（154-165行目相当）：
```csharp
                    if (staff != null)
                    {
                        // 職員として登録されている → 認証成功
                        AuthenticatedIdm = e.Idm;
                        AuthenticatedStaffName = staff.Name;

                        _soundPlayer.Play(SoundType.Notify);
                        _timeoutTimer.Stop();

                        DialogResult = true;
                        Close();
                    }
```

変更後：
```csharp
                    if (staff != null)
                    {
                        // 職員として登録されている → 認証成功
                        AuthenticatedIdm = e.Idm;
                        AuthenticatedStaffName = staff.Name;

                        _soundPlayer.Play(SoundType.Notify);
                        _timeoutTimer.Stop();

                        // Issue #1509: 成功時もステータス表示してスクリーンリーダーに通知。
                        // 700ms 後にクローズ（タイムアウト失敗側と同じ CloseAfterDelay テンプレート）。
                        ShowStatus($"認証に成功しました（{staff.Name}）", isError: false);
                        CloseAfterDelay(TimeSpan.FromMilliseconds(700), dialogResult: true);
                    }
```

- [ ] **Step 4.3: CloseAfterDelay プライベートメソッドを追加**

`StaffAuthDialog.xaml.cs` の `ShowStatus` メソッドの直前（`OnCardRead` の直後）に以下を挿入：

```csharp
        /// <summary>
        /// 指定遅延後にダイアログを閉じる（Issue #1509）。
        /// </summary>
        /// <remarks>
        /// 認証成功（700ms 表示）とタイムアウト失敗（1 秒表示）の両方で使用される
        /// 共通テンプレート。スクリーンリーダー利用者にステータスを読み上げる時間を確保するため、
        /// 即座に Close せず短時間だけ表示する。
        /// </remarks>
        private void CloseAfterDelay(TimeSpan delay, bool dialogResult)
        {
            var closeTimer = new DispatcherTimer { Interval = delay };
            closeTimer.Tick += (s, args) =>
            {
                closeTimer.Stop();
                DialogResult = dialogResult;
                Close();
            };
            closeTimer.Start();
        }
```

- [ ] **Step 4.4: ビルドが通ることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/src/ICCardManager/ICCardManager.csproj`

Expected: ビルド成功、警告ゼロ

- [ ] **Step 4.5: 残りの静的検査 1 件と全体が GREEN になることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests --filter "FullyQualifiedName~DialogAutomationPropertiesCoverageTests"`

Expected: 全 43 件（既存 39 件 + 新規 4 件） PASS

- [ ] **Step 4.6: 全 Tests プロジェクトを実行して回帰がないことを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests`

Expected: 全件 PASS、警告ゼロ（既存 3128 件 + 新規 4 件 = 3132 件）

- [ ] **Step 4.7: コミット**

```bash
git add ICCardManager/src/ICCardManager/Views/Dialogs/StaffAuthDialog.xaml.cs
git commit -m "$(cat <<'EOF'
fix: Issue #1509 認証成功時にもステータス表示してスクリーンリーダーに通知

- OnCardRead の認証成功パスで ShowStatus(\$\"認証に成功しました（{name}）\") を呼び出し、
  CloseAfterDelay(700ms) で短時間表示してからクローズ。
- OnTimeoutTick のタイムアウトクローズロジックも CloseAfterDelay に集約し、
  認証成功（700ms）/タイムアウト失敗（1 秒）の共通テンプレートとして再利用。
- DialogAutomationPropertiesCoverageTests 全 43 件 PASS（うち Issue #1509 関連 4 件）。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: UI Automation 統合テストを ICCardManager.UITests に追加

**Files:**
- Create: `ICCardManager/tests/ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs`

**Background:** FlaUI 5.0 で StaffAuthDialog を実起動し、StatusText 要素の `AutomationProperties.LiveSetting` と Text 更新が UIA から観察できることを検証する。`AppFixture.Launch()` パターンを踏襲。

**StaffAuthDialog をトリガーする経路（調査済み）:**
1. ツールバー「職員管理」ボタンクリック → `StaffManageDialog` が開く
2. 職員一覧で既存職員を選択し「削除」ボタンクリック → `StaffManageViewModel.cs:424` が `_staffAuthService.RequestAuthenticationAsync("職員の削除")` を呼び、`StaffAuthDialog` が表示される
3. `StaffAuthService.cs:46` で `new StaffAuthDialog(...)` を直接生成

**前提条件:** 削除可能な職員が事前登録されていること。`AppFixture` のテスト DB（`%ProgramData%\ICCardManager\iccard.db`）に「FFFF000000000001」等の IDm で職員を登録するヘルパが必要。これは Step 5.1 で `seed_test_staff.sql` を用意するか、テスト内で職員追加ダイアログを経由する。

**重要な制約:** UI Automation で **クロスプロセスの LiveRegionChangedEvent 購読は WPF/UIA3 の制限により不安定** なケースがある。本テストでは確実に観察できる **(a) StatusText の AutomationElement が UIA tree から発見可能であること**、**(b) Text 更新後に Name/LegacyIAccessible.Value がメッセージに更新されていること**、**(c) LiveSetting プロパティが Assertive であること** を検証する。これによりバグの構造的再発（Visibility Collapsed による AutomationTree 除外）を確実に検出できる。

- [ ] **Step 5.1: テスト用の職員データを事前投入する仕組みを追加**

`ICCardManager/tests/ICCardManager.UITests/Infrastructure/AppFixture.cs` に新規メソッド `LaunchWithSeededStaff()` を追加して、起動前にテスト職員 1 件を DB に投入できるようにする。

`AppFixture.cs` の `Launch()` メソッドの直前（または近く）に以下を追加：

```csharp
        /// <summary>
        /// テスト用の職員 1 件を事前投入してアプリを起動する。
        /// </summary>
        /// <remarks>
        /// StaffAuthDialog をトリガーするテスト用。
        /// IDm "FFFF000000000001"（DebugVirtualTouchButton と一致）で職員「テスト職員」を登録する。
        /// 既存 DB がある場合は退避し、新規空 DB を作って職員を投入する。
        /// </remarks>
        public static AppFixture LaunchWithSeededStaff()
        {
            // 既存 DB を退避
            var dbBackedUp = false;
            if (File.Exists(DbPath))
            {
                dbBackedUp = CopyFileWithRetry(DbPath, DbBackupPath, maxRetries: 5, delayMs: 500);
            }

            // 空 DB ファイルを削除（アプリ初回起動でマイグレーション実行）
            try { File.Delete(DbPath); } catch { /* ignore */ }

            // アプリを起動して初期マイグレーションを実行させる
            var initialFixture = Launch();
            initialFixture.Dispose();

            // SQLite に職員を直接 INSERT する（テスト用ヘルパ）
            using (var conn = new System.Data.SQLite.SQLiteConnection($"Data Source={DbPath};Version=3"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR IGNORE INTO staff (idm, name, is_active, created_at) " +
                    "VALUES ('FFFF000000000001', 'テスト職員', 1, datetime('now'))";
                cmd.ExecuteNonQuery();
            }

            // 再度起動（投入済み DB を使う）
            return Launch();
        }
```

注: `System.Data.SQLite` への参照が `ICCardManager.UITests.csproj` に必要。既存にあるか確認し、無ければ追加：

```bash
grep -i 'sqlite' ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj
```

なければ `<PackageReference Include="System.Data.SQLite.Core" Version="1.0.118" />` を追加（メインプロジェクトと同バージョンを揃える）。

- [ ] **Step 5.2: TestConstants.cs に必要な定数を追加（不足分のみ）**

`ICCardManager/tests/ICCardManager.UITests/Infrastructure/TestConstants.cs` を開き、以下を追加（既存と重複しない場合のみ）：

```csharp
        /// <summary>
        /// StaffAuthDialog のウィンドウタイトル（Title="職員証による認証"）。
        /// </summary>
        public const string StaffAuthDialogName = "職員証による認証";

        /// <summary>
        /// StaffAuthDialog の StatusText に付与される AutomationProperties.HelpText の値。
        /// AutomationId が無い場合は HelpText で要素を識別する。
        /// </summary>
        public const string StaffAuthStatusHelpText = "認証処理の現在の状態（成功・失敗・進行中など）";

        /// <summary>
        /// デバッグ用仮想タッチボタンの AutomationProperties.Name。
        /// DEBUG ビルド時のみ表示される（Issue #688）。
        /// </summary>
        public const string DebugVirtualTouchButtonName = "職員証仮想タッチ（デバッグ用）";

        /// <summary>
        /// StaffAuthDialog のキャンセルボタン名。
        /// </summary>
        public const string StaffAuthCancelButtonName = "キャンセル";
```

- [ ] **Step 5.3: テストファイルを新規作成**

`ICCardManager/tests/ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs` を新規作成：

```csharp
using System;
using System.Linq;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using ICCardManager.UITests.Infrastructure;
using ICCardManager.UITests.PageObjects;
using Xunit;

namespace ICCardManager.UITests.Tests
{
    /// <summary>
    /// Issue #1509: StaffAuthDialog の StatusText が UIA tree から発見可能であり、
    /// 認証失敗・タイムアウト・成功の各シナリオで Text 更新が UIA から観察できることを検証する。
    /// </summary>
    /// <remarks>
    /// PR #1500 で StatusText に LiveSetting="Assertive" を付与したが、
    /// StatusBorder の Visibility="Collapsed" 起点では AutomationTree から除外され
    /// スクリーンリーダーが沈黙していた。本テストは Visibility Collapsed の再混入による
    /// 構造的回帰を確実に検出する。
    /// </remarks>
    [Collection("UI")]
    [Trait("Category", "UI")]
    public class StaffAuthDialogLiveRegionTests
    {
        [Fact]
        public void 認証失敗時_StatusTextがUIAtreeから発見可能でメッセージが反映されること()
        {
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            // 仮想タッチボタンで未登録カードをタッチ（DEBUG ビルドでは "FFFF000000000001" が DB 未登録）
            var virtualTouch = dialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.DebugVirtualTouchButtonName));
            virtualTouch.Should().NotBeNull(
                "DEBUG ビルドでは仮想タッチボタンが表示されるべき。Release ビルドの場合は本テストをスキップ。");
            virtualTouch!.AsButton().Invoke();

            // StatusText 要素が UIA tree から発見できることを検証
            var statusText = Retry.WhileNull(
                () => dialog.FindFirstDescendant(
                    cf => cf.ByHelpText(TestConstants.StaffAuthStatusHelpText)),
                TimeSpan.FromSeconds(3)).Result;

            statusText.Should().NotBeNull(
                "Issue #1509: StatusText が UIA tree から発見できない。" +
                "StatusBorder.Visibility=Collapsed で AutomationTree から除外されている可能性。");

            // Text 内容（Name フォールバック）に失敗メッセージが含まれること
            var displayText = statusText!.Name;
            displayText.Should().Contain("登録されていません",
                "Issue #1509: 失敗メッセージが StatusText に反映されていない。" +
                $"実際の Name: '{displayText}'");

            // LiveSetting=Assertive が UIA から観察できること
            var liveSetting = statusText.Properties.LiveSetting.ValueOrDefault;
            liveSetting.Should().Be(LiveSetting.Assertive,
                "Issue #1509: StatusText の LiveSetting が Assertive でない。");

            // 後片付け: キャンセルでダイアログを閉じる
            var cancelButton = dialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.StaffAuthCancelButtonName));
            cancelButton?.AsButton().Invoke();
        }

        [Fact]
        public void タイムアウト時_StatusTextがUIAtreeから発見可能でタイムアウトメッセージが反映されること()
        {
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            // タイムアウト経過を待つ（AppOptions.StaffCardTimeoutSeconds + 余裕、デフォルト 60 秒）。
            // タイムアウト時間が長すぎる場合は AppFixture 起動時の設定オーバーライドを検討。
            // 暫定: 短縮版コードパスがあれば使う。なければ 65 秒待機する設計。
            var statusText = Retry.WhileNull(
                () => dialog.FindFirstDescendant(
                    cf => cf.ByHelpText(TestConstants.StaffAuthStatusHelpText)),
                TimeSpan.FromSeconds(70)).Result;

            statusText.Should().NotBeNull(
                "Issue #1509: タイムアウト後に StatusText が UIA tree から発見できるべき");

            // タイムアウトメッセージが反映されているか
            Retry.WhileFalse(
                () => statusText!.Name.Contains("タイムアウト"),
                TimeSpan.FromSeconds(5));

            statusText!.Name.Should().Contain("タイムアウト",
                "Issue #1509: タイムアウトメッセージが StatusText に反映されていない");
        }

        [Fact]
        public void 認証成功時_StatusTextに成功メッセージが反映されてからダイアログが閉じること()
        {
            // LaunchWithSeededStaff で IDm "FFFF000000000001" の「テスト職員」を事前投入済み。
            // 仮想タッチボタンは "FFFF000000000001" を返すので、認証成功パスに入る。
            using var fixture = AppFixture.LaunchWithSeededStaff();
            var page = new MainWindowPage(fixture.MainWindow, fixture.Automation);

            var dialog = TriggerStaffAuthDialog(page, fixture);

            var virtualTouch = dialog.FindFirstDescendant(
                cf => cf.ByName(TestConstants.DebugVirtualTouchButtonName));
            virtualTouch.Should().NotBeNull();
            virtualTouch!.AsButton().Invoke();

            // 成功時は 700ms 後に閉じるため、Status 反映タイミングを早めに捕捉する。
            // ダイアログがまだ開いている間にスナップショットを取る。
            var statusText = Retry.WhileNull(
                () => dialog.FindFirstDescendant(
                    cf => cf.ByHelpText(TestConstants.StaffAuthStatusHelpText)),
                TimeSpan.FromMilliseconds(500)).Result;

            statusText.Should().NotBeNull(
                "Issue #1509: 認証成功時にも StatusText が UIA tree から発見できるべき");
            statusText!.Name.Should().Contain("認証に成功",
                "Issue #1509: 認証成功メッセージが StatusText に反映されているべき。" +
                $"実際の Name: '{statusText.Name}'");

            // 700ms 後にダイアログが自動クローズすることを確認
            Retry.WhileTrue(
                () => fixture.MainWindow.ModalWindows.Length > 0,
                TimeSpan.FromSeconds(3));

            fixture.MainWindow.ModalWindows.Should().BeEmpty(
                "Issue #1509: 認証成功後 700ms でダイアログが自動的に閉じるべき");
        }

        /// <summary>
        /// StaffAuthDialog をトリガーするヘルパ。
        /// 職員管理 → 既存職員選択 → 削除ボタン経由で StaffAuthDialog を開く。
        /// </summary>
        /// <remarks>
        /// 前提: AppFixture.LaunchWithSeededStaff() で「テスト職員」が投入済み。
        /// 経路: ツールバー「職員管理」 → StaffManageDialog 表示 → 職員選択 → 削除ボタン → StaffAuthDialog 表示
        /// </remarks>
        private static Window TriggerStaffAuthDialog(MainWindowPage page, AppFixture fixture)
        {
            // 1. 職員管理ダイアログを開く
            var staffManageDialog = page.ClickToolbarButtonAndWaitForDialog(
                TestConstants.OpenStaffManageButton,
                TestConstants.StaffManageDialogName);

            // 2. 一覧の最初の職員行を選択（テスト職員が 1 件投入されている前提）
            var dataGrid = staffManageDialog.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.DataGrid));
            dataGrid.Should().NotBeNull("StaffManageDialog に DataGrid が存在すべき");
            var firstRow = dataGrid!.FindFirstDescendant(
                cf => cf.ByControlType(ControlType.DataItem));
            firstRow.Should().NotBeNull("テスト職員が一覧に表示されているべき");
            firstRow!.Click();

            // 3. 削除ボタンクリック → StaffAuthDialog 表示
            var deleteButton = staffManageDialog.FindFirstDescendant(
                cf => cf.ByName("削除"));
            deleteButton.Should().NotBeNull("削除ボタンが存在すべき");
            deleteButton!.AsButton().Invoke();

            // 4. StaffAuthDialog の出現を待つ
            var authDialog = Retry.WhileNull(
                () => fixture.MainWindow.ModalWindows
                    .FirstOrDefault(w => w.Name == TestConstants.StaffAuthDialogName),
                TimeSpan.FromSeconds(5)).Result;

            authDialog.Should().NotBeNull(
                $"StaffAuthDialog（{TestConstants.StaffAuthDialogName}）が表示されるべき");
            return authDialog!.AsWindow();
        }
    }
}
```

**注意:** `MainWindowPage.ClickToolbarButtonAndWaitForDialog` は既存ヘルパ（`DialogNavigationTests` で使用実績あり）。`StaffManageDialog` 内の DataGrid と「削除」ボタンの検索は FlaUI の `ByControlType` / `ByName` で行う（標準パターン）。

- [ ] **Step 5.4: ビルドが通ることを確認**

Run: `"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj`

Expected: ビルド成功

- [ ] **Step 5.5: UI Automation テストを実行**

メインアプリを先にビルド（`--no-build` 起動の前提）：

```bash
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" build ICCardManager/src/ICCardManager/ICCardManager.csproj -c Debug
"$DOTNET" test ICCardManager/tests/ICCardManager.UITests --filter "FullyQualifiedName~StaffAuthDialogLiveRegionTests"
```

Expected: 全 3 ケース PASS（`LaunchWithSeededStaff` でテスト職員が投入されているため、認証成功パスも検証可能）。タイムアウトテストは 60+秒かかるため、CI でも 2 分以内に完了する。

- [ ] **Step 5.6: コミット**

```bash
git add ICCardManager/tests/ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs ICCardManager/tests/ICCardManager.UITests/Infrastructure/TestConstants.cs ICCardManager/tests/ICCardManager.UITests/Infrastructure/AppFixture.cs ICCardManager/tests/ICCardManager.UITests/ICCardManager.UITests.csproj
git commit -m "$(cat <<'EOF'
test(ui): Issue #1509 StaffAuthDialog StatusText の UIA 観察可能性を検証する統合テスト追加

FlaUI 5.0 で StaffAuthDialog を実起動し、StatusText 要素が UIA tree から発見可能で、
認証失敗/タイムアウト/成功の各シナリオで Text 更新と LiveSetting=Assertive が
観察できることを検証する。

- 認証失敗時: 仮想タッチで未登録カードをタッチ → StatusText に失敗メッセージが反映
- タイムアウト時: 60+秒経過 → StatusText にタイムアウトメッセージが反映
- 認証成功時: ベストエフォート検証（'FFFF000000000001' 登録済み環境のみ）

PR #1500 の StatusBorder.Visibility=Collapsed 起点バグ（Issue #1509）の構造的回帰を
確実に検出する層を追加。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: ドキュメント同期更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`
- Modify: `docs/design/03_画面設計書.md`
- Modify: `docs/design/07_テスト設計書.md`

- [ ] **Step 6.1: CHANGELOG.md を更新**

`ICCardManager/CHANGELOG.md` を開き、`## [Unreleased]` セクション（または現行 `## [2.8.1]` の上）に以下を追加：

```markdown
## [Unreleased]

**バグ修正**
- 職員証認証ダイアログ（`StaffAuthDialog`）のスクリーンリーダー（NVDA / Narrator）への即時通知が発火しない不具合を修正。PR #1500（Issue #1468）で `StatusText` に `AutomationProperties.LiveSetting="Assertive"` を付与したが、(1) `StatusBorder` の初期 `Visibility="Collapsed"` により子要素 `StatusText` が AutomationTree から除外されていた、(2) WPF UI Automation の LiveRegion は「既に可視な要素の Text 変化」のみを通知し Visibility 変化に伴う要素出現は対象外、(3) `TextBlock.Text` 更新だけでは LiveRegionChanged が確実に発火しない、という 3 つの原因で実機検証時に沈黙していた。修正内容: (a) `StatusBorder` を常時可視化（`Background="Transparent" BorderThickness="0" MinHeight="44"`）して AutomationTree に常駐させ、`Text` 設定時に code-behind から背景色・枠を切り替える、(b) `ShowStatus()` 末尾で `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を明示呼び出してスクリーンリーダー通知を発火、(c) 認証成功時にも `ShowStatus($"認証に成功しました（{職員名}）", false)` を呼び出して 700ms 表示してから閉じる（タイムアウト失敗側と同じ `CloseAfterDelay` テンプレートで共通化）。同時に `ShowStatus()` 内の色値リテラル直接指定（`Color.FromRgb(0xFF, 0xEB, 0xEE)` 等）を `AccessibilityStyles.xaml` のブラシキー（`ErrorBackgroundBrush` / `SuccessBackgroundBrush` / `ErrorBorderBrush` / `SuccessBorderBrush` / `ErrorForegroundBrush` / `SuccessForegroundBrush`）を `FindResource` 経由で参照する形に統一し、Issue #1392 の色値 Single Source of Truth 原則違反も解消した。回帰防止として `DialogAutomationPropertiesCoverageTests` に静的検査 4 件（StatusBorder の初期 Visibility が Collapsed でないこと、code-behind に `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` 呼び出しが含まれること、認証成功パスに `ShowStatus` 呼び出しが含まれること、`ShowStatus` 本体に色値リテラルが含まれないこと）を追加し、UI Automation 統合テスト 3 件（`ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs`）で FlaUI 5.0 を使った StatusText の UIA tree 到達性と Text 反映を検証する。`docs/design/03_画面設計書.md` §5.7 と `docs/design/07_テスト設計書.md` §2.45 を同期更新（#1509）

**訂正**
- v2.8.0（Issue #1468）の「認証画面では認証ステータス変化（成功・失敗）と残り時間カウントダウンの読み上げまで網羅」の記述は事実誤認だった。属性は付与されたが実機で読み上げが発火していなかった。本リリース（Issue #1509）で実通知発火を実装してこの主張を有効化した（#1509）
```

- [ ] **Step 6.2: 03_画面設計書.md §5.7 を更新**

`docs/design/03_画面設計書.md` を開き、「5.7 職員証認証ダイアログ」セクション（章番号は実ファイルで確認すること）に以下の小節を追加：

```markdown
### 5.7.X スクリーンリーダー対応（Issue #1468 / #1509）

#### LiveRegion 即時通知の仕組み

`StaffAuthDialog` の `StatusText` には `AutomationProperties.LiveSetting="Assertive"` を付与し、スクリーンリーダー（NVDA / Windows ナレーター）で認証ステータスの変化が即時通知されるよう設計している。WPF UI Automation の LiveRegion は「既に可視な要素の Text が変化したとき」に通知されるため、以下の 2 点を実装上の必須条件とする：

1. **`StatusBorder` は常時可視化**: 初期 `Visibility="Collapsed"` にすると AutomationTree から除外され、Collapsed → Visible 遷移は LiveRegionChanged を発火させない。`Background="Transparent" BorderThickness="0" MinHeight="44"` で視覚的に中立な状態で常時可視化し、`ShowStatus()` 呼び出し時に背景色・枠を切り替える。

2. **`UIElementAutomationPeer.RaiseAutomationEvent` の明示呼び出し**: `TextBlock.Text` 更新だけでは LiveRegionChanged の発火が保証されない（WPF のバージョン・コントロールツリーの状態に依存）。`ShowStatus()` の末尾で必ず以下を呼ぶ：

```csharp
var peer = UIElementAutomationPeer.FromElement(StatusText)
           ?? UIElementAutomationPeer.CreatePeerForElement(StatusText);
peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
```

#### 認証成功時もステータス表示する設計

認証成功時は効果音（Notify）のみで即座にダイアログを閉じる従来仕様だったが、スクリーンリーダー利用者には音声フィードバックが不十分だった。Issue #1509 で「成功時にも `ShowStatus($"認証に成功しました（{職員名}）")` を呼び、700ms 表示してから閉じる」設計に変更。タイムアウト失敗側の「1 秒表示 → 閉じる」と同じ `CloseAfterDelay(TimeSpan, bool)` ヘルパで実装を共通化。
```

- [ ] **Step 6.3: 07_テスト設計書.md §2.45 を更新**

`docs/design/07_テスト設計書.md` を開き、「2.45 ダイアログのアクセシビリティ静的検査」セクション（章番号は実ファイルで確認）の表に以下のテストケースを追加：

```markdown
| UT-058a | StatusBorder の初期 Visibility が Collapsed でないことを静的検査する | Issue #1509 |
| UT-058a-2 | code-behind に RaiseAutomationEvent(LiveRegionChanged) 呼び出しが含まれることを静的検査する | Issue #1509 |
| UT-058a-3 | OnCardRead の認証成功パスに ShowStatus 呼び出しが含まれることを静的検査する | Issue #1509 |
| UT-058a-4 | ShowStatus メソッド本体に色値リテラル（0xRRGGBB）が含まれないことを静的検査する | Issue #1509 / Issue #1392 |
```

新規セクション「2.46 ダイアログのアクセシビリティ UI Automation 統合テスト」を追加：

```markdown
## 2.46 ダイアログのアクセシビリティ UI Automation 統合テスト（Issue #1509）

| ID | テスト内容 | 期待結果 |
|----|-----------|---------|
| UT-058b-1 | StaffAuthDialog で未登録カードを仮想タッチした際、StatusText が UIA tree から発見でき、Name に失敗メッセージが反映され、LiveSetting=Assertive が観察できること | FlaUI 経由で 3 つの観察が全て成立 |
| UT-058b-2 | StaffAuthDialog でタイムアウト経過後、StatusText に「タイムアウト」を含むメッセージが反映されること | タイムアウト経過後 5 秒以内に観察可能 |
| UT-058b-3 | StaffAuthDialog で登録カードを仮想タッチした際、StatusText に「認証に成功」が反映され、700ms 後にダイアログが自動クローズすること | `AppFixture.LaunchWithSeededStaff()` で「テスト職員」（IDm "FFFF000000000001"）を投入してから検証 |

実装場所: `ICCardManager/tests/ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs`
依存: FlaUI.Core 5.0.0 + FlaUI.UIA3 5.0.0、`AppFixture.LaunchWithSeededStaff()`、`[Collection("UI")]`、`[Trait("Category","UI")]`
```

- [ ] **Step 6.4: ドキュメント更新をコミット**

```bash
git add ICCardManager/CHANGELOG.md docs/design/03_画面設計書.md docs/design/07_テスト設計書.md
git commit -m "$(cat <<'EOF'
docs: Issue #1509 の修正内容を CHANGELOG / 画面設計書 / テスト設計書に反映

- CHANGELOG.md: バグ修正セクションに #1509 の修正内容と PR #1500 の主張訂正を追記
- 03_画面設計書.md §5.7: StaffAuthDialog の LiveRegion 即時通知の仕組みと
  認証成功時もステータス表示する設計を追加
- 07_テスト設計書.md §2.45: 静的検査 4 件を追加、§2.46 として UI Automation
  統合テスト 3 件を新規セクションで追加

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: 全テスト実行・ビルド警告ゼロ・手動 NVDA 検証

**Files:** なし（検証のみ）

- [ ] **Step 7.1: フルビルドとテスト実行**

```bash
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
"$DOTNET" build ICCardManager/ICCardManager.sln 2>&1 | tail -30
"$DOTNET" test ICCardManager/tests/ICCardManager.Tests 2>&1 | tail -20
```

Expected: ビルド警告ゼロ、エラーゼロ、`ICCardManager.Tests` 全件 PASS（既存 3128 + 新規 4 = 3132 件）

- [ ] **Step 7.2: UI Automation テストを実行（時間がかかる）**

```bash
"$DOTNET" build ICCardManager/src/ICCardManager/ICCardManager.csproj -c Debug
"$DOTNET" test ICCardManager/tests/ICCardManager.UITests --filter "FullyQualifiedName~StaffAuthDialogLiveRegionTests" 2>&1 | tail -30
```

Expected: 3 ケース全て PASS。失敗時テスト・タイムアウト時テスト・成功時テスト。タイムアウトテストは ~70 秒かかる。

- [ ] **Step 7.3: NVDA 手動検証（任意、Windows 環境のみ）**

NVDA または Windows ナレーターを起動した状態で：

1. アプリ起動 → 履歴編集 等で StaffAuthDialog を開く
2. **未登録カードをタッチ** → 「このカードは職員証として登録されていません。登録済みの職員証をタッチしてください。」が読み上げられること
3. **タイムアウト経過** → 「認証がタイムアウトしました」が読み上げられること
4. **登録カードをタッチ** → 「認証に成功しました（職員名）」が読み上げられた後、ダイアログが自動的に閉じること

Expected: 全 3 ケースでスクリーンリーダーが読み上げる。

検証結果は PR 本文の「手動検証」チェックリストに反映する。

---

## Task 8: PR を作成

**Files:** なし（GitHub 操作のみ）

- [ ] **Step 8.1: ブランチを push**

```bash
git push -u origin fix/issue-1509-live-region-firing
```

- [ ] **Step 8.2: PR を作成**

```bash
gh pr create --title "fix: 職員証認証ダイアログの LiveRegion 即時通知発火を実装 (Issue #1509)" --body "$(cat <<'EOF'
## Summary

- **Issue #1509**: PR #1500（Issue #1468）で `StatusText` に `AutomationProperties.LiveSetting=\"Assertive\"` を付与したが、`StatusBorder.Visibility=\"Collapsed\"` 起点と `Text` 更新のみではスクリーンリーダー（NVDA/Narrator）への通知が発火しなかった問題を修正
- `StatusBorder` を常時可視化（`Background=\"Transparent\" BorderThickness=\"0\" MinHeight=\"44\"`）して AutomationTree に常駐させ、`ShowStatus()` 末尾で `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を明示発火
- 認証成功時にも `ShowStatus(\"認証に成功しました（{職員名}）\", false)` を呼び出して 700ms 表示してからクローズ（タイムアウト失敗側と同じ `CloseAfterDelay` テンプレートで共通化）
- 派生問題: `ShowStatus()` 内の色値リテラル直接指定（`Color.FromRgb(0xFF, 0xEB, 0xEE)` 等）を `AccessibilityStyles.xaml` のブラシキー（`ErrorBackgroundBrush` / `SuccessBackgroundBrush` 等）を `FindResource` 経由で参照する形に統一し、Issue #1392 同時解消
- 回帰防止: `DialogAutomationPropertiesCoverageTests` に静的検査 4 件 + `ICCardManager.UITests` に UI Automation 統合テスト 3 件を追加

## Test plan

### 自動テスト
- [x] `dotnet build`: 警告ゼロ・エラーゼロ
- [x] `DialogAutomationPropertiesCoverageTests`（全 43 件、うち Issue #1509 関連 4 件）グリーン
- [x] テストスイート全体 3132 件グリーン
- [x] UI Automation テスト（`StaffAuthDialogLiveRegionTests` 3 件）グリーン（`LaunchWithSeededStaff` でテスト職員を事前投入し成功パスも検証）

### 手動検証（マージ前推奨）
- [ ] NVDA / Windows ナレーターで未登録カードタッチ時に失敗メッセージが読み上げられる
- [ ] NVDA / Windows ナレーターでタイムアウト時にタイムアウトメッセージが読み上げられる
- [ ] NVDA / Windows ナレーターで登録カードタッチ時に「認証に成功しました（職員名）」が読み上げられた後、ダイアログが自動クローズする
- [ ] 視覚回帰なし（通常マウス/キーボード操作での見た目変化がないこと）

## 関連 Issue
- Closes #1509
- 派生: Issue #1392（色値 Single Source of Truth）の `StaffAuthDialog.ShowStatus()` 該当箇所を解消
- 訂正: PR #1500（Issue #1468）の「認証成功・失敗の読み上げまで網羅」記述

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 8.3: PR URL を確認**

Run: `gh pr view --web`（任意、ブラウザで開く）

Expected: PR が作成されている。CI（GitHub Actions ci.yml）が自動実行される。

---

## Self-Review チェックリスト（実装者向け）

実装完了後、PR 提出前に以下を確認：

- [ ] Spec の §2.1 目的の 3 ケース（成功/失敗/タイムアウト）すべて手動検証済み
- [ ] Spec の §2.3 範囲外項目を誤って実装していない（他ダイアログには触れていない）
- [ ] Spec の §7 受入基準 9 項目すべて満たす
- [ ] Spec の §8 リスク 4 件への緩和策が動作している（特に `Application.Current` null / `SuccessBorderBrush` 不在）
- [ ] CHANGELOG / 03 / 07 設計書の同期更新が PR に含まれている
- [ ] コミットメッセージが「fix:」「test:」「docs:」のいずれかのプレフィックス付きで意図を表現している
- [ ] `git log fix/issue-1509-live-region-firing` の各コミットが独立してビルド・テスト通過可能（rebase 安全）
