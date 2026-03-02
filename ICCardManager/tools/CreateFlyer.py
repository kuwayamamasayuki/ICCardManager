#!/usr/bin/env python3
"""
プロモーション用チラシ（A4縦 PDF）生成スクリプト

交通系ICカード管理システム：ピッすいの庁内プロモーション用チラシを
Pillow で生成する。

使い方:
    pip install Pillow
    python tools/CreateFlyer.py

出力:
    docs/promotion/チラシ.pdf

依存:
    - Python 3.x
    - Pillow (pip install Pillow)
    - Yu Gothic Bold フォント (Windows標準)
"""

import math
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    print("エラー: Pillow がインストールされていません。")
    print("  pip install Pillow")
    sys.exit(1)

# ============================================================
# 定数
# ============================================================
# A4縦 300 DPI
PAGE_WIDTH, PAGE_HEIGHT = 2480, 3508

# カラーパレット（CreatePromotionVideo.py と統一）
MAIN_BLUE = (33, 150, 243)           # #2196F3 アプリのテーマカラー
DARK_BLUE = (25, 118, 191)           # #1976BF ヘッダー下部グラデーション用
TEXT_DARK = (33, 33, 33)             # #212121
TEXT_WHITE = (255, 255, 255)         # #FFFFFF
TEXT_GRAY = (117, 117, 117)          # #757575
SHADOW_COLOR = (200, 200, 200)       # スクリーンショットの影
BORDER_COLOR = (224, 224, 224)       # #E0E0E0
WHITE = (255, 255, 255)
ACCENT_ORANGE = (255, 152, 0)        # #FF9800 貸出カラー

# カード・リーダーイラスト用（CreatePromotionVideo.py と統一）
CARD_WIDTH = 300
CARD_HEIGHT = 190
CARD_SILVER_BASE = 192
READER_WIDTH = 200
READER_HEIGHT = 120
READER_TOP_HEIGHT = 15
READER_BLACK = (26, 26, 26)
READER_TOP = (50, 50, 50)
READER_LAMP_ON = (76, 175, 80)

# フォント
FONT_PATH = "/mnt/c/Windows/Fonts/YuGothB.ttc"

# パス
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent
SCREENSHOTS_DIR = PROJECT_DIR / "docs" / "screenshots"
PROMOTION_DIR = PROJECT_DIR / "docs" / "promotion"

# レイアウト定数
MARGIN = 80
CONTENT_WIDTH = PAGE_WIDTH - MARGIN * 2


# ============================================================
# フォントキャッシュ
# ============================================================
_font_cache = {}


def get_font(size: int) -> ImageFont.FreeTypeFont:
    """Yu Gothic Bold フォントを取得（キャッシュ付き）"""
    if size not in _font_cache:
        try:
            _font_cache[size] = ImageFont.truetype(FONT_PATH, size)
        except OSError:
            print(f"警告: フォント {FONT_PATH} が見つかりません。デフォルトフォントを使用します。")
            _font_cache[size] = ImageFont.load_default()
    return _font_cache[size]


# ============================================================
# 描画ヘルパー
# ============================================================
def load_and_fit_screenshot(path: Path, max_w: int, max_h: int) -> Image.Image:
    """スクリーンショットを読み込み、指定サイズ内にアスペクト比を維持してリサイズ"""
    screenshot = Image.open(path)
    ratio = min(max_w / screenshot.width, max_h / screenshot.height, 1.3)
    new_w = int(screenshot.width * ratio)
    new_h = int(screenshot.height * ratio)
    return screenshot.resize((new_w, new_h), Image.LANCZOS)


def draw_shadow_screenshot(img: Image.Image, screenshot: Image.Image,
                           x: int, y: int):
    """影付きスクリーンショットを描画"""
    draw = ImageDraw.Draw(img)
    shadow_offset = 4
    draw.rectangle(
        (x + shadow_offset, y + shadow_offset,
         x + screenshot.width + shadow_offset,
         y + screenshot.height + shadow_offset),
        fill=SHADOW_COLOR
    )
    img.paste(screenshot, (x, y))
    draw.rectangle(
        (x - 1, y - 1,
         x + screenshot.width, y + screenshot.height),
        outline=BORDER_COLOR, width=1
    )


# ============================================================
# カード・リーダーイラスト（CreatePromotionVideo.py から移植）
# ============================================================
def draw_staff_card(img: Image.Image, cx: int, cy: int):
    """職員証を描画（白地＋青帯上部＋「職員証」テキスト）"""
    card = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), (0, 0, 0, 0))
    cd = ImageDraw.Draw(card)

    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1),
        radius=12, fill=(255, 255, 255), outline=BORDER_COLOR
    )

    blue_h = CARD_HEIGHT // 5
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, blue_h + 12),
        radius=12, fill=MAIN_BLUE
    )
    cd.rectangle((0, blue_h, CARD_WIDTH - 1, blue_h + 12), fill=(255, 255, 255))

    font = get_font(36)
    text = "職員証"
    bbox = cd.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (CARD_WIDTH - tw) // 2
    ty = blue_h + (CARD_HEIGHT - blue_h - th) // 2
    cd.text((tx, ty), text, fill=TEXT_DARK, font=font)

    paste_x = cx - card.width // 2
    paste_y = cy - card.height // 2
    img.paste(card, (paste_x, paste_y), card)


def draw_ic_card(img: Image.Image, cx: int, cy: int):
    """交通系ICカードを描画（銀色グラデーション＋「交通系」テキスト）"""
    card = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), (0, 0, 0, 0))
    cd = ImageDraw.Draw(card)

    mask = Image.new("L", (CARD_WIDTH, CARD_HEIGHT), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle((0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1), radius=12, fill=255)

    for y in range(CARD_HEIGHT):
        brightness = CARD_SILVER_BASE + int(20 * math.sin(y / CARD_HEIGHT * math.pi))
        color = (brightness, brightness, brightness + 5, 255)
        cd.line([(0, y), (CARD_WIDTH - 1, y)], fill=color)

    card.putalpha(mask)

    cd = ImageDraw.Draw(card)
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1),
        radius=12, fill=None, outline=(170, 170, 170)
    )

    font = get_font(32)
    text = "交通系"
    bbox = cd.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (CARD_WIDTH - tw) // 2
    ty = (CARD_HEIGHT - th) // 2
    cd.text((tx, ty), text, fill=(80, 80, 80), font=font)

    paste_x = cx - card.width // 2
    paste_y = cy - card.height // 2
    img.paste(card, (paste_x, paste_y), card)


def draw_card_reader(img: Image.Image, cx: int, cy: int):
    """カードリーダーを描画（黒い台形＋緑ランプ点灯）"""
    margin = 20
    total_w = READER_WIDTH + margin * 2
    total_h = READER_HEIGHT + READER_TOP_HEIGHT + margin * 2
    reader = Image.new("RGBA", (total_w, total_h), (0, 0, 0, 0))
    rd = ImageDraw.Draw(reader)

    ox, oy = margin, margin

    body_pts = [
        (ox + 10, oy + READER_TOP_HEIGHT),
        (ox + READER_WIDTH - 10, oy + READER_TOP_HEIGHT),
        (ox + READER_WIDTH, oy + READER_TOP_HEIGHT + READER_HEIGHT),
        (ox, oy + READER_TOP_HEIGHT + READER_HEIGHT),
    ]
    rd.polygon(body_pts, fill=READER_BLACK)

    top_pts = [
        (ox + 15, oy),
        (ox + READER_WIDTH - 15, oy),
        (ox + READER_WIDTH - 10, oy + READER_TOP_HEIGHT),
        (ox + 10, oy + READER_TOP_HEIGHT),
    ]
    rd.polygon(top_pts, fill=READER_TOP)

    lamp_cx = ox + READER_WIDTH // 2
    lamp_cy = oy + READER_TOP_HEIGHT + 15
    rd.ellipse(
        (lamp_cx - 6, lamp_cy - 6, lamp_cx + 6, lamp_cy + 6),
        fill=READER_LAMP_ON
    )

    # 発光エフェクト
    glow = Image.new("RGBA", reader.size, (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    for r in range(15, 3, -1):
        alpha = int(40 * (1 - r / 15))
        gd.ellipse(
            (lamp_cx - r, lamp_cy - r, lamp_cx + r, lamp_cy + r),
            fill=(76, 175, 80, alpha)
        )
    reader = Image.alpha_composite(reader, glow)

    paste_x = cx - reader.width // 2
    paste_y = cy - reader.height // 2
    img.paste(reader, (paste_x, paste_y), reader)


# ============================================================
# セクション描画
# ============================================================
def draw_header(img: Image.Image) -> int:
    """① ヘッダー（青帯 + アプリ名 + キャッチコピー）"""
    header_h = 220
    draw = ImageDraw.Draw(img)

    draw.rectangle((0, 0, PAGE_WIDTH, header_h), fill=MAIN_BLUE)
    draw.rectangle((0, header_h - 4, PAGE_WIDTH, header_h), fill=DARK_BLUE)

    subtitle_font = get_font(26)
    draw.text((MARGIN + 10, 35), "交通系ICカード管理システム",
              fill=TEXT_WHITE, font=subtitle_font)

    title_font = get_font(72)
    draw.text((MARGIN + 10, 75), "ピッすい",
              fill=TEXT_WHITE, font=title_font)

    tagline = "タッチ2回。帳簿は自動。"
    tagline_font = get_font(42)
    bbox = draw.textbbox((0, 0), tagline, font=tagline_font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    draw.text((PAGE_WIDTH - MARGIN - tw - 10, (header_h - th) // 2),
              tagline, fill=TEXT_WHITE, font=tagline_font)

    return header_h


def draw_operation_section(img: Image.Image, y_start: int) -> int:
    """② 問題提起 + カードタッチイラスト（全幅帯）"""
    draw = ImageDraw.Draw(img)
    y = y_start

    # 背景帯（薄いオレンジ）
    section_h = 560
    bg_color = (255, 248, 240)  # ごく薄いオレンジ
    draw.rectangle((0, y, PAGE_WIDTH, y + section_h), fill=bg_color)

    inner_y = y + 30

    # 問題提起テキスト
    problem_font = get_font(40)
    problem_text = "交通系ICカードの管理、まだ手書きですか？"
    bbox = draw.textbbox((0, 0), problem_text, font=problem_font)
    tw = bbox[2] - bbox[0]
    draw.text(((PAGE_WIDTH - tw) // 2, inner_y), problem_text,
              fill=TEXT_DARK, font=problem_font)

    # 問題提起とイラストの間にサブテキストを挟んで距離を作る
    inner_y += 65
    sub_font = get_font(28)
    sub_text = "ピッすいなら、タッチするだけで記録完了！"
    bbox_sub = draw.textbbox((0, 0), sub_text, font=sub_font)
    sw = bbox_sub[2] - bbox_sub[0]
    draw.text(((PAGE_WIDTH - sw) // 2, inner_y), sub_text,
              fill=TEXT_GRAY, font=sub_font)
    inner_y += 60

    # カードタッチイラスト: [職員証] ──→ [リーダー ピッ♪] ←── [ICカード ピッ♪]
    illust_cy = inner_y + CARD_HEIGHT // 2 + 10
    center_x = PAGE_WIDTH // 2

    # リーダーは中央
    draw_card_reader(img, center_x, illust_cy + 20)

    # 職員証は左側
    card_gap = 380  # リーダーとカードの間隔
    draw_staff_card(img, center_x - card_gap, illust_cy)

    # ICカードは右側
    draw_ic_card(img, center_x + card_gap, illust_cy)

    # 矢印: 職員証 → リーダー
    arrow_y = illust_cy + 5
    arrow_l_start = center_x - card_gap + CARD_WIDTH // 2 + 20
    arrow_l_end = center_x - READER_WIDTH // 2 - 40
    draw.line((arrow_l_start, arrow_y, arrow_l_end, arrow_y),
              fill=MAIN_BLUE, width=4)
    # 矢じり（右向き）
    draw.polygon([
        (arrow_l_end, arrow_y),
        (arrow_l_end - 16, arrow_y - 10),
        (arrow_l_end - 16, arrow_y + 10),
    ], fill=MAIN_BLUE)

    # 矢印: ICカード → リーダー
    arrow_r_start = center_x + card_gap - CARD_WIDTH // 2 - 20
    arrow_r_end = center_x + READER_WIDTH // 2 + 40
    draw.line((arrow_r_start, arrow_y, arrow_r_end, arrow_y),
              fill=MAIN_BLUE, width=4)
    # 矢じり（左向き）
    draw.polygon([
        (arrow_r_end, arrow_y),
        (arrow_r_end + 16, arrow_y - 10),
        (arrow_r_end + 16, arrow_y + 10),
    ], fill=MAIN_BLUE)

    # 「ピッ♪」テキスト（リーダーの上）
    pip_font = get_font(28)
    pip_text = "ピッ♪"
    bbox2 = draw.textbbox((0, 0), pip_text, font=pip_font)
    pw = bbox2[2] - bbox2[0]
    draw.text((center_x - pw // 2, illust_cy - READER_HEIGHT // 2 - 50),
              pip_text, fill=MAIN_BLUE, font=pip_font)

    # ラベル: 「① 職員証」「② 交通系ICカード」
    label_font = get_font(26)
    label1 = "① 職員証"
    bbox3 = draw.textbbox((0, 0), label1, font=label_font)
    lw1 = bbox3[2] - bbox3[0]
    draw.text((center_x - card_gap - lw1 // 2, illust_cy + CARD_HEIGHT // 2 + 15),
              label1, fill=TEXT_DARK, font=label_font)

    label2 = "② 交通系ICカード"
    bbox4 = draw.textbbox((0, 0), label2, font=label_font)
    lw2 = bbox4[2] - bbox4[0]
    draw.text((center_x + card_gap - lw2 // 2, illust_cy + CARD_HEIGHT // 2 + 15),
              label2, fill=TEXT_DARK, font=label_font)

    # 下部テキスト（強調）
    bottom_y = y + section_h - 70
    bottom_font = get_font(44)
    bottom_text = "貸出時も返却時も、この2タッチだけ。"
    bbox5 = draw.textbbox((0, 0), bottom_text, font=bottom_font)
    bw = bbox5[2] - bbox5[0]
    draw.text(((PAGE_WIDTH - bw) // 2, bottom_y), bottom_text,
              fill=MAIN_BLUE, font=bottom_font)

    return y + section_h


def draw_result_section(img: Image.Image, y_start: int, section_h: int,
                        title: str, description: str,
                        ss_path: Path) -> int:
    """③④ 結果セクション（全幅帯: タイトル + 説明 + スクリーンショット中央配置）"""
    draw = ImageDraw.Draw(img)
    x = MARGIN
    y = y_start

    # セクション上部の区切り線
    draw.rectangle((MARGIN, y, PAGE_WIDTH - MARGIN, y + 2), fill=BORDER_COLOR)
    y += 25

    # タイトル
    title_font = get_font(42)
    draw.text((x, y), title, fill=TEXT_DARK, font=title_font)
    y += 60

    # 説明テキスト
    desc_font = get_font(28)
    draw.text((x, y), description, fill=TEXT_GRAY, font=desc_font)
    y += 50

    # スクリーンショット（全幅帯で中央配置）
    if ss_path.exists():
        ss_available_h = (y_start + section_h) - y - 15
        ss_available_w = CONTENT_WIDTH - 40
        ss = load_and_fit_screenshot(ss_path, ss_available_w, ss_available_h)
        ss_x = (PAGE_WIDTH - ss.width) // 2
        draw_shadow_screenshot(img, ss, ss_x, y)

    return y_start + section_h


# ============================================================
# メイン処理
# ============================================================
def main():
    print("=" * 60)
    print("  交通系ICカード管理システム：ピッすい チラシ生成")
    print("=" * 60)

    PROMOTION_DIR.mkdir(parents=True, exist_ok=True)

    # スクリーンショットの存在確認
    required_screenshots = [SCREENSHOTS_DIR / "return.png"]
    report_cropped = PROMOTION_DIR / "report_excel_cropped.png"
    report_raw = SCREENSHOTS_DIR / "report_excel.png"
    report_path = report_cropped if report_cropped.exists() else report_raw
    required_screenshots.append(report_path)

    missing = [p for p in required_screenshots if not p.exists()]
    if missing:
        print("警告: 以下のスクリーンショットが見つかりません:")
        for p in missing:
            print(f"  {p}")
        print("（該当部分は空欄になります）")

    # ページ作成
    print("\nページを生成中...")
    img = Image.new("RGB", (PAGE_WIDTH, PAGE_HEIGHT), WHITE)

    # セクション高さの配分（A4縦: 3508px）
    # ① ヘッダー: 220px
    # ② 操作説明: 560px
    # ③ 結果1(履歴): 残りの60%（メインの訴求）
    # ④ 結果2(帳票): 残りの40%（サブの訴求）
    header_h = 220
    operation_h = 560
    remaining = PAGE_HEIGHT - header_h - operation_h
    result1_h = remaining * 63 // 100  # 履歴セクションを大きく
    result2_h = remaining - result1_h

    print("[1/4] ヘッダー...")
    y = draw_header(img)

    print("[2/4] 操作説明セクション...")
    y = draw_operation_section(img, y)

    print("[3/4] 利用履歴セクション...")
    return_ss = SCREENSHOTS_DIR / "return.png"
    y = draw_result_section(
        img, y, result1_h,
        title="タッチするだけで利用履歴を自動記録",
        description="残高・乗車駅・降車駅を自動で読み取り。手書きの手間がなくなります。",
        ss_path=return_ss,
    )

    print("[4/4] 物品出納簿セクション...")
    draw_result_section(
        img, y, result2_h,
        title="物品出納簿をExcelで自動出力",
        description="利用履歴から帳票を自動生成。庶務担当者の負担を軽減します。",
        ss_path=report_path,
    )

    # PDF保存
    output_path = PROMOTION_DIR / "チラシ.pdf"
    img.save(str(output_path), "PDF", resolution=300.0)

    # PNG プレビューも保存（確認用）
    preview_path = PROMOTION_DIR / "チラシ_preview.png"
    preview = img.resize((PAGE_WIDTH // 3, PAGE_HEIGHT // 3), Image.LANCZOS)
    preview.save(str(preview_path))

    print(f"\nチラシを生成しました: {output_path}")
    print(f"  サイズ: A4縦 ({PAGE_WIDTH}x{PAGE_HEIGHT}px, 300 DPI)")
    print(f"  プレビュー: {preview_path}")


if __name__ == "__main__":
    main()
