# 管理者マニュアルの作業別再構成 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 管理者マニュアルを庶務担当者向けの「作業別」構成へ書き直し、IT 担当者向けの内容を新設の `IT担当者ガイド.md` へ分離する。

**Architecture:** 現行マニュアル（`origin/main` の 2,242 行）を「旧版」として scratchpad に固定し、旧版の節を行範囲で指定して新版の各部へ移し替える。新版は `docs/manual/管理者マニュアル.md` に部ごとに追記して育て、最後にアンカー検査・禁止語検査・既存テストで固定する。追随箇所（アプリ内文言・テスト定数・他マニュアル・配布スクリプト）は別タスクで付け替える。

**Tech Stack:** Markdown（pandoc 変換前提の GFM。画像は `![alt](../screenshots/x.png){width=NN%}` 形式）、xUnit 静的検査テスト（`MarkdownDocumentInspection`）、PowerShell 変換スクリプト、Inno Setup。

**Spec:** `ICCardManager/docs/superpowers/specs/2026-09-05-admin-manual-task-oriented-design.md`

## Global Constraints

- 読者は「部署の庶務担当で IT 操作に必ずしも詳しくない人」。本文に IT 担当者向け・開発者向けの記述を残さない。
- 本文に「Issue #」「で改善」「解消されました」「で修正」を書かない（IT 担当者ガイドは章末の「設計書参照」1 行のみ可）。
- 交通系ICカードを「ICカード」と略さない。「ICカードリーダー」「ICカード管理」等の複合語は可。
- 各作業は「この作業をするとき／前提／手順／できたことの確認」の固定型。1 番号に 1 操作。押す場所は **太字**。手順の中に `>` 引用を挟まない。
- 1 文は 60 文字程度まで。「〜する必要があります」→「〜してください」。
- スクリーンショットは既存の `docs/screenshots/` の 27 参照を移動先で再利用する。新規撮影しない。
- `.docx` / `.pdf` は再生成しない（リリース手順に委ねる）。
- 実装と食い違う記述を見つけても本 PR で直さない。Issue 起票用に `$SCRATCH/found-issues.md` へ書き留める。
- コミットは `git add <個別ファイル>` で行う。`git add -A` 禁止。
- コミットメッセージ末尾:
  ```
  Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01H3hxEXMvzun1owyyquGWRX
  ```
- 作業ブランチ: `docs/admin-manual-task-oriented`（作成済み）。
- `$SCRATCH` = `/tmp/claude-1000/-mnt-d-OneDrive-----src/937ae678-716d-4e87-b2ed-0b161a5d8b8e/scratchpad`。
- テスト実行: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln -c Release --filter "FullyQualifiedName~<Name>"`。

---

## 旧版の節と行範囲（全タスク共通の参照表）

旧版は Task 0 で `$SCRATCH/old.md` に固定する。行番号はこのファイルのもの。

| 行 | 旧節 |
|---|---|
| 1–31 | タイトル・バージョン・目次 |
| 32–126 | こんなときはどこを見る？（作業別クイックガイド） |
| 127–143 | 管理者の年間作業イメージ |
| 144–175 | §1 はじめに（1.1 本書について／1.2 管理者の役割／1.3 システム要件） |
| 176–190 | §2.1 複数PCで利用する場合の事前準備 |
| 191–268 | §2.2 インストーラーを使用する場合 |
| 269–342 | §2.3 2台目以降のPCのセットアップ（308–318 起動中の挙動表、319–329 上書き動作、330–342 切断時フォールバック） |
| 343–349 | §2.4 初回起動 |
| 350–368 | §2.5 共有モードについて |
| 369–420 | §2.6 バージョンアップ（確認事項／バックアップ／手順／保持データ／確認） |
| 421–452 | §2.6 バージョン混在の検出と保護 |
| 453–478 | §2.6 アンインストールについて |
| 479–535 | §3 初期設定（3.1 開く／3.2 基本設定／3.3 バックアップ設定／3.4 データ保存場所） |
| 536–594 | §4 職員管理（4.2 登録／4.3 編集／4.4 削除／4.5 職員証の再登録） |
| 604–687 | §5.2 交通系ICカードの登録 |
| 688–708 | §5.3 交通系ICカード情報の編集 |
| 709–727 | §5.3.1 繰越情報が失われたカードの確認と復旧 |
| 728–735 | §5.4 削除 |
| 736–752 | §5.5 払い戻し |
| 753–790 | §5.5a 読み取らずに持ち出されたカードの貸出記録を作成する |
| 795–815 | §5.6.1 全体像／5.6.2 登録方法の選択 |
| 816–876 | §5.6.3 登録手順（紙の出納簿からの繰越） |
| 877–945 | §5.6.4 月途中からの導入時の履歴について |
| 946–1057 | §5.6.5 月途中からの履歴入力（CSVインポート） |
| 1058–1118 | §5.6.6 帳票出力の確認（1081–1118 出力前の事前チェック） |
| 1119–1156 | §5.6.7 複数カードの導入／5.6.8 導入時のよくある質問 |
| 1159–1248 | §6.1 バックアップとリストア（1167 DB情報／1183 手動／1195 自動／1211 状況／1229 長期未成功の警告） |
| 1249–1266 | §6.2 リストア |
| 1267–1312 | §6.3 データエクスポート（1273 CSV／1281 月次帳票 Excel／1290 先月分一括／1296 チェックリスト） |
| 1313–1354 | §6.4 データインポート |
| 1355–1364 | §6.5 古いデータの削除 |
| 1367–1375 | §7.1 設定の保存場所 |
| 1376–1398 | §7.2 アプリケーション設定 |
| 1399–1472 | §7.3 ログ設定 |
| 1473–1608 | §7.4 組織固有設定（OrganizationOptions） |
| 1609–1659 | §7.4a 同一とみなす駅・バス停の登録 |
| 1662–1701 | §8.1 アクセス制御／8.2 データ保護／8.3 監査ログ |
| 1702–1774 | §8.4 felicalib.dll の完全性管理 |
| 1775–1811 | §8.5 CSV/Excel 式インジェクション対策 |
| 1814–1839 | §9.1 定期メンテナンス／9.2 DB サイズ／9.3 ログファイル |
| 1840–1939 | §9.4 管理者ダッシュボード（1856 運用状況／1878 稼働状況／1896 利用推移／1922 Excel 出力） |
| 1942–1983 | §10.0 まず接続診断を実行する |
| 1984–2019 | §10.1 起動時／10.2 リーダー／10.3 データ |
| 2020–2034 | §10.4 共有フォルダモードの問題（症状表） |
| 2035–2046 | §10.4 SQLite エラーのトラブルシューティング |
| 2047–2065 | §10.4 ネットワーク共有フォルダのアクセス権設定ガイド |
| 2066–2075 | §10.4 推奨PC台数・同時接続数 |
| 2076–2090 | §10.4 バックアップ・リストア時の注意事項（共有モード） |
| 2091–2098 | §10.4 VACUUM 失敗時の対処方法 |
| 2099–2116 | §10.5 バックアップ・リストア |
| 2117–2155 | §11 残高不足時の精算機での現金チャージ |
| 2158–2180 | 付録 A ショートカットキー |
| 2181–2199 | 付録 B 用語集 |
| 2200–2242 | 付録 C アクセス権限の設定（IT 担当者向け） |

## 新版の見出し（アンカーの正典。全タスクはこの文字列をそのまま使う）

```
# 交通系ICカード管理システム：ピッすい 管理者マニュアル
## はじめに
### このマニュアルの読み方
### 管理者の役割
### 動作環境
## 困ったときの早見表
## 第1部 はじめて使うとき
### 1.1 導入の流れ
### 1.2 1台のPCで使う場合のインストール
### 1.3 複数のPCで使う場合のインストール
### 1.4 初回起動と初期設定
### 1.5 職員を登録する
### 1.6 交通系ICカードを登録する（4月から使い始める場合）
### 1.7 年度途中から使い始める場合
#### 1.7.1 紙の出納簿から引き継ぐ
#### 1.7.2 カードに残っていない履歴を取り込む
#### 1.7.3 取り込んだあとの帳票を確かめる
### 1.8 導入時のよくある質問
## 第2部 毎月すること
### 2.1 物品出納簿（月次帳票）を出す
### 2.2 出力前の事前チェックで警告が出たとき
### 2.3 バックアップが動いているか確かめる
## 第3部 人やカードが増減したとき
### 3.1 職員に関する作業
#### 3.1.1 職員が加わった
#### 3.1.2 職員の情報を直す
#### 3.1.3 職員が退職・異動した
#### 3.1.4 職員証を再発行した
### 3.2 交通系ICカードに関する作業
#### 3.2.1 カードを追加する
#### 3.2.2 カードの情報を直す
#### 3.2.3 カードを払い戻す
#### 3.2.4 カードを削除する
### 3.3 読み取らずに持ち出されたカードの貸出記録を作る
### 3.4 繰越情報が失われたカードを復旧する
### 3.5 残高不足で現金チャージした利用の扱い
## 第4部 年に一度すること
### 4.1 新しいバージョンに更新する
### 4.2 古いデータを削除する
### 4.3 同一とみなす駅・バス停を登録する
## 第5部 困ったとき
### 5.1 まず接続診断を実行する
### 5.2 起動しない
### 5.3 カードが読めない
### 5.4 データがおかしい
### 5.5 共有モードの警告が出る
### 5.6 バックアップ・リストアがうまくいかない
### 5.7 「ピッすいの更新が必要です」と表示される
## 第6部 データを取り出す・戻す
### 6.1 手動バックアップ
### 6.2 リストア（復元）
### 6.3 CSV エクスポート
### 6.4 CSV インポート
## 付録
### A. 設定画面（F5）の項目一覧
### B. システム管理画面（F6）の項目一覧
### C. 管理者ダッシュボード（F8）の見方
#### C.1 運用状況タブ
#### C.2 稼働状況タブ
#### C.3 利用推移タブ
#### C.4 Excel 出力
### D. ショートカットキー
### E. 用語集
```

## 作業の固定型（テンプレート）

各 `###` / `####` の作業節はこの形で書く。項目が該当しない場合（前提なし等）はその小見出しを省く。

```markdown
### 3.2.3 カードを払い戻す

**この作業をするとき**: カードを使わなくなり、残額を払い戻したとき。

**前提**: カードが返却済み（貸出中でない）であること。

**手順**

1. メニュー **管理** → **交通系ICカード管理** を開きます（または **F7**）。
2. 一覧から対象のカードを選び、**払い戻し** をクリックします。
3. 確認画面で払い戻し額を確かめ、**OK** をクリックします。

![払い戻し確認ダイアログ](../screenshots/card_refund_dialog.png){width=60%}

**できたことの確認**: 一覧のカードに「払戻済」と表示され、履歴に「払戻しによる払出」の行が追加されています。

**補足**: 払い戻したカードは一覧に残ります。過去の履歴を見るためで、貸出はできません。
```

---

### Task 0: 旧版の固定と検査スクリプトの用意

**Files:**
- Create: `$SCRATCH/old.md`（旧版のコピー。リポジトリには入れない）
- Create: `$SCRATCH/check-anchors.py`
- Create: `$SCRATCH/check-forbidden.sh`
- Create: `$SCRATCH/found-issues.md`（空）

**Interfaces:**
- Produces: `python3 $SCRATCH/check-anchors.py <md>` … 未解決アンカーを 1 行 1 件で出力し、0 件なら exit 0。`bash $SCRATCH/check-forbidden.sh <md>` … 禁止語の行を出力し、0 件なら exit 0。

- [ ] **Step 1: 旧版を固定する**

```bash
cd /mnt/d/OneDrive/交通系/src
git show origin/main:ICCardManager/docs/manual/管理者マニュアル.md > "$SCRATCH/old.md"
wc -l "$SCRATCH/old.md"
```
Expected: `2242`

- [ ] **Step 2: アンカー検査スクリプトを書く**

pandoc / GitHub 互換の見出し → アンカー変換で `[text](#anchor)` を照合する。

```python
# $SCRATCH/check-anchors.py
import re, sys, unicodedata

def slug(h: str) -> str:
    h = h.strip().lower()
    h = re.sub(r'[^\w\s\-ぁ-んァ-ヶ一-龠々ー０-９ａ-ｚＡ-Ｚ]', '', h)
    return re.sub(r'\s+', '-', h)

path = sys.argv[1]
text = open(path, encoding='utf-8-sig').read()
heads = [slug(m.group(2)) for m in re.finditer(r'^(#{1,6})\s+(.+?)\s*$', text, re.M)]
seen, anchors = {}, set()
for h in heads:
    n = seen.get(h, 0)
    anchors.add(h if n == 0 else f'{h}-{n}')
    seen[h] = n + 1
bad = 0
for m in re.finditer(r'\]\(#([^)]+)\)', text):
    a = m.group(1)
    if a not in anchors:
        line = text.count('\n', 0, m.start()) + 1
        print(f'{path}:{line}: unresolved anchor #{a}')
        bad += 1
print(f'{len(anchors)} anchors, {bad} unresolved')
sys.exit(1 if bad else 0)
```

- [ ] **Step 3: 旧版に当てて動くことを確かめる**

```bash
python3 "$SCRATCH/check-anchors.py" "$SCRATCH/old.md"
```
Expected: `N anchors, 0 unresolved`（旧版は既にリンク切れなし。もし件数が出たら `found-issues.md` に記録し、スクリプトの `slug` を旧版の実際のリンクに合わせて調整する）

- [ ] **Step 4: 禁止語検査スクリプトを書く**

```bash
# $SCRATCH/check-forbidden.sh
#!/usr/bin/env bash
f="$1"
grep -nE 'Issue #|で改善|解消されました|で修正され|で対応済' "$f"
n=$(grep -cE 'Issue #|で改善|解消されました|で修正され|で対応済' "$f")
echo "forbidden: $n"
[ "$n" -eq 0 ]
```

```bash
chmod +x "$SCRATCH/check-forbidden.sh"; : > "$SCRATCH/found-issues.md"
bash "$SCRATCH/check-forbidden.sh" "$SCRATCH/old.md" | tail -1
```
Expected: `forbidden: 35` 前後（旧版の現状。新版で 0 にする）

---

### Task 1: IT担当者ガイド.md の新設

**Files:**
- Create: `ICCardManager/docs/manual/IT担当者ガイド.md`

**Interfaces:**
- Produces: 管理者マニュアル側から参照する見出し `## 3. 共有フォルダの構築`、`## 4. データベースの配置と接続`、`## 5. 設定ファイル`、`## 8. セキュリティ`、`## 9. 障害対応`。

- [ ] **Step 1: 骨格を書く**

```markdown
# 交通系ICカード管理システム：ピッすい IT担当者ガイド

**バージョン**: 2.10.0
**最終更新日**: 2026年9月

---

## 目次

1. [このガイドの対象](#1-このガイドの対象)
2. [動作環境と配布形態](#2-動作環境と配布形態)
3. [共有フォルダの構築](#3-共有フォルダの構築)
4. [データベースの配置と接続](#4-データベースの配置と接続)
5. [設定ファイル](#5-設定ファイル)
6. [バックアップ・VACUUM の仕組み](#6-バックアップvacuum-の仕組み)
7. [バージョン管理](#7-バージョン管理)
8. [セキュリティ](#8-セキュリティ)
9. [障害対応](#9-障害対応)

---

## 1. このガイドの対象

本ガイドは、ピッすいを導入する組織の情報システム担当者向けです。共有フォルダの構築、設定ファイルの編集、障害時のログ解析など、管理者マニュアルでは「システム担当者に依頼してください」としている作業を扱います。

| 文書 | 読者 | 扱う内容 |
|------|------|----------|
| 管理者マニュアル | 部署の庶務担当者 | 画面から行う日常の管理作業 |
| IT担当者ガイド（本書） | 情報システム担当者 | 共有フォルダ、設定ファイル、セキュリティ、障害対応 |
| 開発者ガイド | 開発・保守担当者 | ソースコードの構成と規約 |
```

- [ ] **Step 2: 旧版の節を各章へ移す**

`$SCRATCH/old.md` の次の行範囲を、見出しレベルを章に合わせて（`###` → `###`、`####` → `###` または `####`）貼り付け、文体規約に沿って整える。Issue 番号と「〜で改善」は削り、代わりに各章末へ `> 設計書参照: 04_機能設計書 §x.y` を 1 行置く（該当する設計書の節は `ICCardManager/docs/design/04_機能設計書.md` の見出しを `grep -n` で探す。見つからなければ「参照」行は置かない）。

| 章 | 旧版の行 |
|---|---|
| 2. 動作環境と配布形態 | 163–175（システム要件表）、453–478（アンインストール時のデータ扱い） |
| 3. 共有フォルダの構築 | 176–190、2047–2065、2066–2075、2200–2242 |
| 4. データベースの配置と接続 | 350–368、308–342、1367–1375、2020–2034、2035–2046、2076–2090 |
| 5. 設定ファイル | 1399–1472、1473–1608 |
| 6. バックアップ・VACUUM の仕組み | 1195–1248 のうち保持ルール・実施記録の説明部分、1814–1839、2091–2098 |
| 7. バージョン管理 | 421–452 |
| 8. セキュリティ | 1662–1701（8.3 監査ログは保存期間・記録項目の説明のみ。画面の見方は管理者マニュアル付録 B）、1702–1774、1775–1811 |
| 9. 障害対応 | 1942–1983 のうち各診断項目の意味の表、ログファイルの場所と読み方（1830–1839） |

- [ ] **Step 3: 検査する**

```bash
cd /mnt/d/OneDrive/交通系/src
python3 "$SCRATCH/check-anchors.py" ICCardManager/docs/manual/IT担当者ガイド.md
grep -c 'Issue #' ICCardManager/docs/manual/IT担当者ガイド.md
```
Expected: `0 unresolved`、`Issue #` は `0`（「設計書参照」行は Issue 番号を含まない）

- [ ] **Step 4: Commit**

```bash
git add ICCardManager/docs/manual/IT担当者ガイド.md
git commit -m "docs: IT担当者ガイドを新設し、管理者マニュアルの IT 担当者向け記述を移した"
```

---

### Task 2: 新版 管理者マニュアル 冒頭と第1部

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（全面書き換え。以降のタスクで追記）

- [ ] **Step 1: 冒頭を書く**

ファイルを空にして、次を書く。目次は「新版の見出し」の全 `##`／`###` を列挙する（アンカーは Task 0 の `slug` 規則で生成。例: `[1.2 1台のPCで使う場合のインストール](#12-1台のpcで使う場合のインストール)`）。

```markdown
# 交通系ICカード管理システム：ピッすい 管理者マニュアル

**バージョン**: 2.10.0
**最終更新日**: 2026年9月

---

## 目次

（略。全見出しを列挙）

---

## はじめに

### このマニュアルの読み方

このマニュアルは、ピッすいの管理者（部署の庶務担当者）向けです。「いつ発生する作業か」の順に章を分けています。最初から通読する必要はありません。

| 章 | こんなとき |
|----|-----------|
| 第1部 はじめて使うとき | 導入時に一度だけ行う作業 |
| 第2部 毎月すること | 月次帳票の出力とバックアップの確認 |
| 第3部 人やカードが増減したとき | 職員・交通系ICカードの追加や削除 |
| 第4部 年に一度すること | バージョンアップ、古いデータの削除 |
| 第5部 困ったとき | 症状別の対処 |
| 第6部 データを取り出す・戻す | バックアップ、リストア、CSV の入出力 |

共有フォルダの作成やネットワークの設定など、情報システム担当者に依頼する作業は **IT担当者ガイド** にまとめています。本文中では「IT 担当者に依頼してください」と示します。

> **ヒント**: 4 月から新しく使い始める場合は、別紙「かんたん導入ガイド」（2 ページ）が最短の手順です。

### 管理者の役割
（旧 152–162 を文体規約で整える）

### 動作環境
（旧 163–175 の表から OS・メモリ・カードリーダーの 3 行だけ残す。CPU・ビット数・SMB は IT 担当者ガイド §2 へ）

## 困ったときの早見表

| 症状 | 参照先 |
|------|--------|
| 起動しない、エラーが出て終了する | [5.2 起動しない](#52-起動しない) |
| 「ピッすいの更新が必要です」と表示される | [5.7](#57-ピッすいの更新が必要ですと表示される) |
| カードをかざしても反応しない | [5.3 カードが読めない](#53-カードが読めない) |
| 残額が合わない、履歴が食い違う | [5.4 データがおかしい](#54-データがおかしい) |
| 画面上部に共有モードの警告が出る | [5.5](#55-共有モードの警告が出る) |
| バックアップの警告が出る | [2.3](#23-バックアップが動いているか確かめる) |
| 帳票の事前チェックで警告が出る | [2.2](#22-出力前の事前チェックで警告が出たとき) |
| 読み取らずにカードが持ち出された | [3.3](#33-読み取らずに持ち出されたカードの貸出記録を作る) |
| 残高不足で現金チャージした | [3.5](#35-残高不足で現金チャージした利用の扱い) |
```

- [ ] **Step 2: 第1部を書く**

テンプレートに従い、旧版の次の行から内容を移す。

| 新節 | 旧版の行 | 移し方の注意 |
|---|---|---|
| 1.1 導入の流れ | 795–807 | 全体像の図（番号付き 5 ステップ）だけ。4 月開始は 1.6、年度途中は 1.7 へ分岐すると書く |
| 1.2 1台のPCで使う場合 | 191–268 | インストーラーの各ページを手順の番号にする。「部署の選択」「データベースの保存先（このPCのみ）」「帳票出力先」。画像 `installer_*.png` は再利用 |
| 1.3 複数のPCで使う場合 | 176–190（依頼の一文に圧縮）、269–307、343–368 | 冒頭に「共有フォルダは IT 担当者に依頼」。1 台目と 2 台目以降を小見出しで分ける。「1台目が起動中の挙動」表・フォールバック表・上書き動作は IT ガイド §4 へ参照 |
| 1.4 初回起動と初期設定 | 343–349、479–535 | 設定画面の各項目の意味は付録 A へ参照し、ここは「最初に確認する 3 項目」（部署、バックアップ先、帳票出力先）だけ |
| 1.5 職員を登録する | 545–565 | |
| 1.6 カードを登録する（4月開始） | 604–687、808–815 | 「新規購入」を選ぶ場合の手順 |
| 1.7.1 紙の出納簿から引き継ぐ | 816–876、1119–1130 | |
| 1.7.2 カードに残っていない履歴を取り込む | 877–945（「履歴は最大 20 件」を冒頭 1 段落に圧縮）、946–1057 | CSV の列一覧表と文字コードの注意は残す |
| 1.7.3 取り込んだあとの帳票を確かめる | 1058–1080 | 事前チェックの詳細は 2.2 へ参照 |
| 1.8 導入時のよくある質問 | 1131–1156 | |

- [ ] **Step 3: 検査する**

```bash
cd /mnt/d/OneDrive/交通系/src
bash "$SCRATCH/check-forbidden.sh" ICCardManager/docs/manual/管理者マニュアル.md | tail -1
grep -n '^#' ICCardManager/docs/manual/管理者マニュアル.md | grep -c '^'
```
Expected: `forbidden: 0`。見出し数は「はじめに」3 + 早見表 1 + 第1部 12（1.7 の小節含む）+ タイトル = 17 前後。目次内のリンクはこの時点では未解決で構わない（Task 4 で全件検査）。

- [ ] **Step 4: Commit**

```bash
git add ICCardManager/docs/manual/管理者マニュアル.md
git commit -m "docs: 管理者マニュアルを作業別構成へ書き換え（冒頭・第1部 はじめて使うとき）"
```

---

### Task 3: 第2部〜第4部

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（末尾に追記）

- [ ] **Step 1: 第2部を書く**

| 新節 | 旧版の行 | 注意 |
|---|---|---|
| 2.1 物品出納簿を出す | 1281–1312 | 「先月分を一括出力」を主手順にし、単票出力を補足に |
| 2.2 事前チェックで警告が出たとき | 1081–1118 | 警告 5 種別ごとに「意味／直す場所」の表 |
| 2.3 バックアップが動いているか確かめる | 1211–1248、1814–1824 のうち管理者が行う確認 | F6 の「バックアップ状況」を見る手順。警告が出たときは IT 担当者へ、の一文 |

- [ ] **Step 2: 第3部を書く**

| 新節 | 旧版の行 |
|---|---|
| 3.1.1 職員が加わった | 「1.5 職員を登録する」への参照 1 行と、登録後の確認だけ |
| 3.1.2 職員の情報を直す | 566–575 |
| 3.1.3 職員が退職・異動した | 576–583 |
| 3.1.4 職員証を再発行した | 584–594 |
| 3.2.1 カードを追加する | 「1.6」への参照 1 行 |
| 3.2.2 カードの情報を直す | 688–708 |
| 3.2.3 カードを払い戻す | 736–752 |
| 3.2.4 カードを削除する | 728–735 |
| 3.3 読み取らずに持ち出されたカード | 753–790 |
| 3.4 繰越情報が失われたカードを復旧する | 709–727 |
| 3.5 残高不足で現金チャージした利用 | 2117–2155（「検出条件（参考）」は削除。仕組みの説明は 04_機能設計書にある） |

- [ ] **Step 3: 第4部を書く**

| 新節 | 旧版の行 | 注意 |
|---|---|---|
| 4.1 新しいバージョンに更新する | 369–420 | 「全 PC を同じバージョンに揃える」を前提に書く。`latest_version.txt` の運用は IT ガイド §7 へ参照 |
| 4.2 古いデータを削除する | 1355–1364 | |
| 4.3 同一とみなす駅・バス停を登録する | 1609–1659 | 節タイトル直下に「（随時）」の一文。「なぜ必要か」は 3 文以内に圧縮 |

- [ ] **Step 4: 検査・Commit**

```bash
bash "$SCRATCH/check-forbidden.sh" ICCardManager/docs/manual/管理者マニュアル.md | tail -1
git add ICCardManager/docs/manual/管理者マニュアル.md
git commit -m "docs: 管理者マニュアル 第2部〜第4部（毎月・増減時・年次の作業）"
```
Expected: `forbidden: 0`

---

### Task 4: 第5部・第6部・付録と全体検査

**Files:**
- Modify: `ICCardManager/docs/manual/管理者マニュアル.md`（末尾に追記）

- [ ] **Step 1: 第5部を書く**

| 新節 | 旧版の行 | 注意 |
|---|---|---|
| 5.1 まず接続診断を実行する | 1942–1983 | 実行手順と「異常」が出たときの行動。各項目の技術的意味は IT ガイド §9 へ |
| 5.2 起動しない | 1984–1995 | 末尾に「felicalib.dll の整合性エラーが表示されたら、そのまま IT 担当者へ連絡してください」の 1 行 |
| 5.3 カードが読めない | 1996–2005 | |
| 5.4 データがおかしい | 2006–2019 | |
| 5.5 共有モードの警告が出る | 2020–2034 の症状表のうち管理者が対処できる行 | それ以外は「IT 担当者へ」 |
| 5.6 バックアップ・リストアがうまくいかない | 2099–2116 | |
| 5.7 「ピッすいの更新が必要です」 | 421–452 のうち「旧バージョンの起動ブロック」の対処法だけ | 4.1 へ参照 |

- [ ] **Step 2: 第6部を書く**

| 新節 | 旧版の行 |
|---|---|
| 6.1 手動バックアップ | 1167–1194 |
| 6.2 リストア | 1249–1266、2076–2090 のうち「他の PC を全部終了する」注意 |
| 6.3 CSV エクスポート | 1273–1280 |
| 6.4 CSV インポート | 1313–1354 |

- [ ] **Step 3: 付録を書く**

| 新節 | 旧版の行 | 注意 |
|---|---|---|
| A. 設定画面（F5）の項目一覧 | 486–535、1376–1398 | 項目名／意味／既定値の 1 表 |
| B. システム管理画面（F6）の項目一覧 | 1159–1182、1677–1701 のうち画面の見方 | |
| C. 管理者ダッシュボード（F8）の見方 | 1840–1939 | **C.3 利用推移タブの本文（旧 1896–1921）は文言を変えずに移す**。`AdminDashboardOtherSeriesCountManualConventionTests` が「その他（N 名）」の N を実人数と断定しないこと・両方向の但し書き・実装の書式との一致を検査する |
| D. ショートカットキー | 2158–2180 | |
| E. 用語集 | 2181–2199 | |

- [ ] **Step 4: 全体検査**

```bash
cd /mnt/d/OneDrive/交通系/src
M=ICCardManager/docs/manual/管理者マニュアル.md
python3 "$SCRATCH/check-anchors.py" "$M"
bash "$SCRATCH/check-forbidden.sh" "$M" | tail -1
# 画像参照が旧版と同数残っているか
grep -o '\.png' "$SCRATCH/old.md" | wc -l; grep -o '\.png' "$M" ICCardManager/docs/manual/IT担当者ガイド.md | wc -l
# 「ICカード」単独表記（複合語を除く）が増えていないか
grep -oE '(^|[^系])ICカード([^リ管]|$)' "$SCRATCH/old.md" | wc -l; grep -oE '(^|[^系])ICカード([^リ管]|$)' "$M" | wc -l
# 新版の見出しが「新版の見出し」表と一致するか
grep '^#' "$M" > "$SCRATCH/new-heads.txt"; wc -l "$SCRATCH/new-heads.txt"
```
Expected: `0 unresolved`、`forbidden: 0`、画像は合計 27、「ICカード」単独は旧版以下、見出しは 68 行（新版の見出し表と目視で一致）。

- [ ] **Step 5: 旧版の全見出しに対する移動先を確認する**

```bash
grep -n '^#' "$SCRATCH/old.md" > "$SCRATCH/old-heads.txt"
```
`$SCRATCH/coverage.md` に「旧見出し | 移動先（新節 or IT ガイド章 or 削除理由）」の表を書く。削除は「検出条件（参考）」「Issue 経緯」「クイックガイド（目次が代替）」の類だけ許す。表は Task 7 で PR 本文に貼る。

- [ ] **Step 6: Commit**

```bash
git add ICCardManager/docs/manual/管理者マニュアル.md
git commit -m "docs: 管理者マニュアル 第5部・第6部・付録を追加し、作業別構成を完成させた"
```

---

### Task 5: コードとテストの追随

**Files:**
- Modify: `ICCardManager/tests/ICCardManager.Tests/Views/AdminDashboardOtherSeriesCountManualConventionTests.cs:46`
- Modify: `ICCardManager/src/ICCardManager/Views/Dialogs/CardRegistrationModeDialog.xaml:218`
- Modify: `ICCardManager/src/ICCardManager/Common/AppConstants.cs:90`
- Modify: `ICCardManager/src/ICCardManager/Services/OrganizationOptions.cs:252`
- Modify: `ICCardManager/src/ICCardManager/Services/CsvImportService.cs:794`

- [ ] **Step 1: テストを実行して赤を確認する**

```bash
cd /mnt/d/OneDrive/交通系/src
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln -c Release --filter "FullyQualifiedName~AdminDashboardOtherSeriesCountManualConventionTests" 2>&1 | tail -15
```
Expected: FAIL（見出し `#### 9.4.3 利用推移タブ` が見つからない）

- [ ] **Step 2: 見出し定数を付け替える**

```csharp
    /// <summary>集約系列の説明が置かれている節（この節がマニュアル側の正典）。</summary>
    private const string TargetHeading = "#### C.3 利用推移タブ";
```
XML コメント中の「§9.4.3」も「付録 C.3」に置き換える（ファイル内を `grep -n '9\.4\.3'` で確認）。

- [ ] **Step 3: テストが緑になることを確認する**

同じコマンド。Expected: PASS 3 件。

- [ ] **Step 4: ダイアログ文言を付け替える**

```xml
<Run Text="特に年度途中からの導入時は手順が複雑になる場合があります。詳しくは管理者マニュアルの「1.7 年度途中から使い始める場合」をご参照ください。"/>
```

- [ ] **Step 5: XML コメントの参照先を付け替える**

- `AppConstants.cs:90`: `（管理者マニュアル §3.3 / ユーザーマニュアル §7.2 …）` → `（管理者マニュアル 付録 A / ユーザーマニュアル §7.2 …）`
- `OrganizationOptions.cs:252`: `管理者マニュアル §7.4 が` → `IT担当者ガイド §5（旧 管理者マニュアル §7.4）が`
- `CsvImportService.cs:794`: `管理者マニュアル §5.6.5 が` → `管理者マニュアル §1.7.2 が`

- [ ] **Step 6: ビルドとテストの回帰確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln -c Release 2>&1 | grep -E 'warning|error|Warn|Error' | grep -v '0 Warning' | head
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln -c Release --filter "FullyQualifiedName~ConventionTests|FullyQualifiedName~UserFacingText" 2>&1 | tail -5
```
Expected: 警告 0、Convention 系テスト全件 PASS（`OrganizationOptionsUsageConventionTests`、`UserFacingTextConventionTests` を含む）。

- [ ] **Step 7: Commit**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Views/AdminDashboardOtherSeriesCountManualConventionTests.cs \
        ICCardManager/src/ICCardManager/Views/Dialogs/CardRegistrationModeDialog.xaml \
        ICCardManager/src/ICCardManager/Common/AppConstants.cs \
        ICCardManager/src/ICCardManager/Services/OrganizationOptions.cs \
        ICCardManager/src/ICCardManager/Services/CsvImportService.cs
git commit -m "docs: 管理者マニュアルの節番号変更にアプリ内文言・テスト・コメントを追随させた"
```

---

### Task 6: 他文書・配布物の追随

**Files:**
- Modify: `ICCardManager/docs/manual/ユーザーマニュアル.md:434,884`
- Modify: `ICCardManager/docs/manual/かんたん導入ガイド.md:165`
- Modify: `ICCardManager/docs/manual/はじめに.md:33-45,58`
- Modify: `ICCardManager/docs/manual/README.md`（マニュアル一覧・ファイル構成の 2 表）
- Modify: `ICCardManager/docs/manual/convert-to-docx.ps1:189-196`
- Modify: `ICCardManager/docs/manual/convert-to-pdf.ps1:45-50`
- Modify: `ICCardManager/installer/ICCardManager.iss:92,97,102`
- Modify: `ICCardManager/docs/design/08_ドキュメント設計書.md:61,132-140,198`
- Modify: `ICCardManager/CHANGELOG.md`（`### Unreleased` の「ドキュメント」）

- [ ] **Step 1: 他マニュアルの参照を付け替える**

- ユーザーマニュアル L434: `管理者マニュアル §7.4a` → `管理者マニュアル「4.3 同一とみなす駅・バス停を登録する」`
- ユーザーマニュアル L884: `「5.6.5 月途中からの履歴入力（CSVインポート）」および「6.4 データインポート」` → `「1.7.2 カードに残っていない履歴を取り込む」および「6.4 CSV インポート」`
- かんたん導入ガイド L165: `「5.6 利用開始時の交通系ICカード登録（初期導入手順）」` → `「1.7 年度途中から使い始める場合」`
- はじめに: 「初期設定や管理作業を行うとき」の箇条書きを新版の 6 部に合わせ、その下に次を追加

```markdown
### 共有フォルダやネットワークの設定を行うとき

**→ IT担当者ガイド** をお読みください。

- 共有フォルダの構築とアクセス権
- 設定ファイル（appsettings.json）
- セキュリティと障害対応
```
マニュアル一覧の表に `| IT担当者ガイド | 情報システム担当者 | 共有フォルダ、設定ファイル、セキュリティ、障害対応 |` を管理者マニュアルの次の行に追加。

- [ ] **Step 2: README と変換スクリプトに追加する**

README の 2 表に IT担当者ガイドの行を追加。`convert-to-docx.ps1` の `$Manuals` に管理者マニュアルの次として追加:

```powershell
    @{
        Name = "IT担当者ガイド"
        Key = "it"
        Input = "IT担当者ガイド.md"
        Output = "IT担当者ガイド.docx"
        Title = "交通系ICカード管理システム：ピッすい IT担当者ガイド"
        VersionTracked = $true   # アプリバージョンに追従
    },
```
`convert-to-pdf.ps1` の定義にも同じ位置に `Input = "IT担当者ガイド.docx"` / `Output = "IT担当者ガイド.pdf"` を追加。スクリプト冒頭のコメント（`-Target user` の例）に `it` を追記。

- [ ] **Step 3: インストーラーに追加する**

`.iss` の 3 ブロック（md / docx / pdf）それぞれ、管理者マニュアルの行の直後に同形式で `IT担当者ガイド` の行を追加。docx / pdf 行は `.docx` が未生成でもビルドが通るよう `skipifsourcedoesntexist` を付ける（管理者マニュアルの pdf 行と同じ）。

- [ ] **Step 4: 08_ドキュメント設計書を更新する**

- §2.2 の表に `| 5 | IT担当者ガイド | \`IT担当者ガイド.md\` / \`.docx\` | 情報システム担当者 | 詳細版 |` を追加（開発者ガイドを 5 → 6 に繰り下げ、または末尾に追加）
- §4.2 推奨章構成を新版の 6 部＋付録に置き換え、直後に「IT担当者ガイドの章構成」（設計書 §5 の 9 章）を追加
- 作成状況の表（L198 付近）に IT担当者ガイドの行を追加

- [ ] **Step 5: CHANGELOG に追記する**

`### Unreleased` の **ドキュメント** 節の先頭に追加:

```markdown
- **管理者マニュアルを「作業別」構成へ書き直し、IT 担当者向けの内容を新設の `IT担当者ガイド.md` へ分離した**。読者を部署の庶務担当者に絞り、章立てを「はじめて使うとき／毎月すること／人やカードが増減したとき／年に一度すること／困ったとき／データを取り出す・戻す」と付録（設定画面・システム管理画面・管理者ダッシュボードの項目一覧）に組み替えた。各作業は「この作業をするとき／前提／手順／できたことの確認」の固定型で書き、Issue 番号と改善経緯を本文から外した。共有フォルダの構築・SQLite の接続・appsettings と OrganizationOptions・DLL 完全性検証・障害対応は IT担当者ガイドへ移した。節番号の変更に伴い、カード登録方法ダイアログの案内文・`AdminDashboardOtherSeriesCountManualConventionTests` の見出し定数・ユーザーマニュアル／かんたん導入ガイド／はじめに の参照・インストーラーと変換スクリプトのファイル一覧・08_ドキュメント設計書を追随させた。`.docx` / `.pdf` は次回リリース時に再生成する
```

- [ ] **Step 6: 検査・Commit**

```bash
cd /mnt/d/OneDrive/交通系/src
grep -rn '§5\.6\|§7\.4\|§9\.4\|5\.6\.5\|7\.4a' ICCardManager/docs/manual/*.md ICCardManager/src --include='*.md' --include='*.cs' --include='*.xaml' | grep -v 'IT担当者ガイド\|旧 管理者マニュアル' | grep '管理者マニュアル'
```
Expected: 出力なし（旧節番号の参照が残っていない）。

```bash
git add ICCardManager/docs/manual/ユーザーマニュアル.md ICCardManager/docs/manual/かんたん導入ガイド.md \
        ICCardManager/docs/manual/はじめに.md ICCardManager/docs/manual/README.md \
        ICCardManager/docs/manual/convert-to-docx.ps1 ICCardManager/docs/manual/convert-to-pdf.ps1 \
        ICCardManager/installer/ICCardManager.iss ICCardManager/docs/design/08_ドキュメント設計書.md \
        ICCardManager/CHANGELOG.md
git commit -m "docs: IT担当者ガイドを配布物・変換スクリプト・設計書・各マニュアルの案内へ追加した"
```

---

### Task 7: 最終検証と PR

- [ ] **Step 1: 全検査をまとめて実行する**

```bash
cd /mnt/d/OneDrive/交通系/src
for f in ICCardManager/docs/manual/管理者マニュアル.md ICCardManager/docs/manual/IT担当者ガイド.md; do
  python3 "$SCRATCH/check-anchors.py" "$f" | tail -1
  bash "$SCRATCH/check-forbidden.sh" "$f" | tail -1
done
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/ICCardManager.sln -c Release 2>&1 | tail -5
git status --short
```
Expected: 両ファイル `0 unresolved` / `forbidden: 0`、テスト全件 PASS、未追跡ファイルは `merged-branches-2026-09-05.md` のみ（これは本 PR の対象外。ステージしない）。

- [ ] **Step 2: found-issues.md を確認する**

`$SCRATCH/found-issues.md` に記録があれば、1 件ずつ `gh issue create` の下書きを PR 本文の末尾に「別途起票する事項」として列挙する（本 PR では直さない）。

- [ ] **Step 3: PR を作成する**

```bash
git push -u origin docs/admin-manual-task-oriented
```
PR 本文は `$SCRATCH/pr-body.md` に書き、`gh pr create --title "docs: 管理者マニュアルを作業別構成へ書き直し、IT担当者ガイドを新設" --body-file "$SCRATCH/pr-body.md"` で作成する。本文には設計書へのパス、`$SCRATCH/coverage.md` の対応表、検査結果（アンカー・禁止語・テスト）、「`.docx` / `.pdf` は次回リリース時に再生成」の注記、末尾に

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01H3hxEXMvzun1owyyquGWRX
```
を含める。

---

## Self-Review

- **Spec coverage**: 設計書 §3 の章構成 → Task 2–4。§4 対応表 → 各タスクの行範囲表（全行を割り当て済み）。§5 IT ガイド → Task 1。§6 文体規約 → Global Constraints。§7 追随 → Task 5・6。§8 検証 → Task 0 のスクリプトと Task 4・7。§9 スコープ外 → Global Constraints（docx/pdf、スクショ、食い違い）。
- **Placeholder scan**: 各節の本文は旧版の行範囲とテンプレートで指定し、「TBD」「適宜」は無い。目次の「（略）」は生成規則を示している。
- **Type consistency**: テスト定数は Task 4 の見出し `#### C.3 利用推移タブ` と Task 5 の `TargetHeading` が一致。XAML の節名は Task 2 の `### 1.7 年度途中から使い始める場合` と一致。
