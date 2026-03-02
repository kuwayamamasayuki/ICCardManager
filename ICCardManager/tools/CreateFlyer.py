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
LIGHT_BLUE_BG = (237, 247, 255)      # 薄い青背景
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
HEADER_HEIGHT = 280
FOOTER_HEIGHT = 120
MARGIN = 100
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
    line_y = y + th + 8
    draw.rectangle((x, line_y, x + tw, line_y + 4), fill=MAIN_BLUE)
    return y + th + 24


def draw_shadow_screenshot(img: Image.Image, screenshot: Image.Image,
                           x: int, y: int):
    """影付きスクリーンショットを描画"""
    draw = ImageDraw.Draw(img)
    shadow_offset = 5
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
    """鉛筆アイコン（手書きの象徴）"""
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
    """時計アイコン（時間がかかるの象徴）"""
    draw.ellipse(
        (cx - size, cy - size, cx + size, cy + size),
        outline=TEXT_WHITE, width=3
    )
    draw.line((cx, cy, cx, cy - size + 6), fill=TEXT_WHITE, width=3)
    draw.line((cx, cy, cx + size - 8, cy), fill=TEXT_WHITE, width=2)


def draw_feature_card(img: Image.Image, x: int, y: int, w: int, h: int,
                      icon_char: str, title: str, description: str,
                      icon_color=MAIN_BLUE):
    """特長カード1枚を描画（アイコン + タイトル + 説明文）"""
    draw = ImageDraw.Draw(img)
    draw_rounded_rect(draw, (x, y, x + w, y + h), radius=12,
                      fill=WHITE, outline=BORDER_COLOR)

    icon_r = 34
    icon_cx = x + 55
    icon_cy = y + h // 2
    draw.ellipse(
        (icon_cx - icon_r, icon_cy - icon_r,
         icon_cx + icon_r, icon_cy + icon_r),
        fill=icon_color
    )
    icon_font = get_font(32)
    bbox = draw.textbbox((0, 0), icon_char, font=icon_font)
    iw, ih = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((icon_cx - iw // 2, icon_cy - ih // 2), icon_char,
              fill=TEXT_WHITE, font=icon_font)

    text_x = icon_cx + icon_r + 28
    title_font = get_font(30)
    draw.text((text_x, y + 24), title, fill=TEXT_DARK, font=title_font)

    desc_font = get_font(22)
    desc_y = y + 64
    for line in description.split("\n"):
        draw.text((text_x, desc_y), line, fill=TEXT_GRAY, font=desc_font)
        desc_y += 30


# ============================================================
# セクション描画
# ============================================================
def draw_header(img: Image.Image):
    """ヘッダー（青帯 + アプリ名 + キャッチコピー）"""
    draw = ImageDraw.Draw(img)

    draw.rectangle((0, 0, PAGE_WIDTH, HEADER_HEIGHT), fill=MAIN_BLUE)
    draw.rectangle(
        (0, HEADER_HEIGHT - 6, PAGE_WIDTH, HEADER_HEIGHT),
        fill=DARK_BLUE
    )

    subtitle_font = get_font(30)
    draw.text((MARGIN + 10, 50), "交通系ICカード管理システム",
              fill=TEXT_WHITE, font=subtitle_font)

    title_font = get_font(80)
    draw.text((MARGIN + 10, 100), "ピッすい",
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
    y += 12

    card_w = (CONTENT_WIDTH - 40) // 2
    card_h = 140
    problems = [
        ("貸出・返却の記録が\n手書きで面倒...", draw_icon_pencil),
        ("物品出納簿の作成に\n時間がかかる...", draw_icon_clock),
    ]

    for i, (desc, icon_func) in enumerate(problems):
        cx = x + i * (card_w + 40)
        card_bg = (255, 243, 224)  # #FFF3E0
        draw_rounded_rect(draw, (cx, y, cx + card_w, y + card_h),
                          radius=12, fill=card_bg, outline=(255, 204, 128))

        icon_r = 30
        icon_cx = cx + 55
        icon_cy = y + card_h // 2
        draw.ellipse(
            (icon_cx - icon_r, icon_cy - icon_r,
             icon_cx + icon_r, icon_cy + icon_r),
            fill=ACCENT_ORANGE
        )
        icon_func(draw, icon_cx, icon_cy, size=18)

        text_font = get_font(26)
        lines = desc.split("\n")
        text_y = y + 30
        for line in lines:
            draw.text((icon_cx + icon_r + 24, text_y), line,
                      fill=TEXT_DARK, font=text_font)
            text_y += 38

    return y + card_h + 16


def draw_solution_section(img: Image.Image, y_start: int) -> int:
    """使い方セクション（全幅、貸出時も返却時もの2枚並び）"""
    draw = ImageDraw.Draw(img)
    x = MARGIN
    y = y_start

    y = draw_section_title(draw, "ピッすいなら、タッチ2回で完了！", x, y)
    y += 4

    desc_font = get_font(24)
    draw.text((x, y),
              "職員証 → 交通系ICカードの順にタッチするだけ。貸出も返却も同じ操作です。",
              fill=TEXT_GRAY, font=desc_font)
    y += 48

    screenshots = [
        (SCREENSHOTS_DIR / "lend.png", "貸出時も"),
        (SCREENSHOTS_DIR / "return.png", "返却時も"),
    ]

    # 2つのスクリーンショットの間に「同じ操作！」テキストを配置
    gap_between = 140  # SS間のスペース
    ss_w = (CONTENT_WIDTH - gap_between) // 2
    ss_h = min(int(ss_w * 0.7), 700)  # 高さ上限を設定して下部セクション確保

    actual_ss_h = 0  # 実際に描画したSSの高さを記録
    for i, (ss_path, label) in enumerate(screenshots):
        step_x = x + i * (ss_w + gap_between)

        # 「貸出時も」「返却時も」ラベル
        label_font = get_font(36)
        bbox = draw.textbbox((0, 0), label, font=label_font)
        lw = bbox[2] - bbox[0]
        draw.text((step_x + (ss_w - lw) // 2, y), label,
                  fill=MAIN_BLUE, font=label_font)

        # スクリーンショット
        ss_top = y + 54
        if ss_path.exists():
            ss = load_and_fit_screenshot(ss_path, ss_w, ss_h)
            actual_ss_h = ss.height
            draw_shadow_screenshot(img, ss, step_x, ss_top)

        # 操作説明ラベル
        step_label = "職員証 → 交通系ICカード"
        step_font = get_font(20)
        bbox2 = draw.textbbox((0, 0), step_label, font=step_font)
        slw = bbox2[2] - bbox2[0]
        draw.text((step_x + (ss_w - slw) // 2, ss_top + actual_ss_h + 12),
                  step_label, fill=TEXT_GRAY, font=step_font)

    # 「同じ操作！」テキスト（2つのスクリーンショットの間に縦書き風に配置）
    center_x = x + ss_w + gap_between // 2
    center_y = y + 54 + actual_ss_h // 2
    emphasis_font = get_font(30)
    for j, ch in enumerate(["同", "じ", "操", "作", "！"]):
        bbox3 = draw.textbbox((0, 0), ch, font=emphasis_font)
        cw = bbox3[2] - bbox3[0]
        ch_y = center_y - 80 + j * 38
        draw.text((center_x - cw // 2, ch_y), ch,
                  fill=MAIN_BLUE, font=emphasis_font)

    return y + 54 + actual_ss_h + 44


def draw_bottom_section(img: Image.Image, y_start: int) -> int:
    """下部セクション（全幅: 左に特長カード2枚、右に物品出納簿SS）"""
    draw = ImageDraw.Draw(img)
    x = MARGIN
    y = y_start

    # 左側：特長カード
    y = draw_section_title(draw, "主な特長", x, y)
    features_y = y + 12

    left_w = int(CONTENT_WIDTH * 0.48)
    card_w = left_w
    card_h = 150
    gap = 20

    features = [
        ("履", "利用履歴の自動記録",
         "残高・乗車駅・降車駅を自動で読み取り記録。\n手書きの手間がなくなります。",
         MAIN_BLUE),
        ("表", "物品出納簿を自動作成",
         "利用履歴から帳票をExcelで自動出力。\n庶務担当者の負担を軽減します。",
         ACCENT_LIGHT_BLUE),
    ]

    for i, (icon, title, desc, color) in enumerate(features):
        fy = features_y + i * (card_h + gap)
        draw_feature_card(img, x, fy, card_w, card_h,
                          icon, title, desc, icon_color=color)

    # 右側：物品出納簿スクリーンショット
    right_x = x + left_w + 60
    right_w = CONTENT_WIDTH - left_w - 60

    desc_font = get_font(20)
    draw.text((right_x, features_y),
              "▼ 物品出納簿の出力イメージ",
              fill=TEXT_GRAY, font=desc_font)

    report_cropped = PROMOTION_DIR / "report_excel_cropped.png"
    report_raw = SCREENSHOTS_DIR / "report_excel.png"
    ss_path = report_cropped if report_cropped.exists() else report_raw

    if ss_path.exists():
        ss_top = features_y + 36
        available_h = 2 * card_h + gap - 36
        ss = load_and_fit_screenshot(ss_path, right_w, available_h)
        draw_shadow_screenshot(img, ss, right_x, ss_top)

    return features_y + 2 * card_h + gap + 20


def draw_footer(img: Image.Image):
    """フッター（青帯 + アプリ名）"""
    draw = ImageDraw.Draw(img)
    footer_y = PAGE_HEIGHT - FOOTER_HEIGHT

    draw.rectangle((0, footer_y, PAGE_WIDTH, PAGE_HEIGHT), fill=MAIN_BLUE)
    draw.rectangle((0, footer_y, PAGE_WIDTH, footer_y + 4), fill=DARK_BLUE)

    app_name = "交通系ICカード管理システム：ピッすい"
    app_font = get_font(28)
    bbox = draw.textbbox((0, 0), app_name, font=app_font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    draw.text(((PAGE_WIDTH - tw) // 2, footer_y + (FOOTER_HEIGHT - th) // 2),
              app_name, fill=TEXT_WHITE, font=app_font)


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

    # コンテンツ領域
    content_top = HEADER_HEIGHT + 40
    content_bottom = PAGE_HEIGHT - FOOTER_HEIGHT

    # 各セクションの高さを仮計算（セクション間の余白を均等に分配）
    # おおよその高さ: 問題=230, 使い方=870, 下部=430 → 合計≈1530
    available = content_bottom - content_top
    total_content = 1530  # 概算
    section_gap = (available - total_content) // 4  # 3セクション間 + 上下
    section_gap = max(20, min(section_gap, 80))  # 20〜80の範囲に制限

    print("[1/5] ヘッダー...")
    draw_header(img)

    # 背景色帯（使い方セクション用の薄い青帯）
    # 正確な位置はセクション描画後に決まるので、先に描画
    y = content_top + section_gap

    print("[2/5] 課題提起セクション...")
    y = draw_problem_section(img, y)
    y += section_gap

    # 使い方セクションの背景帯
    solution_bg_y = y - 10
    print("[3/5] 使い方セクション...")
    y = draw_solution_section(img, y)
    y += section_gap

    # 使い方セクション背景を後から描画（コンテンツの裏に薄い青帯）
    draw = ImageDraw.Draw(img)

    print("[4/5] 特長・帳票セクション...")
    draw_bottom_section(img, y)

    print("[5/5] フッター...")
    draw_footer(img)

    # PDF保存
    output_path = PROMOTION_DIR / "チラシ.pdf"
    img.save(str(output_path), "PDF", resolution=300.0)

    print(f"\nチラシを生成しました: {output_path}")
    print(f"  サイズ: A4横 ({PAGE_WIDTH}x{PAGE_HEIGHT}px, 300 DPI)")


if __name__ == "__main__":
    main()
