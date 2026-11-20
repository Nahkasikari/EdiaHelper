// NCS Expert coding backups.
//
// NCS Expert writes what it read from a control unit into its WORK folder and overwrites the files
// with every new read. This watches the folder (Windows tells us when a file changes, nothing runs
// in between) and keeps a copy every time NCS Expert writes one, even if the content is the same as
// an earlier copy, so every state can be found by its time:
//
//   <backup folder>\<date> <last 7 of VIN, or chassis>\<module> <time>.TRC            FSW_PSW.TRC (the coding, readable)
//                                                       <module> NETTODAT <time>.TRC   NETTODAT.TRC (the raw coding bytes)
//                                                       FA <time>.TRC                  FA.TRC (the vehicle order)
//
// The module (for example CIC.C1A) is recognised from the parameter names in FSW_PSW.TRC: every
// module description (CABD file DATEN\<chassis>\<module>.Cxx) lists its parameters by number and
// SWTFSWxx.DAT gives the names; the module that contains all of the trace's parameters, with the
// fewest others, is the one that was read. The VIN is taken from NCS Expert's window; if it is not
// there, the folder is named after the chassis in FA.TRC.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class NcsBackup
{
    static readonly string[] Watched = { "FSW_PSW.TRC", "NETTODAT.TRC", "FA.TRC" };
    static readonly string StateFile = Path.Combine(Log.Folder, "NcsBackup.state");
    static readonly object gate = new object();
    static readonly Dictionary<string, string> last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static FileSystemWatcher watcher;
    static Timer timer;
    static Info seen;              // what NCS Expert showed when the file was written
    static DateTime seenAt;
    static DateTime changedAt;
    static int retries;

    public static event Action<string> Saved;     // short text for the tray
    public static string LastSaved = "";

    public class Info { public string Module, Ecu, Vin, Chassis, Raw = ""; public bool FromTrace; }

    public static string DefaultBackupDir(string work)
    {
        try { return Path.Combine(Path.GetDirectoryName(work.TrimEnd('\\')), "TRC_BACKUP"); }
        catch { return @"C:\NCSEXPER\TRC_BACKUP"; }
    }

    public static string BackupDir
    {
        get { return Settings.NcsBackupDir != "" ? Settings.NcsBackupDir : DefaultBackupDir(Settings.NcsWork); }
    }

    public static bool Watching { get { return watcher != null; } }

    // (Re)start watching with the current settings
    public static void Restart()
    {
        lock (gate)
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); watcher = null; }
            if (!Settings.NcsBackup || !Directory.Exists(Settings.NcsWork)) return;
            try
            {
                bool firstRun = !LoadState();
                watcher = new FileSystemWatcher(Settings.NcsWork)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                watcher.Changed += OnChange;
                watcher.Created += OnChange;
                watcher.Renamed += (s, e) => OnChange(s, e);
                watcher.Error += (s, e) => { Log.Write("NCS backup: folder watch error, restarting"); ThreadPool.QueueUserWorkItem(_ => Restart()); };
                watcher.EnableRaisingEvents = true;
                if (timer == null) timer = new Timer(_ => Scan());
                if (firstRun) Remember();               // first start: today's files are not new reads
                else timer.Change(1500, Timeout.Infinite);   // save what was read while we were not running
                Log.WriteOnce("NCS backup: watching " + Settings.NcsWork + ", backups in " + BackupDir);
            }
            catch (Exception ex)
            {
                watcher = null;
                Log.Write("NCS backup: cannot watch " + Settings.NcsWork + ": " + ex.Message);
            }
        }
    }

    static bool IsWatched(string name)
    {
        foreach (string w in Watched) if (string.Equals(w, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static void OnChange(object sender, FileSystemEventArgs e)
    {
        if (!IsWatched(e.Name)) return;
        // Look at NCS Expert's window right away, while it still shows the module it is reading
        if ((DateTime.Now - seenAt).TotalSeconds > 2)
        {
            Info i = ReadNcsWindow();
            lock (gate) { seen = i; seenAt = DateTime.Now; }
        }
        lock (gate) { changedAt = DateTime.Now; retries = 0; if (timer != null) timer.Change(1500, Timeout.Infinite); }
    }

    static byte[] ReadShared(string path)
    {
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (MemoryStream ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static string Md5(byte[] b)
    {
        using (MD5 md5 = MD5.Create()) return BitConverter.ToString(md5.ComputeHash(b));
    }

    // First start: remember what is there without saving it
    static void Remember()
    {
        foreach (string n in Watched)
        {
            try { string p = Path.Combine(Settings.NcsWork, n); if (File.Exists(p)) last[n] = Stamp(p); }
            catch { }
        }
        SaveState();
    }

    // A file counts as newly written when its time or size changes
    static string Stamp(string path)
    {
        FileInfo fi = new FileInfo(path);
        return fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
    }

    static void Scan()
    {
        lock (gate)
        {
            if (watcher == null) return;
            try
            {
                Info info = null;
                List<string> done = new List<string>();
                // FSW_PSW first so NETTODAT gets the same module name
                foreach (string n in Watched)
                {
                    string p = Path.Combine(Settings.NcsWork, n);
                    if (!File.Exists(p)) continue;
                    byte[] data;
                    DateTime written = File.GetLastWriteTime(p);
                    string stamp = Stamp(p), old;
                    if (last.TryGetValue(n, out old) && old == stamp) continue;
                    try
                    {
                        if ((DateTime.Now - written).TotalSeconds < 1) throw new IOException("still being written");
                        data = ReadShared(p);
                    }
                    catch (IOException)
                    {
                        if (++retries < 20) { timer.Change(1000, Timeout.Infinite); return; }
                        continue;
                    }
                    if (data.Length == 0) continue;
                    if (info == null) info = Describe(written);
                    // The module is recognised from the parameters in the trace itself (like NCS Dummy does);
                    // the name shown in NCS Expert's window is only used when that does not find one
                    if (n == "FSW_PSW.TRC")
                    {
                        string m = CabdMatch.Identify(data, info.Chassis);
                        if (m != null) { info.Module = m; info.FromTrace = true; }
                    }
                    string saved = Store(n, data, written, info);
                    last[n] = stamp;
                    if (saved != null) done.Add(saved);
                }
                SaveState();
                if (done.Count > 0)
                {
                    string folder = Path.GetFileName(Path.GetDirectoryName(done[0]));
                    List<string> names = new List<string>();
                    foreach (string d in done) names.Add(Path.GetFileName(d));
                    LastSaved = DateTime.Now.ToString("HH:mm") + "  " + folder + "\\" + string.Join(", ", names.ToArray());
                    Log.Write("NCS backup: saved " + string.Join(", ", names.ToArray()) + " in " + folder);
                    bool moduleFile = done.Exists(d => !Path.GetFileName(d).StartsWith("FA "));
                    if (moduleFile && info != null && info.FromTrace)
                        Log.Write("NCS backup: module " + info.Module + " recognised from the trace (" + CabdMatch.LastDetail + ")");
                    if (moduleFile && info != null && info.Module == null && info.Ecu == null)
                        Log.Write("NCS backup: module not found. " + (info.Raw == "" ? "NCS Expert window not found." :
                                  "NCS Expert window: " + (info.Raw.Length > 800 ? info.Raw.Substring(0, 800) : info.Raw)));
                    Action<string> ev = Saved;
                    if (ev != null)
                    {
                        string mod = Regex.Replace(names[0], @"( NETTODAT)? [0-9.]+(-[0-9]+)?\.TRC$", "");
                        ev("Saved " + mod + " to NCS backups (" + folder + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteOnce("NCS backup failed: " + ex.Message);
            }
        }
    }

    // Puts one file into the backup folder with the time NCS Expert wrote it; returns its path,
    // or null if this very file (same time, same content) was already saved
    static string Store(string name, byte[] data, DateTime written, Info info)
    {
        string label = info.Vin != null ? info.Vin : info.Chassis;
        string dir = Path.Combine(BackupDir, written.ToString("yyyy-MM-dd") + (label != null ? " " + label : ""));
        Directory.CreateDirectory(dir);

        string module = info.Module ?? info.Ecu;
        string baseName;
        if (name.Equals("FSW_PSW.TRC", StringComparison.OrdinalIgnoreCase)) baseName = module ?? "FSW_PSW";
        else if (name.Equals("NETTODAT.TRC", StringComparison.OrdinalIgnoreCase)) baseName = module != null ? module + " NETTODAT" : "NETTODAT";
        else baseName = "FA";

        string stem = Path.Combine(dir, baseName + " " + written.ToString("HH.mm.ss"));
        string target = stem + ".TRC";
        for (int k = 2; File.Exists(target); k++)
        {
            if (SameContent(target, data)) return null;
            target = stem + "-" + k + ".TRC";
        }
        File.WriteAllBytes(target, data);
        File.SetLastWriteTime(target, written);
        return target;
    }

    static bool SameContent(string path, byte[] data)
    {
        try { return Md5(File.ReadAllBytes(path)) == Md5(data); } catch { return false; }
    }

    // Module, VIN and chassis for the file that was just written
    static Info Describe(DateTime written)
    {
        Info info = null;
        lock (gate) { if (seen != null && (DateTime.Now - seenAt).TotalMinutes < 2) info = seen; }
        if (info == null) info = ReadNcsWindow();
        if (info.Chassis == null) info.Chassis = ChassisFromFa();
        return info;
    }

    static string ChassisFromFa()
    {
        try
        {
            string p = Path.Combine(Settings.NcsWork, "FA.TRC");
            if (!File.Exists(p)) return null;
            Match m = Regex.Match(Encoding.Default.GetString(ReadShared(p)), @"^\s*([A-Z][0-9]{2,3}[A-Z]?)_");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    static bool LoadState()
    {
        last.Clear();
        if (!File.Exists(StateFile)) return false;
        try
        {
            foreach (string line in File.ReadAllLines(StateFile))
            {
                string[] kv = line.Split(new[] { '=' }, 2);
                if (kv.Length == 2) last[kv[0]] = kv[1];
            }
            return true;
        }
        catch { return false; }
    }

    static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in last) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(StateFile, lines.ToArray());
        }
        catch { }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + BackupDir + "\"");
        }
        catch (Exception ex) { Log.Write("NCS backup: cannot open folder: " + ex.Message); }
    }

    // ---- Reading NCS Expert's window ----

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendText(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendInt(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    const uint WM_GETTEXT = 0x0D, WM_GETTEXTLENGTH = 0x0E, LB_GETTEXT = 0x189, LB_GETTEXTLEN = 0x18A, LB_GETCURSEL = 0x188;
    const uint SMTO_ABORTIFHUNG = 2;

    static string Text(IntPtr h)
    {
        IntPtr len;
        if (SendInt(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out len) == IntPtr.Zero) return "";
        int n = (int)len;
        if (n <= 0) return "";
        if (n > 2000) n = 2000;
        StringBuilder sb = new StringBuilder(n + 1);
        IntPtr r;
        SendText(h, WM_GETTEXT, (IntPtr)(n + 1), sb, SMTO_ABORTIFHUNG, 200, out r);
        return sb.ToString();
    }

    static string SelectedListItem(IntPtr h)
    {
        IntPtr sel, len, r;
        if (SendInt(h, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out sel) == IntPtr.Zero) return "";
        if ((int)sel < 0) return "";
        if (SendInt(h, LB_GETTEXTLEN, sel, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out len) == IntPtr.Zero) return "";
        int n = (int)len;
        if (n <= 0 || n > 500) return "";
        // LB_GETTEXT has no buffer size: leave plenty of room in case the item changed in between
        StringBuilder sb = new StringBuilder(n * 2 + 64);
        SendText(h, LB_GETTEXT, sel, sb, SMTO_ABORTIFHUNG, 200, out r);
        return sb.ToString();
    }

    public static Info ReadNcsWindow()
    {
        Info info = new Info();
        try
        {
            HashSet<uint> pids = new HashSet<uint>();
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                string n = p.ProcessName.ToUpperInvariant();
                if (n == "NCSEXPER") pids.Add((uint)p.Id);
            }
            if (pids.Count == 0) return info;

            StringBuilder raw = new StringBuilder();
            EnumProc child = (h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                StringBuilder cls = new StringBuilder(64);
                GetClassName(h, cls, 64);
                string t = Text(h);
                if (cls.ToString().IndexOf("ListBox", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string s = SelectedListItem(h);
                    if (s != "") t = (t + " [selected: " + s + "]").Trim();
                }
                t = t.Replace("\r", " ").Replace("\n", " ").Trim();
                if (t != "") raw.Append(t).Append(" | ");
                return true;
            };
            EnumWindows((h, l) =>
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
                string t = Text(h).Trim();
                if (t != "") raw.Append(t).Append(" | ");
                EnumChildWindows(h, child, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            info.Raw = raw.ToString();
            Parse(info);
        }
        catch (Exception ex) { info.Raw = "(" + ex.Message + ")"; }
        return info;
    }

    static void Parse(Info info)
    {
        string s = info.Raw;
        // CABD name, for example CIC.C1A or KMB_E60.C10
        Match m = Regex.Match(s, @"\b([A-Z][A-Z0-9_]{1,15}\.C[0-9A-F]{2})\b", RegexOptions.IgnoreCase);
        if (m.Success) info.Module = m.Groups[1].Value.ToUpperInvariant();
        // ECU name next to a label, for example "SG: CIC" or "ECU-name: CIC"
        m = Regex.Match(s, @"\b(?:SG|ECU|ECU-name|SG-Name)\s*[:=]\s*([A-Z][A-Z0-9_]{1,15})\b", RegexOptions.IgnoreCase);
        if (m.Success) info.Ecu = m.Groups[1].Value.ToUpperInvariant();
        // Full VIN, or the last 7 characters (FG) next to a label
        m = Regex.Match(s, @"\b([A-HJ-NPR-Z0-9]{3}[A-HJ-NPR-Z0-9]{6}[A-HJ-NPR-Z0-9]{8})\b");
        if (m.Success && Regex.IsMatch(m.Value, "[0-9]") && Regex.IsMatch(m.Value, "[A-Z]"))
            info.Vin = m.Value.Substring(10);
        else
        {
            m = Regex.Match(s, @"\b(?:FG|VIN|FGNR|FG-Nr)\s*[:=]?\s*([A-Z]{2}[0-9]{5})\b", RegexOptions.IgnoreCase);
            if (m.Success) info.Vin = m.Groups[1].Value.ToUpperInvariant();
        }
        m = Regex.Match(s, @"\bChassis\s*:?\s*([EFRK][0-9]{2,3}X?)\b", RegexOptions.IgnoreCase);
        if (m.Success) info.Chassis = m.Groups[1].Value.ToUpperInvariant();
    }
}

// Recognises which module an FSW_PSW.TRC came from, using NCS Expert's own data files.
//
// File format (NCS "IPO" data files): a header of records [length u16][kind u8][data][checksum u8]
// that ends with kind 0xFF; kind 3 records name the tables ([table id u16][name]) and the next kind 4
// record holds the table's column format. After the header come data records
// [length u8][table id u16][data][checksum u8].
static class CabdMatch
{
    class Folder
    {
        public Dictionary<string, int> Ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<int>> Modules = new Dictionary<string, HashSet<int>>();
    }
    static readonly Dictionary<string, Folder> cache = new Dictionary<string, Folder>(StringComparer.OrdinalIgnoreCase);
    public static string LastDetail = "";

    static string Daten { get { return Path.Combine(Path.GetDirectoryName(Settings.NcsWork.TrimEnd('\\')), "DATEN"); } }

    // Table id -> { name, format }, and the offset where the data records start
    static Dictionary<int, string[]> Header(byte[] b, out int start)
    {
        Dictionary<int, string[]> tables = new Dictionary<int, string[]>();
        int i = 0, lastTable = -1;
        while (i + 3 <= b.Length)
        {
            int len = b[i] | b[i + 1] << 8, kind = b[i + 2];
            if (kind == 0xFF) { start = i + 4 + len; return tables; }
            if (i + 3 + len > b.Length) break;
            if (kind == 3 && len > 2)
            {
                lastTable = b[i + 3] | b[i + 4] << 8;
                tables[lastTable] = new[] { CStr(b, i + 5, len - 2), "" };
            }
            else if (kind == 4 && lastTable >= 0 && tables.ContainsKey(lastTable))
                tables[lastTable][1] = CStr(b, i + 3, len);
            i += 4 + len;
        }
        start = -1;
        return tables;
    }

    static string CStr(byte[] b, int at, int max)
    {
        int n = 0;
        while (n < max && at + n < b.Length && b[at + n] != 0) n++;
        return Encoding.Default.GetString(b, at, n);
    }

    // (table id, offset of the record's data)
    static IEnumerable<KeyValuePair<int, int>> Records(byte[] b, int start)
    {
        int i = start;
        while (i + 3 <= b.Length)
        {
            int len = b[i], table = b[i + 1] | b[i + 2] << 8;
            if (i + 3 + len > b.Length) yield break;
            yield return new KeyValuePair<int, int>(table, i + 3);
            i += 4 + len;
        }
    }

    static Folder Load(string dir)
    {
        Folder f;
        if (cache.TryGetValue(dir, out f)) return f;
        f = new Folder();
        string[] swts = Directory.GetFiles(dir, "SWTFSW*.DAT");
        Array.Sort(swts, StringComparer.OrdinalIgnoreCase);
        if (swts.Length > 0)
        {
            try
            {
                byte[] b = File.ReadAllBytes(swts[swts.Length - 1]);
                int start;
                Dictionary<int, string[]> t = Header(b, out start);
                int entry = -1;
                foreach (KeyValuePair<int, string[]> kv in t) if (kv.Value[0] == "SWT_EINTRAG") entry = kv.Key;
                if (start > 0 && entry >= 0)
                    foreach (KeyValuePair<int, int> r in Records(b, start))
                        if (r.Key == entry && r.Value + 2 < b.Length)   // a record needs at least its 2-byte number
                        {
                            int id = b[r.Value] | b[r.Value + 1] << 8;
                            string name = CStr(b, r.Value + 2, 200);
                            if (name != "" && !f.Ids.ContainsKey(name)) f.Ids[name] = id;
                        }
            }
            catch { }   // a damaged name table only disables recognition for this folder
        }
        foreach (string file in Directory.GetFiles(dir, "*.C??"))
        {
            if (!Regex.IsMatch(Path.GetExtension(file), @"^\.C[0-9A-F]{2}$", RegexOptions.IgnoreCase)) continue;
            try
            {
                byte[] b = File.ReadAllBytes(file);
                int start;
                Dictionary<int, string[]> t = Header(b, out start);
                if (start < 0) continue;
                // Parameter tables PARZUWEISUNG_FSW / _DIR; their columns start BLOCKNR,WORTADR,BYTEADR,FSW = {L}LWW
                HashSet<int> fswTables = new HashSet<int>();
                foreach (KeyValuePair<int, string[]> kv in t)
                    if ((kv.Value[0].StartsWith("PARZUWEISUNG_FSW") || kv.Value[0].StartsWith("PARZUWEISUNG_DIR"))
                        && kv.Value[1].StartsWith("{L}LWW")) fswTables.Add(kv.Key);
                HashSet<int> ids = new HashSet<int>();
                foreach (KeyValuePair<int, int> r in Records(b, start))
                {
                    if (!fswTables.Contains(r.Key)) continue;
                    int at = r.Value + 1 + 4 * b[r.Value] + 4 + 2;   // {L}: count + values, then L, W, then FSW
                    if (at + 1 < b.Length) ids.Add(b[at] | b[at + 1] << 8);
                }
                if (ids.Count > 0) f.Modules[Path.GetFileName(file).ToUpperInvariant()] = ids;
            }
            catch { }
        }
        cache[dir] = f;
        return f;
    }

    // Folders to look in: the one for the chassis first (E61 data is in the E60 folder), then the rest
    static List<string> Folders(string chassis)
    {
        List<string> first = new List<string>(), rest = new List<string>();
        if (!Directory.Exists(Daten)) return first;
        foreach (string d in Directory.GetDirectories(Daten))
        {
            bool match = chassis != null && (Path.GetFileName(d).Equals(chassis, StringComparison.OrdinalIgnoreCase)
                         || Directory.GetFiles(d, chassis + "AT.*").Length > 0);
            (match ? first : rest).Add(d);
        }
        first.AddRange(rest);
        return first;
    }

    public static string Identify(byte[] trace, string chassis)
    {
        LastDetail = "";
        try
        {
            List<string> names = new List<string>();
            foreach (string line in Encoding.Default.GetString(trace).Split('\n'))
            {
                if (line.Length == 0 || line[0] == '\t' || line[0] == ' ') continue;
                string n = line.Trim();
                if (n != "") names.Add(n);
            }
            if (names.Count == 0) return null;
            int mostContained = 0; string mostDir = "";

            foreach (string dir in Folders(chassis))
            {
                Folder f = Load(dir);
                if (f.Ids.Count == 0 || f.Modules.Count == 0) continue;
                List<int> ids = new List<int>();
                foreach (string n in names) { int id; ids.Add(f.Ids.TryGetValue(n, out id) ? id : -1); }

                string best = null; double bestCover = 0, bestPrec = 0;
                int contained = 0;   // modules whose parameters are all in the trace
                foreach (KeyValuePair<string, HashSet<int>> m in f.Modules)
                {
                    int hit = 0;
                    foreach (int id in ids) if (id >= 0 && m.Value.Contains(id)) hit++;
                    double cover = (double)hit / ids.Count, prec = (double)hit / m.Value.Count;
                    if (prec >= 0.97 && m.Value.Count >= 10) contained++;
                    // all of the trace's parameters, then the fewest extra ones, then the newest version
                    bool better = cover > bestCover + 1e-9 ||
                        (Math.Abs(cover - bestCover) < 1e-9 && (prec > bestPrec + 1e-9 ||
                        (Math.Abs(prec - bestPrec) < 1e-9 && string.CompareOrdinal(m.Key, best) > 0)));
                    if (better) { best = m.Key; bestCover = cover; bestPrec = prec; }
                }
                if (best != null && bestCover >= 0.97)
                {
                    LastDetail = string.Format("{0} parameters, {1:P0} found in {2}, {3}", names.Count, bestCover, best, Path.GetFileName(dir));
                    return best;
                }
                if (contained > mostContained) { mostContained = contained; mostDir = dir; }
            }
            // A whole-car write (for example after processing the vehicle order): several modules in one file
            if (mostContained >= 3)
            {
                LastDetail = string.Format("{0} parameters, whole-car file ({1} module versions fully inside), {2}", names.Count, mostContained, Path.GetFileName(mostDir));
                return "All modules";
            }
            LastDetail = names.Count + " parameters, no single module matches";
        }
        catch (Exception ex) { LastDetail = ex.Message; }
        return null;
    }
}
