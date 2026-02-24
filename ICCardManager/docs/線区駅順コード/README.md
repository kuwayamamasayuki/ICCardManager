# 線区駅順コード

## 概要

このディレクトリには、交通系ICカードの利用履歴から駅名を解決するための駅コードマスターデータが含まれています。

## ファイル

| ファイル名 | 説明 |
|------------|------|
| `StationCode.csv` | 全国の鉄道・軌道の駅コードマスター |

## CSVフォーマット

```
AreaCode,LineCode,StationCode,CompanyName,LineName,StationName,Note
```

| カラム | 説明 |
|--------|------|
| AreaCode | エリアコード（0:関東, 1:関西, 2:中部, 3:九州） |
| LineCode | 路線コード |
| StationCode | 駅コード |
| CompanyName | 事業者名 |
| LineName | 路線名 |
| StationName | 駅名 |
| Note | 備考 |

## 出典

本データは以下の情報源を基に作成・統合されています。

### 1. IC SFCard Fan（データの源流）

> **IC SFCard Fan**
> http://www.denno.net/SFCardFan/

交通系ICカードの利用履歴を閲覧するフリーソフト。本ソフトに同梱されている線区駅順コードデータが、各種駅コードCSVの原典です。

### 2. プロデルで交通系ICカード履歴ビューアを作る

> **プロデルで交通系ICカード履歴ビューアを作る**
> https://produ.irelang.jp/blog/2017/08/305/

上記サイトにてIC SFCard Fan由来の線区駅順コードがCSV形式で公開されており、本プロジェクトの初期データとして利用させていただきました。

### 3. MasanoriYONO/StationCode（統合データ）

> **MasanoriYONO/StationCode**
> https://github.com/MasanoriYONO/StationCode

IC SFCard Fan由来の駅コードデータをGitHub上でCSV公開しているリポジトリ。改名後の駅名（大阪難波、神戸三宮、とうきょうスカイツリー等）が反映されており、本プロジェクトではこのデータを統合して重複除去・駅名更新を行いました。

### 統合について

`tools/merge_station_codes.py` を実行することで、上記データソースの統合・重複除去を再実行できます。

## エンコーディング

UTF-8（BOMなし）

## 使用方法

このCSVファイルは `src/ICCardManager/Resources/StationCode.csv` にコピーされ、アプリケーションの埋め込みリソースとして使用されます。

`StationMasterService` クラスが起動時にこのデータを読み込み、FeliCaカードから読み取った利用履歴の駅コードを駅名に変換します。

## 注意事項

- 駅コードは各エリア（関東/関西/中部/九州）ごとに管理されています
- 同じ路線コード・駅コードでもエリアが異なると別の駅を指す場合があります
- 新駅の開業や駅名変更があった場合は、CSVの更新が必要です
