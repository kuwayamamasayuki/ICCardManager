#!/usr/bin/env python3
"""
プロモーション動画生成スクリプト

交通系ICカード管理システム：ピッすいの庁内プロモーション用動画（約24.5秒）を
Pillow + ffmpeg で生成する。

使い方:
    pip install Pillow
    python tools/CreatePromotionVideo.py

出力:
    docs/promotion/promotion_video.mp4

依存:
    - Python 3.x
    - Pillow (pip install Pillow)
    - ffmpeg (パスが通っていること)
    - Yu Gothic Bold フォント (Windows標準)
"""

import math
import os
import shutil
import subprocess
import sys
import tempfile
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
WIDTH, HEIGHT = 1920, 1080
FPS = 30

# カラーパレット
BG_COLOR = (245, 245, 245)           # #F5F5F5 ライトグレー背景
MAIN_BLUE = (33, 150, 243)           # #2196F3 アプリのテーマカラー
TEXT_DARK = (33, 33, 33)             # #212121
TEXT_WHITE = (255, 255, 255)         # #FFFFFF
CARD_SILVER_BASE = 192               # 交通系ICカードの銀色ベース値
READER_BLACK = (26, 26, 26)          # #1A1A1A カードリーダー本体
READER_TOP = (50, 50, 50)            # リーダー上面
READER_LAMP_OFF = (60, 60, 60)       # インジケーターOFF
READER_LAMP_ON = (76, 175, 80)       # #4CAF50 インジケーターON
SHADOW_COLOR = (180, 180, 180)       # スクリーンショットの影
BORDER_COLOR = (200, 200, 200)       # カード・スクリーンショットの枠線

# フォント
FONT_PATH = "/mnt/c/Windows/Fonts/YuGothB.ttc"

# パス
SCRIPT_DIR = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent
SCREENSHOTS_DIR = PROJECT_DIR / "docs" / "screenshots"
SOUNDS_DIR = PROJECT_DIR / "src" / "ICCardManager" / "Resources" / "Sounds"
OUTPUT_DIR = PROJECT_DIR / "docs" / "promotion"

# カードサイズ (実際のICカード比率 85.6mm×54mm ≒ 1.585:1 に近似)
CARD_WIDTH = 300
CARD_HEIGHT = 190

# リーダーサイズ
READER_WIDTH = 200
READER_HEIGHT = 120
READER_TOP_HEIGHT = 15


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
def new_frame() -> Image.Image:
    """背景色で塗りつぶした新規フレームを作成"""
    return Image.new("RGB", (WIDTH, HEIGHT), BG_COLOR)


def draw_staff_card(img: Image.Image, cx: int, cy: int, angle: float = 0):
    """
    職員証を描画
    白地の角丸長方形、上1/5が青(#2196F3)、下部中央に「職員証」テキスト
    """
    card = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), (0, 0, 0, 0))
    cd = ImageDraw.Draw(card)

    # カード本体 (白い角丸長方形)
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1),
        radius=12, fill=(255, 255, 255), outline=BORDER_COLOR
    )

    # 上部1/5を青く塗る
    blue_h = CARD_HEIGHT // 5
    # 上部の角丸部分を青で描画
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, blue_h + 12),
        radius=12, fill=MAIN_BLUE
    )
    # 角丸の下にはみ出た部分を白で修正（下半分を白に戻す）
    cd.rectangle((0, blue_h, CARD_WIDTH - 1, blue_h + 12), fill=(255, 255, 255))

    # 「職員証」テキスト (下部エリアの中央)
    font = get_font(36)
    text = "職員証"
    bbox = cd.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (CARD_WIDTH - tw) // 2
    ty = blue_h + (CARD_HEIGHT - blue_h - th) // 2
    cd.text((tx, ty), text, fill=TEXT_DARK, font=font)

    # 回転（傾き演出用）
    if angle != 0:
        card = card.rotate(angle, expand=True, resample=Image.BICUBIC)

    # 中心座標基準で貼り付け
    paste_x = cx - card.width // 2
    paste_y = cy - card.height // 2
    img.paste(card, (paste_x, paste_y), card)


def draw_ic_card(img: Image.Image, cx: int, cy: int, angle: float = 0):
    """
    交通系ICカードを描画
    銀色ベースの角丸長方形にグラデーション風の質感、中央に「交通系」テキスト
    """
    card = Image.new("RGBA", (CARD_WIDTH, CARD_HEIGHT), (0, 0, 0, 0))
    cd = ImageDraw.Draw(card)

    # 角丸でマスクを作成してからグラデーション塗り
    mask = Image.new("L", (CARD_WIDTH, CARD_HEIGHT), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle((0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1), radius=12, fill=255)

    # 銀色グラデーション（縦方向にsin波で明暗をつける）
    for y in range(CARD_HEIGHT):
        brightness = CARD_SILVER_BASE + int(20 * math.sin(y / CARD_HEIGHT * math.pi))
        color = (brightness, brightness, brightness + 5, 255)
        cd.line([(0, y), (CARD_WIDTH - 1, y)], fill=color)

    # マスク適用（角丸の外側を透過に）
    card.putalpha(mask)

    # 枠線
    cd = ImageDraw.Draw(card)
    cd.rounded_rectangle(
        (0, 0, CARD_WIDTH - 1, CARD_HEIGHT - 1),
        radius=12, fill=None, outline=(170, 170, 170)
    )

    # 「交通系」テキスト
    font = get_font(32)
    text = "交通系"
    bbox = cd.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (CARD_WIDTH - tw) // 2
    ty = (CARD_HEIGHT - th) // 2
    cd.text((tx, ty), text, fill=(80, 80, 80), font=font)

    if angle != 0:
        card = card.rotate(angle, expand=True, resample=Image.BICUBIC)

    paste_x = cx - card.width // 2
    paste_y = cy - card.height // 2
    img.paste(card, (paste_x, paste_y), card)


def draw_card_reader(img: Image.Image, cx: int, cy: int, lit: bool = False):
    """
    カードリーダーを描画
    黒い台形型の本体、上面に奥行き感、インジケーターランプ付き
    """
    margin = 20
    total_w = READER_WIDTH + margin * 2
    total_h = READER_HEIGHT + READER_TOP_HEIGHT + margin * 2
    reader = Image.new("RGBA", (total_w, total_h), (0, 0, 0, 0))
    rd = ImageDraw.Draw(reader)

    ox, oy = margin, margin

    # 本体（台形：上辺が少し狭い）
    body_pts = [
        (ox + 10, oy + READER_TOP_HEIGHT),
        (ox + READER_WIDTH - 10, oy + READER_TOP_HEIGHT),
        (ox + READER_WIDTH, oy + READER_TOP_HEIGHT + READER_HEIGHT),
        (ox, oy + READER_TOP_HEIGHT + READER_HEIGHT),
    ]
    rd.polygon(body_pts, fill=READER_BLACK)

    # 上面（パースを出す台形）
    top_pts = [
        (ox + 15, oy),
        (ox + READER_WIDTH - 15, oy),
        (ox + READER_WIDTH - 10, oy + READER_TOP_HEIGHT),
        (ox + 10, oy + READER_TOP_HEIGHT),
    ]
    rd.polygon(top_pts, fill=READER_TOP)

    # インジケーターランプ
    lamp_color = READER_LAMP_ON if lit else READER_LAMP_OFF
    lamp_cx = ox + READER_WIDTH // 2
    lamp_cy = oy + READER_TOP_HEIGHT + 15
    rd.ellipse(
        (lamp_cx - 6, lamp_cy - 6, lamp_cx + 6, lamp_cy + 6),
        fill=lamp_color
    )

    # ランプ点灯時の発光エフェクト
    if lit:
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


def draw_ripple(img: Image.Image, cx: int, cy: int, radius: int, alpha: int = 80):
    """タッチ時の波紋エフェクトを描画（同心円が広がる）"""
    overlay = Image.new("RGBA", img.size, (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)

    for i in range(3):
        r = radius - i * (radius // 4)
        if r <= 0:
            continue
        a = max(0, alpha - i * 30)
        od.ellipse(
            (cx - r, cy - r, cx + r, cy + r),
            outline=(*MAIN_BLUE, a), width=3
        )

    img_rgba = img.convert("RGBA")
    composited = Image.alpha_composite(img_rgba, overlay)
    img.paste(composited.convert("RGB"))


def draw_text_multiline(img: Image.Image, text: str, font_size: int = 56,
                        color=TEXT_DARK, y_offset: int = 0, line_spacing: int = 20):
    """中央揃えで複数行テキストを描画"""
    draw = ImageDraw.Draw(img)
    font = get_font(font_size)
    lines = text.split("\n")

    # 各行の高さを計算して全体の高さを求める
    line_heights = []
    line_widths = []
    for line in lines:
        bbox = draw.textbbox((0, 0), line, font=font)
        line_widths.append(bbox[2] - bbox[0])
        line_heights.append(bbox[3] - bbox[1])

    total_h = sum(line_heights) + line_spacing * (len(lines) - 1)
    current_y = (HEIGHT - total_h) // 2 + y_offset

    for i, line in enumerate(lines):
        x = (WIDTH - line_widths[i]) // 2
        draw.text((x, current_y), line, fill=color, font=font)
        current_y += line_heights[i] + line_spacing


def apply_fade(img: Image.Image, alpha: float) -> Image.Image:
    """フレームにフェード効果を適用 (0.0=背景色のみ, 1.0=そのまま)"""
    if alpha >= 1.0:
        return img
    bg = Image.new("RGB", (WIDTH, HEIGHT), BG_COLOR)
    return Image.blend(bg, img, max(0.0, min(1.0, alpha)))


def load_and_fit_screenshot(path: Path, max_w: int, max_h: int) -> Image.Image:
    """スクリーンショットを読み込み、指定サイズ内にアスペクト比を維持してリサイズ"""
    screenshot = Image.open(path)
    ratio = min(max_w / screenshot.width, max_h / screenshot.height)
    new_w = int(screenshot.width * ratio)
    new_h = int(screenshot.height * ratio)
    return screenshot.resize((new_w, new_h), Image.LANCZOS)


def draw_caption_bar(img: Image.Image, text: str, y: int):
    """画面下部にキャプションバー（半透明の青帯+白文字）を描画"""
    bar_height = 70
    bar_img = Image.new("RGBA", (WIDTH, bar_height), (*MAIN_BLUE, 220))
    img_rgba = img.convert("RGBA")
    img_rgba.paste(bar_img, (0, y), bar_img)
    img.paste(img_rgba.convert("RGB"))

    draw = ImageDraw.Draw(img)
    font = get_font(32)
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    draw.text(((WIDTH - tw) // 2, y + 18), text, fill=TEXT_WHITE, font=font)


def draw_message_panel(img: Image.Image, text: str, cy: int,
                       font_size: int = 48, alpha: float = 1.0):
    """半透明の青パネル付きメッセージを描画（タッチ後の反応表示用）"""
    font = get_font(font_size)
    draw_tmp = ImageDraw.Draw(img)
    lines = text.split("\n")

    # 各行のサイズを計測
    line_sizes = []
    for line in lines:
        bbox = draw_tmp.textbbox((0, 0), line, font=font)
        line_sizes.append((bbox[2] - bbox[0], bbox[3] - bbox[1]))

    max_w = max(w for w, h in line_sizes)
    line_spacing = 12
    total_h = sum(h for _, h in line_sizes) + line_spacing * (len(lines) - 1)

    # パネルサイズ（テキスト + パディング）
    pad_x, pad_y = 60, 30
    panel_w = max_w + pad_x * 2
    panel_h = total_h + pad_y * 2
    panel_x = (WIDTH - panel_w) // 2
    panel_y = cy - panel_h // 2

    # 半透明の青パネル（角丸）
    panel = Image.new("RGBA", (panel_w, panel_h), (0, 0, 0, 0))
    pd = ImageDraw.Draw(panel)
    panel_alpha = int(200 * max(0.0, min(1.0, alpha)))
    pd.rounded_rectangle(
        (0, 0, panel_w - 1, panel_h - 1),
        radius=16, fill=(*MAIN_BLUE, panel_alpha)
    )

    # パネルを合成
    img_rgba = img.convert("RGBA")
    img_rgba.paste(panel, (panel_x, panel_y), panel)

    # テキスト描画
    text_alpha = int(255 * max(0.0, min(1.0, alpha)))
    text_overlay = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    td = ImageDraw.Draw(text_overlay)
    current_y = panel_y + pad_y
    for i, line in enumerate(lines):
        lw, lh = line_sizes[i]
        lx = (WIDTH - lw) // 2
        td.text((lx, current_y), line,
                fill=(*TEXT_WHITE, text_alpha), font=font)
        current_y += lh + line_spacing

    result = Image.alpha_composite(img_rgba, text_overlay)
    img.paste(result.convert("RGB"))


# ============================================================
# シーン生成
# ============================================================
def scene_text(text: str, duration_sec: float,
               fade_in_sec: float = 0.5, fade_out_sec: float = 0.5,
               font_size: int = 56) -> list:
    """テキスト表示シーン（フェードイン → ホールド → フェードアウト）"""
    frames = []
    total = int(duration_sec * FPS)
    fi = int(fade_in_sec * FPS)
    fo = int(fade_out_sec * FPS)

    for i in range(total):
        img = new_frame()
        draw_text_multiline(img, text, font_size=font_size)

        if i < fi:
            img = apply_fade(img, i / fi)
        elif i >= total - fo:
            img = apply_fade(img, (total - i) / fo)

        frames.append(img)
    return frames


class _FrameList(list):
    """touch_times 属性を持てるリスト"""
    touch_times: tuple = (0.0, 0.0)


def scene_double_touch(label: str, ic_sound: str = "ピッ♪",
                       duration_sec: float = 4.5,
                       label_offset_x: int = 0,
                       toast_img: "Image.Image | None" = None) -> "_FrameList":
    """
    職員証 → 交通系ICカードの連続タッチシーン
    label: "貸出時も" or "返却時も"（画面上部に表示）
    ic_sound: ICカードタッチ時の音テキスト（"ピッ♪" or "ピピッ♪"）
    label_offset_x: ラベルの水平オフセット（負=左、正=右）
    toast_img: ICカードタッチ後に表示するトースト通知画像

    戻り値の .touch_times 属性にタッチ時刻（秒）のタプルを付与:
        (staff_touch_sec, ic_touch_sec)
    """
    frames = _FrameList()
    total = int(duration_sec * FPS)

    # タイミング（フレーム数）
    staff_slide_dur = int(0.8 * FPS)         # 職員証スライド時間
    staff_touch = staff_slide_dur            # 職員証タッチ瞬間
    gap_start = staff_touch + int(0.6 * FPS) # 職員証退場開始
    gap_dur = int(0.4 * FPS)                 # 退場時間
    ic_slide_start = gap_start + gap_dur     # ICカードスライド開始
    ic_slide_dur = int(0.8 * FPS)            # ICカードスライド時間
    ic_touch = ic_slide_start + ic_slide_dur # ICカードタッチ瞬間
    ripple_dur = int(0.6 * FPS)
    sound_text_dur = int(0.8 * FPS)
    toast_delay = int(0.3 * FPS)         # トースト表示開始（ICタッチ後）
    toast_slide_dur = int(0.3 * FPS)     # トーストスライドイン時間

    # 位置
    reader_cx = WIDTH // 2
    reader_cy = HEIGHT // 2 + 60
    card_start_x = WIDTH + CARD_WIDTH
    card_end_x = reader_cx + 20
    card_exit_x = -CARD_WIDTH
    card_base_y = reader_cy - 100

    # ラベル描画用
    label_font = get_font(44)

    for i in range(total):
        img = new_frame()

        # ラベル描画（画面上部に「貸出時も」or「返却時も」）
        draw = ImageDraw.Draw(img)
        bbox = draw.textbbox((0, 0), label, font=label_font)
        lw = bbox[2] - bbox[0]
        draw.text(((WIDTH - lw) // 2 + label_offset_x, 80), label,
                  fill=MAIN_BLUE, font=label_font)

        # リーダーのランプ状態
        lamp_on = (staff_touch <= i < gap_start) or (i >= ic_touch)
        draw_card_reader(img, reader_cx, reader_cy, lit=lamp_on)

        # --- 職員証アニメーション ---
        if i < gap_start + gap_dur:
            if i < staff_slide_dur:
                # スライドイン
                t = i / staff_slide_dur
                t_ease = 1 - (1 - t) ** 3
                sx = int(card_start_x + (card_end_x - card_start_x) * t_ease)
                sy = card_base_y - int(20 * math.sin(t * math.pi))
                sa = -4 * (1 - t)
            elif i < gap_start:
                # タッチ後、静止
                sx, sy, sa = card_end_x, card_base_y, 0
            else:
                # 退場（左へスライドアウト）
                t = (i - gap_start) / gap_dur
                t_ease = t ** 2
                sx = int(card_end_x + (card_exit_x - card_end_x) * t_ease)
                sy = card_base_y
                sa = 3 * t
            draw_staff_card(img, sx, sy, sa)

        # --- 交通系ICカードアニメーション ---
        if i >= ic_slide_start:
            if i < ic_touch:
                # スライドイン
                t = (i - ic_slide_start) / ic_slide_dur
                t_ease = 1 - (1 - t) ** 3
                ix = int(card_start_x + (card_end_x - card_start_x) * t_ease)
                iy = card_base_y - int(20 * math.sin(t * math.pi))
                ia = -4 * (1 - t)
            else:
                ix, iy, ia = card_end_x, card_base_y, 0
            draw_ic_card(img, ix, iy, ia)

        # --- 職員証タッチ時の波紋 + 音テキスト ---
        if staff_touch <= i < staff_touch + ripple_dur:
            rt = (i - staff_touch) / ripple_dur
            draw_ripple(img, reader_cx, reader_cy - 30,
                        int(20 + 60 * rt), int(80 * (1 - rt)))

        if staff_touch <= i < staff_touch + sound_text_dur:
            tt = (i - staff_touch) / sound_text_dur
            _draw_float_text(img, "ピッ♪", reader_cy - 160, tt)

        # --- ICカードタッチ時の波紋 + 音テキスト ---
        if ic_touch <= i < ic_touch + ripple_dur:
            rt = (i - ic_touch) / ripple_dur
            draw_ripple(img, reader_cx, reader_cy - 30,
                        int(20 + 60 * rt), int(80 * (1 - rt)))

        if ic_touch <= i < ic_touch + sound_text_dur:
            tt = (i - ic_touch) / sound_text_dur
            _draw_float_text(img, ic_sound, reader_cy - 160, tt)

        # --- トースト表示（ICカードタッチ後にスライドイン） ---
        if toast_img is not None and i >= ic_touch + toast_delay:
            toast_target_x = WIDTH - toast_img.width - 40
            toast_y = 30
            elapsed = i - ic_touch - toast_delay
            if elapsed < toast_slide_dur:
                t = elapsed / toast_slide_dur
                t_ease = 1 - (1 - t) ** 3
                toast_x = int(WIDTH + (toast_target_x - WIDTH) * t_ease)
            else:
                toast_x = toast_target_x
            # 背景色で角丸外の隙間を隠す
            draw_bg = ImageDraw.Draw(img)
            draw_bg.rectangle(
                (toast_x - 2, toast_y - 2,
                 toast_x + toast_img.width + 2, toast_y + toast_img.height + 2),
                fill=BG_COLOR
            )
            img.paste(toast_img, (toast_x, toast_y))

        frames.append(img)

    # タッチ時刻（秒）を属性として付与（効果音同期用）
    frames.touch_times = (staff_touch / FPS, ic_touch / FPS)
    return frames


def _draw_float_text(img: Image.Image, text: str, base_y: int, progress: float):
    """上へ浮かびながらフェードする音テキストを描画"""
    alpha = 1.0 if progress < 0.6 else max(0, (1 - progress) / 0.4)
    y = int(base_y - 25 * progress)
    overlay = Image.new("RGBA", (WIDTH, HEIGHT), (0, 0, 0, 0))
    td = ImageDraw.Draw(overlay)
    font = get_font(36)
    bbox = td.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    a = int(255 * max(0.0, min(1.0, alpha)))
    td.text(((WIDTH - tw) // 2, y), text, fill=(*MAIN_BLUE, a), font=font)
    img_rgba = img.convert("RGBA")
    result = Image.alpha_composite(img_rgba, overlay)
    img.paste(result.convert("RGB"))


def scene_screenshot(screenshot_path: Path, caption: str,
                     duration_sec: float = 3.0) -> list:
    """スクリーンショット表示シーン（スライドイン + キャプション）"""
    frames = []
    total = int(duration_sec * FPS)
    slide_frames = int(0.4 * FPS)

    # スクリーンショット読み込み（キャプションバー分の余白を残す）
    max_h = HEIGHT - 120
    screenshot = load_and_fit_screenshot(screenshot_path, WIDTH - 120, max_h)

    ss_x = (WIDTH - screenshot.width) // 2
    ss_y = (HEIGHT - 70 - screenshot.height) // 2

    for i in range(total):
        img = new_frame()
        draw = ImageDraw.Draw(img)

        # スライドインアニメーション
        if i < slide_frames:
            t = i / slide_frames
            t_ease = 1 - (1 - t) ** 3
            offset_x = int((WIDTH // 4) * (1 - t_ease))
            alpha = t_ease
        else:
            offset_x = 0
            alpha = 1.0

        # 影
        draw.rectangle(
            (ss_x + offset_x + 4, ss_y + 4,
             ss_x + offset_x + screenshot.width + 4, ss_y + screenshot.height + 4),
            fill=SHADOW_COLOR
        )

        # スクリーンショット
        img.paste(screenshot, (ss_x + offset_x, ss_y))

        # 枠線
        draw.rectangle(
            (ss_x + offset_x - 1, ss_y - 1,
             ss_x + offset_x + screenshot.width, ss_y + screenshot.height),
            outline=BORDER_COLOR, width=1
        )

        # キャプション
        draw_caption_bar(img, caption, HEIGHT - 70)

        # フェード
        if alpha < 1.0:
            img = apply_fade(img, alpha)

        frames.append(img)
    return frames


def scene_end_card(duration_sec: float = 4.0) -> list:
    """エンドカード（キャッチコピー + アプリ名）"""
    frames = []
    total = int(duration_sec * FPS)
    fi = int(0.8 * FPS)

    for i in range(total):
        img = new_frame()
        draw = ImageDraw.Draw(img)

        # メインコピー
        main_text = "タッチ2回。帳簿は自動。"
        font_main = get_font(64)
        bbox = draw.textbbox((0, 0), main_text, font=font_main)
        tw = bbox[2] - bbox[0]
        draw.text(((WIDTH - tw) // 2, HEIGHT // 2 - 80), main_text,
                  fill=TEXT_DARK, font=font_main)

        # 区切り線
        line_w = 200
        line_y = HEIGHT // 2 + 10
        draw.line(
            ((WIDTH - line_w) // 2, line_y, (WIDTH + line_w) // 2, line_y),
            fill=MAIN_BLUE, width=2
        )

        # アプリ名
        app_text = "交通系ICカード管理システム：ピッすい"
        font_app = get_font(36)
        bbox = draw.textbbox((0, 0), app_text, font=font_app)
        tw = bbox[2] - bbox[0]
        draw.text(((WIDTH - tw) // 2, HEIGHT // 2 + 30), app_text,
                  fill=MAIN_BLUE, font=font_app)

        # フェードイン
        if i < fi:
            img = apply_fade(img, i / fi)

        frames.append(img)
    return frames


# ============================================================
# メイン処理
# ============================================================
def save_frames(frames: list, tmpdir: Path, start_idx: int) -> int:
    """フレーム一覧をPNGとして保存し、次のインデックスを返す"""
    for img in frames:
        img.save(tmpdir / f"frame_{start_idx:05d}.png")
        start_idx += 1
    return start_idx


def build_ffmpeg_command(tmpdir: Path, output_path: Path,
                         total_duration: float,
                         sound_events: list) -> list:
    """ffmpegコマンドを構築（映像 + 効果音）

    sound_events: [(time_sec, wav_path), ...] 効果音のリスト
    """
    cmd = [
        "ffmpeg", "-y",
        "-framerate", str(FPS),
        "-i", str(tmpdir / "frame_%05d.png"),
    ]

    # 音声入力と遅延フィルター
    filter_parts = []
    audio_labels = []
    input_idx = 1

    for time_sec, wav_path in sound_events:
        if wav_path.exists():
            cmd.extend(["-i", str(wav_path)])
            delay_ms = int(time_sec * 1000)
            filter_parts.append(
                f"[{input_idx}:a]adelay={delay_ms}|{delay_ms}[a{input_idx}]"
            )
            audio_labels.append(f"[a{input_idx}]")
            input_idx += 1

    if audio_labels:
        n = len(audio_labels)
        mix = "".join(audio_labels) + f"amix=inputs={n}:duration=longest[aout]"
        filter_complex = ";".join(filter_parts) + ";" + mix
        cmd.extend(["-filter_complex", filter_complex])
        cmd.extend(["-map", "0:v", "-map", "[aout]"])
        cmd.extend(["-c:a", "aac", "-b:a", "128k"])

    cmd.extend([
        "-c:v", "libx264",
        "-preset", "medium",
        "-crf", "23",
        "-pix_fmt", "yuv420p",
        "-t", str(total_duration),
        str(output_path),
    ])
    return cmd


def main():
    print("=" * 60)
    print("  交通系ICカード管理システム：ピッすい プロモーション動画生成")
    print("=" * 60)

    # 依存チェック
    if shutil.which("ffmpeg") is None:
        print("\nエラー: ffmpeg が見つかりません。")
        print("  sudo apt install ffmpeg  または")
        print("  https://ffmpeg.org/ からインストールしてください。")
        sys.exit(1)

    # 出力ディレクトリ作成
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    # 物品出納簿のトリミング済み画像を生成（テーブル本体のみ）
    screenshot_raw = SCREENSHOTS_DIR / "report_excel.png"
    screenshot_report = OUTPUT_DIR / "report_excel_cropped.png"
    if not screenshot_raw.exists():
        print(f"エラー: スクリーンショットが見つかりません: {screenshot_raw}")
        sys.exit(1)
    raw = Image.open(screenshot_raw)
    cropped = raw.crop((30, 230, 1200, 830))
    # 備考欄の「テストデータ」テキストを消去（プロモーション用）
    # 罫線(y=149,200,251,...,557) の間のセル内部のみ白で塗りつぶし
    cd = ImageDraw.Draw(cropped)
    for row_top in [200, 251, 302, 353, 404, 455, 506, 557]:
        cd.rectangle((900, row_top + 1, 990, row_top + 50), fill=(255, 255, 255))
    cropped.save(screenshot_report)
    print(f"物品出納簿画像をトリミング: {screenshot_report}")

    # トースト通知画像をクロップ（貸出・返却時の通知表示）
    toast_crop_box = (1474, 3, 2007, 216)
    toast_w = 480
    lend_full = Image.open(SCREENSHOTS_DIR / "lend.png")
    toast_lend = lend_full.crop(toast_crop_box)
    toast_h = int(toast_w * toast_lend.height / toast_lend.width)
    toast_lend = toast_lend.resize((toast_w, toast_h), Image.LANCZOS)

    ret_full = Image.open(SCREENSHOTS_DIR / "return.png")
    toast_return = ret_full.crop(toast_crop_box)
    toast_return = toast_return.resize((toast_w, toast_h), Image.LANCZOS)
    print(f"トースト画像をクロップ: {toast_w}x{toast_h}px")

    # シーンタイミング（各シーンの秒数）
    # Scene 1:  3.0s  キャッチコピー
    # Scene 2:  2.5s  ブリッジ（ピッすいなら…）
    # Scene 3:  4.5s  貸出（職員証→ICカード連続タッチ）
    # Scene 4:  4.5s  返却（職員証→ICカード連続タッチ）
    # Scene 5:  3.0s  物品出納簿テキスト
    # Scene 6:  3.0s  物品出納簿画面
    # Scene 7:  4.0s  エンドカード
    # 合計:    24.5s

    lend_wav = SOUNDS_DIR / "lend.wav"
    return_wav = SOUNDS_DIR / "return.wav"

    with tempfile.TemporaryDirectory(prefix="promo_") as tmpdir:
        tmpdir = Path(tmpdir)
        idx = 0

        print("\n[1/7] キャッチコピー (0-3s)...")
        idx = save_frames(
            scene_text("交通系ICカードの管理、\nまだ手書きですか？", 3.0),
            tmpdir, idx
        )

        print("[2/7] ブリッジ (3-5.5s)...")
        idx = save_frames(
            scene_text("ピッすいなら、\nピッとタッチするだけ", 2.5),
            tmpdir, idx
        )

        scene3_start = 5.5
        print(f"[3/7] 貸出シーン ({scene3_start}-{scene3_start + 4.5}s)...")
        lend_frames = scene_double_touch("貸出時も", "ピッ♪", 4.5,
                                         label_offset_x=-200,
                                         toast_img=toast_lend)
        idx = save_frames(lend_frames, tmpdir, idx)

        scene4_start = scene3_start + 4.5
        print(f"[4/7] 返却シーン ({scene4_start}-{scene4_start + 4.5}s)...")
        return_frames = scene_double_touch("返却時も", "ピピッ♪", 4.5,
                                           label_offset_x=200,
                                           toast_img=toast_return)
        idx = save_frames(return_frames, tmpdir, idx)

        print("[5/7] 物品出納簿メッセージ (14.5-17.5s)...")
        idx = save_frames(
            scene_text("貸出も返却も\n2回タッチするだけで\n物品出納簿を自動作成！",
                        3.0, font_size=48),
            tmpdir, idx
        )

        print("[6/7] 物品出納簿出力画面 (17.5-20.5s)...")
        idx = save_frames(
            scene_screenshot(screenshot_report,
                             "物品出納簿をExcelで自動出力", 3.0),
            tmpdir, idx
        )

        print("[7/7] エンドカード (20.5-24.5s)...")
        idx = save_frames(
            scene_end_card(4.0),
            tmpdir, idx
        )

        total_duration = idx / FPS
        print(f"\n全 {idx} フレーム生成完了（約 {total_duration:.1f} 秒）")

        # 効果音イベントを構築
        # 各 scene_double_touch は .touch_times = (staff_sec, ic_sec)
        # これはシーン開始からの相対時刻
        lend_staff_t, lend_ic_t = lend_frames.touch_times
        ret_staff_t, ret_ic_t = return_frames.touch_times

        sound_events = [
            (scene3_start + lend_staff_t, lend_wav),    # 貸出: 職員証タッチ
            (scene3_start + lend_ic_t, lend_wav),        # 貸出: ICカードタッチ
            (scene4_start + ret_staff_t, lend_wav),      # 返却: 職員証タッチ
            (scene4_start + ret_ic_t, return_wav),       # 返却: ICカードタッチ
        ]

        # MP4エンコード
        print("\nffmpegでMP4エンコード中...")
        output_path = OUTPUT_DIR / "promotion_video.mp4"

        cmd = build_ffmpeg_command(
            tmpdir, output_path, total_duration,
            sound_events
        )

        result = subprocess.run(cmd, capture_output=True, text=True)

        if result.returncode != 0:
            print(f"ffmpegエラー:\n{result.stderr[:500]}")
            print("\n音声なしでリトライします...")
            cmd_simple = [
                "ffmpeg", "-y",
                "-framerate", str(FPS),
                "-i", str(tmpdir / "frame_%05d.png"),
                "-c:v", "libx264",
                "-preset", "medium",
                "-crf", "23",
                "-pix_fmt", "yuv420p",
                "-t", str(total_duration),
                str(output_path),
            ]
            result = subprocess.run(cmd_simple, capture_output=True, text=True)
            if result.returncode != 0:
                print(f"ffmpegエラー:\n{result.stderr[:500]}")
                sys.exit(1)
            print("（音声なしで生成しました）")

        print(f"\n動画を生成しました: {output_path}")
        print(f"  解像度: {WIDTH}x{HEIGHT}")
        print(f"  長さ: 約 {total_duration:.1f} 秒")
        print(f"  フレームレート: {FPS} fps")


if __name__ == "__main__":
    main()
