#!/usr/bin/env python3
"""
StationCode.csv マージスクリプト

現行CSVとMasanoriYONO/StationCodeリポジトリのデータを統合・重複除去する。
再実行可能（冪等）。

出典:
  - IC SFCard Fan (http://www.denno.net/SFCardFan/) が駅コードデータの源流
  - MasanoriYONO/StationCode (https://github.com/MasanoriYONO/StationCode)
    がIC SFCard FanのデータをCSV化したもの

使い方:
    python3 tools/merge_station_codes.py

出力:
    - ICCardManager/src/ICCardManager/Resources/StationCode.csv (UTF-8)
    - ICCardManager/docs/線区駅順コード/StationCode.csv (UTF-8)
"""

import csv
import io
import os
import sys
import urllib.request

# === パス設定 ===
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(SCRIPT_DIR)

RESOURCES_CSV = os.path.join(
    PROJECT_ROOT, "ICCardManager", "src", "ICCardManager", "Resources", "StationCode.csv"
)
DOCS_CSV = os.path.join(
    PROJECT_ROOT, "ICCardManager", "docs", "線区駅順コード", "StationCode.csv"
)

YONO_URL = "https://raw.githubusercontent.com/MasanoriYONO/StationCode/master/StationCode.csv"

HEADER = ["AreaCode", "LineCode", "StationCode", "CompanyName", "LineName", "StationName", "Note"]

# === 競合解決マップ ===

# MasanoriYONO版の駅名を採用するキー
# 値: Note列に追加する注釈（Noneなら注釈なし）
USE_YONO = {
    # 駅順コードずれ修正
    (0, 6, 48): None,              # 天拝山（現行は誤って「二日市」）
    (0, 41, 32): None,             # 越谷レイクタウン（駅追加でコードずれ）
    (0, 41, 33): None,             # 吉川
    (0, 41, 34): None,             # 吉川美南
    (0, 42, 13): None,             # 新川崎（現行は誤って「武蔵小杉」）

    # 改名反映
    (0, 16, 129): None,            # 後免（誤字「御免」を修正）
    (0, 16, 131): None,            # 後免町
    (0, 17, 36): None,             # 後免
    (0, 157, 2): None,             # とうきょうスカイツリー（2012年改名）
    (0, 212, 9): None,             # 日本大通り（「り」欠落修正）
    (0, 214, 6): None,             # 羽田空港国際線ターミナル（仮称→正式名）
    (0, 250, 10): None,            # 羽田空港国際線ビル
    (2, 157, 34): None,            # 神戸三宮（2013年改名）
    (2, 158, 11): None,            # 服部天神（2013年改名）
    (2, 158, 31): None,            # 中山観音（2013年改名）
    (2, 165, 49): None,            # 松尾大社（2013年改名）
    (2, 197, 84): None,            # 清水五条（2008年改名）
    (2, 197, 90): None,            # 神宮丸太町（2008年改名）
    (2, 231, 1): None,             # 大阪難波（2009年改名）
    (2, 231, 3): None,             # 大阪上本町（2009年改名）
    (2, 239, 27): None,            # 川越富洲原（2009年改名）
    (2, 255, 189): None,           # みなとじま（2014年改名）
    (2, 255, 199): None,           # 医療センター
    (3, 215, 124): None,           # 紫（2017年改名）

    # その他の修正
    (0, 7, 40): None,              # 宇佐
    (0, 80, 27): None,             # 越前下山（先頭スペース除去）
    (0, 83, 26): None,             # 中込

    # 注釈を駅名からNote列に移動
    (0, 11, 54): "旧・城崎",      # 城崎温泉
    (0, 36, 203): "旧 東篠路",    # 拓北
    (0, 55, 193): "2006.4.21廃止",  # 池田
    (0, 55, 194): "06.4.21廃止",    # 様舞
    (0, 55, 228): "06.4.21廃止",    # 北光社
    (0, 55, 230): "2006.4.21廃止",  # 北見
    (0, 76, 1): "旧、のと穴水",   # 穴水
    (1, 134, 90): "6号線",         # 野並
}

# 現行データを維持するキー
USE_CURRENT = {
    (0, 0, 1),      # 東京（実駅データをテストデータより優先）
    (0, 6, 47),     # 二日市（テストで使用中、駅順で正しい）
    (1, 129, 52),   # 本陣（YONO版が文字化け）
    (1, 129, 66),   # 栄（YONO版が文字化け）
    (2, 148, 4),    # 桜川（エリア固有の正しいデータ）
    (2, 197, 86),   # 祇園四条（現行のほうが新名称）
}

# YONO版から除外するキー（ダミーデータ）
EXCLUDE_YONO = {
    (3, 255, 255),  # "日本電気鉄道/日本/日本" はダミーエントリ（hex: 3,ff,ff）
}

# YONO版データの後処理修正（データ誤りの補正）
STATION_NAME_OVERRIDES = {
    (0, 157, 2): "とうきょうスカイツリー",  # YONO版の末尾「＋」を除去
}


def is_garbled(text):
    """文字化け（エンコーディングエラー）の検出。

    日本語テキストに本来出現しないLatin-1 Supplement/Latin Extended範囲の
    文字が含まれていれば文字化けと判定する。
    """
    for ch in text:
        code = ord(ch)
        if 0x80 <= code <= 0x024F:
            return True
    return False


def download_yono():
    """MasanoriYONO版StationCode.csvをダウンロードしてパースする。

    MasanoriYONO版はCSV形式で、コードは16進数、Note列なし（6列）。
    ヘッダー: 地区コード(16進),線区コード(16進),駅順コード(16進),会社名,線区名,駅名

    Returns:
        dict: キー(area, line, station) → エントリdict
    """
    print(f"Downloading from {YONO_URL}...")
    try:
        req = urllib.request.Request(YONO_URL)
        with urllib.request.urlopen(req, timeout=30) as response:
            raw = response.read()
    except Exception as e:
        print(f"ERROR: Download failed: {e}", file=sys.stderr)
        sys.exit(1)

    # UTF-8でデコード
    content = raw.decode("utf-8")

    entries = {}
    garbled_count = 0

    reader = csv.reader(io.StringIO(content))
    header = next(reader)  # ヘッダースキップ

    for row in reader:
        if len(row) < 6:
            continue

        try:
            area = int(row[0], 16)
            line = int(row[1], 16)
            station = int(row[2], 16)
        except ValueError:
            continue

        company = row[3].strip()
        line_name = row[4].strip()
        station_name = row[5].strip()

        if not station_name:
            continue

        key = (area, line, station)

        # ダミーエントリを除外
        if key in EXCLUDE_YONO:
            print(f"  [skip] dummy: ({area},{line},{station}) {station_name}")
            continue

        # 文字化けエントリを除外
        if is_garbled(company) or is_garbled(line_name) or is_garbled(station_name):
            garbled_count += 1
            print(f"  [skip] garbled: ({area},{line},{station}) {station_name}")
            continue

        # 同一キーで複数エントリがある場合、後のエントリが改名後の
        # 新しいデータであるため、常に上書きする（last entry wins）
        entries[key] = {
            "AreaCode": area,
            "LineCode": line,
            "StationCode": station,
            "CompanyName": company,
            "LineName": line_name,
            "StationName": station_name,
            "Note": "",
        }

    print(f"  Loaded {len(entries)} unique entries ({garbled_count} garbled skipped)")
    return entries


def read_current():
    """現行StationCode.csv（UTF-8）を読み込む。

    同一キーで複数エントリが存在する場合はリストとして保持する。

    Returns:
        dict: キー(area, line, station) → エントリdictのリスト
    """
    entries = {}
    total_rows = 0

    # エンコーディングを自動判定（Shift_JIS or UTF-8）
    try:
        with open(RESOURCES_CSV, "r", encoding="utf-8") as f:
            content = f.read()
    except UnicodeDecodeError:
        with open(RESOURCES_CSV, "r", encoding="shift_jis") as f:
            content = f.read()

    reader = csv.reader(io.StringIO(content))
    header = next(reader)

    for row in reader:
        if len(row) < 6:
            continue

        try:
            area = int(row[0].strip())
            line = int(row[1].strip())
            station = int(row[2].strip())
        except ValueError:
            continue

        company = row[3].strip()
        line_name = row[4].strip()
        station_name = row[5].strip()
        note = row[6].strip() if len(row) > 6 else ""

        if not station_name:
            continue

        entry = {
            "AreaCode": area,
            "LineCode": line,
            "StationCode": station,
            "CompanyName": company,
            "LineName": line_name,
            "StationName": station_name,
            "Note": note,
        }

        key = (area, line, station)
        if key not in entries:
            entries[key] = []
        entries[key].append(entry)
        total_rows += 1

    print(f"  Loaded {total_rows} rows ({len(entries)} unique keys)")
    return entries


def pick_best_current(candidates):
    """現行データの重複エントリから最適なものを選択する。

    テストデータ（CompanyName='試験'）より実データを優先する。
    """
    for c in candidates:
        if c["CompanyName"] != "試験":
            return c
    return candidates[0]


def merge(current, yono):
    """現行データとYONOデータを競合解決マップに従ってマージする。

    Returns:
        dict: キー(area, line, station) → マージ済みエントリdict
    """
    merged = {}
    all_keys = set(current.keys()) | set(yono.keys())

    stats = {
        "current_only": 0,
        "yono_only_added": 0,
        "both_same": 0,
        "conflict_use_current": 0,
        "conflict_use_yono": 0,
        "duplicates_removed": 0,
    }

    for key in sorted(all_keys):
        in_current = key in current
        in_yono = key in yono

        # === Case 1: 現行データを強制採用 ===
        if key in USE_CURRENT:
            if in_current:
                merged[key] = pick_best_current(current[key])
                if in_current and len(current[key]) > 1:
                    stats["duplicates_removed"] += len(current[key]) - 1
            elif in_yono:
                merged[key] = yono[key]
            stats["conflict_use_current"] += 1
            continue

        # === Case 2: YONO版の駅名を強制採用 ===
        if key in USE_YONO:
            note_to_add = USE_YONO[key]
            if in_yono:
                if in_current:
                    # 現行のCompanyName/LineNameを維持し、駅名だけYONOに置換
                    base = pick_best_current(current[key]).copy()
                    base["StationName"] = yono[key]["StationName"]
                    if note_to_add:
                        base["Note"] = note_to_add
                    if len(current[key]) > 1:
                        stats["duplicates_removed"] += len(current[key]) - 1
                else:
                    base = yono[key].copy()
                    if note_to_add:
                        base["Note"] = note_to_add
                merged[key] = base
            elif in_current:
                # YONOにない場合は現行データをフォールバック
                merged[key] = pick_best_current(current[key])
                print(f"  [warn] USE_YONO key {key} not in YONO, using current")
            stats["conflict_use_yono"] += 1
            continue

        # === Case 3: 両方に存在 ===
        if in_current and in_yono:
            best = pick_best_current(current[key])

            if best["CompanyName"] == "試験":
                # 現行が全てテストデータ → YONO版を採用
                merged[key] = yono[key]
                stats["yono_only_added"] += 1
            else:
                merged[key] = best
                stats["both_same"] += 1

            if len(current[key]) > 1:
                stats["duplicates_removed"] += len(current[key]) - 1
            continue

        # === Case 4: 現行のみ ===
        if in_current:
            merged[key] = pick_best_current(current[key])
            if len(current[key]) > 1:
                stats["duplicates_removed"] += len(current[key]) - 1
            stats["current_only"] += 1
            continue

        # === Case 5: YONOのみ ===
        if in_yono:
            merged[key] = yono[key]
            stats["yono_only_added"] += 1
            continue

    # 駅名の後処理修正を適用
    for key, name in STATION_NAME_OVERRIDES.items():
        if key in merged:
            merged[key]["StationName"] = name

    print(f"\nMerge statistics:")
    for k, v in stats.items():
        print(f"  {k}: {v}")
    print(f"  Total merged entries: {len(merged)}")

    return merged


def write_csv(merged, output_path):
    """マージ結果をCSV（UTF-8, BOMなし）で出力する。"""
    sorted_keys = sorted(merged.keys())

    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    with open(output_path, "w", encoding="utf-8", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(HEADER)

        for key in sorted_keys:
            entry = merged[key]
            writer.writerow([
                entry["AreaCode"],
                entry["LineCode"],
                entry["StationCode"],
                entry["CompanyName"],
                entry["LineName"],
                entry["StationName"],
                entry["Note"],
            ])

    print(f"  Wrote {len(sorted_keys) + 1} lines to {os.path.basename(output_path)}")


def verify(merged):
    """テストで使用されるキーの駅名が正しいことを検証する。"""
    checks = [
        ((0, 0, 1), "東京"),
        ((0, 1, 1), "東京"),
        ((0, 6, 47), "二日市"),
        ((0, 6, 48), "天拝山"),
        ((3, 231, 15), "天神"),
        ((3, 231, 25), "福岡空港"),
        ((2, 197, 86), "祇園四条"),
        ((2, 157, 34), "神戸三宮"),
    ]

    all_ok = True
    for key, expected in checks:
        if key not in merged:
            print(f"  FAIL: {key} not found")
            all_ok = False
            continue

        actual = merged[key]["StationName"]
        if actual != expected:
            print(f"  FAIL: {key} expected '{expected}' but got '{actual}'")
            all_ok = False
        else:
            print(f"  OK: {key} = '{actual}'")

    # 重複チェック（dictなので構造上重複はないが念のため）
    print(f"  Unique keys: {len(merged)}")

    return all_ok


def main():
    print("=== StationCode.csv Merge Tool ===\n")

    print("Step 1: Download MasanoriYONO StationCode.csv")
    yono = download_yono()

    print("\nStep 2: Read current StationCode.csv")
    current = read_current()

    print("\nStep 3: Merge with conflict resolution")
    merged = merge(current, yono)

    print("\nStep 4: Verify critical entries")
    if not verify(merged):
        print("\nERROR: Verification failed! Aborting.")
        sys.exit(1)

    print("\nStep 5: Write output files")
    write_csv(merged, RESOURCES_CSV)
    write_csv(merged, DOCS_CSV)

    print("\nDone!")


if __name__ == "__main__":
    main()
