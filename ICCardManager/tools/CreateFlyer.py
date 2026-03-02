#!/usr/bin/env python3
"""
プロモーション用チラシ（A4横 PDF）生成スクリプト

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
# A4横 300 DPI
PAGE_WIDTH, PAGE_HEIGHT = 3508, 2480

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
ACCENT_LIGHT_BLUE = (3, 169, 244)    # #03A9F4 返却カラー

# フォント
FONT_PATH = "/mnt/c/Windows/Fonts/YuGothB.ttc"

# パス
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent
SCREENSHOTS_DIR = PROJECT_DIR / "docs" / "screenshots"
PROMOTION_DIR = PROJECT_DIR / "docs" / "promotion"

# レイアウト定数
HEADER_HEIGHT = 260
MARGIN = 90
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
    ratio = min(max_w / screenshot.width, max_h / screenshot.height)
    new_w = int(screenshot.width * ratio)
    new_h = int(screenshot.height * ratio)
    return screenshot.resize((new_w, new_h), Image.LANCZOS)


def draw_rounded_rect(draw: ImageDraw.Draw, xy: tuple, radius: int,
                      fill=None, outline=None, width: int = 1):
    """角丸四角形を描画"""
    draw.rounded_rectangle(xy, radius=radius, fill=fill,
                           outline=outline, width=width)


def draw_section_title(draw: ImageDraw.Draw, text: str, x: int, y: int,
                       font_size: int = 40, color=TEXT_DARK):
    """セクションタイトル（左揃え、下に青いアクセント線）を描画し、次のY座標を返す"""
    font = get_font(font_size)
    draw.text((x, y), text, fill=color, font=font)
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    line_y = y + th + 6
    draw.rectangle((x, line_y, x + tw, line_y + 4), fill=MAIN_BLUE)
    return y + th + 20


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


def draw_icon_pencil(draw: ImageDraw.Draw, cx: int, cy: int, size: int = 20):
    """鉛筆アイコン"""
    draw.line((cx - size, cy + size, cx + size, cy - size),
              fill=TEXT_WHITE, width=4)
    draw.polygon([
        (cx - size, cy + size),
        (cx - size + 8, cy + size - 4),
        (cx - size + 4, cy + size - 8),
    ], fill=TEXT_WHITE)
    draw.rectangle(
        (cx + size - 6, cy - size - 2, cx + size + 2, cy - size + 6),
        fill=TEXT_WHITE
    )


def draw_icon_clock(draw: ImageDraw.Draw, cx: int, cy: int, size: int = 20):
    """時計アイコン"""
    draw.ellipse(
        (cx - size, cy - size, cx + size, cy + size),
        outline=TEXT_WHITE, width=3
    )
    draw.line((cx, cy, cx, cy - size + 6), fill=TEXT_WHITE, width=3)
    draw.line((cx, cy, cx + size - 8, cy), fill=TEXT_WHITE, width=2)


# ============================================================
# セクション描画
# ============================================================
def draw_header(img: Image.Image):
    """ヘッダー（青帯 + アプリ名 + キャッチコピー）"""
    draw = ImageDraw.Draw(img)

    draw.rectangle((0, 0, PAGE_WIDTH, HEADER_HEIGHT), fill=MAIN_BLUE)
    draw.rectangle(
        (0, HEADER_HEIGHT - 5, PAGE_WIDTH, HEADER_HEIGHT),
        fill=DARK_BLUE
    )

    subtitle_font = get_font(28)
    draw.text((MARGIN + 10, 45), "交通系ICカード管理システム",
              fill=TEXT_WHITE, font=subtitle_font)

    title_font = get_font(76)
    draw.text((MARGIN + 10, 90), "ピッすい",
              fill=TEXT_WHITE, font=title_font)

    tagline = "タッチ2回。帳簿は自動。"
    tagline_font = get_font(44)
    bbox = draw.textbbox((0, 0), tagline, font=tagline_font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    draw.text((PAGE_WIDTH - MARGIN - tw - 10, (HEADER_HEIGHT - th) // 2),
              tagline, fill=TEXT_WHITE, font=tagline_font)


def draw_problem_section(img: Image.Image, y_start: int) -> int:
    """課題提起セクション（全幅、2カード横並び）"""
    draw = ImageDraw.Draw(img)
    x = MARGIN
    y = y_start

    y = draw_section_title(draw, "こんなお悩みありませんか？", x, y)
    y += 8

    card_w = (CONTENT_WIDTH - 50) // 2
    card_h = 120
    problems = [
        ("貸出・返却の記録が\n手書きで面倒...", draw_icon_pencil),
        ("物品出納簿の作成に\n時間がかかる...", draw_icon_clock),
    ]

    for i, (desc, icon_func) in enumerate(problems):
        cx = x + i * (card_w + 50)
        card_bg = (255, 243, 224)  # #FFF3E0
        draw_rounded_rect(draw, (cx, y, cx + card_w, y + card_h),
                          radius=12, fill=card_bg, outline=(255, 204, 128))

        icon_r = 28
        icon_cx = cx + 50
        icon_cy = y + card_h // 2
        draw.ellipse(
            (icon_cx - icon_r, icon_cy - icon_r,
             icon_cx + icon_r, icon_cy + icon_r),
            fill=ACCENT_ORANGE
        )
        icon_func(draw, icon_cx, icon_cy, size=16)

        text_font = get_font(24)
        lines = desc.split("\n")
        text_y = y + 22
        for line in lines:
            draw.text((icon_cx + icon_r + 22, text_y), line,
                      fill=TEXT_DARK, font=text_font)
            text_y += 36

    return y + card_h


def draw_solution_section(img: Image.Image, y_start: int,
                          max_ss_h: int) -> int:
    """使い方セクション（全幅、貸出時も返却時もの2枚並び）"""
    draw = ImageDraw.Draw(img)
    x = MARGIN
    y = y_start

    y = draw_section_title(draw, "ピッすいなら、タッチ2回で完了！", x, y)
    y += 2

    desc_font = get_font(24)
    draw.text((x, y),
              "職員証 → 交通系ICカードの順にタッチするだけ。貸出も返却も同じ操作です。",
              fill=TEXT_GRAY, font=desc_font)
    y += 44

    screenshots = [
        (SCREENSHOTS_DIR / "lend.png", "貸出時も"),
        (SCREENSHOTS_DIR / "return.png", "返却時も"),
    ]

    gap_between = 120
    ss_w = (CONTENT_WIDTH - gap_between) // 2
    ss_h = max_ss_h

    actual_ss_h = 0
    for i, (ss_path, label) in enumerate(screenshots):
        step_x = x + i * (ss_w + gap_between)

        # 「貸出時も」「返却時も」ラベル
        label_font = get_font(34)
        bbox = draw.textbbox((0, 0), label, font=label_font)
        lw = bbox[2] - bbox[0]
        draw.text((step_x + (ss_w - lw) // 2, y), label,
                  fill=MAIN_BLUE, font=label_font)

        # スクリーンショット
        ss_top = y + 50
        if ss_path.exists():
            ss = load_and_fit_screenshot(ss_path, ss_w, ss_h)
            actual_ss_h = ss.height
            draw_shadow_screenshot(img, ss, step_x, ss_top)

    # 「同じ操作！」縦書きテキスト（2つのSSの間）
    center_x = x + ss_w + gap_between // 2
    center_y = y + 50 + actual_ss_h // 2
    emphasis_font = get_font(28)
    chars = ["同", "じ", "操", "作", "！"]
    char_h = 36
    ey = center_y - len(chars) * char_h // 2
    for ch in chars:
        bbox3 = draw.textbbox((0, 0), ch, font=emphasis_font)
        cw = bbox3[2] - bbox3[0]
        draw.text((center_x - cw // 2, ey), ch,
                  fill=MAIN_BLUE, font=emphasis_font)
        ey += char_h

    return y + 50 + actual_ss_h + 10


def draw_features_section(img: Image.Image, y_start: int) -> int:
    """特長セクション（全幅、3カラム: 特長1 + 特長2 + 帳票SS）"""
    draw = ImageDraw.Draw(img)
    x = MARGIN
    y = y_start

    y = draw_section_title(draw, "主な特長", x, y)
    y += 8

    # 3カラム: [特長カード1] [特長カード2] [帳票出力イメージ]
    col_gap = 30
    col_w = (CONTENT_WIDTH - col_gap * 2) // 3
    card_h = 160

    # 特長カード1: 利用履歴の自動記録
    _draw_feature_card_full(
        img, x, y, col_w, card_h,
        "履", "利用履歴の自動記録",
        "残高・乗車駅・降車駅を\n自動で読み取り記録。\n手書きの手間がなくなります。",
        MAIN_BLUE
    )

    # 特長カード2: 物品出納簿を自動作成
    _draw_feature_card_full(
        img, x + col_w + col_gap, y, col_w, card_h,
        "表", "物品出納簿を自動作成",
        "利用履歴から帳票を\nExcelで自動出力。\n庶務担当者の負担を軽減。",
        ACCENT_LIGHT_BLUE
    )

    # 帳票出力イメージ（3列目）
    ss_x = x + (col_w + col_gap) * 2
    desc_font = get_font(18)
    draw.text((ss_x, y), "▼ 出力イメージ", fill=TEXT_GRAY, font=desc_font)

    report_cropped = PROMOTION_DIR / "report_excel_cropped.png"
    report_raw = SCREENSHOTS_DIR / "report_excel.png"
    ss_path = report_cropped if report_cropped.exists() else report_raw

    if ss_path.exists():
        ss_top = y + 28
        ss = load_and_fit_screenshot(ss_path, col_w, card_h - 28)
        draw_shadow_screenshot(img, ss, ss_x, ss_top)

    return y + card_h


def _draw_feature_card_full(img: Image.Image, x: int, y: int,
                            w: int, h: int, icon_char: str,
                            title: str, description: str,
                            icon_color=MAIN_BLUE):
    """特長カード1枚を描画（アイコン + タイトル + 説明文）"""
    draw = ImageDraw.Draw(img)
    draw_rounded_rect(draw, (x, y, x + w, y + h), radius=12,
                      fill=WHITE, outline=BORDER_COLOR)

    icon_r = 32
    icon_cx = x + 50
    icon_cy = y + h // 2
    draw.ellipse(
        (icon_cx - icon_r, icon_cy - icon_r,
         icon_cx + icon_r, icon_cy + icon_r),
        fill=icon_color
    )
    icon_font = get_font(30)
    bbox = draw.textbbox((0, 0), icon_char, font=icon_font)
    iw, ih = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((icon_cx - iw // 2, icon_cy - ih // 2), icon_char,
              fill=TEXT_WHITE, font=icon_font)

    text_x = icon_cx + icon_r + 24
    title_font = get_font(28)
    draw.text((text_x, y + 20), title, fill=TEXT_DARK, font=title_font)

    desc_font = get_font(20)
    desc_y = y + 56
    for line in description.split("\n"):
        draw.text((text_x, desc_y), line, fill=TEXT_GRAY, font=desc_font)
        desc_y += 28


# ============================================================
# メイン処理
# ============================================================
def main():
    print("=" * 60)
    print("  交通系ICカード管理システム：ピッすい チラシ生成")
    print("=" * 60)

    PROMOTION_DIR.mkdir(parents=True, exist_ok=True)

    # スクリーンショットの存在確認
    required_screenshots = [
        SCREENSHOTS_DIR / "lend.png",
        SCREENSHOTS_DIR / "return.png",
    ]
    missing = [p for p in required_screenshots if not p.exists()]
    if missing:
        print("警告: 以下のスクリーンショットが見つかりません:")
        for p in missing:
            print(f"  {p}")
        print("（該当部分は空欄になります）")

    # ページ作成
    print("\nページを生成中...")
    img = Image.new("RGB", (PAGE_WIDTH, PAGE_HEIGHT), WHITE)

    print("[1/4] ヘッダー...")
    draw_header(img)

    # 各セクションの高さから、スクリーンショットに使える高さを逆算
    # ヘッダー=260, 問題≈200, 問題後余白, 使い方固定部≈120, 特長≈240, 余白
    fixed_height = 260 + 200 + 120 + 240  # ≈820
    gaps = 40 * 3 + 30  # セクション間余白3つ + 上部余白
    max_ss_h = PAGE_HEIGHT - fixed_height - gaps - 20  # 残りをSSに割当

    y = HEADER_HEIGHT + 30

    print("[2/4] 課題提起セクション...")
    y = draw_problem_section(img, y)
    y += 40

    print("[3/4] 使い方セクション...")
    y = draw_solution_section(img, y, max_ss_h)
    y += 40

    print("[4/4] 特長セクション...")
    draw_features_section(img, y)

    # PDF保存
    output_path = PROMOTION_DIR / "チラシ.pdf"
    img.save(str(output_path), "PDF", resolution=300.0)

    print(f"\nチラシを生成しました: {output_path}")
    print(f"  サイズ: A4横 ({PAGE_WIDTH}x{PAGE_HEIGHT}px, 300 DPI)")


if __name__ == "__main__":
    main()
