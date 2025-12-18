#!/usr/bin/env python3
"""
テストデータ追加スクリプト（Python版）
印刷プレビューのページネーション確認用

使い方:
  python add_test_data.py
"""

import sqlite3
import os
from pathlib import Path
from datetime import datetime, timedelta

def generate_test_data(year, month):
    """指定した年月のテストデータを生成"""
    from calendar import monthrange
    days_in_month = monthrange(year, month)[1]

    card_idm = '0123456789ABCDEF'
    staff_idm = 'STAFF001TESTIDM0'

    # 摘要のパターン
    summaries = [
        ('鉄道（博多駅～天神駅）テスト', 0, 260, '通勤'),
        ('鉄道（天神駅～博多駅）テスト', 0, 260, '帰宅'),
        ('鉄道（博多駅～福岡空港駅）テスト', 0, 260, '出張'),
        ('チャージ テスト', 3000, 0, 'セブンイレブン'),
        ('鉄道（博多駅～薬院駅）テスト', 0, 210, '会議'),
        ('鉄道（薬院駅～天神駅）テスト', 0, 210, '移動'),
        ('バス（★）テスト', 0, 190, '市内移動'),
        ('鉄道（天神駅～貝塚駅）テスト', 0, 340, '研修'),
        ('鉄道（貝塚駅～博多駅）テスト', 0, 340, '帰宅'),
        ('バス（★）テスト', 0, 230, '外出'),
        ('鉄道（博多駅～姪浜駅）テスト', 0, 300, '出張'),
        ('鉄道（姪浜駅～天神駅）テスト', 0, 260, '移動'),
        ('鉄道（天神駅～西新駅）テスト', 0, 210, '会議'),
        ('鉄道（西新駅～博多駅）テスト', 0, 260, '帰宅'),
        ('チャージ テスト', 5000, 0, 'ローソン'),
    ]

    test_data = []
    balance = 10000  # 初期残高

    # 50件のデータを生成
    for i in range(50):
        day = min(i + 1, days_in_month)  # 日にちは月末を超えないように
        date_str = f"{year:04d}-{month:02d}-{day:02d}"

        summary, income, expense, note = summaries[i % len(summaries)]
        balance = balance + income - expense

        # 貸出/返却時刻を生成
        lent_at = f"{date_str} 09:00:00"
        returned_at = f"{date_str} 17:00:00"

        test_data.append((
            card_idm, staff_idm, date_str, summary,
            income, expense, balance, 'テスト職員', note,
            staff_idm, lent_at, returned_at, 0  # returner_idm, lent_at, returned_at, is_lent_record
        ))

    return test_data

def main():
    # データベースパス
    local_appdata = os.environ.get('LOCALAPPDATA', '')
    db_path = Path(local_appdata) / 'ICCardManager' / 'iccard.db'

    print("=" * 60)
    print(" 印刷プレビュー テストデータ追加ツール（改良版）")
    print("=" * 60)
    print()

    if not db_path.exists():
        print(f"[ERROR] データベースが見つかりません: {db_path}")
        print()
        print("アプリケーションを一度起動してデータベースを初期化してください。")
        return 1

    print(f"[INFO] データベース: {db_path}")
    print()

    # 現在の年月を取得
    today = datetime.today()
    current_year = today.year
    current_month = today.month

    print(f"[INFO] 今月（{current_year}年{current_month}月）のテストデータを50件追加します")
    print()

    # 確認
    confirm = input("テストデータを追加します。続行しますか？ (Y/N): ")
    if confirm.upper() != 'Y':
        print("キャンセルしました。")
        return 0

    print()
    print("[INFO] テストデータを追加中...")

    conn = sqlite3.connect(str(db_path))
    cursor = conn.cursor()

    try:
        # テスト用カードを追加
        cursor.execute("""
            INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number, note, is_deleted, is_lent)
            VALUES ('0123456789ABCDEF', 'はやかけん', 'TEST-001', 'ページネーションテスト用', 0, 0)
        """)

        # テスト用職員を追加
        cursor.execute("""
            INSERT OR IGNORE INTO staff (staff_idm, name, number, note, is_deleted)
            VALUES ('STAFF001TESTIDM0', 'テスト職員', 'EMP001', 'テスト用', 0)
        """)

        # 既存のテストデータを削除（このカードの「テスト」を含むデータのみ）
        cursor.execute("""
            DELETE FROM ledger_detail WHERE ledger_id IN (
                SELECT id FROM ledger WHERE card_idm = '0123456789ABCDEF' AND summary LIKE '%テスト%'
            )
        """)
        cursor.execute("""
            DELETE FROM ledger WHERE card_idm = '0123456789ABCDEF' AND summary LIKE '%テスト%'
        """)

        # 今月のテストデータを生成
        test_data = generate_test_data(current_year, current_month)

        cursor.executemany("""
            INSERT INTO ledger (
                card_idm, lender_idm, date, summary, income, expense, balance,
                staff_name, note, returner_idm, lent_at, returned_at, is_lent_record
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """, test_data)

        conn.commit()

        # 確認
        cursor.execute("SELECT COUNT(*) FROM ledger WHERE card_idm = '0123456789ABCDEF'")
        count = cursor.fetchone()[0]

        print()
        print(f"[SUCCESS] テストデータの追加が完了しました！")
        print(f"  追加されたレコード数: {len(test_data)}")
        print(f"  総レコード数: {count}")
        print()
        print("アプリを再起動して、はやかけん TEST-001 の履歴を確認してください。")
        print(f"対象期間: {current_year}年{current_month}月")

    except Exception as e:
        conn.rollback()
        print(f"[ERROR] エラーが発生しました: {e}")
        import traceback
        traceback.print_exc()
        return 1
    finally:
        conn.close()

    return 0

if __name__ == "__main__":
    exit(main())
