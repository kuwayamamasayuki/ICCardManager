# Issue #1550: 同構造 Fact テスト群を Theory + InlineData に統合（5 クラス）

- 起票日: 2026-05-18
- 関連 Issue: #1550
- 関連設計書: `ICCardManager/docs/superpowers/specs/2026-05-18-test-suite-cleanup-design.md`「Theory 化候補（別 Issue で対応）」セクション
- 種別: refactor（テストコードのみ。本体コード変更なし）
- 優先度: Low

## 1. 背景

2026-05-18 のテストスイート整理セッションで、本体（Arrange-Act-Assert）が完全に一致する同構造の Fact / Theory テスト群が複数発見された。入力値だけが異なる境界値・同値分割テストであるため削除対象ではないが、`[Theory] + [InlineData]` への統合でコード行数を削減できる。

本 Issue では 5 クラスを対象に Theory 化リファクタリングを実施する。

## 2. ゴール

- 対象 5 クラスにおいて、AAA 構造が完全に同一の Fact / Theory メソッド群を **単一の `[Theory] + [InlineData]` メソッド**に統合する
- **テストケース数（`dotnet test --list-tests` 上の件数）は不変**を維持する
- テストメソッド名は「意図を表す総称名」に刷新する（ユーザー指定方針）

## 3. ノンゴール

- テスト挙動（入力値・期待値・Assert 内容）の変更
- 本体コード（プロダクトコード）の変更
- 新規テストケースの追加
- 別 Issue で扱う未統合テストへの介入

## 4. 統合対象と方針

### 4.1 `FileSizeConverterTests`（`tests/Common/ConvertersTests.cs`）

| 統合前 | 種類 | 件数 |
|---|---|---|
| `Convert_バイト単位の場合B表示になること` | Theory | 3 |
| `Convert_KB単位の場合KB表示になること` | Theory | 2 |
| `Convert_MB単位の場合MB表示になること` | Fact | 1 |
| `Convert_GB単位の場合GB表示になること` | Fact | 1 |

**統合後**: 1 メソッド `Convert_バイト数を適切な単位で表示すること(long input, string expected)` に統合（`[InlineData]` 7 件）。

| Input | Expected |
|---|---|
| 0L | "0 B" |
| 512L | "512 B" |
| 1023L | "1023 B" |
| 1024L | "1 KB" |
| 1536L | "1.5 KB" |
| 1048576L | "1 MB" |
| 1073741824L | "1 GB" |

統合対象外（型シグネチャが異なる）: `Convert_long以外の場合文字列表現を返すこと`、`Convert_nullの場合空文字列を返すこと`。

### 4.2 `PathValidatorTests`（`tests/Common/PathValidatorTests.cs`）

| 統合前 | 種類 | 件数 |
|---|---|---|
| `ValidateBackupPath_PathTraversal_ReturnsInvalid` | Fact | 1 |
| `ValidateBackupPath_PathTraversalInMiddle_ReturnsInvalid` | Fact | 1 |
| `ValidateBackupPath_PathTraversalAtEnd_ReturnsInvalid` | Fact | 1 |
| `ValidateBackupPath_UncPathWithTraversal_ReturnsInvalid` | Fact | 1 |
| `ValidateBackupPath_UncPathDeepTraversal_ReturnsInvalid` | Fact | 1 |
| `ValidateBackupPath_UrlEncodedTraversal_ReturnsInvalid` | Fact | 1 |
| `ValidateBackupPath_MixedSeparatorTraversal_ReturnsInvalid` | Theory | 3 |
| `ValidateBackupPath_DotSpaceTraversal_ReturnsInvalid` | Theory | 2 |

**全 11 件の Assert は完全に同一**:
```csharp
result.IsValid.Should().BeFalse();
result.ErrorMessage.Should().Contain("..");
```

**統合後**: 1 メソッド `ValidateBackupPath_パストラバーサルパターンを検出すること(string path)` に統合（`[InlineData]` 11 件）。

統合対象外:
- `ValidateBackupPath_SafeLookalikePaths_NotFlaggedAsTraversal`: 逆方向検証（IsValid=true 側）
- `ContainsTraversalSegment_DetectsCorrectly`: 別 API のテスト

### 4.3 `FormulaInjectionSanitizerTests`（`tests/Infrastructure/Security/FormulaInjectionSanitizerTests.cs`）

| 統合前 | 種類 | 件数 |
|---|---|---|
| `IsDangerous_DoesNotStartWithDangerousChar_ReturnsFalse` | Theory | 7 |
| `IsDangerous_NullOrEmpty_ReturnsFalse` | Theory | 2 |

**統合後**: 1 メソッド `IsDangerous_危険文字で始まらない入力はFalseを返すこと(string? input)` に統合（`[InlineData]` 9 件）。

**統合対象外（分離維持）**: `IsDangerous_StartsWithDangerousChar_ReturnsTrue` (Theory, 10 件)。`bool expected` を導入して True/False を 1 つに統合する案もあるが、「危険判定の仕様」のドキュメント性を優先して分離する。

### 4.4 `SharedModeMonitorTests`（`tests/Services/SharedModeMonitorTests.cs`）

| 統合前 | 種類 | 件数 |
|---|---|---|
| `UpdateSyncDisplayText_5秒未満はたった今と表示されること` | Theory | 3 |
| `UpdateSyncDisplayText_5秒以上60秒未満はN秒前と表示されること` | Theory | 4 |
| `UpdateSyncDisplayText_60秒以上はN分前と表示されること` | Theory | 3 |

**統合後**: 1 メソッド `UpdateSyncDisplayText_経過時間に応じたテキストを生成すること(int elapsedSeconds, string expectedText)` に統合（`[InlineData]` 10 件）。

| Seconds | Expected Text |
|---|---|
| 0, 2, 4 | "最終同期: たった今" |
| 5 | "最終同期: 5秒前" |
| 15 | "最終同期: 15秒前" |
| 30 | "最終同期: 30秒前" |
| 59 | "最終同期: 59秒前" |
| 60 | "最終同期: 1分前" |
| 120 | "最終同期: 2分前" |
| 3599 | "最終同期: 59分前" |

**統合対象外（分離維持）**:
- `UpdateSyncDisplayText_最終同期がない場合は同期待ちと表示されること` (Fact): 初期状態の Assert（`IsStale.Should().BeFalse()` も含む）が異なる
- `UpdateSyncDisplayText_経過時間がStaleThresholdSeconds以上ならIsStaleがtrueになること` (Theory): `IsStale` を Assert する別関心

### 4.5 `StationMasterServiceTests`（`tests/Services/StationMasterServiceTests.cs`）

| 統合前メソッド | CardType | 件数 |
|---|---|---|
| `GetStationName_AirportLine_WithHayakaken_ReturnsCorrectName` | Hayakaken | 13 |
| `GetStationName_HakozakiLine_WithHayakaken_ReturnsCorrectName` | Hayakaken | 7 |
| `GetStationName_NanakumaLine_WithHayakaken_ReturnsCorrectName` | Hayakaken | 9 |
| `GetStationName_KagoshimaLine_WithHayakaken_ReturnsCorrectName` | Hayakaken | 15 |
| `GetStationName_YamanoteLine_WithSuica_ReturnsCorrectName` | Suica | 5 |
| `GetStationName_TokaidoLine_Kanto_WithSuica_ReturnsCorrectName` | Suica | 3 |
| `GetStationName_HokurikuShinkansen_Extension_ReturnsCorrectName` | Suica | 7 |
| `GetStationName_SotetsuShinYokohamaLine_ReturnsCorrectName` | PASMO | 2 |
| `GetStationName_TokyuShinYokohamaLine_ReturnsCorrectName` | PASMO | 2 |
| `GetStationName_KitaOsakaKyuko_Extension_ReturnsCorrectName` | TOICA | 2 |

合計 `[InlineData]` 65 件。AAA テンプレートは全 10 メソッドで完全に同一:
```csharp
var service = new StationMasterService();
var result = service.GetStationName(stationCode, CardType.XXX);
result.Should().Be(expectedName);
```

**統合後**: 1 メソッド `GetStationName_カード種別と駅コードに応じた駅名を返すこと(int stationCode, CardType cardType, string expectedName)` に統合。

**実装上の工夫**:
- `[InlineData]` を路線別に `#region` でグルーピングし、レビュー性を保つ
- `KitaOsakaKyuko_Extension` の「Area 2 優先のため TOICA を使用」コメントは該当 `[InlineData]` 行の直上にコメントブロックで保持

## 5. 命名規則

統合後のメソッド名は「**動作（Subject + 動詞）_意図**」の総称形式とする。

| Before | After |
|---|---|
| `Convert_バイト単位の場合B表示になること` 等 4 メソッド | `Convert_バイト数を適切な単位で表示すること` |
| `ValidateBackupPath_PathTraversal_ReturnsInvalid` 等 8 メソッド | `ValidateBackupPath_パストラバーサルパターンを検出すること` |
| `IsDangerous_DoesNotStartWithDangerousChar_ReturnsFalse` 等 2 メソッド | `IsDangerous_危険文字で始まらない入力はFalseを返すこと` |
| `UpdateSyncDisplayText_5秒未満は…` 等 3 メソッド | `UpdateSyncDisplayText_経過時間に応じたテキストを生成すること` |
| `GetStationName_*_With{CardType}_ReturnsCorrectName` 10 メソッド | `GetStationName_カード種別と駅コードに応じた駅名を返すこと` |

## 6. テストケース数の保全

リファクタリング前後で `dotnet test --list-tests` の件数が **完全に一致** することを保証する。

### 検証手順
1. リファクタリング前: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --list-tests` を実行し、件数を記録
2. リファクタリング後: 同上を実行し、件数を比較
3. CI（`.github/workflows/test-count-sync-check.yml`、Issue #1546）の自動検証で乖離があれば修正

### テスト件数表 §1.1a の扱い
`docs/design/07_テスト設計書.md` §1.1a の件数表が「テストメソッド数」基準なら、リファクタリングで件数が減るため同期更新が必要。「テストケース数（`[InlineData]` 展開後）」基準なら据え置き。実装着手前に集計単位を確認する。

## 7. リスクと対策

| リスク | 対策 |
|---|---|
| `[InlineData]` の verbatim 文字列リテラルで `\` の取り扱いを誤り、検証対象が変わる | リファクタリング前後で各テストケースの実行を `dotnet test --logger "console;verbosity=detailed"` で差分確認 |
| `StationMasterService` の 65 件 `[InlineData]` 単一ブロックでレビューが困難 | `#region` でグルーピング + コミットメッセージに「Theory 化のみ、入力値・期待値は不変」を明記 |
| TOICA の「Area 2 優先」コメント等、固有コメントの欠落 | 該当 `[InlineData]` 行の直上にコメントブロックを保持 |
| 件数表 §1.1a と実態の乖離 | CI で自動検証されているため、ローカルで `dotnet test --list-tests` の合計件数を集計表と照合してから PR |

## 8. PR 構成

ユーザー指定方針: **1 PR で 5 クラスを統合**。

- ブランチ名: `refactor/issue-1550-theory-consolidation`
- 1 PR で 5 クラス（5 ファイル）を変更
- コミット粒度はクラスごと（5 コミット）で diff の追跡を容易に

## 9. 受け入れ条件

- [ ] 5 ファイルすべてで対象メソッドが Theory に統合されている
- [ ] `dotnet test --list-tests` の件数が変更前後で完全一致
- [ ] `dotnet test` が全 Pass
- [ ] `dotnet build` 警告ゼロ維持
- [ ] テスト件数表 §1.1a と実態が一致（必要なら更新済み）
- [ ] PR が Issue #1550 を `Closes #1550` で紐付け
