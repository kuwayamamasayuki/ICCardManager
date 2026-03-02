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

import os
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
BG_COLOR = (245, 245, 245)           # #F5F5F5 ライトグレー背景
MAIN_BLUE = (33, 150, 243)           # #2196F3 アプリのテーマカラー
DARK_BLUE = (25, 118, 191)           # #1976BF ヘッダー下部グラデーション用
LIGHT_BLUE_BG = (227, 242, 253)      # #E3F2FD 薄い青背景
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
FOOTER_HEIGHT = 260
MARGIN = 80
COLUMN_GAP = 60
CONTENT_TOP = HEADER_HEIGHT + 30
CONTENT_HEIGHT = PAGE_HEIGHT - HEADER_HEIGHT - FOOTER_HEIGHT - 60
LEFT_COL_X = MARGIN
LEFT_COL_W = (PAGE_WIDTH - MARGIN * 2 - COLUMN_GAP) // 2
RIGHT_COL_X = LEFT_COL_X + LEFT_COL_W + COLUMN_GAP
RIGHT_COL_W = LEFT_COL_W


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


def draw_text_centered(draw: ImageDraw.Draw, text: str, cx: int, cy: int,
                       font_size: int, color=TEXT_DARK):
    """中央揃えテキスト描画（cx, cy が中心座標）"""
    font = get_font(font_size)
    bbox = draw.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((cx - tw // 2, cy - th // 2), text, fill=color, font=font)


def draw_section_title(draw: ImageDraw.Draw, text: str, x: int, y: int,
                       font_size: int = 38, color=TEXT_DARK):
    """セクションタイトル（左揃え、下に青いアクセント線）を描画"""
    font = get_font(font_size)
    draw.text((x, y), text, fill=color, font=font)
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    # 青いアクセント線
    line_y = y + th + 8
    draw.rectangle((x, line_y, x + tw, line_y + 4), fill=MAIN_BLUE)
    return y + th + 20


def draw_step_badge(img: Image.Image, number: int, cx: int, cy: int,
                    radius: int = 30):
    """ステップ番号バッジ（青丸+白数字）を描画"""
    draw = ImageDraw.Draw(img)
    draw.ellipse(
        (cx - radius, cy - radius, cx + radius, cy + radius),
        fill=MAIN_BLUE
    )
    font = get_font(28)
    text = str(number)
    bbox = draw.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((cx - tw // 2, cy - th // 2), text,
              fill=TEXT_WHITE, font=font)


def draw_arrow(draw: ImageDraw.Draw, x: int, cy: int, size: int = 28):
    """右向き矢印を描画"""
    # 矢印の軸
    draw.line((x - size, cy, x + size - 6, cy), fill=MAIN_BLUE, width=5)
    # 矢印の先端（三角形）
    draw.polygon([
        (x + size, cy),
        (x + size - 16, cy - 12),
        (x + size - 16, cy + 12),
    ], fill=MAIN_BLUE)


def draw_icon_pencil(draw: ImageDraw.Draw, cx: int, cy: int, size: int = 20):
    """鉛筆アイコン（手書きの象徴）"""
    # 鉛筆の軸（斜め線）
    draw.line((cx - size, cy + size, cx + size, cy - size),
              fill=TEXT_WHITE, width=4)
    # 鉛筆の先端
    draw.polygon([
        (cx - size, cy + size),
        (cx - size + 8, cy + size - 4),
        (cx - size + 4, cy + size - 8),
    ], fill=TEXT_WHITE)
    # 鉛筆の上端（消しゴム部分）
    draw.rectangle(
        (cx + size - 6, cy - size - 2, cx + size + 2, cy - size + 6),
        fill=TEXT_WHITE
    )


def draw_icon_clock(draw: ImageDraw.Draw, cx: int, cy: int, size: int = 20):
    """時計アイコン（時間がかかるの象徴）"""
    # 円（時計の外枠）
    draw.ellipse(
        (cx - size, cy - size, cx + size, cy + size),
        outline=TEXT_WHITE, width=3
    )
    # 時計の針
    draw.line((cx, cy, cx, cy - size + 6), fill=TEXT_WHITE, width=3)
    draw.line((cx, cy, cx + size - 8, cy), fill=TEXT_WHITE, width=2)


def draw_icon_question(draw: ImageDraw.Draw, cx: int, cy: int, size: int = 20):
    """?アイコン（分からないの象徴）"""
    font = get_font(size * 2)
    text = "?"
    bbox = draw.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((cx - tw // 2, cy - th // 2), text, fill=TEXT_WHITE, font=font)


def draw_feature_card(img: Image.Image, x: int, y: int, w: int, h: int,
                      icon_char: str, title: str, description: str,
                      icon_color=MAIN_BLUE):
    """特長カード1枚を描画（アイコン + タイトル + 説明文）"""
    draw = ImageDraw.Draw(img)

    # カード背景（白い角丸矩形）
    draw_rounded_rect(draw, (x, y, x + w, y + h), radius=12,
                      fill=WHITE, outline=BORDER_COLOR)

    # アイコン円
    icon_r = 32
    icon_cx = x + 50
    icon_cy = y + h // 2
    draw.ellipse(
        (icon_cx - icon_r, icon_cy - icon_r,
         icon_cx + icon_r, icon_cy + icon_r),
        fill=icon_color
    )
    # アイコン文字
    icon_font = get_font(30)
    bbox = draw.textbbox((0, 0), icon_char, font=icon_font)
    iw, ih = bbox[2] - bbox[0], bbox[3] - bbox[1]
    draw.text((icon_cx - iw // 2, icon_cy - ih // 2), icon_char,
              fill=TEXT_WHITE, font=icon_font)

    # タイトル
    text_x = icon_cx + icon_r + 24
    title_font = get_font(28)
    draw.text((text_x, y + 22), title, fill=TEXT_DARK, font=title_font)

    # 説明文
    desc_font = get_font(20)
    desc_lines = description.split("\n")
    desc_y = y + 58
    for line in desc_lines:
        draw.text((text_x, desc_y), line, fill=TEXT_GRAY, font=desc_font)
        desc_y += 28


def draw_shadow_screenshot(img: Image.Image, screenshot: Image.Image,
                           x: int, y: int):
    """影付きスクリーンショットを描画"""
    draw = ImageDraw.Draw(img)
    # 影
    shadow_offset = 5
    draw.rectangle(
        (x + shadow_offset, y + shadow_offset,
         x + screenshot.width + shadow_offset,
         y + screenshot.height + shadow_offset),
        fill=SHADOW_COLOR
    )
    # スクリーンショット
    img.paste(screenshot, (x, y))
    # 枠線
    draw.rectangle(
        (x - 1, y - 1,
         x + screenshot.width, y + screenshot.height),
        outline=BORDER_COLOR, width=1
    )


# ============================================================
# セクション描画
# ============================================================
def draw_header(img: Image.Image):
    """ヘッダー（青帯 + アプリ名 + キャッチコピー）"""
    draw = ImageDraw.Draw(img)

    # 青い背景帯
    draw.rectangle((0, 0, PAGE_WIDTH, HEADER_HEIGHT), fill=MAIN_BLUE)
    # 下部にやや暗い帯（立体感）
    draw.rectangle(
        (0, HEADER_HEIGHT - 6, PAGE_WIDTH, HEADER_HEIGHT),
        fill=DARK_BLUE
    )

    # 左側：アプリ名
    subtitle_font = get_font(30)
    draw.text((MARGIN + 10, 50), "交通系ICカード管理システム",
              fill=(*TEXT_WHITE[:3],), font=subtitle_font)

    title_font = get_font(80)
    draw.text((MARGIN + 10, 100), "ピッすい",
              fill=TEXT_WHITE, font=title_font)

    # 右側：キャッチコピー
    tagline = "タッチ2回。帳簿は自動。"
    tagline_font = get_font(44)
    bbox = draw.textbbox((0, 0), tagline, font=tagline_font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    draw.text((PAGE_WIDTH - MARGIN - tw - 10, (HEADER_HEIGHT - th) // 2),
              tagline, fill=TEXT_WHITE, font=tagline_font)


def draw_problem_section(img: Image.Image, y_start: int) -> int:
    """課題提起セクション（こんなお悩みありませんか？）"""
    draw = ImageDraw.Draw(img)
    x = LEFT_COL_X
    y = y_start

    # セクションタイトル
    y = draw_section_title(draw, "こんなお悩みありませんか？", x, y)
    y += 16

    # 3つの課題カード
    card_w = (LEFT_COL_W - 30) // 3
    card_h = 150
    problems = [
        ("手書きで面倒...", "貸出・返却の記録が\n手書きで面倒",
         draw_icon_pencil),
        ("帳票に時間...", "物品出納簿の作成に\n時間がかかる",
         draw_icon_clock),
        ("所在が不明...", "誰がどのカードを\n持っているか分からない",
         draw_icon_question),
    ]

    for i, (_, desc, icon_func) in enumerate(problems):
        cx = x + i * (card_w + 15)

        # カード背景（薄いオレンジ → 課題カラー）
        card_bg = (255, 243, 224)  # #FFF3E0
        draw_rounded_rect(draw, (cx, y, cx + card_w, y + card_h),
                          radius=10, fill=card_bg, outline=(255, 204, 128))

        # アイコン円
        icon_r = 24
        icon_cx = cx + 40
        icon_cy = y + card_h // 2
        draw.ellipse(
            (icon_cx - icon_r, icon_cy - icon_r,
             icon_cx + icon_r, icon_cy + icon_r),
            fill=ACCENT_ORANGE
        )
        icon_func(draw, icon_cx, icon_cy, size=14)

        # テキスト
        text_font = get_font(20)
        lines = desc.split("\n")
        text_y = y + 35
        for line in lines:
            draw.text((icon_cx + icon_r + 16, text_y), line,
                      fill=TEXT_DARK, font=text_font)
            text_y += 30

    return y + card_h + 20


def draw_solution_section(img: Image.Image, y_start: int) -> int:
    """使い方セクション（タッチ2回で完了！）"""
    draw = ImageDraw.Draw(img)
    x = LEFT_COL_X
    y = y_start

    # セクションタイトル
    y = draw_section_title(draw, "ピッすいなら、タッチ2回で完了！", x, y)
    y += 12

    # 3ステップ表示
    screenshots = [
        (SCREENSHOTS_DIR / "staff_recognized.png", "職員証をタッチ"),
        (SCREENSHOTS_DIR / "lend.png", "交通系ICカードをタッチ"),
        (SCREENSHOTS_DIR / "return.png", "完了！"),
    ]

    # スクリーンショットのサイズ計算
    ss_w = (LEFT_COL_W - 100) // 3  # 矢印分のスペースを確保
    ss_h = int(ss_w * 0.7)  # アスペクト比

    badge_y = y + 5
    ss_y = y + 50

    for i, (ss_path, label) in enumerate(screenshots):
        step_x = x + i * (ss_w + 50)

        # ステップバッジ
        draw_step_badge(img, i + 1, step_x + ss_w // 2, badge_y)

        # スクリーンショット
        if ss_path.exists():
            ss = load_and_fit_screenshot(ss_path, ss_w, ss_h)
            draw_shadow_screenshot(img, ss, step_x, ss_y)

        # ラベル
        label_font = get_font(20)
        bbox = draw.textbbox((0, 0), label, font=label_font)
        lw = bbox[2] - bbox[0]
        draw.text((step_x + (ss_w - lw) // 2, ss_y + ss_h + 12),
                  label, fill=TEXT_DARK, font=label_font)

        # 矢印（最後のステップ以外）
        if i < 2:
            arrow_x = step_x + ss_w + 25
            draw_arrow(draw, arrow_x, ss_y + ss_h // 2)

    return ss_y + ss_h + 50


def draw_features_section(img: Image.Image, y_start: int) -> int:
    """主な特長セクション（2×2グリッド）"""
    draw = ImageDraw.Draw(img)
    x = RIGHT_COL_X
    y = y_start

    # セクションタイトル
    y = draw_section_title(draw, "主な特長", x, y)
    y += 16

    # 4つの特長カード（2×2）
    card_w = (RIGHT_COL_W - 20) // 2
    card_h = 120
    gap = 16

    features = [
        ("表", "物品出納簿を自動作成",
         "利用履歴から帳票を\nExcelで自動出力", MAIN_BLUE),
        ("戻", "30秒やり直し機能",
         "誤操作しても30秒以内に\n再タッチするだけで取消", ACCENT_ORANGE),
        ("履", "利用履歴の自動記録",
         "残高・乗車駅・降車駅を\n自動で読み取り記録", ACCENT_LIGHT_BLUE),
        ("目", "見やすい画面設計",
         "色・アイコン・テキスト・音\nの4要素で状態を伝達", (76, 175, 80)),
    ]

    for i, (icon, title, desc, color) in enumerate(features):
        row = i // 2
        col = i % 2
        fx = x + col * (card_w + gap)
        fy = y + row * (card_h + gap)
        draw_feature_card(img, fx, fy, card_w, card_h,
                          icon, title, desc, icon_color=color)

    return y + 2 * card_h + gap + 20


def draw_ledger_section(img: Image.Image, y_start: int) -> int:
    """物品出納簿スクリーンショットセクション"""
    draw = ImageDraw.Draw(img)
    x = RIGHT_COL_X
    y = y_start

    # セクションタイトル
    y = draw_section_title(draw, "物品出納簿をExcelで自動出力", x, y)
    y += 8

    # 説明文
    desc_font = get_font(20)
    draw.text((x, y),
              "カードの利用履歴から帳票を自動生成。手書きの手間を削減します。",
              fill=TEXT_GRAY, font=desc_font)
    y += 40

    # スクリーンショット（report_excel_cropped.png を優先、なければ生成）
    report_cropped = PROMOTION_DIR / "report_excel_cropped.png"
    report_raw = SCREENSHOTS_DIR / "report_excel.png"

    if report_cropped.exists():
        ss_path = report_cropped
    elif report_raw.exists():
        ss_path = report_raw
    else:
        # スクリーンショットが見つからない場合はスキップ
        desc_font2 = get_font(24)
        draw.text((x + 20, y + 40),
                  "（スクリーンショットが見つかりません）",
                  fill=TEXT_GRAY, font=desc_font2)
        return y + 120

    # 残りスペースに合わせてスクリーンショットを配置
    available_h = PAGE_HEIGHT - FOOTER_HEIGHT - y - 40
    available_w = RIGHT_COL_W - 20
    ss = load_and_fit_screenshot(ss_path, available_w, available_h)
    draw_shadow_screenshot(img, ss, x, y)

    return y + ss.height + 20


def draw_footer(img: Image.Image):
    """フッター（青帯 + お問い合わせ先）"""
    draw = ImageDraw.Draw(img)
    footer_y = PAGE_HEIGHT - FOOTER_HEIGHT

    # 青い背景帯
    draw.rectangle((0, footer_y, PAGE_WIDTH, PAGE_HEIGHT), fill=MAIN_BLUE)
    # 上部にやや暗い線（立体感）
    draw.rectangle((0, footer_y, PAGE_WIDTH, footer_y + 4), fill=DARK_BLUE)

    # 左側：問い合わせテキスト
    contact_font = get_font(32)
    draw.text((MARGIN + 10, footer_y + 40),
              "お気軽にお問い合わせください",
              fill=TEXT_WHITE, font=contact_font)

    detail_font = get_font(24)
    draw.text((MARGIN + 10, footer_y + 90),
              "導入・操作方法に関するご質問は、情報政策課までご連絡ください。",
              fill=(*TEXT_WHITE[:3],), font=detail_font)

    contact_detail_font = get_font(28)
    draw.text((MARGIN + 10, footer_y + 135),
              "内線：XXXX　担当：○○",
              fill=TEXT_WHITE, font=contact_detail_font)

    # 右側：アプリ名
    app_name = "交通系ICカード管理システム：ピッすい"
    app_font = get_font(22)
    bbox = draw.textbbox((0, 0), app_name, font=app_font)
    tw = bbox[2] - bbox[0]
    draw.text((PAGE_WIDTH - MARGIN - tw - 10, footer_y + 140),
              app_name, fill=(*TEXT_WHITE[:3],), font=app_font)


# ============================================================
# 背景装飾
# ============================================================
def draw_background(img: Image.Image):
    """ページ全体の背景装飾"""
    draw = ImageDraw.Draw(img)

    # 左カラム背景（非常に薄いグレー）
    draw.rectangle(
        (LEFT_COL_X - 20, HEADER_HEIGHT,
         LEFT_COL_X + LEFT_COL_W + 10, PAGE_HEIGHT - FOOTER_HEIGHT),
        fill=(250, 250, 250)
    )

    # 右カラム背景（非常に薄い青）
    draw.rectangle(
        (RIGHT_COL_X - 10, HEADER_HEIGHT,
         RIGHT_COL_X + RIGHT_COL_W + 20, PAGE_HEIGHT - FOOTER_HEIGHT),
        fill=(245, 249, 255)
    )

    # カラム間の縦区切り線
    sep_x = LEFT_COL_X + LEFT_COL_W + COLUMN_GAP // 2
    draw.line(
        (sep_x, HEADER_HEIGHT + 30, sep_x, PAGE_HEIGHT - FOOTER_HEIGHT - 30),
        fill=BORDER_COLOR, width=2
    )


# ============================================================
# メイン処理
# ============================================================
def main():
    print("=" * 60)
    print("  交通系ICカード管理システム：ピッすい チラシ生成")
    print("=" * 60)

    # 出力ディレクトリ確認
    PROMOTION_DIR.mkdir(parents=True, exist_ok=True)

    # スクリーンショットの存在確認
    required_screenshots = [
        SCREENSHOTS_DIR / "staff_recognized.png",
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

    # 背景装飾
    print("[1/6] 背景装飾...")
    draw_background(img)

    # ヘッダー
    print("[2/6] ヘッダー...")
    draw_header(img)

    # 左カラム：課題提起 + 使い方
    print("[3/6] 課題提起セクション...")
    left_y = CONTENT_TOP
    left_y = draw_problem_section(img, left_y)

    print("[4/6] 使い方セクション...")
    draw_solution_section(img, left_y)

    # 右カラム：特長 + 帳票
    print("[5/6] 特長セクション...")
    right_y = CONTENT_TOP
    right_y = draw_features_section(img, right_y)

    print("[6/6] 物品出納簿セクション...")
    draw_ledger_section(img, right_y)

    # フッター
    draw_footer(img)

    # PDF保存
    output_path = PROMOTION_DIR / "チラシ.pdf"
    img.save(str(output_path), "PDF", resolution=300.0)

    print(f"\nチラシを生成しました: {output_path}")
    print(f"  サイズ: A4横 ({PAGE_WIDTH}x{PAGE_HEIGHT}px, 300 DPI)")
    print(f"  出力: {output_path}")


if __name__ == "__main__":
    main()
