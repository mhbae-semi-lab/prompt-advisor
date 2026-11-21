using System.Text.RegularExpressions;

namespace PromptAdvisor;

/// <summary>린트 규칙 설정. 검증 탭에서 재현 세트로 전/후를 비교한 뒤 전환하고, 이력에서 롤백한다.</summary>
public sealed record RuleConfig
{
    public bool EquipmentLint { get; init; } = true;   // 장비 SW 요청 린트 (대상 장비 · 버전 · 로그 · 재현 조건 · 기대 결과)
    public bool FormatLint { get; init; } = true;      // 출력 형식 미지정 경고
    public int VagueMaxChars { get; init; } = 60;      // 이 길이 미만의 "이거 고쳐줘"류를 범위 불명확으로
    public int LargePasteChars { get; init; } = 8000;  // 대량 붙여넣기 기준
    public int LogLinesMin { get; init; } = 5;         // 로그 원문 경고 기준 줄 수
    public bool LlmPrimary { get; init; }              // Shadow 전환: true 면 검증을 통과한 LLM 개선안을 먼저 채택

    public string Describe() => L.T(
        $"장비 린트 {(EquipmentLint ? "켬" : "끔")} · 형식 린트 {(FormatLint ? "켬" : "끔")} · 범위 불명확 <{VagueMaxChars}자 · 대량 붙여넣기 >{LargePasteChars:N0}자 · 로그 {LogLinesMin}줄+ · 개선안 {(LlmPrimary ? "LLM 우선" : "규칙 우선")}",
        $"equipment lint {(EquipmentLint ? "on" : "off")} · format lint {(FormatLint ? "on" : "off")} · vague <{VagueMaxChars} chars · large paste >{LargePasteChars:N0} · log {LogLinesMin}+ lines · rewrite {(LlmPrimary ? "LLM first" : "rules first")}");
}

/// <summary>
/// 반도체 장비 SW 요청 린트 (한/영). 장비 · 설비 맥락의 요청에서 대상 장비/모듈, SW 버전, 로그 발췌,
/// 재현 조건, 기대 결과가 빠졌는지 본다. 빠진 항목은 개선안의 [장비 정보] 칸으로 채우게 한다.
/// </summary>
public static class Equipment
{
    public sealed record Element(string Id, string Ko, string En, Regex Present, int Severity, long DefaultWaste, string HintKo, string HintEn);

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // 강한 신호(2점)와 약한 신호(1점). 2점 이상이면 장비 SW 요청으로 본다 (예: "stage" 한 단어만으로는 판단하지 않음).
    private static readonly Regex Strong = new(
        @"장비|설비|알람|인터락|웨이퍼|EFEM|SECS|\bGEM\b|HSMS|\bPLC\b|서보|티칭|원점\s?복귀|검사기|로더|언로더|챔버|카세트|" +
        @"\bequipment\b|\balarms?\b|\binterlocks?\b|\bwafers?\b|\bservo\b|\bhoming\b|\bcassette\b|\bchamber\b|\bteaching\b", Ci);
    private static readonly Regex Weak = new(
        @"레시피|스테이지|모션|[XYZTR]\s?축|척\b|비전|카메라|조명|핸들러|캘리브레이션|펌웨어|센서|시퀀스|" +
        @"\brecipes?\b|\bstage\b|\bmotion\b|\b[XYZ][- ]?axis\b|\baxis\b|\bchuck\b|\bvision\b|\bcamera\b|\bhandler\b|\bcalibration\b|\bfirmware\b|\bsensors?\b|\bloader\b", Ci);

    public static readonly Element[] Elements =
    {
        new("eq-target", "대상 장비·모듈", "Target equipment/module",
            new(@"\b[A-Z]{2,5}[-_]?\d{1,3}\b|스테이지|[XYZTR]\s?축|로더|언로더|EFEM|로봇|카메라|조명|핸들러|척|포트\s?\d|챔버|모듈|유닛|컨트롤러|센서|" +
                @"\bstage\b|\b[XYZ][- ]?axis\b|\bloader\b|\bunloader\b|\brobot\b|\bcamera\b|\bhandler\b|\bchuck\b|\bchamber\b|\bmodule\b|\bunit\b|\bcontroller\b|\bsensor\b|\bport\s?\d", Ci),
            2, 1500, "(예: 로더 Z축, EQ-02 검사 카메라)", "(e.g. loader Z-axis, EQ-02 inspection camera)"),
        new("eq-version", "SW·펌웨어 버전", "SW/firmware version",
            new(@"\b(?:v|ver\.?|rev\.?)\s?\d+(?:\.\d+)*\b|\b\d+\.\d+\.\d+\b|버전|version|빌드\s?(?:번호)?\s?\d+|build\s?(?:no\.?|number|#)?\s?\d+|커밋\s?[0-9a-f]{7,}|commit\s?[0-9a-f]{7,}", Ci),
            1, 600, "(예: 장비 SW v2.3.1, 모션 펌웨어 1.8)", "(e.g. tool SW v2.3.1, motion firmware 1.8)"),
        new("eq-log", "로그 발췌", "Log excerpt",
            new(@"Exception|StackTrace|\bat [\w\.]+\(|\b\d{2}:\d{2}:\d{2}\b|\b(?:ALM|ALARM|ERR|E)[-_ ]?\d{3,5}\b|알람\s?(?:코드\s?)?\d{2,}|```|로그\s?발췌|log excerpt|\[(?:ERROR|WARN|INFO)\]", Ci),
            2, 1200, "(알람 전후 5~10줄, 시각 포함)", "(5–10 lines around the alarm, with timestamps)"),
        new("eq-repro", "재현 조건", "Repro conditions",
            new(@"재현|조건에서|할 때|일 때|때마다|하는 중|하던 중|직후|후에|단계에서|매번|간헐|가끔|\d+\s?회\s?중|\d+번에\s?한\s?번|" +
                @"\bwhen\b|\bafter\b|\bwhile\b|every time|intermittent|sometimes|steps to reproduce|\brepro", Ci),
            2, 1500, "(어떤 동작 · 레시피 · 순서에서 몇 번 중 몇 번)", "(which action, recipe and sequence; how often)"),
        new("eq-expected", "기대 결과", "Expected result",
            new(@"기대|원래는|정상이면|정상적으로는|되어야|돼야|해야\s?하는데|이어야|나와야|[가-힣]야\s?(?:함|합니다|한다|하고|하는데|해요)|\bexpected\b|\bshould\b|supposed to|instead of|\bdesired\b", Ci),
            1, 800, "(정상이면 어떻게 동작해야 하는지)", "(what should happen when it works)"),
    };

    /// <summary>장비 SW 요청 점수 (강한 신호 2, 약한 신호 1). 2 이상이면 장비 요청.</summary>
    public static int Score(string text) => Strong.Matches(text).Count * 2 + Weak.Matches(text).Count;
    public static bool IsEquipmentRequest(string text) => Score(text) >= 2;

    /// <summary>빠진 요소 목록. 장비 요청이 아니면 빈 목록.</summary>
    public static List<Element> Missing(string text) =>
        IsEquipmentRequest(text) ? Elements.Where(e => !e.Present.IsMatch(text)).ToList() : new();

    public static string Label(Element e) => L.T(e.Ko, e.En);
}

/// <summary>
/// "내 기록으로 학습한 경고": 가져온 기록에서 규칙별로 경고가 뜬 요청과 안 뜬 요청의 정정 · 턴 · 토큰을 비교한다.
/// 표본이 양쪽 모두 3건 이상일 때만 수치를 쓰고, 부족하면 규칙 기본값을 쓴다.
/// </summary>
public static class RuleImpact
{
    public sealed record Stat(string RuleId, int WithN, int WithoutN, double WithFail, double WithoutFail,
        double WithTurns, double WithoutTurns, long WithTok, long WithoutTok, int TokN)
    {
        public bool Enough => WithN >= 3 && WithoutN >= 3;
        public long ExtraTokens => TokN >= 3 && WithTok > WithoutTok ? WithTok - WithoutTok : 0;
        public double ExtraTurns => WithTurns - WithoutTurns;
    }

    private static Dictionary<string, Stat> _cache = new();
    private static (int, long, int) _cacheKey = (-1, -1, -1);
    private static RuleConfig? _cacheCfg;

    public static Dictionary<string, Stat> Compute(IReadOnlyList<PromptRow> rows, RuleConfig cfg)
    {
        var hits = rows.Select(r => Advisor.LintRewrite(r.Text, cfg).Select(l => l.Id).ToHashSet()).ToList();
        var ids = hits.SelectMany(h => h).Distinct().ToList();
        var result = new Dictionary<string, Stat>();
        foreach (var id in ids)
        {
            var with = new List<PromptRow>(); var without = new List<PromptRow>();
            for (int i = 0; i < rows.Count; i++) (hits[i].Contains(id) ? with : without).Add(rows[i]);
            static double Avg(List<PromptRow> l, Func<PromptRow, double> f) => l.Count == 0 ? 0 : l.Average(f);
            var wt = with.Where(r => r.TotalTok > 0).ToList(); var wot = without.Where(r => r.TotalTok > 0).ToList();
            result[id] = new Stat(id, with.Count, without.Count,
                Avg(with, r => r.Fail), Avg(without, r => r.Fail),
                Avg(with, r => r.SegTurns), Avg(without, r => r.SegTurns),
                (long)Avg(wt, r => r.TotalTok), (long)Avg(wot, r => r.TotalTok), Math.Min(wt.Count, wot.Count));
        }
        return result;
    }

    /// <summary>스토어 기준 캐시 (행 수 · 최대 id · 설정이 같으면 재계산하지 않음).</summary>
    public static Dictionary<string, Stat> For(Store store, RuleConfig cfg)
    {
        var ver = store.GetCorpusVersion();
        if (ver == _cacheKey && cfg == _cacheCfg) return _cache;
        _cache = Compute(store.All(), cfg);
        _cacheKey = ver; _cacheCfg = cfg;
        return _cache;
    }

    public static void Invalidate() => _cacheKey = (-1, -1, -1);

    public static string Describe(Stat s) => L.T(
        $"내 기록: 이 경고가 뜬 요청 {s.WithN}건은 평균 정정 {s.WithFail:F1}회 · {s.WithTurns:F1}턴, 안 뜬 요청 {s.WithoutN}건은 {s.WithoutFail:F1}회 · {s.WithoutTurns:F1}턴",
        $"Your history: {s.WithN} requests with this warning averaged {s.WithFail:F1} corrections · {s.WithTurns:F1} turns; {s.WithoutN} without it averaged {s.WithoutFail:F1} · {s.WithoutTurns:F1}");
}
