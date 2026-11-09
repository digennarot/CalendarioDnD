// Calendario D&D - versione nativa Windows (WinForms, .NET Framework 4)
// Funzioni: presenze per giorno, più campagne, import/export JSON, sincronizzazione via GitHub.
// Compilazione su Windows senza installare nulla: esegui compila.bat
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e) { Report(e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Report(e.ExceptionObject as Exception); };
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
        catch (Exception ex) { Report(ex); }
    }

    static void Report(Exception ex)
    {
        string msg = ex == null ? "Errore sconosciuto" : ex.ToString();
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "calendario_errore.txt"), msg); } catch { }
        MessageBox.Show(msg, "Calendario - errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

// =====================================================================
//  JSON minimale (nessuna dipendenza esterna)
// =====================================================================
static class Json
{
    const int MaxDepth = 32;
    public static object Parse(string s) { int i = 0; object v = Value(s, ref i, 0); Ws(s, ref i); if (i != s.Length) throw Err(i); return v; }

    static Exception Err(int i) { return new FormatException("JSON non valido (posizione " + i + ")"); }
    static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    static object Value(string s, ref int i, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("JSON troppo annidato");
        Ws(s, ref i);
        if (i >= s.Length) throw Err(i);
        char c = s[i];
        if (c == '{')
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            i++; Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                Ws(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Err(i);
                string k = Str(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Err(i);
                i++;
                o[k] = Value(s, ref i, depth + 1);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw Err(i);
            }
        }
        if (c == '[')
        {
            List<object> a = new List<object>();
            i++; Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i, depth + 1));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw Err(i);
            }
        }
        if (c == '"') return Str(s, ref i);
        if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        int st = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        if (st == i) throw Err(i);
        return double.Parse(s.Substring(st, i - st), CultureInfo.InvariantCulture);
    }

    static string Str(string s, ref int i)
    {
        StringBuilder sb = new StringBuilder();
        i++;
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 > s.Length) throw Err(i);
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                default: sb.Append(e); break;
            }
        }
        throw Err(i);
    }

    public static string Quote(string s)
    {
        StringBuilder sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
    public static long Num(object o) { return o is double ? (long)(double)o : 0; }
}

// =====================================================================
//  Modello dati
// =====================================================================
class Entry
{
    public string Status;
    public long Time; // ms dall'epoch (UTC) dell'ultima modifica
    public Entry(string s, long t) { Status = s; Time = t; }
}

class Store
{
    public const string DefaultCampaign = "Campagna principale";
    static readonly StringComparer CI = StringComparer.OrdinalIgnoreCase;

    public const string Approved = "approvato", Pending = "in attesa", Rejected = "rifiutato";

    // Utenti registrati: nome -> stato (approvato / in attesa / rifiutato) con ora della modifica
    public Dictionary<string, Entry> Users = new Dictionary<string, Entry>(CI);
    // Password del Master (hash SHA-256 con sale)
    public string MasterHash = "", MasterSalt = "";
    public long MasterTime = 0;

    public void SetUser(string name, string status, long time)
    {
        Entry e;
        if (Users.TryGetValue(name, out e) && Newer(e, status, time)) return;
        Users.Remove(name);           // aggiorna anche le maiuscole del nome
        Users[name] = new Entry(status, time);
    }

    public string FindUser(string name)
    {
        foreach (string u in Users.Keys) if (CI.Equals(u, name)) return u;
        return null;
    }

    public string UserStatus(string name)
    {
        Entry e;
        return Users.TryGetValue(name, out e) ? e.Status : null;
    }

    public List<string> UsersWithStatus(string status)
    {
        List<string> l = new List<string>();
        foreach (KeyValuePair<string, Entry> kv in Users) if (kv.Value.Status == status) l.Add(kv.Key);
        l.Sort(StringComparer.CurrentCultureIgnoreCase);
        return l;
    }

    // Chi ha già delle presenze (dati delle versioni precedenti) è considerato approvato
    void EnsureUsers()
    {
        foreach (Dictionary<string, Dictionary<string, Entry>> c in Data.Values)
            foreach (string u in c.Keys)
                if (!Users.ContainsKey(u) && !CI.Equals(u, "Master")) Users[u] = new Entry(Approved, 0);
    }

    public void SetMasterPassword(string pwd)
    {
        byte[] salt = new byte[16];
        using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
        MasterSalt = Convert.ToBase64String(salt);
        MasterHash = Hash(MasterSalt, pwd);
        MasterTime = Now();
    }

    public bool CheckMasterPassword(string pwd)
    {
        if (MasterHash.Length == 0) return false;
        try
        {
            byte[] a = Encoding.UTF8.GetBytes(Hash(MasterSalt, pwd)), b = Encoding.UTF8.GetBytes(MasterHash);
            int diff = a.Length ^ b.Length;   // confronto a tempo costante
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
        catch { return false; }
    }

    const int Pbkdf2Iterations = 100000;

    // PBKDF2 (lento di proposito): il file dati può essere letto da altri, quindi l'hash deve resistere ai tentativi offline
    static string Hash(string salt, string pwd)
    {
        using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(pwd), Convert.FromBase64String(salt), Pbkdf2Iterations))
            return "pbkdf2:" + Pbkdf2Iterations + ":" + Convert.ToBase64String(kdf.GetBytes(32));
    }

    public List<string> Campaigns = new List<string>();
    public Dictionary<string, long> Created = new Dictionary<string, long>(CI);
    public Dictionary<string, long> Deleted = new Dictionary<string, long>(CI);   // campagne eliminate (per la sincronizzazione)
    public Dictionary<string, Dictionary<string, Dictionary<string, Entry>>> Data =
        new Dictionary<string, Dictionary<string, Dictionary<string, Entry>>>(CI);

    // Rimuove caratteri di controllo (a capo, tab...) e limita la lunghezza dei nomi letti da file esterni
    public static string Clean(string name)
    {
        if (name == null) return "";
        StringBuilder sb = new StringBuilder();
        foreach (char ch in name) if (!char.IsControl(ch)) sb.Append(ch);
        string r = sb.ToString().Trim();
        return r.Length > 60 ? r.Substring(0, 60) : r;
    }

    static bool ValidStatus(string st) { return st == "presente" || st == "assente"; }
    static bool ValidUserStatus(string st) { return st == Approved || st == Pending || st == Rejected; }

    public static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

    public string Find(string name)
    {
        foreach (string c in Campaigns) if (CI.Equals(c, name)) return c;
        return null;
    }

    public void AddCampaign(string name, long created)
    {
        string ex = Find(name);
        if (ex != null) { if (created > Created[ex]) Created[ex] = created; return; }
        Campaigns.Add(name);
        Created[name] = created;
        if (!Data.ContainsKey(name)) Data[name] = new Dictionary<string, Dictionary<string, Entry>>();
    }

    public const string MasterUser = "Master";

    // Sessioni del Master: giorni in cui il Master è "presente" in una campagna -> elenco campagne
    public SortedDictionary<string, List<string>> MasterSessions()
    {
        SortedDictionary<string, List<string>> r = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string c in Campaigns)
            foreach (KeyValuePair<string, Dictionary<string, Entry>> u in Get(c))
            {
                if (!CI.Equals(u.Key, MasterUser)) continue;
                foreach (KeyValuePair<string, Entry> d in u.Value)
                {
                    if (d.Value.Status != "presente") continue;
                    if (!r.ContainsKey(d.Key)) r[d.Key] = new List<string>();
                    if (!r[d.Key].Contains(c)) r[d.Key].Add(c);
                }
            }
        return r;
    }

    // Conflitti: giorni in cui il Master risulta in sessione in più campagne (il Master è uno solo)
    public SortedDictionary<string, List<string>> MasterConflicts()
    {
        SortedDictionary<string, List<string>> r = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<string>> kv in MasterSessions()) if (kv.Value.Count > 1) r[kv.Key] = kv.Value;
        return r;
    }

    public static string Today() { return DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

    public int UpcomingConflicts()
    {
        int n = 0; string today = Today();
        foreach (string d in MasterConflicts().Keys) if (string.CompareOrdinal(d, today) >= 0) n++;
        return n;
    }
    public void EnsureOne() { if (Campaigns.Count == 0) AddCampaign(DefaultCampaign, 0); EnsureUsers(); }

    public Dictionary<string, Dictionary<string, Entry>> Get(string campaign)
    {
        Dictionary<string, Dictionary<string, Entry>> d;
        if (!Data.TryGetValue(campaign, out d)) { d = new Dictionary<string, Dictionary<string, Entry>>(); Data[campaign] = d; }
        return d;
    }

    public void Set(string campaign, string user, string date, string status, long time)
    {
        if (Find(campaign) == null) AddCampaign(campaign, 0);
        Dictionary<string, Dictionary<string, Entry>> d = Get(campaign);
        if (!d.ContainsKey(user)) d[user] = new Dictionary<string, Entry>();
        Entry e;
        if (d[user].TryGetValue(date, out e) && Newer(e, status, time)) return;
        d[user][date] = new Entry(status, time);
    }

    // true se l'esistente vince sul nuovo
    static bool Newer(Entry e, string status, long time)
    {
        if (e.Time != time) return e.Time > time;
        return string.CompareOrdinal(e.Status, status) >= 0;
    }

    public void Delete(string name)
    {
        string c = Find(name);
        if (c == null) return;
        Campaigns.Remove(c);
        Data.Remove(c);
        Created.Remove(c);
        Deleted[c] = Now();
    }

    public void Rename(string oldName, string newName)
    {
        string c = Find(oldName);
        if (c == null) return;
        if (CI.Equals(c, newName)) { Campaigns[Campaigns.IndexOf(c)] = newName; return; } // solo maiuscole/minuscole
        long now = Now();
        Dictionary<string, Dictionary<string, Entry>> d = Get(c);
        int idx = Campaigns.IndexOf(c);
        Campaigns[idx] = newName;
        Data.Remove(c); Created.Remove(c);
        Deleted[c] = now;
        Data[newName] = d;
        Created[newName] = now;
    }

    // Unisce un altro archivio: per ogni presenza vince la modifica più recente.
    // L'ordine delle campagne dell'altro archivio ha la precedenza (così più PC convergono).
    public void Merge(Store other)
    {
        foreach (KeyValuePair<string, Entry> u in other.Users) SetUser(u.Key, u.Value.Status, u.Value.Time);
        if (other.MasterTime > MasterTime || (other.MasterTime == MasterTime && string.CompareOrdinal(other.MasterHash, MasterHash) > 0))
        { MasterHash = other.MasterHash; MasterSalt = other.MasterSalt; MasterTime = other.MasterTime; }

        foreach (KeyValuePair<string, long> kv in other.Deleted)
        {
            long t;
            if (!Deleted.TryGetValue(kv.Key, out t) || kv.Value > t) Deleted[kv.Key] = kv.Value;
        }
        List<string> order = new List<string>();
        foreach (string c in other.Campaigns) order.Add(c);
        foreach (string c in Campaigns) { bool found = false; foreach (string o in order) if (CI.Equals(o, c)) found = true; if (!found) order.Add(c); }

        foreach (string c in other.Campaigns)
        {
            AddCampaign(c, other.Created[c]);
            foreach (KeyValuePair<string, Dictionary<string, Entry>> u in other.Get(c))
                foreach (KeyValuePair<string, Entry> d in u.Value)
                    Set(c, u.Key, d.Key, d.Value.Status, d.Value.Time);
        }
        // rinomine "solo maiuscole": prendi il nome dell'altro
        List<string> newList = new List<string>();
        foreach (string o in order) { string mine = Find(o); if (mine != null) newList.Add(o); }
        Dictionary<string, long> cr = new Dictionary<string, long>(CI);
        foreach (string n in newList) cr[n] = Created[n];
        Campaigns = newList; Created = cr;
        Prune();
    }

    void Prune()
    {
        foreach (string c in new List<string>(Campaigns))
        {
            long del;
            if (Deleted.TryGetValue(c, out del) && del >= Created[c])
            { Campaigns.Remove(c); Data.Remove(c); Created.Remove(c); }
        }
        EnsureOne();
    }

    // ---------- JSON ----------
    public string ToJson()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n  \"versione\": 2,\n  \"campagne\": [");
        for (int i = 0; i < Campaigns.Count; i++)
            sb.Append(i == 0 ? "\n" : ",\n").Append("    { \"nome\": ").Append(Json.Quote(Campaigns[i]))
              .Append(", \"creata\": ").Append(Created[Campaigns[i]]).Append(" }");
        sb.Append(Campaigns.Count > 0 ? "\n  ],\n" : "],\n");

        sb.Append("  \"eliminate\": {");
        List<string> dk = new List<string>(Deleted.Keys); dk.Sort(StringComparer.Ordinal);
        for (int i = 0; i < dk.Count; i++)
            sb.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(Json.Quote(dk[i])).Append(": ").Append(Deleted[dk[i]]);
        sb.Append(dk.Count > 0 ? "\n  },\n" : "},\n");

        sb.Append("  \"presenze\": {");
        for (int i = 0; i < Campaigns.Count; i++)
        {
            string c = Campaigns[i];
            sb.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(Json.Quote(c)).Append(": {");
            Dictionary<string, Dictionary<string, Entry>> d = Get(c);
            List<string> users = new List<string>(d.Keys); users.Sort(StringComparer.Ordinal);
            bool firstU = true;
            foreach (string u in users)
            {
                if (d[u].Count == 0) continue;
                sb.Append(firstU ? "\n" : ",\n").Append("      ").Append(Json.Quote(u)).Append(": {");
                firstU = false;
                List<string> dates = new List<string>(d[u].Keys); dates.Sort(StringComparer.Ordinal);
                for (int j = 0; j < dates.Count; j++)
                {
                    Entry e = d[u][dates[j]];
                    sb.Append(j == 0 ? "\n" : ",\n").Append("        ").Append(Json.Quote(dates[j]))
                      .Append(": { \"stato\": ").Append(Json.Quote(e.Status)).Append(", \"t\": ").Append(e.Time).Append(" }");
                }
                sb.Append("\n      }");
            }
            sb.Append(firstU ? "}" : "\n    }");
        }
        sb.Append(Campaigns.Count > 0 ? "\n  },\n" : "},\n");

        sb.Append("  \"utenti\": {");
        List<string> uk = new List<string>(Users.Keys); uk.Sort(StringComparer.Ordinal);
        for (int i = 0; i < uk.Count; i++)
            sb.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(Json.Quote(uk[i]))
              .Append(": { \"stato\": ").Append(Json.Quote(Users[uk[i]].Status)).Append(", \"t\": ").Append(Users[uk[i]].Time).Append(" }");
        sb.Append(uk.Count > 0 ? "\n  },\n" : "},\n");

        sb.Append("  \"master\": { \"hash\": ").Append(Json.Quote(MasterHash)).Append(", \"sale\": ").Append(Json.Quote(MasterSalt))
          .Append(", \"t\": ").Append(MasterTime).Append(" }\n}\n");
        return sb.ToString();
    }

    // legacyCampaign: dove mettere i dati se il JSON è nel vecchio formato del calendario HTML
    public static Store FromJson(string json, string legacyCampaign)
    {
        Store s = new Store();
        Dictionary<string, object> root = Json.Obj(Json.Parse(json));
        if (!root.ContainsKey("versione"))
        {
            // Formato del vecchio Calendario.html: { "Marco": { "2026-11-12": "presente" } }
            foreach (KeyValuePair<string, object> u in root)
                foreach (KeyValuePair<string, object> d in Json.Obj(u.Value))
                    if (ValidStatus(d.Value as string) && Clean(u.Key).Length > 0) s.Set(legacyCampaign, Clean(u.Key), d.Key, (string)d.Value, 0);
            s.EnsureOne();
            return s;
        }
        List<object> camps = root.ContainsKey("campagne") ? root["campagne"] as List<object> : null;
        if (camps != null)
            foreach (object o in camps)
            {
                Dictionary<string, object> c = Json.Obj(o);
                string nome = c.ContainsKey("nome") ? Clean(c["nome"] as string) : "";
                if (nome.Length > 0) s.AddCampaign(nome, c.ContainsKey("creata") ? Json.Num(c["creata"]) : 0);
            }
        if (root.ContainsKey("eliminate"))
            foreach (KeyValuePair<string, object> kv in Json.Obj(root["eliminate"])) if (Clean(kv.Key).Length > 0) s.Deleted[Clean(kv.Key)] = Json.Num(kv.Value);
        if (root.ContainsKey("presenze"))
            foreach (KeyValuePair<string, object> c in Json.Obj(root["presenze"]))
                foreach (KeyValuePair<string, object> u in Json.Obj(c.Value))
                    foreach (KeyValuePair<string, object> d in Json.Obj(u.Value))
                    {
                        Dictionary<string, object> e = Json.Obj(d.Value);
                        string st = e.ContainsKey("stato") ? e["stato"] as string : null;
                        DateTime dt;
                        if (ValidStatus(st) && Clean(c.Key).Length > 0 && Clean(u.Key).Length > 0 &&
                            DateTime.TryParseExact(d.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                            s.Set(Clean(c.Key), Clean(u.Key), d.Key, st, e.ContainsKey("t") ? Json.Num(e["t"]) : 0);
                    }
        if (root.ContainsKey("utenti"))
            foreach (KeyValuePair<string, object> u in Json.Obj(root["utenti"]))
            {
                Dictionary<string, object> e = Json.Obj(u.Value);
                string st = e.ContainsKey("stato") ? e["stato"] as string : null;
                if (ValidUserStatus(st) && Clean(u.Key).Length > 0)
                    s.SetUser(Clean(u.Key), st, e.ContainsKey("t") ? Json.Num(e["t"]) : 0);
            }
        if (root.ContainsKey("master"))
        {
            Dictionary<string, object> m = Json.Obj(root["master"]);
            s.MasterHash = m.ContainsKey("hash") ? (m["hash"] as string ?? "") : "";
            s.MasterSalt = m.ContainsKey("sale") ? (m["sale"] as string ?? "") : "";
            s.MasterTime = m.ContainsKey("t") ? Json.Num(m["t"]) : 0;
        }
        s.EnsureOne();
        return s;
    }

    // Vecchio file di testo della prima versione dell'app
    public static Store FromLegacyText(string[] lines)
    {
        Store s = new Store();
        foreach (string line in lines)
        {
            string[] p = line.Split(';');
            if (p.Length == 2 && p[0] == "C") s.AddCampaign(p[1], 0);
            else if (p.Length == 5 && p[0] == "S") s.Set(p[1], p[2], p[3], p[4], 0);
            else if (p.Length == 3 && p[0] != "L") s.Set(DefaultCampaign, p[0], p[1], p[2], 0);
        }
        s.EnsureOne();
        return s;
    }
}

// =====================================================================
//  Impostazioni locali (per questo PC) + sincronizzazione GitHub
// =====================================================================
class Settings
{
    public string Repo = "digennarot/CalendarioDnD";
    public string Branch = "";
    public string FilePath = "dati/calendario.json";
    public string Token = "";
    public bool AutoSync = true;
    public bool TokenSaved;
    public Dictionary<string, string> LastCampaign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly string file;

    public Settings(string file) { this.file = file; Load(); }

    public bool GitHubConfigured { get { return Token.Length > 0 && Validate() == null; } }

    // Restituisce un messaggio di errore, o null se repository/branch/percorso sono validi
    public string Validate()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(Repo.Trim(), @"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$"))
            return "Repository non valido: usa il formato proprietario/nome.";
        if (Branch.Trim().Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(Branch.Trim(), @"^[A-Za-z0-9._/-]{1,100}$"))
            return "Nome del branch non valido.";
        string p = FilePath.Trim().Trim('/');
        if (!System.Text.RegularExpressions.Regex.IsMatch(p, @"^[A-Za-z0-9._ /-]{1,200}$") || ("/" + p + "/").Contains("/../") || ("/" + p + "/").Contains("/./"))
            return "Percorso del file non valido.";
        return null;
    }

    void Load()
    {
        if (!File.Exists(file)) return;
        foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq), v = line.Substring(eq + 1);
            if (k == "repo") Repo = v;
            else if (k == "branch") Branch = v;
            else if (k == "file") FilePath = v;
            else if (k == "auto") AutoSync = v == "1";
            else if (k == "token") Token = Unprotect(v);
            else if (k.StartsWith("last.")) LastCampaign[k.Substring(5)] = v;
        }
    }

    public void Save()
    {
        List<string> l = new List<string>();
        l.Add("repo=" + Repo); l.Add("branch=" + Branch); l.Add("file=" + FilePath);
        l.Add("auto=" + (AutoSync ? "1" : "0"));
        string prot = Protect(Token);
        TokenSaved = prot.Length > 0;
        l.Add("token=" + prot);
        foreach (KeyValuePair<string, string> kv in LastCampaign)
            if (SafeIni(kv.Key) && kv.Key.IndexOf('=') < 0 && SafeIni(kv.Value)) l.Add("last." + kv.Key + "=" + kv.Value);
        try { File.WriteAllLines(file, l.ToArray(), Encoding.UTF8); } catch { }
    }

    static bool SafeIni(string v) { foreach (char c in v) if (char.IsControl(c)) return false; return true; }

    // Il token viene cifrato con l'account Windows (DPAPI): il file copiato su un altro PC non lo rivela.
    static string Protect(string s)
    {
        if (s.Length == 0) return "";
        try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; } // meglio non salvarlo che salvarlo in chiaro
    }

    static string Unprotect(string s)
    {
        try
        {
            if (s.StartsWith("dpapi:")) return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.Substring(6)), null, DataProtectionScope.CurrentUser));
        }
        catch { }
        return "";
    }
}

class GitHubException : Exception
{
    public int Status;
    public GitHubException(int status, string msg) : base(msg) { Status = status; }
}

static class GitHub
{
    static string Url(Settings s, bool withRef)
    {
        string[] parts = s.FilePath.Trim().Trim('/').Split('/');
        for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
        string u = "https://api.github.com/repos/" + s.Repo.Trim() + "/contents/" + string.Join("/", parts);
        if (withRef && s.Branch.Trim().Length > 0) u += "?ref=" + Uri.EscapeDataString(s.Branch.Trim());
        return u;
    }

    static string Call(Settings s, string method, string url, string body)
    {
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // TLS 1.2
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = method;
        req.UserAgent = "CalendarioDnD-Windows";
        req.Accept = "application/vnd.github+json";
        req.Headers["Authorization"] = "Bearer " + s.Token;
        req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
        req.Timeout = 20000;
        if (body != null)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            req.ContentType = "application/json";
            req.ContentLength = b.Length;
            using (Stream st = req.GetRequestStream()) st.Write(b, 0, b.Length);
        }
        try
        {
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }
        catch (WebException ex)
        {
            HttpWebResponse resp = ex.Response as HttpWebResponse;
            if (resp == null) throw new GitHubException(0, "Connessione a GitHub non riuscita: " + ex.Message);
            string text = "";
            try { using (StreamReader r = new StreamReader(resp.GetResponseStream())) text = r.ReadToEnd(); } catch { }
            string msg = text;
            try { msg = (string)Json.Obj(Json.Parse(text))["message"]; } catch { }
            int code = (int)resp.StatusCode;
            string hint = code == 401 ? " (token non valido o scaduto)"
                        : code == 403 ? " (il token non ha il permesso di scrivere su questo repository)"
                        : code == 404 ? " (repository, branch o file non trovato, oppure token senza accesso)" : "";
            throw new GitHubException(code, "GitHub ha risposto " + code + ": " + msg + hint);
        }
    }

    // Restituisce il contenuto del file (o null se non esiste ancora) e il suo sha
    public static string Download(Settings s, out string sha)
    {
        sha = null;
        string resp;
        try { resp = Call(s, "GET", Url(s, true), null); }
        catch (GitHubException ex)
        {
            if (ex.Status == 404) { Call(s, "GET", "https://api.github.com/repos/" + s.Repo.Trim(), null); return null; } // repo ok, file assente
            throw;
        }
        Dictionary<string, object> o = Json.Obj(Json.Parse(resp));
        sha = o.ContainsKey("sha") ? o["sha"] as string : null;
        string content = o.ContainsKey("content") ? o["content"] as string : null;
        if (content == null) throw new GitHubException(0, "Il percorso indicato su GitHub non è un file.");
        return Encoding.UTF8.GetString(Convert.FromBase64String(content));
    }

    public static void Upload(Settings s, string text, string sha, string message)
    {
        StringBuilder b = new StringBuilder("{");
        b.Append("\"message\":").Append(Json.Quote(message));
        b.Append(",\"content\":").Append(Json.Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(text))));
        if (sha != null) b.Append(",\"sha\":").Append(Json.Quote(sha));
        if (s.Branch.Trim().Length > 0) b.Append(",\"branch\":").Append(Json.Quote(s.Branch.Trim()));
        b.Append("}");
        Call(s, "PUT", Url(s, false), b.ToString());
    }
}

// =====================================================================
//  Finestra principale
// =====================================================================
class MainForm : Form
{
    static readonly string[] MonthNames = { "Gennaio", "Febbraio", "Marzo", "Aprile", "Maggio", "Giugno",
        "Luglio", "Agosto", "Settembre", "Ottobre", "Novembre", "Dicembre" };
    static readonly string[] DayNames = { "Dom", "Lun", "Mar", "Mer", "Gio", "Ven", "Sab" };

    static readonly Color Blue = Color.FromArgb(0, 123, 255);
    static readonly Color Green = Color.FromArgb(40, 167, 69);
    static readonly Color Red = Color.FromArgb(220, 53, 69);
    static readonly Color Yellow = Color.FromArgb(255, 193, 7);

    const string MasterName = "Master";

    readonly string dataFile, legacyFile;
    readonly Settings settings;
    Store store = new Store();
    string currentCampaign = Store.DefaultCampaign;

    string loggedInUser;
    int year, month; // month 1..12

    Panel loginPanel, calendarPanel;
    TextBox usernameBox;
    Label errorLabel, userLabel, monthLabel, statusLabel, loginStatus;
    ListBox userList;
    ComboBox campaignBox;
    Button manageBtn, dataBtn;
    ContextMenuStrip statusMenu, manageMenu, dataMenu;
    ToolStripMenuItem importMerge, importReplace;
    DayCell[] cells = new DayCell[42];
    string selectedDate;
    bool fillingCombo;
    ToolTip tip = new ToolTip();

    bool IsMaster { get { return loggedInUser == MasterName; } }
    Dictionary<string, Dictionary<string, Entry>> data { get { return store.Get(currentCampaign); } }

    public MainForm()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        dataFile = Path.Combine(dir, "calendario_dati.json");
        legacyFile = Path.Combine(dir, "calendario_dati.txt");
        settings = new Settings(Path.Combine(dir, "calendario_impostazioni.ini"));

        Text = "Calendario Condiviso";
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.FromArgb(244, 244, 249);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 528);

        year = DateTime.Today.Year;
        month = DateTime.Today.Month;

        LoadData();
        BuildLogin();
        BuildCalendar();
        BuildMenus();
        UpdateUI();
        Shown += delegate
        {
            if (settings.AutoSync && settings.GitHubConfigured) Sync(false);
            else SetStatus("Dati salvati solo su questo PC (sincronizzazione GitHub non configurata)");
            FillUserList();
        };
    }

    // ---------- Persistenza locale ----------
    void LoadData()
    {
        try
        {
            if (File.Exists(dataFile)) store = Store.FromJson(File.ReadAllText(dataFile, Encoding.UTF8), Store.DefaultCampaign);
            else if (File.Exists(legacyFile)) { store = Store.FromLegacyText(File.ReadAllLines(legacyFile, Encoding.UTF8)); SaveData(); }
            else store = new Store();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Errore nel caricamento dei dati:\n" + ex.Message, Text);
            store = new Store();
        }
        store.EnsureOne();
        currentCampaign = store.Find(currentCampaign) ?? store.Campaigns[0];
    }

    void SaveData()
    {
        try { File.WriteAllText(dataFile, store.ToJson(), new UTF8Encoding(false)); }
        catch (Exception ex) { MessageBox.Show("Errore nel salvataggio dei dati:\n" + ex.Message, Text); }
    }

    // ---------- Costruzione interfaccia ----------
    void BuildLogin()
    {
        loginPanel = new Panel { Dock = DockStyle.Fill };

        Label title = new Label { Text = "Accesso al Calendario", Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 18, 380, 40) };

        // --- Accesso: scelta dalla lista dei giocatori approvati ---
        Label prompt = new Label { Text = "Chi sei?", AutoSize = false, Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(85, 66, 250, 24) };
        userList = new ListBox { Bounds = new Rectangle(85, 92, 250, 190), IntegralHeight = false };
        userList.DoubleClick += delegate { HandleLogin(); };
        userList.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; HandleLogin(); }
        };
        Button loginBtn = new Button { Text = "Entra", Bounds = new Rectangle(160, 290, 100, 34),
            BackColor = Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        loginBtn.FlatAppearance.BorderSize = 0;
        loginBtn.Click += delegate { HandleLogin(); };

        // --- Registrazione: richiesta da approvare dal Master ---
        Label sep = new Label { BorderStyle = BorderStyle.Fixed3D, Bounds = new Rectangle(40, 338, 340, 2) };
        Label regLabel = new Label { Text = "Nuovo giocatore? Chiedi al Master di approvarti:", AutoSize = false,
            Font = new Font("Segoe UI", 9f), ForeColor = Color.DimGray, Bounds = new Rectangle(40, 346, 340, 22) };
        usernameBox = new TextBox { Bounds = new Rectangle(40, 372, 230, 28) };
        usernameBox.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; HandleRegister(); }
        };
        Button regBtn = new Button { Text = "Registrati", Bounds = new Rectangle(276, 370, 104, 30),
            FlatStyle = FlatStyle.Flat, ForeColor = Blue, BackColor = Color.White };
        regBtn.FlatAppearance.BorderColor = Blue;
        regBtn.Click += delegate { HandleRegister(); };

        errorLabel = new Label { ForeColor = Color.Red, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(20, 408, 380, 44) };

        LinkLabel refresh = new LinkLabel { Text = "Aggiorna da GitHub", AutoSize = true, Location = new Point(40, 470) };
        refresh.LinkClicked += delegate { Sync(true); FillUserList(); };
        LinkLabel ghSettings = new LinkLabel { Text = "Impostazioni GitHub", AutoSize = true, Location = new Point(240, 470) };
        ghSettings.LinkClicked += delegate { GitHubSettingsDialog(); FillUserList(); };
        loginStatus = new Label { AutoSize = false, Bounds = new Rectangle(20, 496, 380, 22), ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true };

        loginPanel.Controls.AddRange(new Control[] { title, prompt, userList, loginBtn, sep, regLabel, usernameBox, regBtn,
            errorLabel, refresh, ghSettings, loginStatus });
        Controls.Add(loginPanel);
    }

    void BuildCalendar()
    {
        calendarPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

        userLabel = new Label { AutoSize = false, Bounds = new Rectangle(15, 12, 205, 25), ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        dataBtn = new Button { Text = "Dati ▾", Bounds = new Rectangle(226, 10, 88, 30),
            FlatStyle = FlatStyle.Flat, ForeColor = Blue, BackColor = Color.White };
        dataBtn.FlatAppearance.BorderColor = Blue;
        dataBtn.Click += delegate { dataMenu.Show(dataBtn, new Point(0, dataBtn.Height)); };
        Button logoutBtn = new Button { Text = "Logout", Bounds = new Rectangle(320, 10, 85, 30),
            ForeColor = Red, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
        logoutBtn.FlatAppearance.BorderColor = Red;
        logoutBtn.Click += delegate { loggedInUser = null; UpdateUI(); };

        Label campLabel = new Label { Text = "Campagna:", AutoSize = false, Bounds = new Rectangle(15, 50, 78, 28),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray };
        campaignBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(93, 50, 190, 28) };
        campaignBox.SelectedIndexChanged += delegate { OnCampaignChanged(); };
        manageBtn = new Button { Text = "Gestisci ▾", Bounds = new Rectangle(290, 49, 115, 30),
            FlatStyle = FlatStyle.Flat, ForeColor = Blue, BackColor = Color.White };
        manageBtn.FlatAppearance.BorderColor = Blue;
        manageBtn.Click += delegate { manageMenu.Show(manageBtn, new Point(0, manageBtn.Height)); };

        Button prevBtn = MakeNavButton("←", 15);
        prevBtn.Click += delegate { ChangeMonth(-1); };
        Button nextBtn = MakeNavButton("→", 355);
        nextBtn.Click += delegate { ChangeMonth(1); };
        monthLabel = new Label { AutoSize = false, Bounds = new Rectangle(65, 88, 290, 40),
            Font = new Font("Segoe UI", 15f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(51, 51, 51) };

        statusLabel = new Label { AutoSize = false, Bounds = new Rectangle(15, 500, 390, 22), ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

        calendarPanel.Controls.AddRange(new Control[] { userLabel, dataBtn, logoutBtn, campLabel, campaignBox, manageBtn,
            prevBtn, nextBtn, monthLabel, statusLabel });

        const int left = 15, top = 138, cw = 55, ch = 55;
        for (int i = 0; i < 7; i++)
        {
            Label wd = new Label { Text = DayNames[i], Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(85, 85, 85), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                Bounds = new Rectangle(left + i * cw, top, cw, 25) };
            calendarPanel.Controls.Add(wd);
        }
        for (int i = 0; i < 42; i++)
        {
            DayCell c = new DayCell { Bounds = new Rectangle(left + (i % 7) * cw + 1, top + 28 + (i / 7) * ch + 1, cw - 2, ch - 2) };
            c.Click += OnCellClick;
            cells[i] = c;
            calendarPanel.Controls.Add(c);
        }

        Controls.Add(calendarPanel);
    }

    Button MakeNavButton(string text, int x)
    {
        Button b = new Button { Text = text, Bounds = new Rectangle(x, 90, 50, 36), FlatStyle = FlatStyle.Flat,
            ForeColor = Blue, BackColor = Color.White, Font = new Font("Segoe UI", 15f, FontStyle.Bold), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void BuildMenus()
    {
        statusMenu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
        ToolStripMenuItem present = new ToolStripMenuItem("Ci sono") { BackColor = Green, ForeColor = Color.White };
        present.Click += delegate { SaveStatus("presente"); };
        ToolStripMenuItem absent = new ToolStripMenuItem("Manco") { BackColor = Yellow, ForeColor = Color.FromArgb(51, 51, 51) };
        absent.Click += delegate { SaveStatus("assente"); };
        statusMenu.Items.Add(present);
        statusMenu.Items.Add(absent);

        manageMenu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
        manageMenu.Items.Add("Giocatori e richieste...", null, delegate { UsersDialog(); });
        manageMenu.Items.Add("Verifica conflitti di sessione...", null, delegate { ConflictsDialog(); });
        manageMenu.Items.Add("Cambia password del Master...", null, delegate { ChangeMasterPassword(); });
        manageMenu.Items.Add(new ToolStripSeparator());
        manageMenu.Items.Add("Nuova campagna...", null, delegate { NewCampaign(); });
        manageMenu.Items.Add("Rinomina campagna...", null, delegate { RenameCampaign(); });
        manageMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem del = new ToolStripMenuItem("Elimina campagna...") { ForeColor = Red };
        del.Click += delegate { DeleteCampaign(); };
        manageMenu.Items.Add(del);

        dataMenu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
        dataMenu.Items.Add("Sincronizza con GitHub ora", null, delegate { Sync(true); });
        dataMenu.Items.Add("Impostazioni GitHub...", null, delegate { GitHubSettingsDialog(); });
        dataMenu.Items.Add(new ToolStripSeparator());
        dataMenu.Items.Add("Esporta in JSON...", null, delegate { ExportJson(); });
        importMerge = new ToolStripMenuItem("Importa JSON (unisci)... [Master]");
        importMerge.Click += delegate { ImportJson(false); };
        importReplace = new ToolStripMenuItem("Importa JSON (sostituisci tutto)... [Master]");
        importReplace.Click += delegate { ImportJson(true); };
        dataMenu.Items.Add(importMerge);
        dataMenu.Items.Add(importReplace);
        dataMenu.Opening += delegate { importMerge.Visible = importReplace.Visible = IsMaster; };
    }

    // ---------- Campagne ----------
    void FillCampaignBox()
    {
        fillingCombo = true;
        campaignBox.Items.Clear();
        foreach (string c in store.Campaigns) campaignBox.Items.Add(c);
        campaignBox.SelectedItem = currentCampaign;
        fillingCombo = false;
        manageBtn.Visible = IsMaster;
        Text = "Calendario Condiviso - " + currentCampaign;
    }

    void OnCampaignChanged()
    {
        if (fillingCombo || campaignBox.SelectedItem == null) return;
        currentCampaign = (string)campaignBox.SelectedItem;
        Text = "Calendario Condiviso - " + currentCampaign;
        if (loggedInUser != null) { settings.LastCampaign[loggedInUser] = currentCampaign; settings.Save(); }
        RenderCalendar();
    }

    string AskCampaignName(string title, string initial)
    {
        string name = InputBox(title, "Nome della campagna:", initial);
        if (name == null) return null;
        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

    void NewCampaign()
    {
        string name = AskCampaignName("Nuova campagna", "");
        if (name == null) return;
        LoadData();
        if (store.Find(name) != null) { MessageBox.Show("Esiste già una campagna con questo nome.", Text); return; }
        store.AddCampaign(name, Store.Now());
        currentCampaign = name;
        AfterChange("Nuova campagna: " + name);
    }

    void RenameCampaign()
    {
        string oldName = currentCampaign;
        string name = AskCampaignName("Rinomina campagna", oldName);
        if (name == null || name == oldName) return;
        LoadData();
        if (store.Find(oldName) == null) { MessageBox.Show("La campagna non esiste più.", Text); RefreshAll(); return; }
        string existing = store.Find(name);
        if (existing != null && !string.Equals(existing, oldName, StringComparison.OrdinalIgnoreCase))
        { MessageBox.Show("Esiste già una campagna con questo nome.", Text); return; }
        store.Rename(oldName, name);
        currentCampaign = name;
        List<string> users = new List<string>(settings.LastCampaign.Keys);
        foreach (string u in users)
            if (string.Equals(settings.LastCampaign[u], oldName, StringComparison.OrdinalIgnoreCase)) settings.LastCampaign[u] = name;
        settings.Save();
        AfterChange("Campagna rinominata: " + oldName + " -> " + name);
    }

    void DeleteCampaign()
    {
        if (store.Campaigns.Count <= 1) { MessageBox.Show("Deve esistere almeno una campagna.", Text); return; }
        string name = currentCampaign;
        if (MessageBox.Show("Eliminare la campagna \"" + name + "\" e tutte le sue presenze?\n" +
            "Verrà eliminata anche sugli altri PC alla prossima sincronizzazione.", Text,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        LoadData();
        store.Delete(name);
        store.EnsureOne();
        currentCampaign = store.Campaigns[0];
        AfterChange("Campagna eliminata: " + name);
    }

    static string InputBox(string title, string prompt, string initial) { return InputBox(title, prompt, initial, false); }

    static string InputBox(string title, string prompt, string initial, bool password)
    {
        using (Form f = new Form())
        {
            f.Text = title;
            f.Font = new Font("Segoe UI", 10f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(320, 125);
            Label l = new Label { Text = prompt, Bounds = new Rectangle(12, 10, 296, 22) };
            TextBox t = new TextBox { Text = initial, Bounds = new Rectangle(12, 36, 296, 28), UseSystemPasswordChar = password };
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(132, 80, 85, 32) };
            Button cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(223, 80, 85, 32) };
            f.Controls.AddRange(new Control[] { l, t, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? t.Text : null;
        }
    }

    // Salva, aggiorna la vista e (se attiva) sincronizza
    void AfterChange(string description)
    {
        SaveData();
        RefreshAll();
        if (settings.AutoSync && settings.GitHubConfigured) Sync(false, description);
    }

    void RefreshAll()
    {
        currentCampaign = store.Find(currentCampaign) ?? store.Campaigns[0];
        FillCampaignBox();
        UpdateManageButton();
        FillUserList();
        RenderCalendar();
    }

    // ---------- Import / Export JSON ----------
    void ExportJson()
    {
        using (SaveFileDialog d = new SaveFileDialog())
        {
            d.Filter = "File JSON (*.json)|*.json";
            d.FileName = "calendario_" + DateTime.Now.ToString("yyyy-MM-dd") + ".json";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            LoadData();
            try
            {
                File.WriteAllText(d.FileName, store.ToJson(), new UTF8Encoding(false));
                SetStatus("Esportato in " + Path.GetFileName(d.FileName));
            }
            catch (Exception ex) { MessageBox.Show("Esportazione non riuscita:\n" + ex.Message, Text); }
        }
    }

    void ImportJson(bool replace)
    {
        if (!IsMaster) return;
        using (OpenFileDialog d = new OpenFileDialog())
        {
            d.Filter = "File JSON (*.json)|*.json|Tutti i file|*.*";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            Store imported;
            try { imported = Store.FromJson(File.ReadAllText(d.FileName, Encoding.UTF8), currentCampaign); }
            catch (Exception ex) { MessageBox.Show("Il file non è un JSON valido:\n" + ex.Message, Text); return; }

            if (replace)
            {
                if (MessageBox.Show("Sostituire TUTTI i dati attuali con quelli del file?\n" +
                    "Consiglio: prima esporta una copia di sicurezza.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                LoadData();
                // le campagne locali che non sono nel file vengono segnate come eliminate (anche per la sincronizzazione)
                foreach (string c in new List<string>(store.Campaigns)) if (imported.Find(c) == null) imported.Deleted[c] = Store.Now();
                foreach (KeyValuePair<string, long> kv in store.Deleted) if (!imported.Deleted.ContainsKey(kv.Key)) imported.Deleted[kv.Key] = kv.Value;
                // le campagne del file "rinascono" adesso, anche se in passato erano state eliminate
                long now = Store.Now();
                foreach (string c in imported.Campaigns)
                {
                    long t;
                    if (imported.Deleted.TryGetValue(c, out t) && t >= imported.Created[c]) imported.Created[c] = now + 1;
                }
                store = imported;
            }
            else
            {
                LoadData();
                store.Merge(imported);
            }
            AfterChange("Import da " + Path.GetFileName(d.FileName));
            SetStatus("Importato " + Path.GetFileName(d.FileName) + (replace ? " (sostituito)" : " (unito)"));
        }
    }

    // ---------- GitHub ----------
    void Sync(bool manual) { Sync(manual, "Aggiornamento calendario"); }

    void Sync(bool manual, string description)
    {
        if (!settings.GitHubConfigured)
        {
            if (manual && MessageBox.Show("GitHub non è ancora configurato. Aprire le impostazioni?", Text,
                MessageBoxButtons.YesNo) == DialogResult.Yes) GitHubSettingsDialog();
            return;
        }
        Cursor = Cursors.WaitCursor;
        SetStatus("Sincronizzazione con GitHub...");
        Application.DoEvents();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    LoadData();
                    string sha;
                    string remote = GitHub.Download(settings, out sha);
                    if (remote != null) store.Merge(Store.FromJson(remote, Store.DefaultCampaign));
                    SaveData();
                    string json = store.ToJson();
                    if (remote == null || remote.Replace("\r\n", "\n") != json)
                        GitHub.Upload(settings, json, sha, description + " (" + (loggedInUser ?? "?") + ", " + Environment.MachineName + ")");
                    break;
                }
                catch (GitHubException ex)
                {
                    // 409/422: il file è cambiato nel frattempo da un altro PC -> riprova
                    if ((ex.Status == 409 || ex.Status == 422) && attempt < 2) continue;
                    throw;
                }
            }
            RefreshAll();
            SetStatus("Sincronizzato con GitHub alle " + DateTime.Now.ToString("HH:mm"));
        }
        catch (Exception ex)
        {
            SetStatus("Sincronizzazione non riuscita: " + ex.Message);
            if (manual) MessageBox.Show("Sincronizzazione non riuscita:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { Cursor = Cursors.Default; }
    }

    void SetStatus(string s)
    {
        if (statusLabel != null) { statusLabel.Text = s; tip.SetToolTip(statusLabel, s); }
        if (loginStatus != null) { loginStatus.Text = s; tip.SetToolTip(loginStatus, s); }
    }

    void GitHubSettingsDialog()
    {
        using (Form f = new Form())
        {
            f.Text = "Impostazioni GitHub";
            f.Font = new Font("Segoe UI", 10f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(440, 370);

            int y = 12;
            Label info = new Label { Bounds = new Rectangle(12, y, 416, 62), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.5f),
                Text = "I dati vengono salvati come file JSON in un repository GitHub, così più PC restano allineati. " +
                       "Serve un token (github.com > Settings > Developer settings > Fine-grained tokens) " +
                       "con permesso \"Contents: Read and write\" sul repository." };
            y += 66;
            TextBox repo = AddField(f, "Repository (proprietario/nome):", settings.Repo, ref y);
            TextBox branch = AddField(f, "Branch (vuoto = principale):", settings.Branch, ref y);
            TextBox path = AddField(f, "Percorso del file nel repository:", settings.FilePath, ref y);
            TextBox token = AddField(f, settings.Token.Length > 0 ? "Token (vuoto = mantieni quello salvato):" : "Token:", "", ref y);
            token.UseSystemPasswordChar = true;
            CheckBox auto = new CheckBox { Text = "Sincronizza automaticamente all'accesso e a ogni modifica",
                Checked = settings.AutoSync, Bounds = new Rectangle(12, y, 416, 24) };
            y += 34;
            Button ok = new Button { Text = "Salva", DialogResult = DialogResult.OK, Bounds = new Rectangle(252, y, 85, 32) };
            Button cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(343, y, 85, 32) };
            f.Controls.AddRange(new Control[] { info, auto, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog(this) != DialogResult.OK) return;

            string oldRepo = settings.Repo, oldBranch = settings.Branch, oldPath = settings.FilePath;
            settings.Repo = repo.Text.Trim();
            settings.Branch = branch.Text.Trim();
            settings.FilePath = path.Text.Trim().Length > 0 ? path.Text.Trim() : "calendario.json";
            string err = settings.Validate();
            if (err != null)
            {
                settings.Repo = oldRepo; settings.Branch = oldBranch; settings.FilePath = oldPath;
                MessageBox.Show(err + "\nImpostazioni non salvate.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (token.Text.Trim().Length > 0) settings.Token = token.Text.Trim();
            settings.AutoSync = auto.Checked;
            settings.Save();
            if (settings.Token.Length > 0 && !settings.TokenSaved)
                MessageBox.Show("Non è stato possibile cifrare il token con Windows: verrà usato solo fino alla chiusura del programma " +
                    "e non viene salvato su disco.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (settings.GitHubConfigured) Sync(true);
        }
    }

    static TextBox AddField(Form f, string label, string value, ref int y)
    {
        Label l = new Label { Text = label, Bounds = new Rectangle(12, y, 416, 20) };
        TextBox t = new TextBox { Text = value, Bounds = new Rectangle(12, y + 21, 416, 26) };
        f.Controls.Add(l); f.Controls.Add(t);
        y += 54;
        return t;
    }

    // ---------- Logica ----------
    void UpdateUI()
    {
        if (loggedInUser != null)
        {
            loginPanel.Visible = false;
            calendarPanel.Visible = true;
            userLabel.Text = "Accesso come: " + loggedInUser;
            RefreshAll();
        }
        else
        {
            calendarPanel.Visible = false;
            loginPanel.Visible = true;
            usernameBox.Text = "";
            errorLabel.Text = "";
            Text = "Calendario Condiviso";
            FillUserList();
            userList.Focus();
        }
    }

    void FillUserList()
    {
        if (userList == null) return;
        string sel = userList.SelectedItem as string;
        userList.Items.Clear();
        userList.Items.Add(MasterName);
        foreach (string u in store.UsersWithStatus(Store.Approved)) userList.Items.Add(u);
        if (sel != null && userList.Items.Contains(sel)) userList.SelectedItem = sel;
    }

    static string NormalizeName(string name)
    {
        name = name.Trim();
        StringBuilder sb = new StringBuilder();
        foreach (char ch in name) if (!char.IsControl(ch)) sb.Append(ch);
        name = sb.ToString();
        if (name.Length == 0) return "";
        string norm = name.ToLowerInvariant();
        return char.ToUpperInvariant(norm[0]) + norm.Substring(1);
    }

    void HandleRegister()
    {
        errorLabel.ForeColor = Color.Red;
        string name = NormalizeName(usernameBox.Text);
        if (name.Length == 0) { errorLabel.Text = "Scrivi il tuo nome per registrarti."; return; }
        if (name.Length > 30) { errorLabel.Text = "Il nome è troppo lungo (massimo 30 caratteri)."; return; }
        if (string.Equals(name, MasterName, StringComparison.OrdinalIgnoreCase)) { errorLabel.Text = "Il nome Master è riservato."; return; }

        if (settings.AutoSync && settings.GitHubConfigured) Sync(false);
        LoadData();
        string existing = store.FindUser(name);
        string st = existing == null ? null : store.UserStatus(existing);
        if (st == Store.Approved) { errorLabel.Text = "Sei già registrato: scegli il tuo nome dalla lista."; FillUserList(); return; }
        if (st == Store.Pending) { errorLabel.Text = "Richiesta già inviata: attendi l'approvazione del Master."; return; }

        store.SetUser(name, Store.Pending, Store.Now());
        SaveData();
        if (settings.AutoSync && settings.GitHubConfigured) Sync(false, "Richiesta di registrazione: " + name);
        errorLabel.ForeColor = Green;
        errorLabel.Text = "Richiesta inviata! Potrai entrare quando il Master ti approva." +
            (settings.GitHubConfigured ? "" : "\n(Senza GitHub il Master deve approvarti da questo PC.)");
        usernameBox.Text = "";
    }

    bool AskMasterPassword()
    {
        if (store.MasterHash.Length == 0)
        {
            MessageBox.Show("Primo accesso del Master: scegli una password.\nServirà a te per entrare come Master su tutti i PC.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            string p1 = InputBox("Nuova password del Master", "Password (almeno 6 caratteri):", "", true);
            if (p1 == null) return false;
            if (p1.Length < 6) { MessageBox.Show("La password deve avere almeno 6 caratteri.", Text); return false; }
            string p2 = InputBox("Nuova password del Master", "Ripeti la password:", "", true);
            if (p2 == null) return false;
            if (p1 != p2) { MessageBox.Show("Le due password non coincidono.", Text); return false; }
            store.SetMasterPassword(p1);
            SaveData();
            return true;
        }
        string p = InputBox("Accesso Master", "Password del Master:", "", true);
        if (p == null) return false;
        if (store.CheckMasterPassword(p)) return true;
        System.Threading.Thread.Sleep(1000); // rallenta i tentativi
        MessageBox.Show("Password errata.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    void ChangeMasterPassword()
    {
        string p1 = InputBox("Cambia password del Master", "Nuova password (almeno 6 caratteri):", "", true);
        if (p1 == null) return;
        if (p1.Length < 6) { MessageBox.Show("La password deve avere almeno 6 caratteri.", Text); return; }
        string p2 = InputBox("Cambia password del Master", "Ripeti la nuova password:", "", true);
        if (p2 == null) return;
        if (p1 != p2) { MessageBox.Show("Le due password non coincidono.", Text); return; }
        LoadData();
        store.SetMasterPassword(p1);
        AfterChange("Password del Master cambiata");
        MessageBox.Show("Password cambiata.", Text);
    }

    void UsersDialog()
    {
        using (Form f = new Form())
        {
            f.Text = "Giocatori e richieste";
            f.Font = new Font("Segoe UI", 10f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(380, 430);

            Label l1 = new Label { Text = "Richieste in attesa", Font = new Font("Segoe UI", 10f, FontStyle.Bold), Bounds = new Rectangle(12, 10, 356, 22) };
            ListBox pending = new ListBox { Bounds = new Rectangle(12, 34, 240, 140), IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
            Button approve = new Button { Text = "Approva", Bounds = new Rectangle(260, 34, 108, 32), ForeColor = Green };
            Button reject = new Button { Text = "Rifiuta", Bounds = new Rectangle(260, 72, 108, 32), ForeColor = Red };
            Label l2 = new Label { Text = "Giocatori approvati", Font = new Font("Segoe UI", 10f, FontStyle.Bold), Bounds = new Rectangle(12, 186, 356, 22) };
            ListBox approved = new ListBox { Bounds = new Rectangle(12, 210, 240, 170), IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
            Button remove = new Button { Text = "Rimuovi", Bounds = new Rectangle(260, 210, 108, 32), ForeColor = Red };
            Button close = new Button { Text = "Chiudi", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(283, 390, 85, 32) };

            Action fill = delegate
            {
                pending.Items.Clear(); approved.Items.Clear();
                foreach (string u in store.UsersWithStatus(Store.Pending)) pending.Items.Add(u);
                foreach (string u in store.UsersWithStatus(Store.Approved)) approved.Items.Add(u);
            };
            Action<ListBox, string, string> apply = delegate(ListBox box, string status, string what)
            {
                if (box.SelectedItems.Count == 0) return;
                List<string> names = new List<string>();
                foreach (object o in box.SelectedItems) names.Add((string)o);
                if (status == Store.Rejected && MessageBox.Show(what + ": " + string.Join(", ", names.ToArray()) + "?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                LoadData();
                long now = Store.Now();
                foreach (string n in names) store.SetUser(n, status, now);
                AfterChange(what + ": " + string.Join(", ", names.ToArray()));
                fill();
            };
            approve.Click += delegate { apply(pending, Store.Approved, "Approvati"); };
            reject.Click += delegate { apply(pending, Store.Rejected, "Rifiutati"); };
            remove.Click += delegate { apply(approved, Store.Rejected, "Rimossi"); };

            f.Controls.AddRange(new Control[] { l1, pending, approve, reject, l2, approved, remove, close });
            f.CancelButton = close;
            fill();
            f.ShowDialog(this);
        }
        UpdateManageButton();
    }

    static string FormatDate(string key)
    {
        DateTime d;
        if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return key;
        return d.ToString("dddd d MMMM yyyy", new CultureInfo("it-IT"));
    }

    void ConflictsDialog()
    {
        using (Form f = new Form())
        {
            f.Text = "Conflitti di sessione del Master";
            f.Font = new Font("Segoe UI", 10f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(520, 400);

            Label info = new Label { Bounds = new Rectangle(12, 10, 496, 40), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 9f),
                Text = "Giorni in cui il Master risulta \"Ci sono\" in più campagne. Il Master è uno solo: per ogni giorno scegli quale campagna tiene la sessione." };
            ListView list = new ListView { Bounds = new Rectangle(12, 54, 496, 260), View = View.Details, FullRowSelect = true,
                MultiSelect = false, HideSelection = false };
            list.Columns.Add("Giorno", 190);
            list.Columns.Add("Campagne in conflitto", 300);
            CheckBox past = new CheckBox { Text = "Mostra anche i giorni passati", Bounds = new Rectangle(12, 320, 300, 24) };
            Label summary = new Label { Bounds = new Rectangle(12, 346, 496, 20), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 9f) };
            Button resolve = new Button { Text = "Risolvi...", Bounds = new Rectangle(12, 368, 100, 30) };
            Button go = new Button { Text = "Vai al giorno", Bounds = new Rectangle(118, 368, 110, 30) };
            Button close = new Button { Text = "Chiudi", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(423, 368, 85, 30) };

            Action fill = delegate
            {
                list.Items.Clear();
                string today = Store.Today();
                int shown = 0;
                foreach (KeyValuePair<string, List<string>> kv in store.MasterConflicts())
                {
                    if (!past.Checked && string.CompareOrdinal(kv.Key, today) < 0) continue;
                    ListViewItem it = new ListViewItem(FormatDate(kv.Key));
                    it.SubItems.Add(string.Join(", ", kv.Value.ToArray()));
                    it.Tag = kv.Key;
                    if (string.CompareOrdinal(kv.Key, today) < 0) it.ForeColor = Color.Gray;
                    list.Items.Add(it);
                    shown++;
                }
                summary.Text = shown == 0 ? "\u2714 Nessun conflitto" + (past.Checked ? "." : " nei prossimi giorni.") : (shown == 1 ? "1 giorno in conflitto." : shown + " giorni in conflitto.");
                summary.ForeColor = shown == 0 ? Green : Red;
                resolve.Enabled = go.Enabled = shown > 0;
                if (shown > 0) list.Items[0].Selected = true;
            };
            past.CheckedChanged += delegate { fill(); };

            Action goTo = delegate
            {
                if (list.SelectedItems.Count == 0) return;
                DateTime d = DateTime.ParseExact((string)list.SelectedItems[0].Tag, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                year = d.Year; month = d.Month;
                RenderCalendar();
                f.Close();
            };
            go.Click += delegate { goTo(); };
            list.DoubleClick += delegate { goTo(); };

            resolve.Click += delegate
            {
                if (list.SelectedItems.Count == 0) return;
                string date = (string)list.SelectedItems[0].Tag;
                List<string> camps;
                if (!store.MasterConflicts().TryGetValue(date, out camps)) { fill(); return; }
                string keep = ChooseCampaign("Risolvi conflitto", "Il " + FormatDate(date) + "\nquale campagna tiene la sessione?", camps);
                if (keep == null) return;
                LoadData();
                long now = Store.Now();
                List<string> current;
                if (store.MasterSessions().TryGetValue(date, out current))
                    foreach (string c in current)
                        if (!string.Equals(c, keep, StringComparison.OrdinalIgnoreCase)) store.Set(c, Store.MasterUser, date, "assente", now);
                AfterChange("Conflitto del " + date + " risolto: sessione a " + keep);
                fill();
            };

            f.Controls.AddRange(new Control[] { info, list, past, summary, resolve, go, close });
            f.CancelButton = close;
            fill();
            f.ShowDialog(this);
        }
    }

    string ChooseCampaign(string title, string prompt, List<string> options)
    {
        using (Form f = new Form())
        {
            f.Text = title;
            f.Font = new Font("Segoe UI", 10f);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false; f.MaximizeBox = false; f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.ClientSize = new Size(340, 150);
            Label l = new Label { Text = prompt, Bounds = new Rectangle(12, 10, 316, 44) };
            ComboBox cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(12, 60, 316, 28) };
            foreach (string o in options) cb.Items.Add(o);
            cb.SelectedIndex = 0;
            Button ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(152, 106, 85, 32) };
            Button cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(243, 106, 85, 32) };
            f.Controls.AddRange(new Control[] { l, cb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog(this) == DialogResult.OK ? (string)cb.SelectedItem : null;
        }
    }

    void UpdateManageButton()
    {
        int n = store.UsersWithStatus(Store.Pending).Count + store.UpcomingConflicts();
        manageBtn.Text = n > 0 ? "Gestisci (" + n + ") ▾" : "Gestisci ▾";
        manageBtn.ForeColor = n > 0 ? Red : Blue;
        manageBtn.FlatAppearance.BorderColor = manageBtn.ForeColor;
    }

    void HandleLogin()
    {
        errorLabel.ForeColor = Color.Red;
        string name = userList.SelectedItem as string;
        if (name == null) { errorLabel.Text = "Scegli il tuo nome dalla lista (o registrati qui sotto)."; return; }
        LoadData();
        if (name == MasterName)
        {
            if (!AskMasterPassword()) return;
        }
        else if (store.UserStatus(name) != Store.Approved)
        {
            errorLabel.Text = "Questo giocatore non è più approvato.";
            FillUserList();
            return;
        }
        loggedInUser = name;
        string last;
        if (settings.LastCampaign.TryGetValue(loggedInUser, out last) && store.Find(last) != null) currentCampaign = store.Find(last);
        UpdateUI();
        SetStatus(settings.GitHubConfigured ? "" : "Dati salvati solo su questo PC (sincronizzazione GitHub non configurata)");
        if (settings.AutoSync && settings.GitHubConfigured) Sync(false);
    }

    void ChangeMonth(int delta)
    {
        month += delta;
        if (month < 1) { month = 12; year--; }
        if (month > 12) { month = 1; year++; }
        RenderCalendar();
    }

    void OnCellClick(object sender, EventArgs e)
    {
        DayCell c = (DayCell)sender;
        if (c.DateKey == null || loggedInUser == null) return;
        selectedDate = c.DateKey;
        Point p = new Point(c.Width / 2 - statusMenu.Width / 2, -statusMenu.Height - 4);
        statusMenu.Show(c, p);
    }

    void SaveStatus(string status)
    {
        if (selectedDate == null || loggedInUser == null) return;
        string campaign = currentCampaign;
        LoadData(); // non sovrascrivere modifiche fatte da altri nel frattempo
        currentCampaign = store.Find(campaign) ?? campaign;

        List<string> moveFrom = new List<string>();
        if (IsMaster && status == "presente")
        {
            List<string> others;
            if (store.MasterSessions().TryGetValue(selectedDate, out others))
            {
                others = others.FindAll(delegate(string c) { return !string.Equals(c, currentCampaign, StringComparison.OrdinalIgnoreCase); });
                if (others.Count > 0)
                {
                    DialogResult r = MessageBox.Show(
                        "Il " + FormatDate(selectedDate) + " hai già una sessione di: " + string.Join(", ", others.ToArray()) + ".\n" +
                        "Il Master non può condurre due sessioni lo stesso giorno.\n\n" +
                        "Sì = sposta la sessione su \"" + currentCampaign + "\" (segna assente nelle altre)\n" +
                        "No = segna comunque (resterà un conflitto da risolvere)\n" +
                        "Annulla = non fare nulla",
                        "Conflitto di sessione", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (r == DialogResult.Cancel) return;
                    if (r == DialogResult.Yes) moveFrom = others;
                }
            }
        }

        long now = Store.Now();
        store.Set(currentCampaign, loggedInUser, selectedDate, status, now);
        foreach (string c in moveFrom) store.Set(c, loggedInUser, selectedDate, "assente", now);
        AfterChange(loggedInUser + ": " + selectedDate + " " + status + " [" + currentCampaign + "]" +
            (moveFrom.Count > 0 ? " (sessione spostata da " + string.Join(", ", moveFrom.ToArray()) + ")" : ""));
    }

    void RenderCalendar()
    {
        monthLabel.Text = MonthNames[month - 1] + " " + year;
        DateTime first = new DateTime(year, month, 1);
        int offset = (int)first.DayOfWeek; // 0 = domenica
        int days = DateTime.DaysInMonth(year, month);
        DateTime today = DateTime.Today;
        Dictionary<string, Dictionary<string, Entry>> d = data;
        SortedDictionary<string, List<string>> conflicts = IsMaster ? store.MasterConflicts() : new SortedDictionary<string, List<string>>();

        for (int i = 0; i < 42; i++)
        {
            DayCell c = cells[i];
            int day = i - offset + 1;
            if (day < 1 || day > days)
            {
                c.DateKey = null; c.Day = 0; c.Present = c.Absent = 0;
                c.MineStatus = null; c.IsToday = false; c.Conflict = false;
                c.Cursor = Cursors.Default;
                tip.SetToolTip(c, null);
                c.Invalidate();
                continue;
            }
            string key = first.AddDays(day - 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            c.DateKey = key;
            c.Day = day;
            c.IsToday = year == today.Year && month == today.Month && day == today.Day;
            c.Cursor = Cursors.Hand;

            List<string> pres = new List<string>(), abs = new List<string>();
            string mine = null;
            foreach (KeyValuePair<string, Dictionary<string, Entry>> u in d)
            {
                Entry e;
                if (!u.Value.TryGetValue(key, out e)) continue;
                if (e.Status == "presente") pres.Add(u.Key);
                else if (e.Status == "assente") abs.Add(u.Key);
                if (string.Equals(u.Key, loggedInUser, StringComparison.OrdinalIgnoreCase)) mine = e.Status;
            }
            c.Present = pres.Count; c.Absent = abs.Count;
            c.MineStatus = mine;

            string t = null;
            if (pres.Count > 0 || abs.Count > 0)
            {
                t = "";
                if (pres.Count > 0) t += "Presenti: " + string.Join(", ", pres.ToArray());
                if (abs.Count > 0) t += (t.Length > 0 ? "\n" : "") + "Assenti: " + string.Join(", ", abs.ToArray());
            }
            List<string> conf;
            c.Conflict = conflicts.TryGetValue(key, out conf);
            if (c.Conflict)
                t = (t == null ? "" : t + "\n") + "\u26A0 Conflitto: sessioni di " + string.Join(", ", conf.ToArray());
            tip.SetToolTip(c, t);
            c.Invalidate();
        }
    }

    // ---------- Cella giorno disegnata a mano ----------
    class DayCell : Control
    {
        public string DateKey;
        public int Day;
        public bool IsToday;
        public int Present, Absent;
        public string MineStatus;
        public bool Conflict;
        bool hover;

        public DayCell()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.White);
            if (Day == 0) return;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fg = Color.FromArgb(51, 51, 51);
            if (IsToday)
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = Rounded(r, 6))
                using (SolidBrush b = new SolidBrush(Blue)) g.FillPath(b, path);
                fg = Color.White;
            }
            else if (hover)
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = Rounded(r, 6))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(238, 238, 238))) g.FillPath(b, path);
            }

            if (Conflict)
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 6))
                using (Pen pen = new Pen(Red, 2f)) g.DrawPath(pen, path);
                using (Font fw = new Font("Segoe UI", 8f, FontStyle.Bold))
                    TextRenderer.DrawText(g, "!", fw, new Rectangle(Width - 14, 2, 12, 14), IsToday ? Color.White : Red,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            }

            using (Font f = new Font("Segoe UI", 10.5f, IsToday ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(g, Day.ToString(), f, new Rectangle(0, 4, Width, 22), fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            List<KeyValuePair<string, Color>> parts = new List<KeyValuePair<string, Color>>();
            List<bool> underline = new List<bool>();
            if (Present > 0) { parts.Add(new KeyValuePair<string, Color>("P: " + Present, IsToday ? Color.White : Green)); underline.Add(MineStatus == "presente"); }
            if (Absent > 0) { parts.Add(new KeyValuePair<string, Color>("A: " + Absent, IsToday ? Color.White : Red)); underline.Add(MineStatus == "assente"); }
            if (parts.Count == 0) return;

            Font[] fonts = new Font[parts.Count];
            int total = 0; int gap = 4;
            int[] widths = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                fonts[i] = new Font("Segoe UI", 7.5f, underline[i] ? FontStyle.Bold | FontStyle.Underline : FontStyle.Bold);
                widths[i] = TextRenderer.MeasureText(g, parts[i].Key, fonts[i], Size.Empty, TextFormatFlags.NoPadding).Width;
                total += widths[i];
            }
            total += gap * (parts.Count - 1);
            int x = (Width - total) / 2;
            for (int i = 0; i < parts.Count; i++)
            {
                TextRenderer.DrawText(g, parts[i].Key, fonts[i], new Point(x, Height - 20), parts[i].Value, TextFormatFlags.NoPadding);
                x += widths[i] + gap;
                fonts[i].Dispose();
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int rad)
        {
            System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
