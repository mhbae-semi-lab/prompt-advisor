namespace PromptAdvisor;

/// <summary>
/// 합성 예시 데이터. 가상 프로젝트 3개(장비 운영 UI · 모션 서비스 · 검사 리포트)의 프롬프트 기록 약 100건과
/// 예측 · 개선 추적 · Shadow 기록을 만든다. 실제 사용자 기록은 쓰지 않는다.
/// 턴 · 정정 수는 "장비 정보가 빠질수록 늘어난다"는 가정으로 만든 값이라, 이 데이터의 수치는 기능 시연용이지 효과 증명이 아니다.
/// </summary>
public static class SampleData
{
    public const string P1 = "wafer-handler-ui", P2 = "stage-motion-svc", P3 = "inspect-report-py";

    // (프로젝트, 출처, 프롬프트) — 출처 cursor 는 토큰이 없는 기록 (실제 Cursor 기록과 같은 모양)
    private static readonly (string Proj, string Src, string Text)[] Prompts =
    {
        // ---- wafer-handler-ui (C# WPF 장비 운영 화면) ----
        (P1, "claude", "로더 화면 알람 뜨는 거 고쳐줘"),
        (P1, "claude", "알람 팝업이 안 닫혀요 확인해줘"),
        (P1, "cursor", "레시피 저장하면 장비가 멈춰요"),
        (P1, "claude", "카세트 맵핑 화면 이상해 고쳐줘"),
        (P1, "claude", "이거 왜 인터락 걸려? 고쳐줘"),
        (P1, "cursor", "장비 시작 버튼 눌러도 반응 없음"),
        (P1, "claude", "알람 이력 화면 느린 거 해결해줘"),
        (P1, "claude", "웨이퍼 카운트가 틀려요 확인 좀"),
        (P1, "claude", "AlarmPanel.xaml.cs 에서 ALM-2041 알람이 뜬 뒤 팝업 닫기 버튼이 안 먹습니다. 확인 부탁드립니다."),
        (P1, "claude", "로더 Z축 원점 복귀 후 LoaderViewModel.cs 상태 표시가 '대기'로 안 바뀝니다. 정상이면 '준비 완료'가 되어야 합니다."),
        (P1, "cursor", "RecipeEditor.cs 레시피 저장 시 파라미터 범위 검사를 추가해줘. 노광 시간은 0~500ms 범위여야 하고, 벗어나면 저장 버튼을 막아야 합니다. 변경 요약과 코드만 주세요."),
        (P1, "claude", "장비 SW v2.3.1 에서 LotHistory.cs 조회가 30초 넘게 걸립니다. 로트 1,000건 이상일 때 매번 재현됩니다. 원인 후보를 표로 정리해줘."),
        (P1, "claude", "EQ-02 로더 카세트 맵핑에서 슬롯 25가 빈 슬롯으로 나옵니다. 카세트를 다시 올릴 때마다 재현됩니다."),
        (P1, "claude", "EQ-01 장비 SW v2.3.1, 로더 Z축. 원점 복귀 직후 MainView.xaml 상태 바가 '이동 중'으로 남습니다.\n로그:\n10:21:03 [INFO] Z homing done\n10:21:03 [WARN] status event dropped\n기대 결과: 상태 바가 '대기'로 바뀌어야 합니다. LoaderViewModel.cs 이벤트 처리만 고치고 변경 요약 → 코드 → 확인 방법 순서로 답해 주세요."),
        (P1, "claude", "EQ-03 장비 SW v2.4.0. 알람 ALM-1107 (도어 인터락) 해제 후 AlarmPanel.xaml.cs 목록에 알람이 남습니다. 도어를 닫고 리셋할 때마다 재현되고, 정상이면 목록에서 사라져야 합니다. 로그: 14:02:11 [INFO] ALM-1107 cleared. 수정 코드와 확인 방법만 주세요."),
        (P1, "cursor", "MainView.xaml 상단 상태 바에 현재 레시피 이름을 표시하는 TextBlock 을 추가해줘. 바인딩은 MainViewModel.CurrentRecipe, 코드만."),
        (P1, "claude", "RecipeEditor.cs 의 Save 메서드에 저장 전 백업 파일(.bak)을 만드는 기능을 추가해줘. 기존 동작은 유지하고 diff 형식으로."),
        (P1, "claude", "LotHistory.cs 에 CSV 내보내기 버튼 핸들러를 추가해줘. 열: 로트 ID, 시작 시각, 종료 시각, 결과. 코드만 주세요."),
        (P1, "claude", "WPF DataGrid 에서 가상화를 켜면 정렬이 풀리는 이유를 3줄로 설명해줘"),
        (P1, "cursor", "이 화면 다크 모드에서 글자가 안 보여"),
        (P1, "claude", "레시피 비교 화면 만들어줘"),
        (P1, "claude", "알람 화면 전부 다시 만들어줘. 필터, 정렬, 색상, 내보내기, 다국어까지 한 번에"),
        (P1, "claude", "EQ-01 장비 SW v2.3.1 에서 레시피 저장 직후 화면이 3초 멈춥니다. 매번 재현.\n로그: 09:40:12 [WARN] UI thread blocked 3012ms (RecipeEditor.Save)\n기대 결과: 저장 중에도 화면이 멈추지 않아야 합니다. RecipeEditor.cs 만 고치고, 변경 요약 → 코드 → 확인 방법 순서로."),
        (P1, "claude", "AlarmPanel 정렬 순서 거꾸로 됨"),
        (P1, "cursor", "로더 상태 아이콘 색이 가끔 안 바뀌어요"),
        (P1, "claude", "LoaderViewModel.cs 에 슬롯별 웨이퍼 존재 여부를 bool[25] 로 노출하는 속성을 추가해줘. 기존 바인딩은 건드리지 말고 코드만."),
        (P1, "claude", "MainView.xaml 의 버튼 탭 순서를 왼쪽 위부터 오른쪽 아래로 정리해줘. TabIndex 만 바꾸고 변경 목록을 표로."),
        (P1, "claude", "장비 연결 끊기면 재연결 되게 해줘"),
        (P1, "claude", "EQ-02 장비 SW v2.3.4, SECS/GEM 연결이 끊긴 뒤 재연결되지 않습니다. 호스트를 재시작할 때 매번 재현됩니다.\n로그:\n11:05:40 [ERROR] HSMS T7 timeout\n11:05:41 [INFO] link state NOT_SELECTED\n기대 결과: 30초 안에 자동 재연결되어야 합니다. 원인 후보 3개를 표로 정리해 주세요."),

        // ---- stage-motion-svc (C++ 모션 제어 서비스) ----
        (P2, "claude", "Z축 원점 복귀 실패하는 거 고쳐줘"),
        (P2, "claude", "서보 알람 자꾸 떠요"),
        (P2, "claude", "스테이지 이동이 느려요 빠르게 해줘"),
        (P2, "cursor", "인터락 테이블 이상함 확인해줘"),
        (P2, "claude", "X축 리미트 센서 걸리는 문제 해결해줘"),
        (P2, "claude", "모션 시퀀스 중간에 멈춰요"),
        (P2, "claude", "Fix the homing timeout on the Z axis"),
        (P2, "claude", "servo alarm keeps coming back, please fix"),
        (P2, "claude", "HomingSequence.cpp 에서 Z축 원점 복귀가 가끔 타임아웃 납니다. 10회 중 2회 정도 재현됩니다."),
        (P2, "claude", "AxisController.cpp 의 MoveAbs 가 목표 위치 도달 전에 완료 이벤트를 보냅니다. 기대 결과는 in-position 신호 뒤에 완료 이벤트가 나가는 것입니다."),
        (P2, "cursor", "InterlockTable.h 에 도어 열림 시 Z축 하강 금지 조건을 추가해줘. 기존 조건 순서는 유지, 코드만."),
        (P2, "claude", "모션 펌웨어 1.8.2, ServoDriver.cpp. Y축 서보 온 직후 알람 ALM-3305 (following error) 가 납니다. 서보 온을 할 때마다 재현.\n로그: 08:14:22 [ERROR] ALM-3305 Y following error 1520 pulse\n기대 결과: 서보 온 뒤 알람 없이 정지 상태여야 합니다. 원인 후보와 확인 순서를 표로."),
        (P2, "claude", "EQ-01 스테이지 X축, 모션 서비스 v3.1.0. 원점 복귀 직후 첫 MoveAbs 가 50um 오차로 끝납니다. 매번 재현.\n로그:\n13:30:01 [INFO] X homing done offset=0\n13:30:02 [WARN] X pos error 50um\n기대 결과: 오차 5um 이내여야 합니다. HomingSequence.cpp 와 AxisController.cpp 만 보고 변경 요약 → 코드 → 확인 방법 순서로."),
        (P2, "claude", "MotionConfig.json 의 가감속 값을 축별로 읽도록 AxisController.cpp 를 바꿔줘. 기존 기본값은 유지하고 diff 로."),
        (P2, "claude", "Z-axis homing on EQ-02 (motion service v3.1.0) times out about 2 of 10 times after a cold start.\nLog:\n07:58:10 [WARN] Z home sensor not found within 8000 ms\nExpected: homing should finish within 5 s. Look at HomingSequence.cpp only and answer as: summary, code, how to verify."),
        (P2, "claude", "Add a unit test for InterlockTable.h that checks the door-open rule. Code only."),
        (P2, "cursor", "축 이동 로그 형식 바꿔줘"),
        (P2, "claude", "std::thread 대신 std::jthread 로 바꾸면 얻는 장점을 짧게 목록으로 정리해줘"),
        (P2, "claude", "모션 서비스 전체 구조 리팩터링해줘. 스레드 구조도 바꾸고 로그도 바꾸고 설정도 다 바꿔줘"),
        (P2, "claude", "아니 그게 아니라 Z축 말고 X축 원점 복귀 문제야"),
        (P2, "claude", "아니 그거 말고 서보 알람 쪽을 봐줘"),
        (P2, "claude", "ServoDriver.cpp 의 알람 리셋 함수가 리셋 후 상태를 다시 읽지 않습니다. 리셋 직후 상태를 읽어 알람이 남았으면 false 를 반환하게 고쳐줘. 코드와 확인 방법만."),
        (P2, "claude", "티칭 위치 저장이 안 돼요"),
        (P2, "claude", "EQ-03 로봇 티칭 위치 저장 시 HomingSequence 가 다시 실행됩니다. 모션 서비스 v3.0.9, 티칭 화면에서 저장을 누를 때마다 재현.\n로그: 16:44:09 [INFO] teach save -> homing requested\n기대 결과: 저장만 하고 원점 복귀는 하지 않아야 합니다. 원인 함수 후보를 표로."),
        (P2, "cursor", "AxisController 에 소프트 리미트 확인 추가"),
        (P2, "claude", "the stage stops in the middle of the sequence"),
        (P2, "claude", "Why does the chuck vacuum alarm show up only after the wafer is loaded? Explain in 3 bullets."),

        // ---- inspect-report-py (Python 검사 결과 리포트) ----
        (P3, "claude", "리포트 차트 이상해 고쳐줘"),
        (P3, "claude", "결함 집계가 안 맞아요"),
        (P3, "cursor", "csv 읽을 때 에러 나요"),
        (P3, "claude", "report_builder.py 에서 결함 유형별 막대 차트의 범례가 잘립니다. figsize 는 유지하고 범례만 바깥으로 빼 주세요. 코드만."),
        (P3, "claude", "csv_loader.py 가 검사 장비 결과 CSV 의 빈 줄에서 ValueError 를 냅니다. 빈 줄은 건너뛰고 몇 번째 줄인지 경고로 남기게 고쳐줘. 변경 요약 → 코드 순서로."),
        (P3, "claude", "defect_summary.py 의 다이별 결함 수 집계를 pandas groupby 로 바꿔줘. 결과 열 순서는 유지, diff 로."),
        (P3, "cursor", "chart_utils.py 에 색약 친화 팔레트 함수 하나 추가해줘. 코드만."),
        (P3, "claude", "검사 장비 결과 파일 v1.4 형식에서 결함 좌표 단위가 um 로 바뀌었습니다. csv_loader.py 에서 단위를 읽어 mm 로 통일하게 해줘. 기대 결과: 리포트 좌표가 이전 버전과 같아야 합니다. 코드와 확인 방법만."),
        (P3, "claude", "templates/report.md 에 로트 요약 표(로트 ID · 검사 수 · 결함 수 · 수율)를 추가해줘. 표 형식으로."),
        (P3, "claude", "리포트 전부 다시 짜줘. PDF 도 만들고 메일도 보내고 차트도 바꾸고"),
        (P3, "claude", "파이썬에서 큰 CSV 를 빠르게 읽는 방법 3가지를 표로 비교해줘"),
        (P3, "claude", "이번 주 검사 결과를 요약해서 보고서 초안 써줘"),
        (P3, "claude", "아니 그게 아니라 수율 계산식이 틀렸다는 거야"),
        (P3, "cursor", "report_builder.py 에서 날짜 축 라벨이 겹칩니다. 45도 회전하고 7일 간격으로만 표시, 코드만."),
        (P3, "claude", "csv_loader.py 에 인코딩 자동 감지 추가해줘 (utf-8, cp949 순서로 시도). 실패하면 파일 이름과 함께 예외. 코드만."),
        (P3, "claude", "결함 맵 그려줘"),
        (P3, "claude", "defect_summary.py 의 수율 계산에서 재검사 웨이퍼가 두 번 세어집니다. 웨이퍼 ID 기준으로 마지막 결과만 쓰게 고쳐줘. 기대 결과: 로트 수율이 장비 화면 값과 같아야 합니다. 변경 요약 → 코드 → 확인 방법."),
        (P1, "claude", "알람 코드 표 화면에서 검색이 안 돼요"),
        (P1, "claude", "레시피 목록 정렬 이상함"),
        (P1, "cursor", "RecipeEditor.cs 에서 레시피 이름 중복 검사를 추가해줘. 대소문자 무시, 중복이면 저장 버튼 비활성화. 코드만."),
        (P1, "claude", "EQ-02 장비 SW v2.3.4 에서 로트 시작 직후 LotHistory 에 같은 로트가 두 번 기록됩니다. 로트를 시작할 때마다 재현.\n로그: 08:30:12 [INFO] lot L2411-07 start (x2)\n기대 결과: 한 번만 기록되어야 합니다. 원인 후보를 표로."),
        (P1, "claude", "MainView.xaml 다크 테마에서 알람 배지 대비가 낮습니다. 배경 #C42B1C, 글자 흰색으로 바꾸고 변경 줄만 diff 로 보여주세요."),
        (P2, "claude", "축 속도 프로파일 바꿔줘"),
        (P2, "claude", "카메라 트리거 타이밍이 안 맞아요"),
        (P2, "cursor", "ServoDriver.cpp 의 상태 폴링 주기를 MotionConfig.json 에서 읽게 바꿔줘. 기본값 10ms 유지, 코드만."),
        (P2, "claude", "EQ-01 Z축 모션 펌웨어 1.8.2, 웨이퍼 로딩 중 비상 정지 후 재시작하면 원점 복귀 없이 이동 명령이 실행됩니다. 비상 정지 후에 매번 재현.\n로그: 10:44:03 [WARN] MoveAbs accepted while not homed\n기대 결과: 원점 복귀 전 이동 명령은 거부되어야 합니다. AxisController.cpp 만 고치고 변경 요약 → 코드 → 확인 방법."),
        (P2, "claude", "Add a homing state check to AxisController::MoveAbs so moves are rejected before homing. Keep the public API unchanged. Code only."),
        (P2, "claude", "인터락 걸렸을 때 로그 좀 더 자세히"),
        (P3, "claude", "수율 그래프 범위 이상해요"),
        (P3, "cursor", "chart_utils.py 의 save_png 함수에 dpi 인자를 추가해줘. 기본 150, 코드만."),
        (P3, "claude", "검사 장비 결과 파일 v1.4 의 결함 코드 표를 defect_summary.py 에 반영해줘. 기존 코드 매핑은 유지하고, 새 코드 3개(D21, D22, D23)만 추가. diff 로."),
        (P3, "claude", "report_builder.py 실행하면 가끔 KeyError 'lot_id' 가 납니다"),
    };

    // 보낸 개선본: (원본 프롬프트, 채운 장비 정보, 대상 파일) — 원본은 위 목록의 짧은 요청
    private static readonly (string Original, string Target, string Version, string Log, string Repro, string Expected, string File)[] Rewrites =
    {
        ("로더 화면 알람 뜨는 거 고쳐줘", "EQ-01 로더 Z축", "장비 SW v2.3.1", "09:12:44 [ERROR] ALM-1203 Z following error", "카세트 로드 직후 매번", "알람 없이 로드 완료 상태가 되어야 함", "LoaderViewModel.cs"),
        ("알람 팝업이 안 닫혀요 확인해줘", "EQ-01 알람 패널", "장비 SW v2.3.1", "15:20:02 [WARN] AlarmPopup close ignored", "ALM-2041 발생 후 닫기를 누를 때마다", "닫기를 누르면 팝업이 사라져야 함", "AlarmPanel.xaml.cs"),
        ("카세트 맵핑 화면 이상해 고쳐줘", "EQ-02 로더 카세트 포트 1", "장비 SW v2.3.4", "10:02:17 [WARN] slot 25 map=EMPTY", "카세트를 다시 올릴 때마다", "슬롯 25에 웨이퍼가 있으면 PRESENT 로 표시되어야 함", "LoaderViewModel.cs"),
        ("이거 왜 인터락 걸려? 고쳐줘", "EQ-03 도어 인터락 모듈", "장비 SW v2.4.0", "14:01:55 [ERROR] ALM-1107 door interlock", "도어를 닫고 리셋할 때 10회 중 3회", "리셋 후 인터락이 풀려야 함", "AlarmPanel.xaml.cs"),
        ("알람 이력 화면 느린 거 해결해줘", "EQ-01 알람 이력 화면", "장비 SW v2.3.1", "09:31:40 [WARN] query 28.4 s (AlarmHistory)", "알람 5,000건 이상일 때 매번", "3초 안에 조회되어야 함", "LotHistory.cs"),
        ("Z축 원점 복귀 실패하는 거 고쳐줘", "EQ-02 스테이지 Z축", "모션 서비스 v3.1.0", "07:58:10 [WARN] Z home sensor not found", "전원 투입 직후 10회 중 2회", "5초 안에 원점 복귀가 끝나야 함", "HomingSequence.cpp"),
        ("서보 알람 자꾸 떠요", "EQ-01 Y축 서보", "모션 펌웨어 1.8.2", "08:14:22 [ERROR] ALM-3305 Y following error", "서보 온을 할 때마다", "서보 온 뒤 알람 없이 정지해야 함", "ServoDriver.cpp"),
        ("X축 리미트 센서 걸리는 문제 해결해줘", "EQ-01 스테이지 X축 리미트 센서", "모션 서비스 v3.1.0", "12:10:05 [ERROR] ALM-3102 X +limit", "고속 이동(500mm/s) 후에 가끔", "소프트 리미트에서 먼저 멈춰야 함", "AxisController.cpp"),
        ("모션 시퀀스 중간에 멈춰요", "EQ-03 로딩 모션 시퀀스", "모션 서비스 v3.0.9", "16:02:33 [WARN] step 7 wait timeout", "카세트 교체 직후 매번", "시퀀스가 끝까지 진행되어야 함", "HomingSequence.cpp"),
        ("티칭 위치 저장이 안 돼요", "EQ-03 로봇 티칭 화면", "모션 서비스 v3.0.9", "16:44:09 [INFO] teach save -> homing requested", "저장을 누를 때마다", "저장만 하고 원점 복귀는 하지 않아야 함", "HomingSequence.cpp"),
        ("Fix the homing timeout on the Z axis", "EQ-02 stage Z-axis", "motion service v3.1.0", "07:58:10 [WARN] Z home sensor not found within 8000 ms", "after a cold start, about 2 of 10 times", "homing should finish within 5 s", "HomingSequence.cpp"),
        ("결함 집계가 안 맞아요", "EQ-03 검사 장비 결과 파일 v1.4", "리포트 도구 v0.9.2", "11:20:41 [WARN] duplicate wafer W07", "재검사 웨이퍼가 있는 로트에서 매번", "로트 수율이 장비 화면 값과 같아야 함", "defect_summary.py"),
    };

    /// <summary>검사용 예시 재현 세트 (DB 없이 검증 탭에서 바로 쓰는 합성 10건).</summary>
    public static readonly string[] ExampleSet =
    {
        "로더 화면 알람 뜨는 거 고쳐줘",
        "Z축 원점 복귀 실패하는 거 고쳐줘",
        "서보 알람 자꾸 떠요",
        "이거 왜 인터락 걸려? 고쳐줘",
        "Fix the homing timeout on the Z axis",
        "HomingSequence.cpp 에서 Z축 원점 복귀가 가끔 타임아웃 납니다. 10회 중 2회 정도 재현됩니다.",
        "EQ-02 로더 카세트 맵핑에서 슬롯 25가 빈 슬롯으로 나옵니다. 카세트를 다시 올릴 때마다 재현됩니다.",
        "RecipeEditor.cs 레시피 저장 시 파라미터 범위 검사를 추가해줘. 노광 시간은 0~500ms 범위여야 하고, 벗어나면 저장 버튼을 막아야 합니다. 변경 요약과 코드만 주세요.",
        "report_builder.py 에서 결함 유형별 막대 차트의 범례가 잘립니다. figsize 는 유지하고 범례만 바깥으로 빼 주세요. 코드만.",
        "EQ-01 스테이지 X축, 모션 서비스 v3.1.0. 원점 복귀 직후 첫 MoveAbs 가 50um 오차로 끝납니다. 매번 재현.\n로그:\n13:30:01 [INFO] X homing done offset=0\n13:30:02 [WARN] X pos error 50um\n기대 결과: 오차 5um 이내여야 합니다. HomingSequence.cpp 와 AxisController.cpp 만 보고 변경 요약 → 코드 → 확인 방법 순서로.",
    };

    public static string ImprovedText((string Original, string Target, string Version, string Log, string Repro, string Expected, string File) r)
    {
        bool ko = Advisor.IsKorean(r.Original);
        return ko
            ? $"[작업]\n{r.Original}\n\n[장비 정보]\n- 대상 장비·모듈: {r.Target}\n- SW·펌웨어 버전: {r.Version}\n- 로그 발췌:\n{r.Log}\n- 재현 조건: {r.Repro}\n- 기대 결과: {r.Expected}\n\n[조건]\n- {r.File} 중심으로 확인·수정하고 관련 없는 파일은 건드리지 마세요\n\n[출력 형식]\n- 1) 변경 요약 2) 수정 코드 3) 확인 방법 순서로"
            : $"[Task]\n{r.Original}\n\n[Equipment info]\n- Target equipment/module: {r.Target}\n- SW/firmware version: {r.Version}\n- Log excerpt:\n{r.Log}\n- Repro conditions: {r.Repro}\n- Expected result: {r.Expected}\n\n[Conditions]\n- Work in {r.File} only\n\n[Output format]\n- 1) summary of the change 2) code 3) how to verify";
    }

    /// <summary>예시 DB 를 새로 만든다 (이미 있으면 지우고 다시). --demo 로 실행할 때마다 같은 내용으로 초기화된다.</summary>
    public static void CreateDemoDb(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
        using var store = new Store(dbPath);
        Fill(store, DateTime.Now);
    }

    public static void Fill(Store store, DateTime now)
    {
        var rnd = new Random(20261121);
        var cfg = new RuleConfig();
        var rows = new List<PromptRow>();
        var start = now.Date.AddDays(-27).AddHours(9);
        int session = 0, inSession = 0;
        DateTime t = start;

        PromptRow Make(string proj, string src, string text, int extraFail = 0)
        {
            if (inSession == 0 || inSession >= 3 + rnd.Next(3)) { session++; inSession = 0; t = start.AddDays(rows.Count * 27.0 / 110).AddMinutes(rnd.Next(0, 240)); }
            inSession++;
            t = t.AddMinutes(4 + rnd.Next(18));
            var lints = Advisor.LintRewrite(text, cfg);
            int miss = lints.Count(l => l.Id.StartsWith("eq-"));
            bool vague = lints.Any(l => l.Id == "vague");
            bool noFormat = lints.Any(l => l.Id == "no-format");
            string task = Advisor.Classify(text);
            // 합성 가정: 장비 정보가 빠질수록, 범위가 모호할수록 턴과 정정이 늘어난다
            int turns = Math.Max(1, (int)Math.Round(2 + miss * 1.4 + (vague ? 2 : 0) + (noFormat ? 0.6 : 0) + rnd.NextDouble() * 1.6 - 0.8));
            int fail = (miss >= 4 ? 1 : 0) + (vague ? 1 : 0) + (miss >= 2 && rnd.NextDouble() < 0.35 ? 1 : 0) + extraFail;
            long outTok = src == "cursor" ? 0 : (long)(Advisor.BaseOutputForTask(task) * (0.55 + 0.22 * turns) * (0.85 + rnd.NextDouble() * 0.3));
            long inTok = src == "cursor" ? 0 : (long)((5200 + 2600 * turns) * (0.85 + rnd.NextDouble() * 0.3));
            var r = new PromptRow
            {
                Source = src, Session = $"demo-{session:D3}", Project = proj, Text = text,
                CreatedAt = t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                InTok = inTok, OutTok = outTok, CacheTok = inTok * 3, SegTurns = turns, Fail = fail,
            };
            rows.Add(r);
            return r;
        }

        foreach (var (proj, src, text) in Prompts) Make(proj, src, text);
        // 보낸 개선본 (같은 프로젝트에서 나중에 보낸 것으로)
        var sentRewrites = new List<(string Original, string Improved, string When)>();
        foreach (var rw in Rewrites)
        {
            string proj = Prompts.First(p => p.Text == rw.Original).Proj;
            var improved = ImprovedText(rw);
            var row = Make(proj, "claude", improved);
            sentRewrites.Add((rw.Original, improved, row.CreatedAt));
        }
        store.InsertMany(rows);
        Advisor.ComputeLabels(store);

        var all = store.All();
        // 재현 세트 · 즐겨찾기 표시
        foreach (var p in all.Where(p => p.Text.Length < 40 && Equipment.IsEquipmentRequest(p.Text)).Take(8)) store.SetRepro(p.Id, true);
        foreach (var p in all.Where(p => p.Text.Contains("EQ-01 장비 SW v2.3.1, 로더 Z축") || p.Text.StartsWith("ServoDriver.cpp") || p.Text.StartsWith("csv_loader.py 가"))) store.SetFavorite(p.Id, true);

        // 예측 기록 (분석 때 저장된 예상값) — 실측과 이어져 보정 표본이 된다
        var models = Advisor.LoadModels();
        string modelName = models.FirstOrDefault()?.Name ?? "";
        foreach (var p in all.Where(p => p.Source == "claude" && p.OutTok > 0).Take(16))
        {
            string task = Advisor.Classify(p.Text);
            double ratio = 0.62 + rnd.NextDouble() * 0.9; // 실제/예상 비
            store.SavePrediction(p.Text, task, (long)(p.InTok / (0.8 + rnd.NextDouble() * 0.45)), (long)(p.OutTok / ratio), modelName);
        }
        store.MatchPredictions();

        // 개선 전/후 추적
        foreach (var (orig, improved, when) in sentRewrites)
            store.SaveRewrite(orig, improved, rnd.NextDouble() < 0.25 ? "llm" : "rule",
                Advisor.Lint(orig, null, cfg).Select(l => l.Id), Advisor.LintRewrite(improved, cfg).Select(l => l.Id), when);
        store.MatchRewrites();

        // 개선안 Shadow 기록 (규칙안이 결정, LLM안은 옆에서만)
        for (int i = 0; i < 14; i++)
        {
            bool onTopic = i != 5;
            int judge = onTopic ? 62 + rnd.Next(26) : 41;
            int ruleL = rnd.Next(0, 2), llmL = onTopic ? rnd.Next(0, 3) : 3;
            store.AddShadow($"demo{i}", modelName, onTopic, judge, ruleL, llmL, "rule",
                onTopic ? (llmL <= ruleL ? "LLM안 경고 수 같거나 적음" : "LLM안 경고 더 많음") : "주제 이탈 → 규칙안 사용",
                now.AddDays(-14 + i).ToString("s"));
        }
        store.SaveConfigHistory(System.Text.Json.JsonSerializer.Serialize(cfg), L.T("초기 설정", "Initial settings"));
    }
}
