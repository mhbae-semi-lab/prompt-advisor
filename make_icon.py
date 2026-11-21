"""앱 아이콘 생성: 네이비 라운드 스퀘어 + 터미널 프롬프트 '>_' (teal/white)."""
from PIL import Image, ImageDraw, ImageFont
import os

os.makedirs("Assets", exist_ok=True)
S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([28, 28, S - 28, S - 28], radius=210, fill=(15, 35, 55, 255))
d.rounded_rectangle([28, 28, S - 28, S - 28], radius=210, outline=(0, 166, 200, 255), width=10)

try:
    font = ImageFont.truetype(r"C:\Windows\Fonts\consolab.ttf", 560)
except Exception:
    font = ImageFont.load_default()

# '>' teal + '_' white 를 한 문자열처럼 중앙에
bbox = d.textbbox((0, 0), ">_", font=font)
tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
x, y = (S - tw) // 2 - bbox[0], (S - th) // 2 - bbox[1]
d.text((x, y), ">", font=font, fill=(0, 200, 235, 255))
w_gt = d.textlength(">", font=font)
d.text((x + w_gt, y), "_", font=font, fill=(255, 255, 255, 255))

img.save("Assets/app.png")
sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
img.save("Assets/app.ico", format="ICO", sizes=sizes)
print("saved Assets/app.ico, app.png")
