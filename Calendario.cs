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
    public static object Parse(string s) { int i = 0; object v = Value(s, ref i); Ws(s, ref i); if (i != s.Length) throw Err(i); return v; }

    static Exception Err(int i) { return new FormatException("JSON non valido (posizione " + i + ")"); }
    static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    static object Value(string s, ref int i)
    {
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
                o[k] = Value(s, ref i);
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
                a.Add(Value(s, ref i));
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
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
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

    public List<string> Campaigns = new List<string>();
    public Dictionary<string, long> Created = new Dictionary<string, long>(CI);
    public Dictionary<string, long> Deleted = new Dictionary<string, long>(CI);   // campagne eliminate (per la sincronizzazione)
    public Dictionary<string, Dictionary<string, Dictionary<string, Entry>>> Data =
        new Dictionary<string, Dictionary<string, Dictionary<string, Entry>>>(CI);

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

    public void EnsureOne() { if (Campaigns.Count == 0) AddCampaign(DefaultCampaign, 0); }

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
        sb.Append(Campaigns.Count > 0 ? "\n  }\n}\n" : "}\n}\n");
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
                    if (d.Value is string) s.Set(legacyCampaign, u.Key, d.Key, (string)d.Value, 0);
            s.EnsureOne();
            return s;
        }
        List<object> camps = root.ContainsKey("campagne") ? root["campagne"] as List<object> : null;
        if (camps != null)
            foreach (object o in camps)
            {
                Dictionary<string, object> c = Json.Obj(o);
                if (c.ContainsKey("nome") && c["nome"] is string)
                    s.AddCampaign((string)c["nome"], c.ContainsKey("creata") ? Json.Num(c["creata"]) : 0);
            }
        if (root.ContainsKey("eliminate"))
            foreach (KeyValuePair<string, object> kv in Json.Obj(root["eliminate"])) s.Deleted[kv.Key] = Json.Num(kv.Value);
        if (root.ContainsKey("presenze"))
            foreach (KeyValuePair<string, object> c in Json.Obj(root["presenze"]))
                foreach (KeyValuePair<string, object> u in Json.Obj(c.Value))
                    foreach (KeyValuePair<string, object> d in Json.Obj(u.Value))
                    {
                        Dictionary<string, object> e = Json.Obj(d.Value);
                        if (e.ContainsKey("stato") && e["stato"] is string)
                            s.Set(c.Key, u.Key, d.Key, (string)e["stato"], e.ContainsKey("t") ? Json.Num(e["t"]) : 0);
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
    public Dictionary<string, string> LastCampaign = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    readonly string file;

    public Settings(string file) { this.file = file; Load(); }

    public bool GitHubConfigured { get { return Repo.Trim().Length > 0 && Token.Length > 0 && Repo.Contains("/"); } }

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
        l.Add("token=" + Protect(Token));
        foreach (KeyValuePair<string, string> kv in LastCampaign) l.Add("last." + kv.Key + "=" + kv.Value);
        try { File.WriteAllLines(file, l.ToArray(), Encoding.UTF8); } catch { }
    }

    // Il token viene cifrato con l'account Windows (DPAPI): il file copiato su un altro PC non lo rivela.
    static string Protect(string s)
    {
        if (s.Length == 0) return "";
        try { return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
        catch { return "plain:" + s; }
    }

    static string Unprotect(string s)
    {
        try
        {
            if (s.StartsWith("dpapi:")) return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.Substring(6)), null, DataProtectionScope.CurrentUser));
            if (s.StartsWith("plain:")) return s.Substring(6);
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
    Label errorLabel, userLabel, monthLabel, statusLabel;
    ComboBox campaignBox;
    Button manageBtn, dataBtn;
    ContextMenuStrip statusMenu, manageMenu, dataMenu;
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
            AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 90, 380, 40) };
        Label prompt = new Label { Text = "Inserisci il tuo nome:", AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 140, 380, 25) };
        usernameBox = new TextBox { Bounds = new Rectangle(85, 175, 250, 28) };
        usernameBox.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; HandleLogin(); }
        };
        Button loginBtn = new Button { Text = "Accedi", Bounds = new Rectangle(160, 215, 100, 34),
            BackColor = Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        loginBtn.FlatAppearance.BorderSize = 0;
        loginBtn.Click += delegate { HandleLogin(); };
        errorLabel = new Label { ForeColor = Color.Red, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(20, 260, 380, 25) };
        Label hint = new Label { Text = "Puoi entrare con qualsiasi nome.\nIl Master può creare e gestire le campagne.",
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5f), AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(20, 295, 380, 45) };

        loginPanel.Controls.AddRange(new Control[] { title, prompt, usernameBox, loginBtn, errorLabel, hint });
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
        campaignBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(93, 50, 212, 28) };
        campaignBox.SelectedIndexChanged += delegate { OnCampaignChanged(); };
        manageBtn = new Button { Text = "Gestisci ▾", Bounds = new Rectangle(312, 49, 93, 30),
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
        dataMenu.Items.Add("Importa JSON (unisci)...", null, delegate { ImportJson(false); });
        dataMenu.Items.Add("Importa JSON (sostituisci tutto)...", null, delegate { ImportJson(true); });
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

    static string InputBox(string title, string prompt, string initial)
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
            TextBox t = new TextBox { Text = initial, Bounds = new Rectangle(12, 36, 296, 28) };
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

    void SetStatus(string s) { if (statusLabel != null) { statusLabel.Text = s; tip.SetToolTip(statusLabel, s); } }

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

            settings.Repo = repo.Text.Trim();
            settings.Branch = branch.Text.Trim();
            settings.FilePath = path.Text.Trim().Length > 0 ? path.Text.Trim() : "calendario.json";
            if (token.Text.Trim().Length > 0) settings.Token = token.Text.Trim();
            settings.AutoSync = auto.Checked;
            settings.Save();
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
            usernameBox.Focus();
        }
    }

    void HandleLogin()
    {
        string name = usernameBox.Text.Trim();
        if (name.Length == 0) { errorLabel.Text = "Per favore, inserisci il tuo nome."; return; }
        string norm = name.ToLowerInvariant();
        loggedInUser = char.ToUpperInvariant(norm[0]) + norm.Substring(1);
        LoadData();
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
        store.Set(currentCampaign, loggedInUser, selectedDate, status, Store.Now());
        AfterChange(loggedInUser + ": " + selectedDate + " " + status + " [" + currentCampaign + "]");
    }

    void RenderCalendar()
    {
        monthLabel.Text = MonthNames[month - 1] + " " + year;
        DateTime first = new DateTime(year, month, 1);
        int offset = (int)first.DayOfWeek; // 0 = domenica
        int days = DateTime.DaysInMonth(year, month);
        DateTime today = DateTime.Today;
        Dictionary<string, Dictionary<string, Entry>> d = data;

        for (int i = 0; i < 42; i++)
        {
            DayCell c = cells[i];
            int day = i - offset + 1;
            if (day < 1 || day > days)
            {
                c.DateKey = null; c.Day = 0; c.Present = c.Absent = 0;
                c.MineStatus = null; c.IsToday = false;
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
