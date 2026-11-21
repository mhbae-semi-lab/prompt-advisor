# -*- coding: utf-8 -*-
"""Prompt Advisor 사용자 매뉴얼(PPTX + PDF) 생성기. llm-pulse 매뉴얼 양식을 가져와 고쳐 썼다.

    python docs/manual/build_manual.py          # shots/ 의 캡처로 PPTX 생성
    python docs/manual/build_manual.py --pdf    # PPTX 생성 후 PowerPoint 로 PDF 내보내기 (Windows + PowerPoint 필요)

스크린샷은 합성 예시 DB 로만 찍는다:  PromptAdvisor.exe --shots docs/manual/shots  (영어는 --lang en 을 더함)
캡처 크기는 2028x1244 px 이고, 말풍선 좌표는 이 픽셀 기준이다.
"""
import os
import subprocess
import sys

from PIL import Image
from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_SHAPE
from pptx.enum.text import MSO_ANCHOR, PP_ALIGN
from pptx.util import Inches, Pt

HERE = os.path.dirname(os.path.abspath(__file__))
SHOTS = os.path.join(HERE, "shots")
ASSETS = os.path.join(HERE, "assets")
CACHE = os.path.join(HERE, ".cache")
OUT = os.path.join(HERE, "Prompt-Advisor-User-Manual-ko.pptx")
VERSION = "1.0.0"
DATE = "2026.11.21"
APP = "Prompt Advisor"
PX = (2028, 1244)  # 캡처 크기

# ---------------------------------------------------------------- 디자인 토큰
INK = RGBColor(0x1F, 0x29, 0x37)
SUB = RGBColor(0x5B, 0x65, 0x73)
BLUE = RGBColor(0x00, 0x78, 0xD4)
TEAL = RGBColor(0x00, 0xA6, 0xC8)
NAVY = RGBColor(0x0F, 0x23, 0x37)
CARD = RGBColor(0xF3, 0xF5, 0xF8)
LINE = RGBColor(0xD9, 0xDE, 0xE5)
AMBER = RGBColor(0xE5, 0xA0, 0x2D)
AMBER_BG = RGBColor(0xFF, 0xF6, 0xE5)
AMBER_TX = RGBColor(0x7A, 0x4E, 0x00)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)
FONT = "맑은 고딕"
MONO = "Consolas"

prs = Presentation()
prs.slide_width, prs.slide_height = Inches(13.333), Inches(7.5)
TOTAL = 23
state = {"n": 0}


def new_slide():
    state["n"] += 1
    return prs.slides.add_slide(prs.slide_layouts[6])


def box(s, x, y, w, h, fill=None, line=None, shape=MSO_SHAPE.RECTANGLE, radius=None, lw=0.75):
    sh = s.shapes.add_shape(shape, Inches(x), Inches(y), Inches(w), Inches(h))
    if fill is None:
        sh.fill.background()
    else:
        sh.fill.solid()
        sh.fill.fore_color.rgb = fill
    if line is None:
        sh.line.fill.background()
    else:
        sh.line.color.rgb = line
        sh.line.width = Pt(lw)
    sh.shadow.inherit = False
    if radius is not None and shape == MSO_SHAPE.ROUNDED_RECTANGLE:
        sh.adjustments[0] = radius
    return sh


def rbox(s, x, y, w, h, fill=CARD, line=None, radius=0.03, lw=0.75):
    return box(s, x, y, w, h, fill, line, MSO_SHAPE.ROUNDED_RECTANGLE, radius, lw)


def text(s, x, y, w, h, t, size=12, bold=False, color=INK, align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP,
         shape=None, font=FONT, spacing=None):
    tb = shape or s.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_left = tf.margin_right = Inches(0.05)
    tf.margin_top = tf.margin_bottom = Inches(0.02)
    tf.vertical_anchor = anchor
    lines = t if isinstance(t, list) else [t]
    for i, line in enumerate(lines):
        p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
        p.alignment = align
        if spacing:
            p.line_spacing = spacing
        r = p.add_run()
        r.text = line
        r.font.size = Pt(size)
        r.font.bold = bold
        r.font.color.rgb = color
        r.font.name = font
    return tb


def num_circle(s, x, y, n, d=0.3, size=11):
    c = box(s, x, y, d, d, fill=BLUE, line=WHITE, shape=MSO_SHAPE.OVAL, lw=1.5)
    text(s, 0, 0, 0, 0, str(n), size, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=c)
    c.text_frame.margin_left = c.text_frame.margin_right = 0
    return c


def tip_box(s, x, y, w, t, h=0.42, size=9.5):
    b = rbox(s, x, y, w, h, fill=AMBER_BG, line=AMBER, radius=0.15)
    text(s, 0, 0, 0, 0, "Tip  " + t, size, False, AMBER_TX, PP_ALIGN.LEFT, MSO_ANCHOR.MIDDLE, shape=b)
    b.text_frame.margin_left = Inches(0.12)


# ---------------------------------------------------------------- 공통 틀
def header(s, section, title, bullets):
    pill = box(s, 10.2, 0.0, 2.7, 0.34, fill=BLUE, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.3)
    text(s, 0, 0, 0, 0, section, 10, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=pill)
    text(s, 0.45, 0.32, 11.5, 0.6, f"[{APP}] {title}", 28, True)
    box(s, 0.5, 1.0, 0.9, 0.06, fill=BLUE)
    for i, t in enumerate(bullets):
        box(s, 0.55, 1.37 + i * 0.36, 0.07, 0.07, fill=BLUE, shape=MSO_SHAPE.OVAL)
        text(s, 0.72, 1.24 + i * 0.36, 12.1, 0.32, t, 13.5)
    footer(s)


def footer(s):
    box(s, 0.5, 7.05, 12.33, 0.01, fill=LINE)
    s.shapes.add_picture(os.path.join(ASSETS, "icon.png"), Inches(0.5), Inches(7.12), Inches(0.22), Inches(0.22))
    text(s, 0.78, 7.1, 7, 0.25, f"{APP} 사용자 매뉴얼  ·  v{VERSION}  ·  배민혁", 9, False, SUB)
    text(s, 11.8, 7.1, 1.03, 0.25, f"{state['n']} / {TOTAL}", 9, True, SUB, PP_ALIGN.RIGHT)


# ---------------------------------------------------------------- 스크린샷 카드
def load_shot(name, crop=None):
    """crop: 픽셀 사각형 (x0,y0,x1,y1). 반환: (경로, 크기, 오프셋)"""
    path = os.path.join(SHOTS, f"{name}.png")
    if not crop:
        return path, PX, (0, 0)
    os.makedirs(CACHE, exist_ok=True)
    x0, y0, x1, y1 = crop
    out = os.path.join(CACHE, f"{name}_{x0}_{y0}_{x1}_{y1}.png")
    Image.open(path).crop((x0, y0, x1, y1)).save(out, optimize=True)
    return out, (x1 - x0, y1 - y0), (x0, y0)


def shot_card(s, x, y, w, h, title, name, callouts=(), crop=None, d=0.24, note="※ 합성 예시 데이터로 찍은 화면"):
    rbox(s, x, y, w, h)
    text(s, x + 0.15, y + 0.1, w - 0.3, 0.32, title, 13, True)
    path, (dw, dh), (ox, oy) = load_shot(name, crop)
    bw, bh = w - 0.4, h - 0.82
    k = min(bw / dw, bh / dh)
    iw, ih = dw * k, dh * k
    ix, iy = x + (w - iw) / 2, y + 0.5
    box(s, ix - 0.02, iy - 0.02, iw + 0.04, ih + 0.04, fill=WHITE, line=LINE)
    s.shapes.add_picture(path, Inches(ix), Inches(iy), Inches(iw), Inches(ih))
    for n, r in enumerate(callouts, 1):
        x0, y0, x1, y1 = r
        box(s, ix + (x0 - ox) * k, iy + (y0 - oy) * k, (x1 - x0) * k, (y1 - y0) * k, fill=None, line=TEAL,
            shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.08, lw=2)
        c = box(s, ix + (x0 - ox) * k - d / 2, iy + (y0 - oy) * k - d / 2, d, d, fill=BLUE, line=WHITE,
                shape=MSO_SHAPE.OVAL, lw=1.25)
        text(s, 0, 0, 0, 0, str(n), 10, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=c)
        c.text_frame.margin_left = c.text_frame.margin_right = 0
    if note:
        text(s, x + w - 3.4, y + h - 0.28, 3.25, 0.22, note, 8.5, False, SUB, PP_ALIGN.RIGHT)


def steps_card(s, x, y, w, h, steps, title="보는 법", tip=None, desc_size=10):
    rbox(s, x, y, w, h)
    text(s, x + 0.15, y + 0.1, w - 0.3, 0.32, title, 13, True)
    n = len(steps)
    top = y + 0.52
    avail = h - 0.52 - (0.62 if tip else 0.15)
    pitch = min(0.8, avail / n)
    for i, (hd, ds) in enumerate(steps):
        yy = top + i * pitch
        num_circle(s, x + 0.2, yy + 0.03, i + 1)
        text(s, x + 0.62, yy - 0.02, w - 0.8, 0.28, hd, 12, True)
        text(s, x + 0.62, yy + 0.24, w - 0.8, pitch - 0.22, ds, desc_size, False, SUB)
    if tip:
        tip_box(s, x + 0.15, y + h - 0.55, w - 0.3, tip)


CY, CH = 2.45, 4.5      # 카드 영역
LX, LW = 0.5, 7.4       # 왼쪽(스크린샷) 카드
RX, RW = 8.05, 4.78     # 오른쪽(순서) 카드


def shot_steps(section, title, bullets, shot, callouts, steps, tip=None, card_title=None, crop=None, desc_size=10):
    s = new_slide()
    header(s, section, title, bullets)
    shot_card(s, LX, CY, LW, CH, card_title or title, shot, callouts, crop)
    steps_card(s, RX, CY, RW, CH, steps, tip=tip, desc_size=desc_size)
    return s


def icon_lines(s, cx, y0, color, rows):
    """표지 장식: 프롬프트 줄."""
    for i, w in enumerate(rows):
        box(s, cx, y0 + i * 0.32, w, 0.12, fill=color, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.5)


# ================================================================ 1. 표지
def cover():
    s = new_slide()
    s.background.fill.solid()
    s.background.fill.fore_color.rgb = NAVY
    icon_lines(s, 0.85, 5.4, RGBColor(0x1C, 0x3D, 0x5C), [3.6, 2.4, 3.0, 1.8])
    box(s, 0.85, 5.4, 0.35, 0.12, fill=TEAL, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.5)
    s.shapes.add_picture(os.path.join(ASSETS, "icon.png"), Inches(0.8), Inches(0.9), Inches(0.9), Inches(0.9))
    text(s, 0.75, 2.0, 7, 1.0, APP, 50, True, WHITE)
    text(s, 0.8, 3.05, 7, 0.6, "사용자 매뉴얼", 28, False, RGBColor(0x8F, 0xD8, 0xEA))
    box(s, 0.85, 3.8, 0.9, 0.06, fill=TEAL)
    text(s, 0.8, 4.0, 6.0, 0.9, ["AI 코딩 도구에 보내기 전에", "프롬프트를 점검하는 Windows 앱"], 15, False, RGBColor(0xC9, 0xD3, 0xE0))
    text(s, 0.8, 6.65, 7, 0.3, f"Version {VERSION}   ·   {DATE}   ·   배민혁", 11, False, RGBColor(0x8A, 0x97, 0xA8))
    path, _, _ = load_shot("01_analyze")
    box(s, 6.48, 0.98, 6.62, 4.08, fill=None, line=RGBColor(0x2A, 0x3B, 0x55), lw=1.5)
    s.shapes.add_picture(path, Inches(6.5), Inches(1.0), Inches(6.58), Inches(6.58 * PX[1] / PX[0]))
    text(s, 6.5, 5.15, 6.58, 0.3, "합성 예시 데이터 화면", 9, False, RGBColor(0x8A, 0x97, 0xA8), PP_ALIGN.RIGHT)


# ================================================================ 2. 목차
def toc():
    s = new_slide()
    pill = box(s, 10.2, 0.0, 2.7, 0.34, fill=BLUE, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.3)
    text(s, 0, 0, 0, 0, "목차", 10, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=pill)
    text(s, 0.45, 0.32, 11.5, 0.6, "목차", 28, True)
    box(s, 0.5, 1.0, 0.9, 0.06, fill=BLUE)
    text(s, 0.55, 1.2, 12, 0.35, "처음 쓰는 분은 1번부터, 필요한 화면만 찾는 분은 쪽수를 보세요.", 13.5, False, SUB)
    groups = [
        ("1", "시작하기", [("3", "이 앱이 푸는 문제"), ("4", "설치 · 업데이트 · 제거"), ("5", "첫 실행과 예시 데이터")]),
        ("2", "분석과 개선", [("6", "화면 구성"), ("7", "분석 결과 읽기"), ("8", "장비 SW 요청 린트"), ("9–10", "개선안 · Shadow · 전/후")]),
        ("3", "기록과 검증", [("11", "이력 · 통계"), ("12–15", "현장 검증 절차 4단계"), ("16", "개선 추적")]),
        ("4", "데이터와 개인정보", [("17", "내 데이터 형식 · 오류 메시지"), ("18", "로컬 LLM · 개인정보"), ("19", "영어 화면 · 다크 모드"), ("20", "도움말 · 단축키")]),
        ("5", "부록", [("21", "FAQ · 문제 해결"), ("22", "한계 · 버전 정보")]),
    ]
    pos = [(0.5, 1.9, 3.95, 2.45), (4.69, 1.9, 3.95, 2.45), (8.88, 1.9, 3.95, 2.45),
           (0.5, 4.5, 6.05, 2.4), (6.78, 4.5, 6.05, 2.4)]
    for (no, name, items), (x, y, w, h) in zip(groups, pos):
        rbox(s, x, y, w, h)
        num_circle(s, x + 0.2, y + 0.2, no, 0.38, 13)
        text(s, x + 0.7, y + 0.2, w - 0.9, 0.4, name, 16, True, anchor=MSO_ANCHOR.MIDDLE)
        for i, (pg, nm) in enumerate(items):
            yy = y + 0.74 + i * 0.32
            text(s, x + 0.3, yy, 0.9, 0.3, pg, 11.5, True, BLUE)
            text(s, x + 1.2, yy, w - 1.4, 0.3, nm, 11.5, False, INK)
    footer(s)


# ================================================================ 3. 소개
def intro():
    s = new_slide()
    header(s, "시작하기", "이 앱이 푸는 문제", [
        "장비 SW 를 AI 코딩 도구로 고칠 때, 장비 · 버전 · 로그 · 재현 조건이 빠진 짧은 요청은 정정 턴을 반복하게 만듦",
        "이 앱은 그 낭비를 보내기 전에 경고하고, 빈 칸을 채우게 하는 개선안을 만들어 줌",
        "기록은 이 PC 에만 저장되고, Ollama 가 없어도 모든 기능이 규칙 기반으로 동작",
    ])
    cards = [
        ("내 기록으로 배운 경고", "Claude Code · Cursor 기록을 가져와, 경고마다 '이 경고가 뜬 내 요청은 정정 · 턴이 얼마나 더 들었는지'를 붙여 줍니다."),
        ("장비 SW 요청 린트", "대상 장비/모듈 · SW 버전 · 로그 발췌 · 재현 조건 · 기대 결과가 빠졌는지 한국어 · 영어 요청 모두에서 봅니다."),
        ("개선안 + 현장 검증", "규칙 개선안이 결정하고 로컬 LLM 안은 옆에서만 비교(Shadow). 규칙을 바꿀 땐 재현 → 비교 → 전환 · 롤백."),
    ]
    for i, (hd, ds) in enumerate(cards):
        x = 0.5 + i * 4.19
        rbox(s, x, 2.45, 3.95, 1.95)
        num_circle(s, x + 0.2, 2.65, i + 1, 0.36)
        text(s, x + 0.68, 2.64, 3.1, 0.38, hd, 15, True, anchor=MSO_ANCHOR.MIDDLE)
        text(s, x + 0.25, 3.18, 3.5, 1.1, ds, 11, False, SUB)
    text(s, 0.5, 4.62, 6, 0.3, "프롬프트 하나가 지나가는 길", 13, True)
    flow = [
        ("가져오기", ["Claude Code · Cursor 기록", "CSV · claude.ai zip"]),
        ("분석", ["규칙 린트 + 내 기록 수치", "토큰 예측 · 모델 추천"]),
        ("개선안", ["규칙안(채택)", "로컬 LLM 안(Shadow)"]),
        ("보내고 추적", ["프롬프트만 복사", "다음 가져오기 때 실제 턴과 연결"]),
    ]
    w, gap = 2.78, 0.4
    for i, (hd, ln) in enumerate(flow):
        x = 0.5 + i * (w + gap)
        hl = i == 1
        rbox(s, x, 5.0, w, 1.75, fill=WHITE if hl else CARD, line=BLUE if hl else None, radius=0.04, lw=2)
        text(s, x + 0.1, 5.1, w - 0.2, 0.4, hd, 14, True, BLUE if hl else INK, PP_ALIGN.CENTER)
        text(s, x + 0.1, 5.6, w - 0.2, 1.0, ln, 10.5, False, SUB, PP_ALIGN.CENTER)
        if i < 3:
            box(s, x + w + 0.06, 5.72, gap - 0.12, 0.34, fill=BLUE, shape=MSO_SHAPE.RIGHT_ARROW)


# ================================================================ 4. 설치
def install():
    s = new_slide()
    header(s, "시작하기", "설치 · 업데이트 · 제거", [
        "GitHub Releases 의 설치 파일 하나로 관리자 권한 없이 설치 (Velopack)",
        "새 버전은 시작할 때 확인하고, [도움말 → 업데이트 확인]으로 받아 다시 시작",
        "제거해도 내 데이터 폴더는 남음 (지우려면 폴더를 직접 삭제)",
    ])
    steps_card(s, 0.5, CY, 6.6, CH, [
        ("설치 파일 받기", "Releases 에서 prompt-advisor-win-Setup.exe 를 받습니다 (약 86MB, .NET 포함)."),
        ("SmartScreen", "서명하지 않은 파일이라 경고가 뜨면 '추가 정보 → 실행'을 누릅니다."),
        ("설치 확인", "%LOCALAPPDATA%\\prompt-advisor 에 설치되고 시작 메뉴 · 바탕 화면에 'Prompt Advisor'가 생깁니다."),
        ("업데이트", "새 버전이 있으면 도움말 탭에 표시됩니다. [업데이트 확인] → 받기 → 자동 재시작."),
        ("제거", "Windows 설정 → 앱에서 Prompt Advisor 제거. 데이터 폴더는 그대로 남습니다."),
    ], title="순서", tip="설치 없이 쓰려면 Portable.zip 을 풀어 PromptAdvisor.exe 를 실행하세요", desc_size=10.5)
    rbox(s, 7.3, CY, 5.53, 2.15)
    text(s, 7.45, CY + 0.1, 5.2, 0.32, "폴더", 13, True)
    text(s, 7.5, CY + 0.5, 5.2, 1.6, [
        "·  프로그램: %LOCALAPPDATA%\\prompt-advisor",
        "·  내 데이터: %LOCALAPPDATA%\\prompt-advisor-data",
        "·  예시 데이터: …\\prompt-advisor-data\\demo (실행마다 새로 만듦)",
        "·  오류 기록: 데이터 폴더의 crash.log",
    ], 10.5, False, SUB, spacing=1.15)
    rbox(s, 7.3, CY + 2.35, 5.53, 2.15)
    text(s, 7.45, CY + 2.45, 5.2, 0.32, "확인한 것 (v1.0.0 → v1.0.1)", 13, True)
    text(s, 7.5, CY + 2.85, 5.2, 1.6, [
        "·  조용한 설치 → 실행 → 셀프테스트 전부 통과",
        "·  로컬 피드로 1.0.0 → 1.0.1 업데이트, 재시작 후 v1.0.1 확인",
        "·  제거 시 설치 폴더 · 레지스트리 · 바로가기 삭제, 데이터 유지",
        "·  Windows 10 / 11 (64비트)",
    ], 10.5, False, SUB, spacing=1.15)


# ================================================================ 5. 첫 실행
def first_run():
    s = new_slide()
    header(s, "시작하기", "첫 실행과 예시 데이터", [
        "처음 열면 안내 창이 뜨고, Ollama(로컬 LLM)가 있는지 함께 알려 줌",
        "내 기록이 아직 없으면 [예시 데이터로 둘러보기]로 합성 데이터(가상 프로젝트 3개 · 100건)를 먼저 볼 수 있음",
        "예시 실행은 내 데이터와 따로 저장되고, 위쪽 주황 띠의 [내 데이터로 돌아가기]로 돌아감",
    ])
    shot_card(s, LX, CY, LW, CH, "가져오기 · 설정 탭 위쪽", "09_import_csv_errors",
              [(15, 220, 1075, 270), (15, 458, 610, 502)], crop=(0, 160, 1380, 520))
    steps_card(s, RX, CY, RW, CH, [
        ("내 기록 가져오기", "Claude Code 세션 / Cursor 대화 / claude.ai zip / CSV 중 쓰는 것을 누릅니다. 이 PC 의 파일만 읽기 전용으로 읽습니다."),
        ("예시 데이터로 둘러보기", "합성 DB 로 앱을 다시 엽니다. 매뉴얼의 화면은 모두 이 데이터입니다."),
        ("시작 체크리스트", "분석 탭 위쪽에 가져오기 → (선택) 임베딩 → 첫 분석 순서가 표시됩니다."),
    ], title="처음 할 일", tip="[도움말 → 처음 안내 다시 보기]로 안내 창을 다시 열 수 있습니다")


# ================================================================ 6~10. 분석과 개선
def analyze():
    SEC = "분석과 개선"
    shot_steps(SEC, "화면 구성", [
        "위쪽 탭 5개: 분석 · 이력·통계 · 검증 · 가져오기·설정 · 도움말 (Ctrl+1~5)",
        "왼쪽은 대화와 입력, 오른쪽은 분석 결과",
        "어느 앱에서든 Ctrl+Shift+P 로 창을 불러옴 (창을 닫으면 트레이에 상주)",
    ], "01_analyze", [(0, 105, 1380, 158), (0, 705, 1385, 880), (5, 960, 1385, 1160), (1395, 165, 2020, 1160)], [
        ("탭", "분석 · 이력 · 검증 · 설정 · 도움말"),
        ("프로젝트 · 템플릿 · 대상 모델", "프로젝트를 고르면 그 폴더의 문서와 직전 대화가 맥락에 들어갑니다. 모델을 고르면 1순위로 고정."),
        ("입력과 버튼", "프롬프트를 붙여넣고 [분석] (Ctrl+Enter). [개선 프롬프트 생성], [프롬프트만 복사]."),
        ("분석 결과", "작업 유형 · 예상 토큰 · 경고 · 비슷한 사례 · 모델 추천."),
    ], tip="이미지는 끌어다 놓거나 Ctrl+V 로 첨부합니다 (장당 약 1,100 tok 으로 계산)", card_title="분석 탭")

    shot_steps(SEC, "분석 결과 읽기", [
        "칩: 프로젝트 · 작업 유형 · 장비 SW 요청 여부 · 예상 입력/출력 토큰 · 예측 신뢰도 · 보정 배수",
        "경고 카드의 '내 기록:' 줄은 가져온 기록에서 계산한 값 (경고가 뜬 요청 vs 안 뜬 요청)",
        "양쪽 표본이 3건 미만이면 '내 기록' 수치를 쓰지 않고 기본값으로 표시",
    ], "01_analyze", [(1398, 210, 2005, 440), (1398, 490, 2018, 850), (1398, 860, 2018, 1160)], [
        ("요약 칩", "보정 표본이 3건 이상이면 예측에 실측 보정(0.5~2.0배)이 붙습니다."),
        ("추론된 작업 맥락", "작업 유형 · 목적 · 대상 파일을 고치면 개선안에 바로 들어갑니다."),
        ("경고 카드", "빨강 = 경고, 주황 = 주의. '예상 낭비'는 내 기록의 토큰 차이(없으면 기본값)."),
    ], crop=(1385, 160, 2028, 1170), card_title="오른쪽 분석 패널")

    shot_steps(SEC, "장비 SW 요청 린트", [
        "장비 · 알람 · 인터락 · 웨이퍼 · 서보 같은 말이 있으면 장비 SW 요청으로 봄 (한국어 · 영어)",
        "다섯 가지를 확인: 대상 장비/모듈 · SW/펌웨어 버전 · 로그 발췌 · 재현 조건 · 기대 결과",
        "빠진 항목은 개선안의 [장비 정보] 칸으로 만들어 채워 보내게 함",
    ], "02_rewrite", [(1400, 255, 2020, 405), (1400, 415, 2020, 560), (1400, 570, 2020, 715)], [
        ("로그 발췌 없음", "알람 전후 5~10줄과 시각을 붙이라고 안내합니다."),
        ("재현 조건 없음", "어떤 동작 · 레시피 · 순서에서 몇 번 중 몇 번인지."),
        ("기대 결과 없음", "정상이면 어떻게 동작해야 하는지."),
    ], tip="일반 요청에 잘못 뜨면 검증 탭에서 끄고 전/후를 비교한 뒤 전환하세요", crop=(1390, 160, 2028, 860),
        card_title="장비 경고 (예: '서보 알람 자꾸 떠요 고쳐줘')")

    shot_steps(SEC, "개선안과 Shadow", [
        "왼쪽 = 규칙 개선안(채택), 오른쪽 = 로컬 LLM 개선안(Shadow, 결정에 쓰지 않음)",
        "LLM 안은 주제 이탈 · 심사 점수 · 남은 경고 수를 기록하고, 기준을 넘으면 LLM 우선으로 전환 가능",
        "그림의 LLM 안은 RTX 3050 4GB 에서 exaone3.5:2.4b 가 만든 실제 출력. Ollama · 모델이 없으면 '없음' 안내만 나오고 규칙안으로 모두 동작",
    ], "02_rewrite", [(310, 185, 485, 220), (25, 225, 700, 685), (715, 225, 1365, 340)], [
        ("프롬프트만 복사", "채택안 본문만 복사하고, 보낸 개선본으로 기록합니다."),
        ("규칙안 (채택)", "[작업] · [장비 정보] · [조건] · [출력 형식]. '(예: …)' 칸을 채워 보내세요."),
        ("LLM 안 (Shadow)", "작은 모델은 맥락을 잘못 섞기도 합니다. 그래서 결정은 규칙안이 하고, LLM 안은 비교만 합니다."),
    ], crop=(0, 165, 1385, 690), card_title="개선안 말풍선")

    shot_steps(SEC, "개선 전 / 후", [
        "개선안을 만들면 오른쪽 아래에 경고 수 전 → 후, 해결된 경고, 남은 칸이 표시됨",
        "개선안의 템플릿 문구는 세지 않고, 작업 본문과 채운 값만 다시 린트함",
        "남은 칸을 채워 보내면 장비 경고가 사라짐",
    ], "02_rewrite", [(1398, 860, 2018, 930), (1398, 985, 2020, 1155)], [
        ("예상 낭비", "경고가 겹치므로 더하지 않고 가장 큰 항목 기준으로 보여줍니다."),
        ("전 / 후 카드", "해결된 경고와 '채워 보내면 사라지는' 남은 경고를 나눠 보여줍니다."),
    ], crop=(1390, 760, 2028, 1170), card_title="개선 전 / 후 카드")


# ================================================================ 11~16. 기록과 검증
def history_verify():
    SEC = "기록과 검증"
    shot_steps(SEC, "이력 · 통계", [
        "가져온 기록을 출처 · 프로젝트 · 라벨로 거르고, 토큰 · 턴 · 정정 · 점수를 봄",
        "라벨: 정정 · 토큰 과다로 자동 계산(최적/최악), 수동 라벨이 우선",
        "[재현 세트]로 표시한 사례는 검증 탭에서 규칙을 바꿀 때 전/후 비교에 쓰임",
    ], "03_history", [(5, 200, 1080, 318), (5, 335, 1950, 392), (10, 510, 2005, 925), (5, 940, 2020, 1158)], [
        ("KPI", "총 프롬프트 · 누적 토큰 · 정정 신호 비율 · 예측 정확도"),
        ("필터와 버튼", "최적/최악 표시, 즐겨찾기(템플릿), 재현 세트, 분석으로 보내기, 주간 리포트"),
        ("이력 표", "행을 누르면 아래에 전체 본문과 점수 근거"),
        ("미리보기", "[프롬프트만 복사]로 다시 쓰기"),
    ])

    shot_steps(SEC, "검증 1 · 재현", [
        "규칙을 바꾸기 전에, 문제 상황을 담은 세트를 버튼 하나로 다시 돌려 봄",
        "예시 세트(합성 10건) 또는 내 재현 세트(이력에서 표시한 사례)",
        "각 사례의 작업 유형과 경고를 지금 규칙으로 보여줌",
    ], "04_verify_repro", [(0, 210, 1380, 262), (10, 272, 1130, 322), (15, 355, 2010, 1150)], [
        ("하위 탭", "재현 → 비교 → Shadow → 전환·롤백 → 개선 추적"),
        ("세트 고르기 · 실행", "[재현 실행], 행을 고르고 [선택한 사례 분석]"),
        ("결과 표", "사례마다 경고 수와 목록"),
    ])

    shot_steps(SEC, "검증 2 · 비교", [
        "바꿀 설정(장비 린트 · 형식 린트 · 기준 길이 등)을 고르고 같은 세트로 전/후를 나란히 비교",
        "바뀐 사례만 주황색으로 강조하고, 생긴 경고(+)와 사라진 경고(−)를 표시",
        "그림: 장비 린트를 껐을 때 10건 중 7건이 바뀜",
    ], "05_verify_compare", [(15, 275, 1470, 320), (15, 325, 1420, 352), (25, 400, 2010, 740)], [
        ("바꿀 설정", "체크 · 숫자 칸으로 후보 설정을 만듭니다."),
        ("요약", "몇 건이 바뀌는지, 경고 합계 전 → 후"),
        ("강조된 행", "바뀐 사례만 굵게 · 주황색"),
    ], tip="비교만으로는 아무것도 바뀌지 않습니다. 적용은 [4 전환 · 롤백]에서")

    shot_steps(SEC, "검증 3 · Shadow", [
        "개선안을 만들 때마다 규칙안과 LLM 안의 차이가 쌓임 (결정은 규칙안)",
        "전환 기준: 표본 10건 이상 · 주제 일치 90% 이상 · 평균 심사 70점 이상",
        "그림은 합성 기록. 실측(예시 10건, exaone 2.4B): 주제 일치 10/10 · 평균 심사 82, 다만 심사도 같은 모델이라 10건 중 7건이 85점",
    ], "06_verify_shadow", [(10, 310, 1590, 345), (585, 355, 690, 1090), (690, 355, 1045, 1090)], [
        ("요약", "표본 · 주제 일치 · 평균 심사, 기준 통과 여부"),
        ("주제 일치", "LLM 안이 원래 주제를 벗어나면 체크가 빠지고 규칙안을 씁니다."),
        ("심사 · 남은 경고", "LLM 심사 점수와 두 안의 남은 경고 수"),
    ])

    shot_steps(SEC, "검증 4 · 전환과 롤백", [
        "[비교한 설정으로 전환]: 비교에서 고른 설정을 적용, 지금 설정은 이력에 보관",
        "[LLM안 우선으로 전환]: Shadow 기준을 넘었을 때만 켜짐 (검증 실패 시 규칙안으로 대체)",
        "[이전 설정으로 되돌리기]: 바로 앞 설정으로 한 번에 복귀 (되돌림도 이력에 남음)",
    ], "07_verify_switch", [(15, 275, 960, 325), (15, 340, 812, 380), (30, 468, 2010, 660)], [
        ("지금 설정", "적용 중인 규칙 설정 한 줄 요약"),
        ("전환 · 롤백 버튼", "전환과 되돌리기"),
        ("변경 이력", "그림: 장비 린트 끔으로 전환 → 되돌림"),
    ], crop=(0, 160, 2028, 880))

    shot_steps(SEC, "개선 추적", [
        "개선안의 [프롬프트만 복사]로 보낸 본문을 기록하고, 다음 가져오기 때 실제 기록과 연결",
        "같은 경고가 있던 기존 요청과 실제 턴 · 정정 수를 비교",
        "그림의 수치(개선본 1.8턴 ↔ 기존 6.0턴)는 합성 가정으로 만든 값이라 효과를 증명하지 않음",
    ], "08_verify_tracking", [(10, 310, 880, 345), (1690, 355, 1810, 975), (1810, 355, 2010, 975)], [
        ("요약", "개선본 평균 턴 · 정정 ↔ 같은 경고 기존 요청"),
        ("경고 전 → 후", "보낼 때 남은 경고 수"),
        ("실제 턴 · 정정", "가져오기 뒤 채워집니다 (전에는 '대기')"),
    ])


# ================================================================ 17~20. 데이터와 개인정보
def data_privacy():
    SEC = "데이터와 개인정보"
    shot_steps(SEC, "내 데이터 형식 · 오류 메시지", [
        "CSV 머리줄: source,session,project,created_at,in_tok,out_tok,turns,fail,label,text (text 만 필수)",
        "잘못된 줄은 건너뛰고 '파일 이름 · 몇 번째 줄 · 이유'를 보여줌",
        "Claude Code · Cursor 기록 형식이 바뀌어 읽지 못하는 줄이 많으면 '형식이 바뀐 것 같다'고 알림",
    ], "09_import_csv_errors", [(10, 280, 790, 390)], [
        ("가져오기 결과", "그림: 5줄 중 1줄 저장, 3줄은 text 없음 · 숫자 아님 · 칸 부족으로 건너뜀"),
    ], crop=(0, 160, 1380, 520), card_title="CSV 가져오기 결과", tip="[CSV 내보내기]와 같은 형식이라 백업 · 다른 도구 기록 옮기기에 씁니다")

    s = new_slide()
    header(s, SEC, "로컬 LLM · 개인정보", [
        "LLM 은 이 PC 의 Ollama 만 씀. API 키 · 계정 · 서버가 없음",
        "보내기 전에 경로 · 메일 · API 키 · 토큰 · IP · 전화번호를 가리고, 보낸 내용을 말풍선에서 그대로 확인",
        "Ollama 주소가 이 PC(localhost)가 아니면 보낼 때마다 묻고, 동의해야 보냄",
    ])
    shot_card(s, LX, CY, LW, CH, "Ollama 설정 (가져오기 · 설정 탭)", "09_import_csv_errors",
              [(205, 800, 880, 960), (205, 965, 1075, 1025)], crop=(0, 760, 1380, 1170))
    rbox(s, RX, CY, RW, CH)
    text(s, RX + 0.15, CY + 0.1, RW - 0.3, 0.32, "알아 둘 것", 13, True)
    text(s, RX + 0.2, CY + 0.55, RW - 0.4, 3.4, [
        "·  설치: winget install Ollama.Ollama",
        "·  생성: ollama pull exaone3.5:2.4b (VRAM 4GB 기준)",
        "·  임베딩: ollama pull bge-m3",
        "·  모델이 없으면 상태 줄에 '모델 없음'과 받는 명령이 나옵니다",
        "·  Ollama 가 없으면 비슷한 요청 찾기는 단어 유사도, 개선안은 규칙 기반으로 동작",
        "·  기록은 %LOCALAPPDATA%\\prompt-advisor-data 에만 저장, 외부 오류 수집 없음",
        "·  지식 내보내기에는 원문 · 프로젝트 이름을 넣지 않음",
    ], 10.5, False, SUB, spacing=1.2)
    tip_box(s, RX + 0.15, CY + CH - 0.55, RW - 0.3, "이 매뉴얼의 화면은 생성 모델이 없는 PC 에서 찍었습니다")

    s = new_slide()
    header(s, SEC, "영어 화면 · 다크 모드", [
        "언어: [설정 → 언어] 에서 시스템 따름 / 한국어 / English, 저장 뒤 다시 열면 적용",
        "린트 규칙과 개선안도 영어 요청에 대응 (영어 요청이면 개선안도 영어)",
        "테마: 시스템 따름 / 라이트 / 다크",
    ])
    shot_card(s, 0.5, CY, 6.08, CH, "English UI", "en_01_analyze", [], note="※ 합성 예시 데이터")
    shot_card(s, 6.75, CY, 6.08, CH, "다크 테마", "11_dark", [], note="※ 합성 예시 데이터")

    s = new_slide()
    header(s, SEC, "도움말 · 단축키", [
        "도움말 탭: 버전 · 업데이트 확인 · 개인정보 · 데이터 형식 · FAQ · 라이선스",
        "키보드만으로 탭 이동 · 분석 · 검색 가능",
        "전역 단축키는 다른 프로그램을 쓰는 중에도 동작",
    ])
    shot_card(s, LX, CY, LW, CH, "도움말 탭", "10_help", [])
    rbox(s, RX, CY, RW, CH)
    text(s, RX + 0.15, CY + 0.1, RW - 0.3, 0.32, "단축키", 13, True)
    keys = [("Ctrl + Enter", "분석"), ("Ctrl + K", "입력창으로"), ("Ctrl + F", "이력 검색"),
            ("Ctrl + 1 … 5", "탭 이동"), ("Ctrl + V", "이미지 붙여넣기 첨부"), ("Ctrl + Shift + P", "창 불러오기 (전역)")]
    for i, (k, d) in enumerate(keys):
        yy = CY + 0.58 + i * 0.5
        glob = "전역" in d
        chip = rbox(s, RX + 0.2, yy, 1.95, 0.36, fill=BLUE if glob else INK, radius=0.2)
        text(s, 0, 0, 0, 0, k, 10.5, True, WHITE, PP_ALIGN.CENTER, MSO_ANCHOR.MIDDLE, shape=chip, font=MONO)
        text(s, RX + 2.3, yy, RW - 2.45, 0.36, d, 11, False, INK, anchor=MSO_ANCHOR.MIDDLE)
    tip_box(s, RX + 0.15, CY + CH - 0.55, RW - 0.3, "파란 칩은 어떤 프로그램에서든 동작하는 전역 단축키")


# ================================================================ 21~23. 부록
def faq():
    s = new_slide()
    header(s, "부록", "FAQ · 문제 해결", [
        "오류는 [설정 → 오류 기록], 예기치 않은 종료는 데이터 폴더의 crash.log",
        "내 데이터는 [설정 → 데이터 폴더 열기]로 바로 열림",
        "DB 백업 · 복원은 [설정 → 백업 · 복원]",
    ])
    qa = [
        ("예측이 실제와 많이 달라요", "분석한 프롬프트를 실제로 보내고 나중에 [가져오기]를 하면 예측과 실측이 이어져 보정됩니다 (3건 이상부터)."),
        ("'내 기록:' 줄이 안 나와요", "그 경고가 뜬 요청과 안 뜬 요청이 각각 3건 이상이어야 나옵니다."),
        ("장비 경고가 일반 요청에도 떠요", "검증 탭에서 장비 린트를 끄고 같은 세트로 비교한 뒤 전환하세요. 언제든 되돌릴 수 있습니다."),
        ("LLM 안이 '없음'으로 나와요", "Ollama 와 생성 모델이 필요합니다: ollama pull exaone3.5:2.4b. 없어도 규칙안은 동작합니다."),
        ("가져오기가 '형식이 바뀐 것 같다'고 해요", "도구 업데이트로 기록 형식이 바뀐 경우입니다. 앱을 업데이트하거나 이슈로 알려 주세요."),
        ("CSV 가 일부만 들어가요", "가져오기 결과에 줄 번호와 이유가 나옵니다. 그 줄을 고쳐 다시 가져오세요 (중복은 자동 제외)."),
        ("SmartScreen 경고가 떠요", "서명하지 않은 설치 파일입니다. '추가 정보 → 실행'을 누르세요."),
        ("예전 개인판 기록을 옮기고 싶어요", "[설정 → 이전 버전 데이터 가져오기]를 누르면 합쳐집니다."),
    ]
    cw, ch = 6.05, 1.02
    for i, (q, a) in enumerate(qa):
        col, row = i % 2, i // 2
        x = 0.5 + col * (cw + 0.23)
        y = CY + row * (ch + 0.13)
        rbox(s, x, y, cw, ch)
        text(s, x + 0.15, y + 0.08, 0.4, 0.32, "Q", 13, True, BLUE)
        text(s, x + 0.5, y + 0.08, cw - 0.65, 0.32, q, 12, True)
        text(s, x + 0.5, y + 0.42, cw - 0.65, 0.56, a, 10, False, SUB)


def limits():
    s = new_slide()
    header(s, "부록", "한계 · 버전 정보", [
        "경고 수치는 내 기록의 상관 관계이며, 경고 때문에 턴이 늘었다는 인과를 증명하지 않음",
        "예시 DB 의 수치는 '장비 정보가 빠질수록 턴이 늘어난다'는 가정으로 만든 합성 값 (기능 시연용)",
        "토큰 수는 Claude Code 기록에만 있음. Cursor 기록은 턴 · 정정만 있어 토큰 보정에 쓰이지 않음",
    ])
    lim = [
        "장비 SW 린트는 키워드 규칙입니다. 검증 탭에서 끄거나 기준을 바꿀 수 있습니다.",
        "모델 가격표(models.json)는 참고용이며 자동으로 갱신되지 않습니다. [설정 → models.json 열기]로 고칩니다.",
        "업데이트 확인은 GitHub 에 공개 릴리스가 있어야 동작합니다.",
        "설치 파일은 서명하지 않았습니다.",
    ]
    rbox(s, 0.5, CY, 7.6, CH)
    text(s, 0.65, CY + 0.1, 7, 0.32, "그 밖의 한계", 13, True)
    for i, t in enumerate(lim):
        box(s, 0.7, CY + 0.66 + i * 0.6, 0.07, 0.07, fill=BLUE, shape=MSO_SHAPE.OVAL)
        text(s, 0.88, CY + 0.56 + i * 0.6, 7.0, 0.55, t, 11, False, INK)
    rbox(s, 8.3, CY, 4.53, CH, fill=WHITE, line=BLUE, lw=2)
    text(s, 8.45, CY + 0.1, 4.2, 0.32, "버전 정보", 13, True, BLUE)
    text(s, 8.5, CY + 0.55, 4.2, 3.6, [
        f"·  버전 {VERSION} ({DATE})",
        "·  Windows 10 / 11 (64비트), .NET 9 내장",
        "·  셀프테스트: PromptAdvisor.exe --selftest",
        "·  라이선스: MIT",
        "·  사용 라이브러리: Microsoft.Data.Sqlite, Velopack (MIT)",
        "·  저장소: github.com/mhbae-semi-lab/prompt-advisor",
        "·  AI 코딩 도구(Claude Code)의 도움을 받아 개발",
    ], 10.5, False, SUB, spacing=1.2)


def back():
    s = new_slide()
    s.background.fill.solid()
    s.background.fill.fore_color.rgb = NAVY
    icon_lines(s, 4.9, 2.3, RGBColor(0x1C, 0x3D, 0x5C), [3.6, 2.4, 3.0])
    box(s, 4.9, 2.3, 0.35, 0.12, fill=TEAL, shape=MSO_SHAPE.ROUNDED_RECTANGLE, radius=0.5)
    s.shapes.add_picture(os.path.join(ASSETS, "icon.png"), Inches(6.2), Inches(3.6), Inches(0.9), Inches(0.9))
    text(s, 0, 5.1, 13.333, 0.6, APP, 30, True, WHITE, PP_ALIGN.CENTER)
    text(s, 0, 5.75, 13.333, 0.4, "보내기 전에 막는 프롬프트 낭비", 13, False, RGBColor(0xC9, 0xD3, 0xE0), PP_ALIGN.CENTER)
    text(s, 0, 6.5, 13.333, 0.3, f"v{VERSION}   ·   만든 사람 배민혁   ·   MIT License   ·   © 2026", 10, False,
         RGBColor(0x8A, 0x97, 0xA8), PP_ALIGN.CENTER)


def export_pdf(pptx):
    pdf = os.path.splitext(pptx)[0] + ".pdf"
    ps = ("$pp = New-Object -ComObject PowerPoint.Application; "
          f"$p = $pp.Presentations.Open('{pptx}', $true, $false, $false); "
          f"$p.SaveAs('{pdf}', 32); $n = $p.Slides.Count; $p.Close(); $pp.Quit(); Write-Output $n")
    r = subprocess.run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True)
    print("pdf", pdf, "slides", r.stdout.strip(), r.stderr.strip()[:200])


def main():
    cover()
    toc()
    intro()
    install()
    first_run()
    analyze()          # 6–10
    history_verify()   # 11–16
    data_privacy()     # 17–20
    faq()              # 21
    limits()           # 22
    back()             # 23
    assert state["n"] == TOTAL, state["n"]
    prs.save(OUT)
    print("saved", OUT)
    if "--pdf" in sys.argv:
        export_pdf(OUT)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
