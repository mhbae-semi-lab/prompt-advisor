using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PromptAdvisor;

public sealed class PromptRow
{
    public long Id;
    public string Source = "", Session = "", Project = "", Text = "", CreatedAt = "";
    public long InTok, OutTok, CacheTok;
    public int SegTurns, Fail;
    public double Score;
    public int Label;            // 0=없음, 1=👍, -1=👎 (수동 보정이 자동 라벨보다 우선)
    public string AutoLabel = ""; // good / bad / ""
    public float[]? Embedding;
    public bool Favorite;
    public bool Repro;           // 재현 세트에 넣은 사례 (검증 탭)
    public long TotalTok => InTok + OutTok;
    public bool IsGood => Label == 1 || (Label == 0 && AutoLabel == "good");
    public bool IsBad => Label == -1 || (Label == 0 && AutoLabel == "bad");
    public string EffectiveLabel => Label == 1 ? L.T("최적", "Best") : Label == -1 ? L.T("최악", "Worst")
        : AutoLabel == "good" ? L.T("최적(자동)", "Best (auto)") : AutoLabel == "bad" ? L.T("최악(자동)", "Worst (auto)") : "-";
    public string DisplayProject => string.IsNullOrWhiteSpace(Project) ? "—" : Project;
    public string Preview => Text.Length > 90 ? Text[..90].Replace("\n", " ") + "…" : Text.Replace("\n", " ");
    public string DisplaySource => Source switch
    {
        "claude" => "Claude Code", "cursor" => "Cursor", "antigravity" => "Antigravity",
        "claude.ai" => "claude.ai", "genspark" => "Genspark", "paste" => L.T("붙여넣기", "Pasted"), "csv" => "CSV", _ => Source,
    };
    public string DisplayTime
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CreatedAt)) return "—";
            return DateTime.TryParse(CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToLocalTime().ToString("MM-dd HH:mm") : "—";
        }
    }
}

public sealed class Store : IDisposable
{
    /// <summary>--data 또는 PROMPTADVISOR_DATA 로 바꿀 수 있다. 기본은 %LOCALAPPDATA%\prompt-advisor-data (설치 폴더와 분리 — 제거해도 데이터는 남는다).</summary>
    public static string? DataDirOverride { get; set; }
    public static string DataDir => DataDirOverride
        ?? (Environment.GetEnvironmentVariable("PROMPTADVISOR_DATA") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "prompt-advisor-data"));
    public static string DbPath => Path.Combine(DataDir, "advisor.db");
    /// <summary>예전 개인판(PromptAdvisor) 데이터 위치 — 설정 탭 [이전 데이터 가져오기] 버튼을 누를 때만 읽는다.</summary>
    public static string LegacyDbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptAdvisor", "advisor.db");

    /// <summary>화면 표시용 경로: 사용자 폴더 부분을 %LOCALAPPDATA% · %USERPROFILE% 로 바꿔 사용자명이 보이지 않게 한다.</summary>
    public static string DisplayPath(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (local.Length > 0 && path.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return "%LOCALAPPDATA%" + path[local.Length..];
        if (home.Length > 0 && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return "%USERPROFILE%" + path[home.Length..];
        return path;
    }

    /// <summary>현재 스키마 버전 (PRAGMA user_version). 올릴 때는 Migrate 에 단계를 추가한다.</summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _db;
    public int OpenedFromVersion { get; private set; }

    public Store(string? dbPath = null)
    {
        var path = dbPath ?? DbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Migrate();
    }

    private int Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private bool HasColumn(string table, string col) => Scalar($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{col}'") > 0;

    /// <summary>단계별 스키마 마이그레이션. 버전 0 = 이전 개인판(컬럼을 그때그때 추가하던 DB). 새 앱보다 높은 버전이면 예외.</summary>
    private void Migrate()
    {
        int v = Scalar("PRAGMA user_version");
        OpenedFromVersion = v;
        if (v > SchemaVersion)
            throw new InvalidOperationException(L.T(
                $"이 DB 는 더 새 버전의 앱(스키마 {v})에서 만들어졌습니다. 앱을 업데이트하세요.",
                $"This database was created by a newer app version (schema {v}). Please update the app."));
        using var tx = _db.BeginTransaction();
        if (v < 1)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS prompts(
                  id INTEGER PRIMARY KEY, source TEXT, session TEXT, text TEXT, created_at TEXT,
                  in_tok INTEGER, out_tok INTEGER, cache_tok INTEGER, seg_turns INTEGER, fail INTEGER,
                  score REAL DEFAULT 0, label INTEGER DEFAULT 0, auto_label TEXT DEFAULT '',
                  embedding BLOB, hash TEXT UNIQUE);
                CREATE INDEX IF NOT EXISTS ix_prompts_source ON prompts(source);
                CREATE TABLE IF NOT EXISTS predictions(
                  id INTEGER PRIMARY KEY, hash TEXT, task TEXT, est_in INTEGER, est_out INTEGER,
                  model TEXT, created_at TEXT, actual_in INTEGER, actual_out INTEGER, prompt_id INTEGER);
                CREATE TABLE IF NOT EXISTS errors(
                  id INTEGER PRIMARY KEY, created_at TEXT, stage TEXT, message TEXT);
                CREATE TABLE IF NOT EXISTS template_versions(
                  id INTEGER PRIMARY KEY, prompt_id INTEGER, text TEXT, created_at TEXT, note TEXT);
                """);
            if (!HasColumn("predictions", "text")) Exec("ALTER TABLE predictions ADD COLUMN text TEXT DEFAULT ''");
            if (!HasColumn("prompts", "favorite")) Exec("ALTER TABLE prompts ADD COLUMN favorite INTEGER DEFAULT 0");
            if (!HasColumn("prompts", "project")) Exec("ALTER TABLE prompts ADD COLUMN project TEXT DEFAULT ''");
            Exec("CREATE INDEX IF NOT EXISTS ix_prompts_project ON prompts(project)");
        }
        if (v < 2)
        {
            // 재현 세트 표시, 개선 전/후 추적, 개선안 Shadow 기록, 규칙 설정 이력
            if (!HasColumn("prompts", "repro")) Exec("ALTER TABLE prompts ADD COLUMN repro INTEGER DEFAULT 0");
            Exec("""
                CREATE TABLE IF NOT EXISTS rewrites(
                  id INTEGER PRIMARY KEY, created_at TEXT, original TEXT, improved TEXT, kind TEXT,
                  rules_before TEXT, rules_after TEXT, hash TEXT,
                  prompt_id INTEGER, actual_turns INTEGER, actual_fail INTEGER, actual_tok INTEGER);
                CREATE TABLE IF NOT EXISTS shadow_log(
                  id INTEGER PRIMARY KEY, created_at TEXT, prompt_hash TEXT, model TEXT,
                  on_topic INTEGER, judge INTEGER, rule_lints INTEGER, llm_lints INTEGER, decided TEXT, note TEXT);
                CREATE TABLE IF NOT EXISTS config_history(
                  id INTEGER PRIMARY KEY, created_at TEXT, json TEXT, note TEXT);
                """);
        }
        Exec($"PRAGMA user_version = {SchemaVersion}");
        tx.Commit();
    }

    public static string HashOf(string source, string session, string createdAt, string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{session}|{createdAt}|{text}")));

    /// <summary>중복(hash) 무시하고 삽입하되, 기존 행에 project가 비어 있으면 채워 넣는다. 실제 신규 삽입 건수 반환.</summary>
    public int InsertMany(IEnumerable<PromptRow> rows)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO prompts " +
            "(source,session,project,text,created_at,in_tok,out_tok,cache_tok,seg_turns,fail,label,hash) " +
            "VALUES ($s,$se,$pr,$t,$c,$i,$o,$ca,$st,$f,$l,$h)";
        using var updProj = _db.CreateCommand();
        updProj.CommandText = "UPDATE prompts SET project=$pr WHERE hash=$h AND (project IS NULL OR project='') AND $pr<>''";
        foreach (var p in new[] { "s", "se", "pr", "t", "c", "i", "o", "ca", "st", "f", "l", "h" })
            cmd.Parameters.Add(new SqliteParameter("$" + p, null));
        updProj.Parameters.Add(new SqliteParameter("$pr", null));
        updProj.Parameters.Add(new SqliteParameter("$h", null));
        int n = 0;
        foreach (var r in rows)
        {
            var h = HashOf(r.Source, r.Session, r.CreatedAt, r.Text);
            var proj = r.Project ?? "";
            cmd.Parameters["$s"].Value = r.Source; cmd.Parameters["$se"].Value = r.Session;
            cmd.Parameters["$pr"].Value = proj;
            cmd.Parameters["$t"].Value = r.Text; cmd.Parameters["$c"].Value = r.CreatedAt;
            cmd.Parameters["$i"].Value = r.InTok; cmd.Parameters["$o"].Value = r.OutTok;
            cmd.Parameters["$ca"].Value = r.CacheTok; cmd.Parameters["$st"].Value = r.SegTurns;
            cmd.Parameters["$f"].Value = r.Fail;
            cmd.Parameters["$l"].Value = r.Label;
            cmd.Parameters["$h"].Value = h;
            int added = cmd.ExecuteNonQuery();
            n += added;
            if (added == 0 && proj.Length > 0)
            {
                updProj.Parameters["$pr"].Value = proj;
                updProj.Parameters["$h"].Value = h;
                updProj.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return n;
    }

    private static PromptRow Read(SqliteDataReader rd, bool withEmbedding)
    {
        var r = new PromptRow
        {
            Id = rd.GetInt64(0), Source = rd.GetString(1), Session = rd.GetString(2),
            Text = rd.GetString(3), CreatedAt = rd.GetString(4),
            InTok = rd.GetInt64(5), OutTok = rd.GetInt64(6), CacheTok = rd.GetInt64(7),
            SegTurns = rd.GetInt32(8), Fail = rd.GetInt32(9), Score = rd.GetDouble(10),
            Label = rd.GetInt32(11), AutoLabel = rd.GetString(12),
        };
        if (withEmbedding && !rd.IsDBNull(13))
        {
            var bytes = (byte[])rd.GetValue(13);
            r.Embedding = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, r.Embedding, 0, bytes.Length);
        }
        r.Favorite = rd.GetInt32(14) == 1;
        r.Project = rd.FieldCount > 15 && !rd.IsDBNull(15) ? rd.GetString(15) : "";
        r.Repro = rd.FieldCount > 16 && !rd.IsDBNull(16) && rd.GetInt32(16) == 1;
        return r;
    }

    private const string Cols = "id,source,session,text,created_at,in_tok,out_tok,cache_tok,seg_turns,fail,score,label,auto_label,embedding,COALESCE(favorite,0),COALESCE(project,''),COALESCE(repro,0)";

    /// <summary>코퍼스 변경 여부 빠른 확인용 버전 토큰 (전체 행 수, 최대 id, 임베딩 보유 행 수).</summary>
    public (int Count, long MaxId, int EmbeddedCount) GetCorpusVersion()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(MAX(id),0), SUM(CASE WHEN embedding IS NOT NULL THEN 1 ELSE 0 END) FROM prompts";
        using var rd = cmd.ExecuteReader();
        return rd.Read()
            ? (rd.GetInt32(0), rd.GetInt64(1), rd.IsDBNull(2) ? 0 : rd.GetInt32(2))
            : (0, 0L, 0);
    }

    public List<PromptRow> All(bool withEmbedding = false)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM prompts ORDER BY created_at";
        using var rd = cmd.ExecuteReader();
        var list = new List<PromptRow>();
        while (rd.Read()) list.Add(Read(rd, withEmbedding));
        return list;
    }

    public List<PromptRow> Query(string filter, int limit = 300, string search = "", string project = "")
    {
        // filter: all | good | bad | claude | cursor | proj:<name> ...
        using var cmd = _db.CreateCommand();
        if (filter.StartsWith("proj:", StringComparison.Ordinal))
        {
            project = filter[5..];
            filter = "all";
        }
        string where = filter switch
        {
            "good" => "WHERE (label=1 OR (label=0 AND auto_label='good'))",
            "bad" => "WHERE (label=-1 OR (label=0 AND auto_label='bad'))",
            "fav" => "WHERE favorite=1",
            "repro" => "WHERE repro=1",
            "all" => "",
            _ => "WHERE source=$f",
        };
        if (!string.IsNullOrWhiteSpace(project))
        {
            where += (where.Length == 0 ? "WHERE " : " AND ") + "LOWER(project)=LOWER($pr)";
            cmd.Parameters.AddWithValue("$pr", project.Trim());
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            where += (where.Length == 0 ? "WHERE " : " AND ") + "text LIKE $q";
            cmd.Parameters.AddWithValue("$q", "%" + search.Trim() + "%");
        }
        cmd.CommandText = $"SELECT {Cols} FROM prompts {where} ORDER BY created_at DESC LIMIT $lim";
        if (where.Contains("$f")) cmd.Parameters.AddWithValue("$f", filter);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<PromptRow>();
        while (rd.Read()) list.Add(Read(rd, false));
        return list;
    }

    /// <summary>DB에 기록된 프로젝트 목록 (건수 내림차순).</summary>
    public List<(string Project, int Count)> DistinctProjects()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(project,''), COUNT(*) FROM prompts WHERE project IS NOT NULL AND project <> '' GROUP BY project ORDER BY COUNT(*) DESC";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, int)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetInt32(1)));
        return list;
    }

    /// <summary>해당 프로젝트(또는 전체)의 가장 최근 세션 프롬프트들을 시간순으로 반환 — 후속 지시어 맥락 파악용.</summary>
    public List<PromptRow> RecentSessionPrompts(string? project, int limit = 4)
    {
        using var cmd = _db.CreateCommand();
        if (!string.IsNullOrWhiteSpace(project))
        {
            cmd.CommandText = $"SELECT {Cols} FROM prompts WHERE LOWER(project)=LOWER($pr) ORDER BY created_at DESC, id DESC LIMIT $lim";
            cmd.Parameters.AddWithValue("$pr", project.Trim());
        }
        else
        {
            cmd.CommandText = $"SELECT {Cols} FROM prompts ORDER BY created_at DESC, id DESC LIMIT $lim";
        }
        cmd.Parameters.AddWithValue("$lim", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<PromptRow>();
        while (rd.Read()) list.Add(Read(rd, false));
        if (list.Count > 1)
        {
            var latestSession = list[0].Session;
            list = list.Where(r => r.Session == latestSession).Reverse().ToList();
        }
        return list;
    }

    public void SetLabel(long id, int label)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE prompts SET label=$l WHERE id=$id";
        cmd.Parameters.AddWithValue("$l", label);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- 예측 기록/매칭 ----------
    /// <summary>공백 정규화한 텍스트 해시 — 분석 시점과 Import 시점의 동일 프롬프트를 잇는 키.</summary>
    public static string HashNorm(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))));

    public void SavePrediction(string text, string task, long estIn, long estOut, string model)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO predictions (hash,task,est_in,est_out,model,created_at,text) VALUES ($h,$t,$i,$o,$m,$c,$x)";
        cmd.Parameters.AddWithValue("$h", HashNorm(text));
        cmd.Parameters.AddWithValue("$t", task);
        cmd.Parameters.AddWithValue("$i", estIn);
        cmd.Parameters.AddWithValue("$o", estOut);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("s"));
        cmd.Parameters.AddWithValue("$x", text.Length > 4000 ? text[..4000] : text);
        cmd.ExecuteNonQuery();
    }

    public int PendingPredictionCount()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM predictions WHERE actual_out IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---------- 템플릿 버전 기록 (즐겨찾기 프롬프트의 변경 이력) ----------
    /// <summary>최신 버전과 본문이 같으면 저장하지 않는다. 저장했으면 true.</summary>
    public bool SaveTemplateVersion(long promptId, string text, string note)
    {
        text = text.Trim();
        if (text.Length == 0) return false;
        using var chk = _db.CreateCommand();
        chk.CommandText = "SELECT text FROM template_versions WHERE prompt_id=$p ORDER BY id DESC LIMIT 1";
        chk.Parameters.AddWithValue("$p", promptId);
        if (chk.ExecuteScalar() as string == text) return false;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO template_versions (prompt_id,text,created_at,note) VALUES ($p,$t,$c,$n)";
        cmd.Parameters.AddWithValue("$p", promptId);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("s"));
        cmd.Parameters.AddWithValue("$n", note);
        cmd.ExecuteNonQuery();
        return true;
    }

    public int TemplateVersionCount(long promptId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM template_versions WHERE prompt_id=$p";
        cmd.Parameters.AddWithValue("$p", promptId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<(int No, string Text, string CreatedAt, string Note)> TemplateVersions(long promptId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT text,created_at,note FROM template_versions WHERE prompt_id=$p ORDER BY id";
        cmd.Parameters.AddWithValue("$p", promptId);
        using var rd = cmd.ExecuteReader();
        var list = new List<(int, string, string, string)>();
        while (rd.Read()) list.Add((list.Count + 1, rd.GetString(0), rd.GetString(1), rd.GetString(2)));
        return list;
    }

    /// <summary>아직 실제값이 없는 예측을, 동일 텍스트의 실제 프롬프트와 연결. 매칭 건수 반환.</summary>
    public int MatchPredictions()
    {
        var actualByHash = new Dictionary<string, PromptRow>();
        foreach (var p in All()) actualByHash.TryAdd(HashNorm(p.Text), p);

        using var sel = _db.CreateCommand();
        sel.CommandText = "SELECT id, hash FROM predictions WHERE actual_out IS NULL";
        using var rd = sel.ExecuteReader();
        var pending = new List<(long id, string hash)>();
        while (rd.Read()) pending.Add((rd.GetInt64(0), rd.GetString(1)));
        rd.Close();

        int matched = 0;
        using var tx = _db.BeginTransaction();
        using var upd = _db.CreateCommand();
        upd.CommandText = "UPDATE predictions SET actual_in=$i, actual_out=$o, prompt_id=$p WHERE id=$id";
        foreach (var p in new[] { "i", "o", "p", "id" }) upd.Parameters.Add(new SqliteParameter("$" + p, null));
        foreach (var (id, hash) in pending)
        {
            if (!actualByHash.TryGetValue(hash, out var actual)) continue;
            upd.Parameters["$i"].Value = actual.InTok;
            upd.Parameters["$o"].Value = actual.OutTok;
            upd.Parameters["$p"].Value = actual.Id;
            upd.Parameters["$id"].Value = id;
            matched += upd.ExecuteNonQuery();
        }
        tx.Commit();

        // 근사 매칭: 사용자가 개선안을 고쳐 보낸 경우 해시가 달라 정확 매칭이 실패한다.
        // 본문이 보관된 미매칭 예측을 단어 유사도 0.65 이상인 미사용 실측과 연결해 보정 표본을 넓힌다.
        var usedIds = new HashSet<long>();
        using (var ucmd = _db.CreateCommand())
        {
            ucmd.CommandText = "SELECT prompt_id FROM predictions WHERE prompt_id IS NOT NULL";
            using var urd = ucmd.ExecuteReader();
            while (urd.Read()) usedIds.Add(urd.GetInt64(0));
        }
        var candidates = All().Where(p => p.OutTok > 0 && !usedIds.Contains(p.Id)).ToList();
        var fuzzyPending = new List<(long id, string text)>();
        using (var fcmd = _db.CreateCommand())
        {
            fcmd.CommandText = "SELECT id, COALESCE(text,'') FROM predictions WHERE actual_out IS NULL AND COALESCE(text,'') <> ''";
            using var frd = fcmd.ExecuteReader();
            while (frd.Read()) fuzzyPending.Add((frd.GetInt64(0), frd.GetString(1)));
        }
        if (fuzzyPending.Count > 0 && candidates.Count > 0)
        {
            using var tx2 = _db.BeginTransaction();
            using var upd2 = _db.CreateCommand();
            upd2.CommandText = "UPDATE predictions SET actual_in=$i, actual_out=$o, prompt_id=$p WHERE id=$id";
            foreach (var p in new[] { "i", "o", "p", "id" }) upd2.Parameters.Add(new SqliteParameter("$" + p, null));
            foreach (var (id, text) in fuzzyPending)
            {
                var best = candidates.Select(c => (Row: c, Sim: Advisor.TextSimilarity(text, c.Text)))
                    .Where(x => x.Sim >= 0.65).OrderByDescending(x => x.Sim).FirstOrDefault();
                if (best.Row == null) continue;
                upd2.Parameters["$i"].Value = best.Row.InTok;
                upd2.Parameters["$o"].Value = best.Row.OutTok;
                upd2.Parameters["$p"].Value = best.Row.Id;
                upd2.Parameters["$id"].Value = id;
                matched += upd2.ExecuteNonQuery();
                candidates.Remove(best.Row);
            }
            tx2.Commit();
        }
        return matched;
    }

    public List<(string task, long estOut, long actOut)> CalibrationRows()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT task, est_out, actual_out FROM predictions WHERE actual_out IS NOT NULL";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, long, long)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetInt64(1), rd.GetInt64(2)));
        return list;
    }

    public List<(string model, long estOut, long actOut)> CalibrationRowsByModel()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT model, est_out, actual_out FROM predictions WHERE actual_out IS NOT NULL AND model <> ''";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, long, long)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetInt64(1), rd.GetInt64(2)));
        return list;
    }

    /// <summary>(모델, 작업유형) 이원 보정용 실측 행.</summary>
    public List<(string model, string task, long estOut, long actOut)> CalibrationRowsByModelTask()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT model, task, est_out, actual_out FROM predictions WHERE actual_out IS NOT NULL AND model <> ''";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, string, long, long)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetString(1), rd.GetInt64(2), rd.GetInt64(3)));
        return list;
    }

    /// <summary>입력 토큰 추정 보정용 실측 행 (est_in/actual_in).</summary>
    public List<(string task, long est, long act)> InputCalibrationRows()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT task, est_in, actual_in FROM predictions WHERE actual_in IS NOT NULL";
        using var rd = cmd.ExecuteReader();
        var list = new List<(string, long, long)>();
        while (rd.Read()) list.Add((rd.GetString(0), rd.GetInt64(1), rd.GetInt64(2)));
        return list;
    }

    // ---------- 오류 추적 ----------
    public void LogError(string stage, string message)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO errors (created_at, stage, message) VALUES ($c,$s,$m)";
            cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("$s", stage);
            cmd.Parameters.AddWithValue("$m", message.Length > 500 ? message[..500] : message);
            cmd.ExecuteNonQuery();
        }
        catch { /* 오류 기록 자체가 실패하면 조용히 무시 */ }
    }

    public List<string> RecentErrors(int limit = 100)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT created_at, stage, message FROM errors ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<string>();
        while (rd.Read()) list.Add($"[{rd.GetString(0)}] {rd.GetString(1)} — {rd.GetString(2)}");
        return list;
    }

    public int ErrorCount()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM errors";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void ClearErrors()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM errors";
        cmd.ExecuteNonQuery();
    }

    public void SetFavorite(long id, bool fav)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE prompts SET favorite=$f WHERE id=$id";
        cmd.Parameters.AddWithValue("$f", fav ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetScoreLabel(long id, double score, string autoLabel)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE prompts SET score=$s, auto_label=$a WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", score);
        cmd.Parameters.AddWithValue("$a", autoLabel);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetEmbedding(long id, float[] vec)
    {
        var bytes = new byte[vec.Length * 4];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE prompts SET embedding=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$e", bytes);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int CountEmbedded()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompts WHERE embedding IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int CountUnembedded()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM prompts WHERE embedding IS NULL";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<PromptRow> Unembedded(int limit)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT {Cols} FROM prompts WHERE embedding IS NULL LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<PromptRow>();
        while (rd.Read()) list.Add(Read(rd, false));
        return list;
    }

    public (int total, int sessions, long inTok, long outTok, Dictionary<string, int> bySource) Stats()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT session), COALESCE(SUM(in_tok),0), COALESCE(SUM(out_tok),0) FROM prompts";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        var result = (rd.GetInt32(0), rd.GetInt32(1), rd.GetInt64(2), rd.GetInt64(3), new Dictionary<string, int>());
        rd.Close();
        using var cmd2 = _db.CreateCommand();
        cmd2.CommandText = "SELECT source, COUNT(*) FROM prompts GROUP BY source";
        using var rd2 = cmd2.ExecuteReader();
        while (rd2.Read()) result.Item5[rd2.GetString(0)] = rd2.GetInt32(1);
        return result;
    }

    // ---------- 재현 세트 ----------
    public void SetRepro(long id, bool on)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE prompts SET repro=$r WHERE id=$id";
        cmd.Parameters.AddWithValue("$r", on ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- 개선 전/후 추적 ----------
    public sealed record RewriteRow(long Id, string CreatedAt, string Original, string Improved, string Kind,
        string RulesBefore, string RulesAfter, long? PromptId, int? ActualTurns, int? ActualFail, long? ActualTok);

    /// <summary>개선안을 실제로 보낼 때(프롬프트만 복사) 기록한다. 같은 개선본은 한 번만.</summary>
    public bool SaveRewrite(string original, string improved, string kind, IEnumerable<string> rulesBefore, IEnumerable<string> rulesAfter, string? createdAt = null)
    {
        var h = HashNorm(improved);
        if (Scalar($"SELECT COUNT(*) FROM rewrites WHERE hash='{h}'") > 0) return false;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO rewrites (created_at,original,improved,kind,rules_before,rules_after,hash) VALUES ($c,$o,$i,$k,$b,$a,$h)";
        cmd.Parameters.AddWithValue("$c", createdAt ?? DateTime.Now.ToString("s"));
        cmd.Parameters.AddWithValue("$o", original);
        cmd.Parameters.AddWithValue("$i", improved);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$b", string.Join(",", rulesBefore));
        cmd.Parameters.AddWithValue("$a", string.Join(",", rulesAfter));
        cmd.Parameters.AddWithValue("$h", h);
        cmd.ExecuteNonQuery();
        return true;
    }

    public List<RewriteRow> Rewrites()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,created_at,original,improved,kind,rules_before,rules_after,prompt_id,actual_turns,actual_fail,actual_tok FROM rewrites ORDER BY id DESC";
        using var rd = cmd.ExecuteReader();
        var list = new List<RewriteRow>();
        while (rd.Read())
            list.Add(new RewriteRow(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4),
                rd.GetString(5), rd.GetString(6),
                rd.IsDBNull(7) ? null : rd.GetInt64(7), rd.IsDBNull(8) ? null : rd.GetInt32(8),
                rd.IsDBNull(9) ? null : rd.GetInt32(9), rd.IsDBNull(10) ? null : rd.GetInt64(10)));
        return list;
    }

    /// <summary>보낸 개선본을 가져온 실제 기록과 잇는다 (정확 해시 → 단어 유사도 0.65 이상). 실제 턴 · 정정 · 토큰을 채운다.</summary>
    public int MatchRewrites()
    {
        var pending = Rewrites().Where(r => r.PromptId == null).ToList();
        if (pending.Count == 0) return 0;
        var all = All();
        var used = Rewrites().Where(r => r.PromptId != null).Select(r => r.PromptId!.Value).ToHashSet();
        var byHash = new Dictionary<string, PromptRow>();
        foreach (var p in all) byHash.TryAdd(HashNorm(p.Text), p);
        int n = 0;
        using var tx = _db.BeginTransaction();
        using var upd = _db.CreateCommand();
        upd.CommandText = "UPDATE rewrites SET prompt_id=$p, actual_turns=$t, actual_fail=$f, actual_tok=$k WHERE id=$id";
        foreach (var k in new[] { "p", "t", "f", "k", "id" }) upd.Parameters.Add(new SqliteParameter("$" + k, null));
        foreach (var r in pending)
        {
            PromptRow? hit = byHash.TryGetValue(HashNorm(r.Improved), out var exact) && !used.Contains(exact.Id) ? exact
                : all.Where(p => !used.Contains(p.Id))
                     .Select(p => (p, sim: Advisor.TextSimilarity(r.Improved, p.Text)))
                     .Where(x => x.sim >= 0.65).OrderByDescending(x => x.sim).Select(x => x.p).FirstOrDefault();
            if (hit == null) continue;
            used.Add(hit.Id);
            upd.Parameters["$p"].Value = hit.Id;
            upd.Parameters["$t"].Value = hit.SegTurns;
            upd.Parameters["$f"].Value = hit.Fail;
            upd.Parameters["$k"].Value = hit.TotalTok;
            upd.Parameters["$id"].Value = r.Id;
            n += upd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
    }

    // ---------- 개선안 Shadow 기록 ----------
    public sealed record ShadowRow(string CreatedAt, string Model, bool OnTopic, int Judge, int RuleLints, int LlmLints, string Decided, string Note);

    public void AddShadow(string promptHash, string model, bool onTopic, int judge, int ruleLints, int llmLints, string decided, string note, string? createdAt = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO shadow_log (created_at,prompt_hash,model,on_topic,judge,rule_lints,llm_lints,decided,note) VALUES ($c,$h,$m,$o,$j,$r,$l,$d,$n)";
        cmd.Parameters.AddWithValue("$c", createdAt ?? DateTime.Now.ToString("s"));
        cmd.Parameters.AddWithValue("$h", promptHash);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$o", onTopic ? 1 : 0);
        cmd.Parameters.AddWithValue("$j", judge);
        cmd.Parameters.AddWithValue("$r", ruleLints);
        cmd.Parameters.AddWithValue("$l", llmLints);
        cmd.Parameters.AddWithValue("$d", decided);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.ExecuteNonQuery();
    }

    public List<ShadowRow> ShadowRows(int limit = 500)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT created_at,model,on_topic,judge,rule_lints,llm_lints,decided,note FROM shadow_log ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        using var rd = cmd.ExecuteReader();
        var list = new List<ShadowRow>();
        while (rd.Read())
            list.Add(new ShadowRow(rd.GetString(0), rd.GetString(1), rd.GetInt32(2) == 1, rd.GetInt32(3), rd.GetInt32(4), rd.GetInt32(5), rd.GetString(6), rd.GetString(7)));
        return list;
    }

    // ---------- 규칙 설정 변경 이력 (전환 · 롤백) ----------
    public void SaveConfigHistory(string json, string note)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO config_history (created_at,json,note) VALUES ($c,$j,$n)";
        cmd.Parameters.AddWithValue("$c", DateTime.Now.ToString("s"));
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$n", note);
        cmd.ExecuteNonQuery();
    }

    public List<(long Id, string CreatedAt, string Json, string Note)> ConfigHistory()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,created_at,json,note FROM config_history ORDER BY id DESC";
        using var rd = cmd.ExecuteReader();
        var list = new List<(long, string, string, string)>();
        while (rd.Read()) list.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));
        return list;
    }

    public void DeleteConfigHistory(long id) => Exec($"DELETE FROM config_history WHERE id={id}");

    /// <summary>SQLite 온라인 백업 (쓰는 중에도 일관된 사본).</summary>
    public void BackupTo(string path)
    {
        using var dst = new SqliteConnection($"Data Source={path};Pooling=False");
        dst.Open();
        _db.BackupDatabase(dst);
    }

    public void Dispose() => _db.Dispose();
}
