// "Last seen": when each adapter was last connected, and which car was read at that time.
//
// EdiaHelper already checks every few seconds what is connected (tray icon). Each adapter it
// sees is noted in %LOCALAPPDATA%\EdiaHelper\LastSeen.txt as a "session" (first and last time seen;
// a gap of more than 10 minutes starts a new session). For the time before this existed, the log
// is read as well (relay connections and automatic switches).
//
// The car comes from what was read during that session:
//   ISTA      - its session logs are named <date>_<time>_<chassis>_<VIN>
//   NCS Expert - the coding backup folders (<date> <VIN or chassis>)
// INPA keeps no record of the car.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

static class LastSeen
{
    static readonly string FileName = Path.Combine(Log.Folder, "LastSeen.txt");
    static readonly object gate = new object();
    static Dictionary<string, Entry> entries;
    static DateTime lastSave = DateTime.MinValue;
    static bool dirty;
    static readonly TimeSpan Gap = TimeSpan.FromMinutes(10);

    class Entry { public string Key, Label; public DateTime First, Last; }

    // An MHD adapter is identified by the unit code at the end of its WiFi name ("MHD ENET 1A2B" -> 1A2B),
    // so its K-line, CAN and ENET networks count as the same adapter.
    static string MhdKey(string ssid)
    {
        string[] parts = ssid.Trim().Split(' ');
        return "MHD " + parts[parts.Length - 1].ToUpperInvariant();
    }

    // Called by the tray's status check every few seconds
    public static void Note(string ssid, bool cablePresent, string cablePort, int choice, string letter)
    {
        try
        {
            lock (gate)
            {
                Load();
                DateTime now = DateTime.Now;
                bool newSession = false;
                if (Relay.IsMhdSsid(ssid)) newSession |= Touch(MhdKey(ssid), ssid, now);
                if (cablePresent) newSession |= Touch("CABLE", "K+DCAN cable on " + cablePort, now);
                if (letter != null && choice == Switcher.EnetCable) newSession |= Touch("ENET", "ENET cable", now);
                if (letter != null && choice == Switcher.Icom) newSession |= Touch("ICOM", "ICOM at " + Settings.IcomIp, now);
                if (newSession || (dirty && (now - lastSave).TotalSeconds >= 60)) Save();
            }
        }
        catch { }
    }

    static bool Touch(string key, string label, DateTime now)
    {
        Entry e;
        dirty = true;
        if (entries.TryGetValue(key, out e) && now - e.Last < Gap && now >= e.Last) { e.Last = now; e.Label = label; return false; }
        entries[key] = new Entry { Key = key, Label = label, First = now, Last = now };
        return true;
    }

    static void Load()
    {
        if (entries != null) return;
        entries = new Dictionary<string, Entry>();
        if (!File.Exists(FileName)) return;
        string[] lines;
        try { lines = File.ReadAllLines(FileName); }
        catch (Exception ex) { Log.WriteOnce("Could not read " + FileName + ": " + ex.Message); return; }
        foreach (string line in lines)
        {
            string[] p = line.Split('|');
            DateTime f, l;
            if (p.Length == 4 && DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out f)
                && DateTime.TryParse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out l))
                entries[p[0]] = new Entry { Key = p[0], First = f, Last = l, Label = p[3] };
        }
    }

    static void Save()
    {
        Directory.CreateDirectory(Log.Folder);
        List<string> lines = new List<string>();
        foreach (Entry e in entries.Values)
            lines.Add(e.Key + "|" + e.First.ToString("s") + "|" + e.Last.ToString("s") + "|" + e.Label.Replace("|", " "));
        File.WriteAllLines(FileName, lines.ToArray());
        lastSave = DateTime.Now;
        dirty = false;
    }

    // Sessions found in the log (for the time before LastSeen.txt existed)
    static List<Entry> FromLog()
    {
        List<Entry> found = new List<Entry>();
        List<string> files = new List<string>();
        if (File.Exists(Log.PathName + ".old")) files.Add(Log.PathName + ".old");
        if (File.Exists(Log.PathName)) files.Add(Log.PathName);
        // the time separator in the log follows Windows' language settings ("19.36.43" or "19:36:43")
        Regex time = new Regex(@"^(\d{4})-(\d{2})-(\d{2}) (\d{2})[.:](\d{2})[.:](\d{2})");
        Regex wifi = new Regex(@"(?:connected, WiFi '|Automatic switch: |Automatic switch skipped: MHD WiFi )(MHD [A-Za-z]+ [A-Za-z0-9]+)");
        Regex cable = new Regex(@"K\+DCAN cable on (COM\d+) detected|and K\+DCAN cable both connected");
        foreach (string f in files)
        {
            string[] lines;
            try
            {
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n');
            }
            catch { continue; }
            foreach (string line in lines)
            {
                Match t = time.Match(line);
                if (!t.Success) continue;
                DateTime when;
                try
                {
                    int[] v = new int[6];
                    for (int k = 0; k < 6; k++) v[k] = int.Parse(t.Groups[k + 1].Value);
                    when = new DateTime(v[0], v[1], v[2], v[3], v[4], v[5]);
                }
                catch { continue; }
                Match m = wifi.Match(line);
                if (m.Success) Add(found, MhdKey(m.Groups[1].Value), m.Groups[1].Value, when);
                m = cable.Match(line);
                if (m.Success) Add(found, "CABLE", "K+DCAN cable" + (m.Groups[1].Success ? " on " + m.Groups[1].Value : ""), when);
            }
        }
        return found;
    }

    static void Add(List<Entry> list, string key, string label, DateTime when)
    {
        Entry last = list.FindLast(e => e.Key == key);
        if (last != null && when - last.Last < Gap && when >= last.Last) { last.Last = when; last.Label = label; return; }
        list.Add(new Entry { Key = key, Label = label, First = when, Last = when });
    }

    // The newest session of each adapter that belongs to this choice
    static List<Entry> Latest(int choice)
    {
        Dictionary<string, Entry> best = new Dictionary<string, Entry>();
        List<Entry> all = FromLog();
        lock (gate) { Load(); all.AddRange(entries.Values); }
        foreach (Entry e in all)
        {
            bool fits = choice == Switcher.MhdKD || choice == Switcher.MhdEnet ? e.Key.StartsWith("MHD ")
                      : choice == Switcher.Cable ? e.Key == "CABLE"
                      : choice == Switcher.EnetCable ? e.Key == "ENET"
                      : choice == Switcher.Icom && e.Key == "ICOM";
            Entry b;
            if (fits && (!best.TryGetValue(e.Key, out b) || e.Last > b.Last)) best[e.Key] = e;
        }
        List<Entry> result = new List<Entry>(best.Values);
        result.Sort((a, b) => b.Last.CompareTo(a.Last));
        return result;
    }

    public static string Report(int choice)
    {
        if (choice < 0) return "Choose an adapter first.";
        List<Entry> list = Latest(choice);
        string what = choice == Switcher.MhdKD || choice == Switcher.MhdEnet ? "MHD adapter" : Switcher.Names[choice];
        if (list.Count == 0)
            return "No record of the " + what + " yet.\n\nEdiaHelper notes every adapter it sees while it runs.";

        StringBuilder sb = new StringBuilder();
        string ssidNow = Relay.CurrentSsid();
        bool cableNow = Cable.Find().Exists(c => c.Present);
        foreach (Entry e in list)
        {
            string name = e.Key.StartsWith("MHD ") ? "MHD adapter " + e.Key.Substring(4) : e.Label;
            // connected right now: checked live, not from the last note (which can be a few seconds old)
            bool now = e.Key.StartsWith("MHD ") ? Relay.IsMhdSsid(ssidNow) && MhdKey(ssidNow) == e.Key
                     : e.Key == "CABLE" ? cableNow
                     : (DateTime.Now - e.Last).TotalSeconds < 15;
            sb.AppendLine(name);
            if (now) sb.AppendLine("   Connected right now (" + e.Label + ")");
            else
            {
                sb.AppendLine("   Last seen: " + e.Last.ToString("dd.MM.yyyy HH:mm") + (e.Key.StartsWith("MHD ") ? "  (WiFi \"" + e.Label + "\")" : ""));
                if (e.First < e.Last.AddMinutes(-1)) sb.AppendLine("   Connected from " + e.First.ToString("HH:mm") + " to " + e.Last.ToString("HH:mm"));
            }
            sb.AppendLine("   Car: " + Car(e.First, now ? DateTime.Now : e.Last));
            sb.AppendLine();
        }
        sb.Append("The car is known when it was read in ISTA or NCS Expert (backups); INPA does not record it.");
        return sb.ToString();
    }

    // What was read between the adapter's first and last sighting (with some margin: the log notes
    // the moment of connecting, the reading usually comes a few minutes later)
    static string Car(DateTime first, DateTime last)
    {
        DateTime from = first.AddMinutes(-10), to = last.AddMinutes(30);
        List<string> found = new List<string>();

        // ISTA: ...\ISTA\Logs\<date>_<time>_<chassis>_<VIN>(.zip.log / folder), e.g. 2026-11-16_194034_F11_WBA0000000X000000
        try
        {
            string logs = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Settings.IstaBin.TrimEnd('\\'))), "Logs");
            if (Directory.Exists(logs))
            {
                Regex r = new Regex(@"^(\d{4}-\d{2}-\d{2})_(\d{6})_([A-Z0-9]+)_([A-HJ-NPR-Z0-9]{17})", RegexOptions.IgnoreCase);
                DateTime bestTime = DateTime.MinValue; string best = null;
                foreach (string p in Directory.GetFileSystemEntries(logs))
                {
                    Match m = r.Match(Path.GetFileName(p));
                    DateTime t;
                    if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value + " " + m.Groups[2].Value, "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                    if (t >= from && t <= to && t > bestTime)
                    {
                        bestTime = t;
                        best = m.Groups[3].Value.ToUpperInvariant() + ", VIN " + m.Groups[4].Value.ToUpperInvariant() + " (ISTA, " + t.ToString("HH:mm") + ")";
                    }
                }
                if (best != null) found.Add(best);
            }
        }
        catch { }

        // NCS Expert coding backups: a folder per day and car
        try
        {
            string dir = NcsBackup.BackupDir;
            if (Directory.Exists(dir))
                foreach (string d in Directory.GetDirectories(dir))
                {
                    DateTime newest = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(d))
                    {
                        DateTime w = File.GetLastWriteTime(f);
                        if (w >= from && w <= to && w > newest) newest = w;
                    }
                    if (newest > DateTime.MinValue)
                    {
                        string n = Path.GetFileName(d);
                        int sp = n.IndexOf(' ');
                        found.Add((sp > 0 ? n.Substring(sp + 1) : n) + " (NCS Expert, " + newest.ToString("HH:mm") + ")");
                    }
                }
        }
        catch { }

        return found.Count > 0 ? string.Join("; ", found.ToArray()) : "not known";
    }
}
