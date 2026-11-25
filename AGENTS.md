# Prompt Advisor — 에이전트 공통 맥락

> Cursor · Claude Code · Codex 등 코딩 에이전트가 세션 시작 시 읽는 상시 맥락. 사람이 유지한다.

## 한 줄 요약

AI 코딩 도구에 보내기 전에 프롬프트를 점검하는 Windows 앱 (WPF, .NET 9).
내 기록(토큰 · 턴 · 정정)으로 배운 경고, 반도체 장비 SW 요청 린트(한/영), 토큰 예측 · 보정, 규칙 개선안 + 로컬 LLM Shadow, 현장 검증 절차(재현 → 비교 → Shadow → 전환 · 롤백).
데이터는 `%LOCALAPPDATA%\prompt-advisor-data` 에만 둔다 (`--data` 로 바꿀 수 있음).

## 구조 (단일 프로젝트, 코드비하인드, MVVM/DI 없음)

| 파일 | 역할 |
|---|---|
| `Program.cs` | 진입점. Velopack 훅 → `--data` · `--demo` · `--lang` 처리 → App |
| `App.xaml.cs` | 설정, 전역 예외 처리(crash.log), 헤드리스 모드(`--selftest` `--mcp` `--measure` `--update` `--import` `--export-knowledge`) |
| `MainWindow.*.cs` | 화면: 분석 · 개선안 / 이력 · 가져오기 · 설정 / 검증 · 도움말 / 화면 촬영(`--shots`) |
| `Advisor.cs` | 린트 · 분류 · 토큰 추정 · 이웃 검색 · 모델 추천 · 개선안 · 마스킹 · 지식 내보내기 |
| `Domain.cs` | `RuleConfig`, 장비 SW 린트(`Equipment`), 내 기록 기반 규칙 영향(`RuleImpact`) |
| `Verification.cs` | 재현 · 비교 · Shadow 전환 기준 · 개선 추적 · 측정 보고 |
| `Importers.cs` | Claude Code jsonl · Cursor vscdb · claude.ai zip · CSV → `ImportReport`(줄 번호 경고, 형식 변경 감지) |
| `Store.cs` | SQLite. `PRAGMA user_version` 스키마 버전 + 단계별 마이그레이션 |
| `SampleData.cs` | 합성 예시 DB (가상 프로젝트 3개, 100건) |
| `Loc.cs` · `Loc.Strings.cs` | 한/영. 코드는 `L.T("한", "en")`, XAML 은 번역표 |
| `Updater.cs` | Velopack + GitHub Releases 업데이트 |

## 확정된 결정

- 파인튜닝 안 함. 학습은 임베딩 검색(bge-m3) + 통계 보정 + 규칙으로 대체. 생성 기본값 `bonsai-2.5:27b` (2026-11-25, 이전 `exaone3.5:2.4b`). GGUF + Modelfile 로 만들며 pull 불가 — README Ollama 절.
- 규칙이 먼저 결정하고 LLM 개선안은 Shadow. 주제 이탈 · 심사 60점 미만이면 규칙안. 전환은 Shadow 기준(표본 10 · 주제 일치 90% · 평균 심사 70) 통과 시에만.
- 보정: 표본 3건 미만이면 하지 않음, 0.5~2.0 배 제한. 규칙 영향 수치도 양쪽 3건 이상일 때만.
- 개선안 뒤 경고 수는 `Advisor.LintRewrite` 로 센다 (템플릿 문구가 답으로 잡히지 않게).
- LLM 에 보내기 전 `MaskSensitive`. 지식 카드에는 원문 · 프로젝트 이름을 넣지 않는다.
- 설치 폴더(`%LOCALAPPDATA%\prompt-advisor`)와 데이터 폴더를 나눈다 (제거해도 데이터 유지).

## 작업 규칙

- 응답 · 문서 · 주석은 한국어. 화면 문자열은 한/영 모두 (XAML 에 한글을 더하면 `Loc.Strings.cs` 에도).
- `dotnet build -c Release` 경고/오류 0, `PromptAdvisor.exe --selftest --data <임시폴더>` 전부 PASS (종료 코드 = 실패 수).
- 기능을 바꾸면 `SelfTest.cs` 에 항목을 더한다.
- 화면 촬영은 `--shots` (합성 예시 DB 만). 실제 기록이 화면 · 저장소에 들어가지 않게 한다.
- 새 NuGet 은 꼭 필요할 때만 (지금: Microsoft.Data.Sqlite, Velopack).
- 배포물에 `.bat` 을 넣지 않는다.

## 하지 말 것

- `*.db` · 프롬프트 원문 · `docs/STATUS.md` · `docs/knowledge/` 를 커밋하지 않는다.
- `bin/` · `obj/` · `publish/` · `releases/` 를 커밋하지 않는다.
