"""生成 Cloudshot 应用图标（多尺寸 .ico），无外部依赖，仅用 Pillow。"""
import os
from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "cloudshot.ico")

def rounded_rect(draw, box, radius, fill):
    draw.rounded_rectangle(box, radius=radius, fill=fill)

def make(size):
    scale = 8
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 圆角方块底：蓝紫渐变（用两条色带近似，保证小尺寸也清晰）
    pad = int(s * 0.04)
    box = (pad, pad, s - pad, s - pad)
    rounded_rect(d, box, int(s * 0.22), (47, 108, 240, 255))
    # 顶部高光
    top = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    dt = ImageDraw.Draw(top)
    dt.rectangle((0, 0, s, int(s * 0.55)), fill=(255, 255, 255, 38))
    top.putalpha(top.getchannel("A"))
    img = Image.alpha_composite(img, top)
    d = ImageDraw.Draw(img)
    # 重新画底边圆角遮罩，去掉高光溢出
    d.rounded_rectangle(box, radius=int(s * 0.22), outline=None)

    # 取景框四个角
    lw = max(2, int(s * 0.055))
    m = int(s * 0.24)
    arm = int(s * 0.17)
    white = (255, 255, 255, 255)
    for (x0, y0, dx, dy) in [
        (m, m, 1, 1),
        (s - m, m, -1, 1),
        (m, s - m, 1, -1),
        (s - m, s - m, -1, -1),
    ]:
        # 横线
        d.rounded_rectangle(
            (min(x0, x0 + dx * arm), y0 - lw // 2, max(x0, x0 + dx * arm) + 1, y0 + lw // 2 + 1),
            radius=lw // 2, fill=white)
        # 竖线
        d.rounded_rectangle(
            (x0 - lw // 2, min(y0, y0 + dy * arm), x0 + lw // 2 + 1, max(y0, y0 + dy * arm) + 1),
            radius=lw // 2, fill=white)

    # 中心快门圆点
    r = int(s * 0.085)
    cx, cy = s // 2, s // 2
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=white)

    return img.resize((size, size), Image.LANCZOS)

def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    sizes = [256, 128, 64, 48, 32, 24, 16]
    imgs = [make(s) for s in sizes]
    imgs[0].save(OUT, format="ICO", sizes=[(s, s) for s in sizes])
    print("wrote", OUT, os.path.getsize(OUT), "bytes")

if __name__ == "__main__":
    main()
