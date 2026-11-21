// EdiaHelper - chooses which diagnostic adapter EDIABAS (INPA, NCS Expert, ISTA) uses,
// shows the connection state in the tray and runs the MHD relay (see Relay.cs).
//
//   MHD WiFi - K-line / D-CAN : EdiabasLib 8.0, INPA interface STD:OBD, traffic through the relay
//   MHD WiFi - ENET           : EdiabasLib 8.0, INPA interface ENET
//   K+DCAN cable              : BMW original DLLs (obd.ini), INPA interface STD:OBD
//   ENET cable                : BMW original DLLs, INPA interface ENET ([XEthernet] RemoteHost=Autodetect)
//   ICOM                      : BMW original DLLs, INPA interface
//                                 RPLUS:ICOM_P (address in RPLUS.INI [ICOM_P])  or
//                                 REMOTE       (address in EDIABAS.INI [TCP])
//
// Start options: no argument = open the window, /tray = start in the tray only (autostart).
// One instance only; starting it again just opens the window of the running one.

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("EdiaHelper")]
[assembly: System.Reflection.AssemblyProduct("EdiaHelper")]
[assembly: System.Reflection.AssemblyDescription("Adapter switching, MHD WiFi relay and NCS Expert coding backups for INPA, NCS Expert and ISTA")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.1.0")]

static class Program
{
    public const string Version = "1.0.1";
    public const string RepoUrl = "https://github.com/Nahkasikari/EdiaHelper";

    [STAThread]
    static void Main(string[] args)
    {
        Log.MoveOldData();
        Settings.Load();

        // /lastseen <choice> "<file>": write the "Last seen" report to a file (no window)
        if (args.Length == 3 && args[0] == "/lastseen")
        {
            int ch;
            File.WriteAllText(args[2], int.TryParse(args[1], out ch) && ch >= 0 && ch < Switcher.Names.Length ? LastSeen.Report(ch) : "unknown choice");
            return;
        }

        // /check "<file>": write the "Check connection" report to a file (no window)
        if (args.Length == 2 && args[0] == "/check")
        {
            StringBuilder sb = new StringBuilder();
            foreach (Check.Item it in Check.Run())
                sb.AppendLine((it.Level == Check.Level.Ok ? "OK" : it.Level == Check.Level.Warn ? "!" : it.Level == Check.Level.Fail ? "X" : "i").PadRight(3) + it.Text);
            File.WriteAllText(args[1], sb.ToString());
            return;
        }

        // /identify "<FSW_PSW trace>" "<report file>": which module the trace came from (for testing)
        if (args.Length == 3 && args[0] == "/identify")
        {
            string mod = CabdMatch.Identify(File.ReadAllBytes(args[1]), null);
            File.WriteAllText(args[2], (mod ?? "(not recognised)") + "  -  " + CabdMatch.LastDetail);
            return;
        }

        // /screenshot "<file prefix>": pictures of the window's tabs (for the README; no tray, no relay)
        if (args.Length == 2 && args[0] == "/screenshot")
        {
            Application.EnableVisualStyles();
            MainForm f = new MainForm(null);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-3000, -3000);
            f.ShowInTaskbar = false;
            f.Show();
            f.LoadState();
            f.ShowStatus(Switcher.Current(), "(screenshot)");
            f.SaveTabPictures(args[1]);
            f.ReallyClose();
            using (AboutForm a = new AboutForm())
            {
                a.StartPosition = FormStartPosition.Manual;
                a.Location = new Point(-3000, -3000);
                a.Show();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(a.Width, a.Height))
                {
                    a.DrawToBitmap(bmp, new Rectangle(0, 0, a.Width, a.Height));
                    bmp.Save(args[1] + "-about.png", System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            return;
        }

        // Started elevated by ourselves: /latency "<registry path of the cable>"
        // Only a cable that Windows itself lists under FTDIBUS is accepted, whatever path is passed.
        if (args.Length == 2 && args[0] == "/latency")
        {
            if (!Cable.Find().Exists(c => string.Equals(c.RegPath, args[1], StringComparison.OrdinalIgnoreCase)))
            {
                Log.Write("Refused administrator request /latency: not a K+DCAN cable entry (" + args[1] + ")");
                Environment.Exit(2);
            }
            try { Cable.SetLatency(args[1]); Environment.Exit(0); }
            catch (Exception ex) { MessageBox.Show("Could not set the latency timer: " + ex.Message, "EdiaHelper"); Environment.Exit(1); }
            return;
        }

        // Started elevated by ourselves: /apply:<choice> [ip] [port] [method]
        if (args.Length >= 1 && args[0].StartsWith("/apply:"))
        {
            int i;
            // Only the expected kinds of values are accepted: a choice number, an IPv4 address (or none),
            // a port number and RPLUS / REMOTE
            IPAddress ipa; int port;
            string ip = args.Length > 1 ? args[1] : "", portText = args.Length > 2 ? args[2] : "6801", method = args.Length > 3 ? args[3] : "RPLUS";
            bool valid = int.TryParse(args[0].Substring(7), out i) && i >= 0 && i < Switcher.Names.Length && args.Length <= 4
                && (ip == "" || (IPAddress.TryParse(ip, out ipa) && ipa.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.Split('.').Length == 4))
                && int.TryParse(portText, out port) && port >= 1 && port <= 65535 && portText == port.ToString()
                && (method == "RPLUS" || method == "REMOTE");
            if (!valid)
            {
                Log.Write("Refused administrator request: unexpected values (" + string.Join(" ", args) + ")");
                Environment.Exit(2);
            }
            {
                // The folders come only from the installer's settings in Program Files (only administrators can
                // change them), never from the user's own settings file
                Settings.UseInstallerFolders();
                Settings.IcomIp = ip;
                Settings.IcomPort = portText;
                Settings.IcomMethod = method;
                string err;
                try { err = Switcher.ApplyFiles(i); }
                catch (Exception ex) { err = "Switching failed: " + ex.Message; }
                if (err != null) MessageBox.Show(err, "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(err == null ? 0 : 1);
            }
            return;
        }

        bool first;
        Mutex m = new Mutex(true, "Local\\EdiaHelperSingleInstance", out first);
        EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\EdiaHelperShow");
        if (!first)
        {
            if (!(args.Length > 0 && args[0] == "/tray")) show.Set();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Relay.Verbose = File.Exists(Path.Combine(Settings.Dir, "verbose.txt"));
        Log.Write("EdiaHelper " + Version + " started");
        bool relayOk = Relay.Start();

        TrayApp app = new TrayApp(show, !(args.Length > 0 && args[0] == "/tray"));
        NcsBackup.Restart();
        if (!relayOk) app.Balloon("The MHD relay could not start: port " + Relay.ListenPort + " is in use. Is the old MhdRelay still running?");
        Application.Run(app);
        GC.KeepAlive(m);
    }
}

static class Settings
{
    public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string UserIni = Path.Combine(Log.Folder, "EdiaHelper.ini");
    static readonly string DefaultIni = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EdiaHelper.ini");

    public static string EdiabasBin = @"C:\EDIABAS\Bin";
    public static string IstaBin = @"C:\EC-APPS\ISTA\Ediabas\BIN";
    public static string IcomIp = "";
    public static string IcomPort = "6801";
    public static string IcomMethod = "RPLUS";   // RPLUS or REMOTE
    public static bool AutoSwitch = true;        // switch when an MHD WiFi or the cable is connected
    public static bool NcsBackup = true;         // keep copies of NCS Expert's coding traces
    public static string NcsWork = @"C:\NCSEXPER\WORK";
    public static string NcsBackupDir = "";      // empty = TRC_BACKUP next to the WORK folder
    // ENET cable and ICOM are not tested yet: hidden unless ShowUntested=1 is in the settings file
    public static bool ShowUntested = false;

    // The EDIABAS folders the installer set up (from Program Files, administrator-only)
    public static string InstallerEdiabasBin = @"C:\EDIABAS\Bin", InstallerIstaBin = @"C:\EC-APPS\ISTA\Ediabas\BIN";

    public static void Load()
    {
        Read(DefaultIni);   // written by the installer
        InstallerEdiabasBin = EdiabasBin; InstallerIstaBin = IstaBin;
        Read(UserIni);      // changes made in the window
    }

    public static void UseInstallerFolders()
    {
        EdiabasBin = InstallerEdiabasBin;
        IstaBin = InstallerIstaBin;
    }

    public static bool SameFolder(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static void Read(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadAllLines(path))
        {
            string[] kv = line.Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            string k = kv[0].Trim().ToLowerInvariant(), v = kv[1].Trim();
            if (k == "ediabasbin") EdiabasBin = v;
            else if (k == "istabin") IstaBin = v;
            else if (k == "icomip") IcomIp = v;
            else if (k == "icomport") IcomPort = v;
            else if (k == "icommethod") IcomMethod = v.ToUpperInvariant() == "REMOTE" ? "REMOTE" : "RPLUS";
            else if (k == "autoswitch") AutoSwitch = v != "0";
            else if (k == "ncsbackup") NcsBackup = v != "0";
            else if (k == "ncswork") NcsWork = v;
            else if (k == "ncsbackupdir") NcsBackupDir = v;
            else if (k == "showuntested") ShowUntested = v == "1";
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllLines(UserIni, new[] {
                "EdiabasBin=" + EdiabasBin, "IstaBin=" + IstaBin,
                "IcomIp=" + IcomIp, "IcomPort=" + IcomPort, "IcomMethod=" + IcomMethod,
                "AutoSwitch=" + (AutoSwitch ? "1" : "0"),
                "NcsBackup=" + (NcsBackup ? "1" : "0"), "NcsWork=" + NcsWork, "NcsBackupDir=" + NcsBackupDir,
                "ShowUntested=" + (ShowUntested ? "1" : "0") });
        }
        catch { }
    }
}

static class Switcher
{
    public const int MhdKD = 0, MhdEnet = 1, Cable = 2, EnetCable = 3, Icom = 4;
    public static readonly string[] Names = { "MHD WiFi - K-line / D-CAN", "MHD WiFi - ENET", "K+DCAN cable", "ENET cable", "ICOM" };

    // Choices shown in the window and the tray. ENET cable and ICOM (the last two) stay in the code
    // but are only shown with ShowUntested=1 until they have been tested.
    public static int Visible { get { return Settings.ShowUntested ? Names.Length : EnetCable; } }
    static readonly string[] DllSet = { "EdiabasLib8", "EdiabasLib8", "BmwOriginal", "BmwOriginal", "BmwOriginal" };

    public static string Iface(int i)
    {
        switch (i)
        {
            case MhdKD: case Cable: return "STD:OBD";
            case MhdEnet: case EnetCable: return "ENET";
            default: return Settings.IcomMethod == "REMOTE" ? "REMOTE" : "RPLUS:ICOM_P";
        }
    }

    public static string Profiles { get { return Path.Combine(Settings.Dir, "profiles"); } }

    public static bool IsEdiabasDir(string d)
    {
        return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "EDIABAS.INI"));
    }

    static bool IstaSeparate()
    {
        return IsEdiabasDir(Settings.IstaBin) && !Settings.SameFolder(Settings.IstaBin, Settings.EdiabasBin);
    }

    // ---- BMW's original DLLs, kept per EDIABAS folder ----
    // INPA and ISTA can have different EDIABAS versions, so each folder keeps its own BMW originals in
    // <folder>\EdiaHelper-original\. Before EdiaHelper overwrites a BMW Api32/Api64 it saves it there.
    // Fallback: the copies the installer made in Program Files (profiles\BmwOriginal\INPA or \ISTA,
    // or the single set profiles\BmwOriginal of version 1.0.0 builds).
    public const string OriginalDir = "EdiaHelper-original";

    public static bool IsEdiabasLib(string path)
    {
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 1024 * 1024) return true;   // BMW's Api32/Api64 are well under 1 MB
        FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
        return (v.ProductName + " " + v.FileDescription + " " + v.CompanyName).IndexOf("EdiabasLib", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static string FileHash(string path)
    {
        using (SHA256 sha = SHA256.Create())
        using (FileStream fs = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(fs));
    }

    // Where BMW's original <dll> for this EDIABAS folder is kept, or null if none is known
    public static string OriginalSource(string bin, string dll)
    {
        string own = Path.Combine(bin, OriginalDir, dll);
        if (File.Exists(own) && !IsEdiabasLib(own)) return own;
        string role = IstaSeparate() && Settings.SameFolder(bin, Settings.IstaBin) ? "ISTA" : "INPA";
        foreach (string p in new[] { Path.Combine(Profiles, "BmwOriginal", role, dll), Path.Combine(Profiles, "BmwOriginal", dll) })
            if (File.Exists(p) && !IsEdiabasLib(p)) return p;
        return null;
    }

    // Saves the BMW DLL that is in the folder now before it is overwritten. If a different copy was saved
    // earlier (for example before an ISTA update), that one is kept as Api32-<date>.dll.
    static void SaveOriginal(string bin, string dll)
    {
        string cur = Path.Combine(bin, dll);
        if (!File.Exists(cur) || IsEdiabasLib(cur)) return;
        string dir = Path.Combine(bin, OriginalDir);
        string saved = Path.Combine(dir, dll);
        if (File.Exists(saved) && FileHash(saved) == FileHash(cur)) return;
        NotLink(dir);
        Directory.CreateDirectory(dir);
        if (File.Exists(saved))
        {
            NotLink(saved);
            string old = Path.Combine(dir, Path.GetFileNameWithoutExtension(dll) + "-" + File.GetLastWriteTime(saved).ToString("yyyyMMdd-HHmmss") + ".dll");
            if (!File.Exists(old)) File.Move(saved, old); else File.Delete(saved);
        }
        File.Copy(cur, saved);
        Log.Write("Saved BMW's " + dll + " from " + bin);
    }

    // Refuses to write through a link (junction / symbolic link) into some other place
    static void NotLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(path + " is a link; EdiaHelper does not write through links.");
    }

    // ---- current state (hash cached by file time) ----

    static readonly object hashLock = new object();
    static string hashPath, hashValue; static DateTime hashTime;
    static string Hash(string path)
    {
        if (!File.Exists(path)) return "";
        DateTime t = File.GetLastWriteTimeUtc(path);
        lock (hashLock)
        {
            if (path == hashPath && t == hashTime) return hashValue;
            hashValue = FileHash(path);
            hashPath = path; hashTime = t;
            return hashValue;
        }
    }

    static string profileLib;
    static string ProfileHash(string set)
    {
        string p = Path.Combine(Profiles, set, "Api32.dll");
        if (!File.Exists(p)) return "-";
        using (SHA256 sha = SHA256.Create())
        using (FileStream fs = File.OpenRead(p))
            return BitConverter.ToString(sha.ComputeHash(fs));
    }

    public static string InpaInterface() { return IniInterface(Settings.EdiabasBin); }

    // The Interface setting in a folder's EDIABAS.INI
    public static string IniInterface(string bin)
    {
        string ini = Path.Combine(bin, "EDIABAS.INI");
        if (!File.Exists(ini)) return "";
        Match m = Regex.Match(File.ReadAllText(ini, Encoding.Default), @"^\s*Interface\s*=\s*(\S+)", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : "";
    }

    public static int Current()
    {
        try
        {
            if (profileLib == null) profileLib = ProfileHash("EdiabasLib8");
            string api = Path.Combine(Settings.EdiabasBin, "Api32.dll");
            if (!File.Exists(api)) return -1;
            string h = Hash(api);
            string iface = InpaInterface().ToUpperInvariant();
            if (h == profileLib)
            {
                if (iface == "STD:OBD") return MhdKD;
                if (iface == "ENET") return MhdEnet;
            }
            else if (!IsEdiabasLib(api))   // a BMW original (each folder may have its own version)
            {
                if (iface == "STD:OBD") return Cable;
                if (iface == "ENET") return EnetCable;
                if (iface == "RPLUS:ICOM_P" || iface == "REMOTE") return Icom;
            }
        }
        catch { }
        return -1;
    }

    // ---- ICOM settings in RPLUS.INI or EDIABAS.INI ----

    static string Section(string text, string name, out int index)
    {
        Match sec = Regex.Match(text, @"^[ \t]*\[" + Regex.Escape(name) + @"\][^\[]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        index = sec.Success ? sec.Index : -1;
        return sec.Success ? sec.Value : null;
    }

    static string KeyValue(string section, string key)
    {
        if (section == null) return "";
        Match m = Regex.Match(section, @"^[ \t]*" + key + @"[ \t]*=[ \t]*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    static string SetKey(string section, string key, string value)
    {
        Regex r = new Regex(@"^([ \t]*" + key + @"[ \t]*=[ \t]*)\S*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (r.IsMatch(section)) return r.Replace(section, "${1}" + value, 1);
        int nl = section.IndexOf('\n');
        return section.Insert(nl + 1, key + " = " + value + "\r\n");
    }

    static string IcomFile() { return Path.Combine(Settings.EdiabasBin, Settings.IcomMethod == "REMOTE" ? "EDIABAS.INI" : "RPLUS.INI"); }
    static string IcomSection() { return Settings.IcomMethod == "REMOTE" ? "TCP" : "ICOM_P"; }

    public static void ReadIcom(out string ip, out string port)
    {
        ip = ""; port = "";
        try
        {
            int idx;
            string sec = Section(File.ReadAllText(IcomFile(), Encoding.Default), IcomSection(), out idx);
            ip = KeyValue(sec, "RemoteHost");
            port = KeyValue(sec, "Port");
        }
        catch { }
        if (port == "") port = "6801";
    }

    static void WriteIcom()
    {
        string path = IcomFile();
        NotLink(path);
        string t = File.Exists(path) ? File.ReadAllText(path, Encoding.Default) : "";
        int idx;
        string sec = Section(t, IcomSection(), out idx);
        if (sec == null)
        {
            t = t.TrimEnd() + "\r\n[" + IcomSection() + "]\r\nRemoteHost = " + Settings.IcomIp + "\r\nPort = " + Settings.IcomPort +
                "\r\nTimeoutConnect = 5000\r\nTimeoutReceive = 5000\r\nTimeoutFunction = 10000\r\n";
        }
        else
        {
            string s = SetKey(SetKey(sec, "RemoteHost", Settings.IcomIp), "Port", Settings.IcomPort);
            t = t.Substring(0, idx) + s + t.Substring(idx + sec.Length);
        }
        File.WriteAllText(path, t, Encoding.Default);
    }

    // ---- switching ----

    public static string DiagRunning()
    {
        string[] procs = { "INPALOAD", "inpa", "NCSEXPER", "NFS", "tool32", "ISTAGUI", "IstaOperation", "WinKFP" };
        foreach (string p in procs)
            if (Process.GetProcessesByName(p).Length > 0) return p;
        return null;
    }

    // Writes the files. Returns null on success, an error text otherwise.
    // Throws UnauthorizedAccessException when administrator rights are needed.
    public static string ApplyFiles(int i)
    {
        if (!IsEdiabasDir(Settings.EdiabasBin)) return "The INPA / NCS Expert folder is not set correctly (EDIABAS.INI not found).";
        bool lib = DllSet[i] == "EdiabasLib8";
        string libDir = Path.Combine(Profiles, "EdiabasLib8");
        if (lib && !File.Exists(Path.Combine(libDir, "Api32.dll"))) return "The files for this choice are missing: " + libDir;

        string[] bins = IstaSeparate() ? new[] { Settings.EdiabasBin, Settings.IstaBin } : new[] { Settings.EdiabasBin };
        // check everything first, so that a folder is never left half switched
        foreach (string bin in bins)
        {
            NotLink(bin);
            if (!lib && OriginalSource(bin, "Api32.dll") == null && IsEdiabasLib(Path.Combine(bin, "Api32.dll")))
                return "BMW's original Api32.dll for " + bin + " is not known, so it cannot be put back. Run Install.cmd again.";
        }
        foreach (string bin in bins)
        {
            foreach (string dll in new[] { "Api32.dll", "Api64.dll" })
            {
                string dest = Path.Combine(bin, dll);
                NotLink(dest);
                SaveOriginal(bin, dll);
                string from = lib ? Path.Combine(libDir, dll) : OriginalSource(bin, dll);
                if (from != null && File.Exists(from)) File.Copy(from, dest, true);
            }
        }
        if (lib)
        {
            // EdiabasLib settings: through the relay, RAW mode (the BMW original DLLs ignore this file)
            string inpaCfg = Path.Combine(libDir, "EdiabasLib-INPA.config");
            string istaCfg = Path.Combine(libDir, "EdiabasLib-ISTA.config");
            if (File.Exists(inpaCfg)) { string d = Path.Combine(Settings.EdiabasBin, "EdiabasLib.config"); NotLink(d); File.Copy(inpaCfg, d, true); }
            if (File.Exists(istaCfg) && IstaSeparate()) { string d = Path.Combine(Settings.IstaBin, "EdiabasLib.config"); NotLink(d); File.Copy(istaCfg, d, true); }
        }

        // Interface in EDIABAS.INI, for INPA / NCS Expert and for ISTA's own EDIABAS: ISTA's
        // "EDIABAS standard settings" connection uses it (a fresh ISTA installation has REMOTE)
        foreach (string bin in bins)
        {
            string ini = Path.Combine(bin, "EDIABAS.INI");
            NotLink(ini);
            string t = File.ReadAllText(ini, Encoding.Default);
            t = Regex.Replace(t, @"^([ \t]*Interface[ \t]*=[ \t]*)\S+", "${1}" + Iface(i), RegexOptions.Multiline);
            File.WriteAllText(ini, t, Encoding.Default);
        }

        if (i == Icom && Settings.IcomIp != "") WriteIcom();
        return null;
    }

    // Interactive switch with checks and, if needed, one administrator prompt.
    // Returns the message to show on success, null on failure (the error was already shown).
    public static string Apply(int i, IWin32Window owner)
    {
        string running = DiagRunning();
        if (running != null)
        {
            MessageBox.Show(owner, "Please close INPA, NCS Expert, Tool32 and ISTA first (" + running + " is running).", "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        try
        {
            string err = ApplyFiles(i);
            if (err != null) { MessageBox.Show(owner, err, "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
        }
        catch (UnauthorizedAccessException)
        {
            // The administrator part only works on the folders the installer set up
            if (!Settings.SameFolder(Settings.EdiabasBin, Settings.InstallerEdiabasBin) ||
                (IstaSeparate() && !Settings.SameFolder(Settings.IstaBin, Settings.InstallerIstaBin)))
            {
                MessageBox.Show(owner, "Your user account cannot change the EDIABAS files in these folders, and they are not the folders " +
                    "EdiaHelper was installed for.\n\nRun Install.cmd again: it finds the folders and sets them up.", "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            try
            {
                bool icom = i == Icom;
                string a = "/apply:" + i + " \"" + (icom ? Settings.IcomIp : "") + "\" \"" + (icom ? Settings.IcomPort : "6801") + "\" " + (icom ? Settings.IcomMethod : "RPLUS");
                Process p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, a) { Verb = "runas", UseShellExecute = true });
                p.WaitForExit();
                if (p.ExitCode == 2)
                    MessageBox.Show(owner, "The switch was refused because of an unexpected value (see the log).", "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (p.ExitCode != 0) return null;
            }
            catch
            {
                MessageBox.Show(owner, "Administrator rights are needed to change the EDIABAS files.", "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Switching failed: " + ex.Message, "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        Log.Write("Switched to " + Names[i] + " (interface " + Iface(i) + ")");
        string done = "Switched to: " + Names[i];
        if (i == Cable) done += "\n\n" + global::Cable.Match(owner);
        return done;
    }
}

// K+DCAN cable (FTDI): finds the cable's COM port and latency timer in the registry,
// writes the port into obd.ini and sets the latency timer to 1 ms.
static class Cable
{
    const string FtdiKey = @"SYSTEM\CurrentControlSet\Enum\FTDIBUS";

    public class Info
    {
        public string RegPath;   // ...\FTDIBUS\<device>\<instance>\Device Parameters
        public string Name;
        public string Port;      // e.g. COM3
        public int Latency;      // ms, -1 = unknown
        public bool Present;     // plugged in now
    }

    public static System.Collections.Generic.List<Info> Find()
    {
        var list = new System.Collections.Generic.List<Info>();
        string[] present;
        try { present = SerialPort.GetPortNames(); } catch { present = new string[0]; }
        try
        {
            using (Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(FtdiKey))
            {
                if (root == null) return list;
                foreach (string dev in root.GetSubKeyNames())
                    using (Microsoft.Win32.RegistryKey d = root.OpenSubKey(dev))
                    {
                        if (d == null) continue;
                        foreach (string inst in d.GetSubKeyNames())
                            using (Microsoft.Win32.RegistryKey k = d.OpenSubKey(inst))
                            using (Microsoft.Win32.RegistryKey p = d.OpenSubKey(inst + @"\Device Parameters"))
                            {
                                if (p == null) continue;
                                string port = p.GetValue("PortName") as string;
                                if (string.IsNullOrEmpty(port)) continue;
                                object lat = p.GetValue("LatencyTimer");
                                list.Add(new Info
                                {
                                    RegPath = FtdiKey + @"\" + dev + @"\" + inst + @"\Device Parameters",
                                    Name = (k != null ? k.GetValue("FriendlyName") as string : null) ?? "USB Serial Port",
                                    Port = port.ToUpperInvariant(),
                                    Latency = lat is int ? (int)lat : -1,
                                    Present = Array.Exists(present, x => string.Equals(x, port, StringComparison.OrdinalIgnoreCase))
                                });
                            }
                    }
            }
        }
        catch { }
        return list;
    }

    // The cable to use: the plugged-in one, or if none is plugged in, the only one Windows knows
    public static Info Current()
    {
        var all = Find();
        Info p = all.Find(c => c.Present);
        if (p != null) return p;
        return all.Count == 1 ? all[0] : null;
    }

    static string[] ObdIniFiles()
    {
        var l = new System.Collections.Generic.List<string> { Path.Combine(Settings.EdiabasBin, "obd.ini") };
        if (Switcher.IsEdiabasDir(Settings.IstaBin)) l.Add(Path.Combine(Settings.IstaBin, "obd.ini"));
        return l.ToArray();
    }

    public static string ObdIniPort()
    {
        try
        {
            string f = Path.Combine(Settings.EdiabasBin, "obd.ini");
            if (!File.Exists(f)) return "";
            Match m = Regex.Match(File.ReadAllText(f, Encoding.Default), @"^[ \t]*Port[ \t]*=[ \t]*(COM\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
        }
        catch { return ""; }
    }

    // True when obd.ini exists in every EDIABAS folder (INPA and ISTA) and uses this port
    static bool AllObdIniMatch(string port)
    {
        foreach (string f in ObdIniFiles())
        {
            if (!File.Exists(f)) return false;
            Match m = Regex.Match(File.ReadAllText(f, Encoding.Default), @"^[ \t]*Port[ \t]*=[ \t]*(COM\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (!m.Success || !string.Equals(m.Groups[1].Value, port, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    // Writes "Port=ComN" into obd.ini in the INPA folder and ISTA's folder (created there if missing)
    public static void WriteObdIni(string port)
    {
        string value = "Com" + port.Substring(3);
        string template = null;
        string inpaFile = Path.Combine(Settings.EdiabasBin, "obd.ini");
        if (File.Exists(inpaFile)) template = File.ReadAllText(inpaFile, Encoding.Default);
        foreach (string f in ObdIniFiles())
        {
            string t = File.Exists(f) ? File.ReadAllText(f, Encoding.Default)
                     : template ?? "[OBD]\r\nPort=Com1\r\nHardware=OBD\r\nRETRY=ON\r\n";
            // only the first, active Port= line (later ones are examples in some obd.ini files)
            Regex r = new Regex(@"^([ \t]*Port[ \t]*=[ \t]*)COM\d+", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            t = r.IsMatch(t) ? r.Replace(t, "${1}" + value, 1) : "[OBD]\r\nPort=" + value + "\r\nHardware=OBD\r\nRETRY=ON\r\n" + t;
            File.WriteAllText(f, t, Encoding.Default);
        }
    }

    // Needs administrator rights; throws otherwise
    public static void SetLatency(string regPath)
    {
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath, true))
        {
            if (k == null) throw new IOException("cable settings not found in the registry");
            k.SetValue("LatencyTimer", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    // Matches obd.ini to the cable and sets latency 1 ms. Returns a message for the user.
    public static string Match(IWin32Window owner) { return Match(owner, true); }

    // allowElevate = false: never show an administrator prompt (automatic switching)
    public static string Match(IWin32Window owner, bool allowElevate)
    {
        Info c = Current();
        if (c == null)
            return "No K+DCAN cable found. Plug the cable in (the driver must be installed) and press \"Match settings to cable\".";

        StringBuilder msg = new StringBuilder();
        try
        {
            if (!AllObdIniMatch(c.Port)) { WriteObdIni(c.Port); msg.AppendLine("obd.ini set to " + c.Port + "."); }
            else msg.AppendLine("obd.ini already uses " + c.Port + ".");
        }
        catch (Exception ex) { msg.AppendLine("Could not write obd.ini: " + ex.Message); }

        if (c.Latency != 1)
        {
            bool ok = false;
            try { SetLatency(c.RegPath); ok = true; }
            catch (Exception)
            {
                if (allowElevate) try
                {
                    Process p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "/latency \"" + c.RegPath + "\"") { Verb = "runas", UseShellExecute = true });
                    p.WaitForExit();
                    ok = p.ExitCode == 0;
                }
                catch { }
            }
            msg.AppendLine(ok ? "Latency timer set to 1 ms. Unplug the cable and plug it back in to use it."
                              : "Latency timer is " + c.Latency + " ms and could not be changed (administrator rights needed). " +
                                (allowElevate ? "Set it to 1 in Device Manager." : "Open EdiaHelper and press \"Match settings to cable\"."));
        }
        else msg.AppendLine("Latency timer is already 1 ms.");

        int n;
        if (int.TryParse(c.Port.Substring(3), out n) && n > 9)
            msg.AppendLine("\nNote: the cable is on " + c.Port + ". Some EDIABAS / INPA versions only work with COM1-COM9. " +
                           "If it does not connect, change the number in Device Manager: the cable's Properties -> Port Settings -> Advanced -> COM Port Number.");
        if (!c.Present) msg.AppendLine("\n(The cable is not plugged in right now; these are the settings Windows remembers for it.)");
        Log.Write("Cable setup: " + msg.ToString().Replace("\r\n", " ").Trim());
        return msg.ToString().Trim();
    }

    public static string Describe()
    {
        Info c = Current();
        string ini = ObdIniPort();
        if (c == null) return "Cable: not found (plug it in)" + "     obd.ini: " + (ini == "" ? "no port set" : ini);
        return "Cable: " + c.Port + (c.Present ? "" : " (not plugged in)") + ", latency " + (c.Latency < 0 ? "?" : c.Latency.ToString()) + " ms" +
               "     obd.ini: " + (ini == "" ? "no port set" : ini) + (ini == c.Port ? "  - OK" : "  - does not match");
    }
}

// Detects whether the chosen adapter is actually connected, for the tray icon.
static class Status
{
    public static string Letter = null;     // null = nothing connected
    public static string Text = "";

    // What is connected right now, for automatic switching
    public static string Ssid = "";
    public static bool CablePresent;
    public static string CablePort = "";

    // The choice that fits an MHD WiFi network, or -1
    public static int ChoiceForSsid(string ssid)
    {
        if (!Relay.IsMhdSsid(ssid)) return -1;
        if (Relay.IsEnetSsid(ssid)) return Switcher.MhdEnet;
        if (Relay.IsCanSsid(ssid) || Relay.IsKlineSsid(ssid)) return Switcher.MhdKD;
        return -1;
    }

    public static void Update(int choice)
    {
        string ssid = Relay.CurrentSsid();
        Ssid = ssid;
        Cable.Info cable = Cable.Find().Find(x => x.Present);
        CablePresent = cable != null;
        CablePort = cable != null ? cable.Port : "";
        string letter = null, text;
        switch (choice)
        {
            case Switcher.MhdKD:
                if (Relay.IsMhdSsid(ssid) && Relay.IsCanSsid(ssid)) letter = "D";
                else if (Relay.IsMhdSsid(ssid) && Relay.IsKlineSsid(ssid)) letter = "K";
                text = letter != null ? "Connected to " + ssid
                     : Relay.IsMhdSsid(ssid) ? "WiFi is " + ssid + ", but the choice is K-line / D-CAN"
                     : "Not connected to an MHD adapter's WiFi";
                break;
            case Switcher.MhdEnet:
                if (Relay.IsMhdSsid(ssid) && Relay.IsEnetSsid(ssid)) letter = "E";
                text = letter != null ? "Connected to " + ssid
                     : Relay.IsMhdSsid(ssid) ? "WiFi is " + ssid + ", but the choice is ENET"
                     : "Not connected to an MHD adapter's WiFi";
                break;
            case Switcher.Cable:
                {
                    Cable.Info c = Cable.Current();
                    string ini = Cable.ObdIniPort();
                    if (c != null && c.Present && c.Port == ini) letter = "C";
                    text = letter != null ? "K+DCAN cable on " + c.Port
                         : c != null && c.Present ? "Cable on " + c.Port + " but obd.ini says " + (ini == "" ? "nothing" : ini) + " - open EdiaHelper"
                         : "K+DCAN cable not plugged in";
                }
                break;
            case Switcher.EnetCable:
                if (EthernetLinkUp()) letter = "E";
                text = letter != null ? "Ethernet connected" : "Ethernet cable not connected";
                break;
            case Switcher.Icom:
                if (IcomReachable()) letter = "I";
                text = letter != null ? "ICOM answers at " + Settings.IcomIp : "ICOM not reachable" + (Settings.IcomIp != "" ? " at " + Settings.IcomIp : "");
                break;
            default:
                text = "Unknown setting - open EdiaHelper";
                break;
        }
        Letter = letter;
        Text = text;
    }

    static bool EthernetLinkUp()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (d.Contains("virtual") || d.Contains("bluetooth") || d.Contains("rndis") || d.Contains("remote ndis") || d.Contains("vpn")) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    static bool IcomReachable()
    {
        IPAddress a;
        if (!IPAddress.TryParse(Settings.IcomIp, out a)) return false;
        try { using (Ping p = new Ping()) return p.Send(a, 700).Status == IPStatus.Success; }
        catch { return false; }
    }
}

class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly ToolStripMenuItem[] choiceItems = new ToolStripMenuItem[Switcher.Names.Length];
    readonly SynchronizationContext ui;
    MainForm form;
    Icon currentIcon;
    string shownKey = "";
    int choice = -1;
    volatile bool refreshing;
    ToolStripMenuItem autoItem;

    // Automatic switching reacts to changes only, so a manual choice is kept until something new is connected
    string lastSsid = null;
    bool lastCable;
    int pendingAuto = -1;
    string pendingWhy = "";
    bool waitNoticeShown;

    public TrayApp(EventWaitHandle show, bool openWindow)
    {
        ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);

        menu.Items.Add(new ToolStripMenuItem("Open EdiaHelper", null, (s, e) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        for (int i = 0; i < Switcher.Visible; i++)
        {
            int n = i;
            choiceItems[i] = new ToolStripMenuItem(Switcher.Names[i], null, (s, e) => SwitchFromTray(n));
            menu.Items.Add(choiceItems[i]);
        }
        menu.Items.Add(new ToolStripSeparator());
        autoItem = new ToolStripMenuItem("Switch automatically", null, (s, e) => SetAutoSwitch(!Settings.AutoSwitch));
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check connection", null, (s, e) => Check.Show(null));
        menu.Items.Add("Open NCS backups", null, (s, e) => NcsBackup.OpenFolder());
        menu.Items.Add("Open log", null, (s, e) => { try { Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + Log.PathName + "\""); } catch { } });
        menu.Items.Add("Exit", null, (s, e) => ExitApp());
        menu.Opening += (s, e) =>
        {
            for (int i = 0; i < choiceItems.Length; i++) if (choiceItems[i] != null) choiceItems[i].Checked = i == choice;
            autoItem.Checked = Settings.AutoSwitch;
        };

        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        SetIcon(false, null, "EdiaHelper - starting");
        tray.Visible = true;

        Relay.StateChanged += () => ui.Post(_ => Refresh(), null);
        NcsBackup.Saved += text => ui.Post(_ =>
        {
            tray.ShowBalloonTip(3000, "EdiaHelper", text, ToolTipIcon.Info);
            if (form != null && form.Visible) form.ShowNcs();
        }, null);

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 4000 };
        t.Tick += (s, e) => Refresh();
        t.Start();
        Refresh();

        new Thread(() =>
        {
            while (true) { show.WaitOne(); ui.Post(_ => ShowWindow(), null); }
        }) { IsBackground = true }.Start();

        if (openWindow) ShowWindow();
    }

    public void Balloon(string text)
    {
        tray.ShowBalloonTip(6000, "EdiaHelper", text, ToolTipIcon.Warning);
    }

    void SetIcon(bool active, string letter, string tip)
    {
        string key = (active ? "1" : "0") + letter;
        if (key != shownKey)
        {
            Icon old = currentIcon;
            currentIcon = IconArt.TrayIcon(active, letter);
            tray.Icon = currentIcon;
            if (old != null) old.Dispose();
            shownKey = key;
        }
        tray.Text = tip.Length > 63 ? tip.Substring(0, 60) + "..." : tip;
    }

    // Status checks run on a worker thread (netsh, ping); the icon is updated on the UI thread
    public void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int c; string letter, text, ssid, cablePort; bool cable;
            try
            {
                c = Switcher.Current();
                Status.Update(c);
                LastSeen.Note(Status.Ssid, Status.CablePresent, Status.CablePort, c, Status.Letter);
                letter = Status.Letter; text = Status.Text;
                ssid = Status.Ssid; cablePort = Status.CablePort;
                cable = Status.CablePresent;
            }
            catch (Exception ex)
            {
                Log.WriteOnce("Status check failed: " + ex.Message);
                refreshing = false;
                return;
            }
            ui.Post(__ =>
            {
                choice = c;
                string name = c >= 0 ? Switcher.Names[c] : "Unknown setting";
                SetIcon(letter != null, letter, name + " - " + text);
                if (form != null && form.Visible) form.ShowStatus(c, text);
                refreshing = false;
                if (CheckAuto(c, ssid, cable, cablePort)) Refresh();
            }, null);
        });
    }

    public void SetAutoSwitch(bool on)
    {
        Settings.AutoSwitch = on;
        Settings.Save();
        pendingAuto = -1;
        lastSsid = null; lastCable = false;   // look at what is connected now
        if (form != null && form.Visible) form.ShowAuto();
        Refresh();
    }

    // A manual choice wins until the next adapter is connected
    public void CancelAuto() { pendingAuto = -1; }

    // Automatic switching. Returns true when the files were switched.
    bool CheckAuto(int current, string ssid, bool cable, string cablePort)
    {
        // Only changes in the connected adapters count: an MHD WiFi (by name) and the K+DCAN cable.
        // A switch happens only when exactly one adapter is connected; with both, nothing is switched.
        int forSsid = Status.ChoiceForSsid(ssid);
        string mhd = forSsid >= 0 ? ssid : "";
        if (mhd != lastSsid || cable != lastCable)
        {
            waitNoticeShown = false;
            if (mhd != "" && cable)
            {
                pendingAuto = -1;
                if (Settings.AutoSwitch && lastSsid != null)
                    tray.ShowBalloonTip(6000, "EdiaHelper", "Both the MHD WiFi (" + ssid + ") and the K+DCAN cable are connected.\n" +
                        "Not switching automatically - choose the adapter in the tray menu.", ToolTipIcon.Info);
                Log.Write("Automatic switch skipped: MHD WiFi " + ssid + " and K+DCAN cable both connected");
            }
            else if (mhd != "") { pendingAuto = forSsid; pendingWhy = ssid; }
            else if (cable) { pendingAuto = Switcher.Cable; pendingWhy = "K+DCAN cable on " + cablePort; }
            else pendingAuto = -1;
        }
        lastSsid = mhd; lastCable = cable;

        if (!Settings.AutoSwitch || pendingAuto < 0) { pendingAuto = -1; return false; }
        int target = pendingAuto;
        if (target == current) { pendingAuto = -1; return false; }

        string running = Switcher.DiagRunning();
        if (running != null)
        {
            if (!waitNoticeShown)
                tray.ShowBalloonTip(6000, "EdiaHelper", pendingWhy + " detected.\nClose INPA / NCS Expert / ISTA to switch to " + Switcher.Names[target] + ".", ToolTipIcon.Info);
            waitNoticeShown = true;
            return false;   // tried again on the next check
        }

        pendingAuto = -1;
        try
        {
            string err = Switcher.ApplyFiles(target);
            if (err != null)
            {
                tray.ShowBalloonTip(6000, "EdiaHelper", "Automatic switch failed: " + err, ToolTipIcon.Warning);
                return false;
            }
        }
        catch (UnauthorizedAccessException)
        {
            tray.ShowBalloonTip(6000, "EdiaHelper", pendingWhy + " detected, but switching needs administrator rights. Open EdiaHelper and press Apply.", ToolTipIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(6000, "EdiaHelper", "Automatic switch failed: " + ex.Message, ToolTipIcon.Warning);
            return false;
        }

        string msg = pendingWhy + " detected - switched to " + Switcher.Names[target] + ".";
        if (target == Switcher.Cable)
        {
            string m = Cable.Match(null, false);
            if (m.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0) msg += "\nCheck the cable settings in EdiaHelper.";
        }
        Log.Write("Automatic switch: " + msg);
        tray.ShowBalloonTip(4000, "EdiaHelper", msg, ToolTipIcon.Info);
        return true;
    }

    void SwitchFromTray(int i)
    {
        if (i == Switcher.Icom && Settings.IcomIp == "")
        {
            ShowWindow();
            form.SelectChoice(Switcher.Icom);
            return;
        }
        string done = Switcher.Apply(i, null);
        if (done != null)
        {
            CancelAuto();
            if (i == Switcher.Cable) MessageBox.Show(done, "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else tray.ShowBalloonTip(2500, "EdiaHelper", done, ToolTipIcon.Info);
        }
        Refresh();
    }

    void ShowWindow()
    {
        if (form == null || form.IsDisposed) form = new MainForm(this);
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
        form.LoadState();
        Refresh();
    }

    void ExitApp()
    {
        tray.Visible = false;
        if (form != null) form.ReallyClose();
        Log.Write("EdiaHelper closed");
        ExitThread();
    }
}

class MainForm : Form
{
    readonly TrayApp app;
    readonly ComboBox combo;
    readonly Label current, inpaState, istaState, cableInfo;
    readonly TextBox inpaBox, istaBox, icomIp, icomPort;
    readonly RadioButton rplus, remote;
    readonly Panel icomPanel, cablePanel;
    readonly CheckBox autoBox, ncsBox;
    readonly TextBox ncsWorkBox, ncsDirBox;
    readonly Label ncsLast;
    bool reallyClose;

    public MainForm(TrayApp app)
    {
        this.app = app;
        Text = "EdiaHelper " + Program.Version;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(530, 380);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        GroupBox folders = new GroupBox { Text = "EDIABAS folders (where EDIABAS.INI and the DLLs are)", Location = new Point(12, 10), Size = new Size(498, 118) };
        inpaBox = new TextBox { Location = new Point(130, 24), Width = 280 };
        istaBox = new TextBox { Location = new Point(130, 70), Width = 280 };
        Button inpaBrowse = new Button { Text = "Browse...", Location = new Point(416, 22), Size = new Size(80, 26) };
        Button istaBrowse = new Button { Text = "Browse...", Location = new Point(416, 68), Size = new Size(80, 26) };
        inpaState = new Label { Location = new Point(130, 49), AutoSize = true };
        istaState = new Label { Location = new Point(130, 95), AutoSize = true };
        folders.Controls.AddRange(new Control[] {
            new Label { Text = "INPA / NCS Expert:", Location = new Point(10, 27), AutoSize = true }, inpaBox, inpaBrowse, inpaState,
            new Label { Text = "ISTA:", Location = new Point(10, 73), AutoSize = true }, istaBox, istaBrowse, istaState });
        inpaBrowse.Click += (s, e) => Browse(inpaBox);
        istaBrowse.Click += (s, e) => Browse(istaBox);
        inpaBox.Leave += (s, e) => FoldersChanged();
        istaBox.Leave += (s, e) => FoldersChanged();

        Label l1 = new Label { Text = "Adapter:", Location = new Point(14, 145), AutoSize = true };
        combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(80, 141), Width = 200, DropDownWidth = 260 };
        for (int k = 0; k < Switcher.Visible; k++) combo.Items.Add(Switcher.Names[k]);

        icomPanel = new Panel { Location = new Point(12, 174), Size = new Size(506, 112), Visible = false };
        rplus = new RadioButton { Text = "RPLUS:ICOM_P (address in RPLUS.INI)", Location = new Point(130, 0), AutoSize = true, Checked = true };
        remote = new RadioButton { Text = "REMOTE (address in EDIABAS.INI [TCP])", Location = new Point(130, 22), AutoSize = true };
        icomIp = new TextBox { Location = new Point(130, 50), Width = 150 };
        icomPort = new TextBox { Location = new Point(340, 50), Width = 60 };
        icomPanel.Controls.AddRange(new Control[] {
            new Label { Text = "ICOM method:", Location = new Point(2, 2), AutoSize = true }, rplus, remote,
            new Label { Text = "ICOM IP address:", Location = new Point(2, 53), AutoSize = true }, icomIp,
            new Label { Text = "Port:", Location = new Point(300, 53), AutoSize = true }, icomPort,
            new Label { Text = "Reserve the ICOM before use, as its own instructions describe.", Location = new Point(2, 84), AutoSize = true, ForeColor = Color.DimGray } });
        cablePanel = new Panel { Location = new Point(12, 174), Size = new Size(506, 112), Visible = false };
        cableInfo = new Label { Location = new Point(2, 4), Size = new Size(500, 40) };
        Button match = new Button { Text = "Match settings to cable", Location = new Point(2, 48), Size = new Size(190, 30) };
        cablePanel.Controls.AddRange(new Control[] { cableInfo, match,
            new Label { Text = "Writes the cable's COM port into obd.ini and sets its latency timer to 1 ms.", Location = new Point(2, 84), AutoSize = true, ForeColor = Color.DimGray } });
        match.Click += (s, e) =>
        {
            FoldersChanged();
            MessageBox.Show(this, Cable.Match(this), "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            UpdateCableInfo();
            app.Refresh();
        };

        combo.SelectedIndexChanged += (s, e) =>
        {
            icomPanel.Visible = combo.SelectedIndex == Switcher.Icom;
            cablePanel.Visible = combo.SelectedIndex == Switcher.Cable;
            if (cablePanel.Visible) UpdateCableInfo();
        };
        rplus.CheckedChanged += (s, e) => { if (rplus.Checked) { Settings.IcomMethod = "RPLUS"; LoadIcom(); } };
        remote.CheckedChanged += (s, e) => { if (remote.Checked) { Settings.IcomMethod = "REMOTE"; LoadIcom(); } };

        Button apply = new Button { Text = "Apply", Location = new Point(418, 294), Size = new Size(100, 32) };
        current = new Label { Location = new Point(14, 336), Size = new Size(504, 40), ForeColor = Color.DimGray };
        Button lastSeen = new Button { Text = "Last seen", Location = new Point(288, 139), Size = new Size(94, 28) };
        lastSeen.Click += (s, e) =>
        {
            int ch = combo.SelectedIndex;
            MessageBox.Show(this, LastSeen.Report(ch), "Where was it last seen? - " + (ch >= 0 ? Switcher.Names[ch] : ""), MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        apply.Click += (s, e) => DoApply();

        Label legend = new Label
        {
            Location = new Point(14, 382), Size = new Size(504, 36), ForeColor = Color.DimGray,
            Text = Settings.ShowUntested
                 ? "Tray icon:  K = MHD K-line    D = MHD D-CAN    E = ENET (MHD or cable)\r\n" +
                   "                 C = K+DCAN cable    I = ICOM    grey = not connected"
                 : "Tray icon:  K = MHD K-line    D = MHD D-CAN    E = MHD ENET\r\n" +
                   "                 C = K+DCAN cable    grey = not connected"
        };
        GroupBox ncs = new GroupBox { Text = "NCS Expert coding backups", Location = new Point(12, 420), Size = new Size(498, 146) };
        ncsBox = new CheckBox
        {
            Text = "Save a copy of every module NCS Expert reads (FSW_PSW.TRC, NETTODAT.TRC)",
            Location = new Point(10, 20), Size = new Size(490, 24), Checked = Settings.NcsBackup
        };
        ncsWorkBox = new TextBox { Location = new Point(130, 50), Width = 280 };
        ncsDirBox = new TextBox { Location = new Point(130, 80), Width = 280 };
        Button workBrowse = new Button { Text = "Browse...", Location = new Point(416, 48), Size = new Size(80, 26) };
        Button dirBrowse = new Button { Text = "Browse...", Location = new Point(416, 78), Size = new Size(80, 26) };
        Button openNcs = new Button { Text = "Open backups", Location = new Point(396, 108), Size = new Size(100, 28) };
        ncsLast = new Label { Location = new Point(10, 114), Size = new Size(382, 20), ForeColor = Color.DimGray, AutoEllipsis = true };
        ncs.Controls.AddRange(new Control[] { ncsBox,
            new Label { Text = "NCS WORK folder:", Location = new Point(10, 53), AutoSize = true }, ncsWorkBox, workBrowse,
            new Label { Text = "Backups to:", Location = new Point(10, 83), AutoSize = true }, ncsDirBox, dirBrowse,
            ncsLast, openNcs });
        ncsBox.CheckedChanged += (s, e) => NcsChanged();
        ncsWorkBox.Leave += (s, e) => NcsChanged();
        ncsDirBox.Leave += (s, e) => NcsChanged();
        workBrowse.Click += (s, e) => BrowseNcs(ncsWorkBox, "Choose NCS Expert's WORK folder (where FSW_PSW.TRC is written)");
        dirBrowse.Click += (s, e) => BrowseNcs(ncsDirBox, "Choose where the coding backups are saved");
        openNcs.Click += (s, e) => { NcsChanged(); NcsBackup.OpenFolder(); };
        ClientSize = new Size(530, 596);

        Button check = new Button { Text = "Check connection", Location = new Point(388, 139), Size = new Size(130, 28) };
        check.Click += (s, e) => { FoldersChanged(); Check.Show(this); app.Refresh(); };

        Label about = new Label
        {
            Location = new Point(14, 570), AutoSize = true, ForeColor = Color.DimGray,
            Text = "EdiaHelper " + Program.Version + "  -  free software, GNU GPL v3"
        };

        autoBox = new CheckBox
        {
            Text = "Switch automatically when an MHD WiFi or the\r\nK+DCAN cable is connected",
            Location = new Point(14, 290), Size = new Size(390, 42), Checked = Settings.AutoSwitch
        };
        autoBox.CheckedChanged += (s, e) => { if (autoBox.Checked != Settings.AutoSwitch) app.SetAutoSwitch(autoBox.Checked); };

        // Three tabs: the adapter (everyday use), NCS backups, and the EDIABAS folders (set once)
        TabControl tabs = new TabControl { Location = new Point(6, 8), Size = new Size(518, 330) };
        TabPage adapterTab = new TabPage("Adapter"), ncsTab = new TabPage("NCS backups"), folderTab = new TabPage("EDIABAS folders");
        foreach (TabPage p in new[] { adapterTab, ncsTab, folderTab }) p.UseVisualStyleBackColor = true;
        Control[] adapterControls = { l1, combo, check, icomPanel, cablePanel, autoBox, apply, current, lastSeen, legend };
        foreach (Control c in adapterControls) c.Location = new Point(c.Left - 12, c.Top - 131);
        adapterTab.Controls.AddRange(adapterControls);

        ncs.Location = new Point(2, 6);
        ncsTab.Controls.Add(ncs);
        ncsTab.Controls.Add(new Label
        {
            Location = new Point(6, 160), Size = new Size(490, 100), ForeColor = Color.DimGray,
            Text = "NCS Expert overwrites FSW_PSW.TRC in its WORK folder every time it reads a module. " +
                   "A copy is saved every time, named after the module and the time it was read, " +
                   "in a folder per day and car (date + last 7 of the VIN, or the chassis).\r\n\r\n" +
                   "Nothing is ever overwritten or deleted."
        });

        folders.Location = new Point(2, 6);
        folderTab.Controls.Add(folders);
        folderTab.Controls.Add(new Label
        {
            Location = new Point(6, 134), Size = new Size(490, 60), ForeColor = Color.DimGray,
            Text = "The installer finds these folders. Change them only if INPA / NCS Expert or ISTA are installed somewhere else."
        });

        tabs.TabPages.AddRange(new[] { adapterTab, ncsTab, folderTab });
        tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedTab == ncsTab) ShowNcs(); };
        ClientSize = new Size(530, 372);
        about.Location = new Point(14, 346);
        Button aboutButton = new Button { Text = "About", Location = new Point(444, 341), Size = new Size(76, 26) };
        aboutButton.Click += (s, e) => { using (AboutForm f = new AboutForm()) f.ShowDialog(this); };
        Controls.Add(aboutButton);
        Controls.AddRange(new Control[] { tabs, about });
        AcceptButton = apply;
    }

    void UpdateCableInfo()
    {
        if (cablePanel.Visible) cableInfo.Text = Cable.Describe();
    }

    public void LoadState()
    {
        inpaBox.Text = Settings.EdiabasBin;
        istaBox.Text = Settings.IstaBin;
        SetState(inpaState, Settings.EdiabasBin);
        SetState(istaState, Settings.IstaBin);
        if (Settings.IcomMethod == "REMOTE") remote.Checked = true; else rplus.Checked = true;
        LoadIcom();
        int c = Switcher.Current();
        combo.SelectedIndex = c >= 0 && c < combo.Items.Count ? c : -1;
        ShowAuto();
        ncsBox.Checked = Settings.NcsBackup;
        ncsWorkBox.Text = Settings.NcsWork;
        ncsDirBox.Text = NcsBackup.BackupDir;
        ShowNcs();
    }

    public void ShowNcs()
    {
        if (!Settings.NcsBackup) ncsLast.Text = "Off";
        else if (!Directory.Exists(Settings.NcsWork)) ncsLast.Text = "NCS WORK folder not found";
        else if (NcsBackup.LastSaved != "") ncsLast.Text = "Last saved: " + NcsBackup.LastSaved;
        else ncsLast.Text = NcsBackup.Watching ? "Watching - nothing new saved yet" : "Not watching (see the log)";
    }

    void NcsChanged()
    {
        string work = ncsWorkBox.Text.Trim(), dir = ncsDirBox.Text.Trim();
        // The backup folder follows the WORK folder unless it was changed by hand
        string dirSetting = dir == "" || dir.Equals(NcsBackup.DefaultBackupDir(work), StringComparison.OrdinalIgnoreCase)
                            || dir.Equals(NcsBackup.DefaultBackupDir(Settings.NcsWork), StringComparison.OrdinalIgnoreCase) ? "" : dir;
        bool changed = ncsBox.Checked != Settings.NcsBackup || work != Settings.NcsWork || dirSetting != Settings.NcsBackupDir;
        if (!changed) return;
        Settings.NcsBackup = ncsBox.Checked;
        Settings.NcsWork = work;
        Settings.NcsBackupDir = dirSetting;
        Settings.Save();
        NcsBackup.Restart();
        ncsDirBox.Text = NcsBackup.BackupDir;
        ShowNcs();
    }

    void BrowseNcs(TextBox box, string text)
    {
        using (FolderBrowserDialog dlg = new FolderBrowserDialog())
        {
            dlg.Description = text;
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) { box.Text = dlg.SelectedPath; NcsChanged(); }
        }
    }

    public void SaveTabPictures(string prefix)
    {
        TabControl tabs = null;
        foreach (Control c in Controls) if (c is TabControl) tabs = (TabControl)c;
        for (int i = 0; i < tabs.TabCount; i++)
        {
            tabs.SelectedIndex = i;
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(prefix + (i + 1) + ".png", System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }

    public void SelectChoice(int i) { if (i < combo.Items.Count) combo.SelectedIndex = i; }

    public void ShowAuto() { autoBox.Checked = Settings.AutoSwitch; }

    void LoadIcom()
    {
        string ip, port;
        Switcher.ReadIcom(out ip, out port);
        icomIp.Text = ip != "" ? ip : Settings.IcomIp;
        icomPort.Text = port;
    }

    public void ShowStatus(int c, string text)
    {
        current.Text = "Current setting: " + (c >= 0 ? Switcher.Names[c] : "unknown (interface " + Switcher.InpaInterface() + ", files changed by hand)") +
                       "\r\nStatus: " + text;
    }

    void Browse(TextBox box)
    {
        using (FolderBrowserDialog dlg = new FolderBrowserDialog())
        {
            dlg.Description = "Choose the EDIABAS BIN folder (the one that contains EDIABAS.INI)";
            if (Directory.Exists(box.Text)) dlg.SelectedPath = box.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) { box.Text = dlg.SelectedPath; FoldersChanged(); }
        }
    }

    void FoldersChanged()
    {
        Settings.EdiabasBin = inpaBox.Text.Trim();
        Settings.IstaBin = istaBox.Text.Trim();
        SetState(inpaState, Settings.EdiabasBin);
        SetState(istaState, Settings.IstaBin);
        Settings.Save();
        app.Refresh();
    }

    static void SetState(Label l, string d)
    {
        bool ok = Switcher.IsEdiabasDir(d);
        l.Text = ok ? "Found EDIABAS.INI" : "EDIABAS.INI not found in this folder";
        l.ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
    }

    void DoApply()
    {
        FoldersChanged();
        int i = combo.SelectedIndex;
        if (i < 0) return;
        if (i == Switcher.Icom)
        {
            IPAddress a; int p;
            string ipText = icomIp.Text.Trim(), portText = icomPort.Text.Trim();
            // the same rules as the administrator part: four-part IPv4 address, plain port number
            if (!IPAddress.TryParse(ipText, out a) || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ipText.Split('.').Length != 4)
            {
                MessageBox.Show(this, "Please enter the ICOM's IP address, for example 169.254.92.38.", "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (!int.TryParse(portText, out p) || p < 1 || p > 65535 || portText != p.ToString())
            {
                MessageBox.Show(this, "Please enter a valid port (normally 6801).", "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Settings.IcomIp = icomIp.Text.Trim();
            Settings.IcomPort = icomPort.Text.Trim();
            Settings.IcomMethod = remote.Checked ? "REMOTE" : "RPLUS";
            Settings.Save();
        }
        string done = Switcher.Apply(i, this);
        if (done != null)
        {
            app.CancelAuto();
            MessageBox.Show(this, done, "EdiaHelper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        UpdateCableInfo();
        app.Refresh();
    }

    public void ReallyClose() { reallyClose = true; Close(); }

    // Closing the window only hides it; the program keeps running in the tray
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }
}

// About: version, copyright, license (GPLv3 requires these "appropriate legal notices" in the program)
class AboutForm : Form
{
    public const string EdiabasLibVersion = "8.0.0.407";
    public const string EdiabasLibRelease = "binaries_20260607";
    public const string EdiabasLibUrl = "https://github.com/uholeschak/ediabaslib";

    public AboutForm()
    {
        Text = "About EdiaHelper";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(500, 390);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Label title = new Label { Text = "EdiaHelper " + Program.Version, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Location = new Point(16, 14), AutoSize = true };
        Label copy = new Label
        {
            Location = new Point(18, 50), Size = new Size(466, 96),
            Text = "Copyright (C) 2026 Nahkasikari\r\n\r\n" +
                   "This program is free software: you can redistribute it and/or modify it under the terms of the " +
                   "GNU General Public License, version 3. It comes WITHOUT ANY WARRANTY; see the license for details."
        };
        LinkLabel source = Link("Source code: " + Program.RepoUrl, Program.RepoUrl, 150);

        Label lib = new Label
        {
            Location = new Point(18, 186), Size = new Size(466, 58),
            Text = "Includes EdiabasLib " + EdiabasLibVersion + " (Api32.dll / Api64.dll, release " + EdiabasLibRelease + ") " +
                   "by Ulrich Holeschak, also licensed under the GNU GPL version 3. Not modified."
        };
        LinkLabel libSource = Link("EdiabasLib source: " + EdiabasLibUrl, EdiabasLibUrl + "/tree/" + EdiabasLibRelease, 246);

        Label bmw = new Label
        {
            Location = new Point(18, 284), Size = new Size(466, 44), ForeColor = Color.DimGray,
            Text = "Not affiliated with BMW or MHD. INPA, NCS Expert, ISTA and EDIABAS are BMW software and are not included."
        };

        Button license = new Button { Text = "View license", Location = new Point(16, 344), Size = new Size(110, 30) };
        license.Click += (s, e) =>
        {
            string f = Path.Combine(Settings.Dir, "LICENSE.txt");
            try { if (File.Exists(f)) Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + f + "\""); else Process.Start("https://www.gnu.org/licenses/gpl-3.0.html"); } catch { }
        };
        Button close = new Button { Text = "Close", Location = new Point(394, 344), Size = new Size(90, 30), DialogResult = DialogResult.OK };
        AcceptButton = close; CancelButton = close;
        Button update = new Button { Text = "Check for updates", Location = new Point(134, 344), Size = new Size(140, 30) };
        update.Click += (s, e) => CheckForUpdates(update);
        Controls.AddRange(new Control[] { title, copy, source, lib, libSource, bmw, license, update, close });
    }

    // Asks GitHub for the newest release, only when the button is pressed. Nothing about the
    // computer or the user is sent; nothing is downloaded or installed - the user is only told
    // whether a newer version exists and how to update.
    void CheckForUpdates(Button b)
    {
        b.Enabled = false; b.Text = "Checking...";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string tag = null, url = null, notes = null, error = null;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/Nahkasikari/EdiaHelper/releases/latest");
                r.UserAgent = "EdiaHelper-update-check";
                r.Accept = "application/vnd.github+json";
                r.Timeout = 10000;
                r.ReadWriteTimeout = 10000;
                string json;
                using (WebResponse resp = r.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = sr.ReadToEnd();
                tag = JsonString(json, "tag_name");
                url = JsonString(json, "html_url");
                notes = JsonString(json, "body");
            }
            catch (Exception ex) { error = ex.Message; }
            // The About window may have been closed meanwhile; then the answer is simply dropped
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    if (IsDisposed) return;
                    b.Enabled = true; b.Text = "Check for updates";
                    ShowUpdateResult(tag, url, notes, error);
                }));
            }
            catch { }
        });
    }

    void ShowUpdateResult(string tag, string url, string notes, string error)
    {
        string releases = Program.RepoUrl + "/releases";
        Version mine = new Version(Program.Version), latest;
        if (error != null || tag == null || !Version.TryParse(tag.TrimStart('v', 'V'), out latest))
        {
            Log.Write("Update check failed: " + (error ?? "no version in the answer"));
            if (MessageBox.Show(this, "Could not check for updates (no internet connection, or GitHub could not be reached).\n\n" +
                "You can look yourself on the releases page:\n" + releases + "\n\nOpen it now?",
                "Check for updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                try { Process.Start(releases); } catch { }
            return;
        }
        Log.Write("Update check: newest " + tag + ", this is " + Program.Version);
        if (latest <= mine)
        {
            MessageBox.Show(this, "You have the newest version (" + Program.Version + ").", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // only the short introduction of the release text (before its first heading), without markdown
        string what = (notes ?? "").Replace("\r", "");
        int h = what.IndexOf("\n#");
        if (h >= 0) what = what.Substring(0, h);
        what = what.Replace("**", "").Replace("`", "").Trim();
        if (what.Length > 700) what = what.Substring(0, 700) + "\n...";
        string msg = "EdiaHelper " + latest + " is available (you have " + Program.Version + ").\n\n" +
                     (what != "" ? "What's new:\n" + what + "\n\n" : "") +
                     "How to update:\n" +
                     "1. Download EdiaHelper-" + latest + ".zip from the release page and unzip it.\n" +
                     "2. Close INPA, NCS Expert and ISTA.\n" +
                     "3. Run Install.cmd from the unzipped folder.\n" +
                     "Your settings, NCS backups and \"last seen\" records are kept.\n\n" +
                     "Open the release page now?";
        if (MessageBox.Show(this, msg, "Check for updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            try { Process.Start(url != null && url.StartsWith(Program.RepoUrl + "/") ? url : releases); } catch { }
    }

    // The value of a top-level string field in GitHub's JSON answer
    static string JsonString(string json, string name)
    {
        Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return null;
        string v = m.Groups[1].Value;
        v = Regex.Replace(v, @"\\u([0-9a-fA-F]{4})", x => ((char)Convert.ToInt32(x.Groups[1].Value, 16)).ToString());
        return v.Replace("\\r", "").Replace("\\n", "\n").Replace("\\t", " ").Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\\\", "\\");
    }

    static LinkLabel Link(string text, string url, int y)
    {
        LinkLabel l = new LinkLabel { Text = text, Location = new Point(18, y), AutoSize = true };
        l.LinkArea = new LinkArea(text.IndexOf("https"), text.Length - text.IndexOf("https"));
        l.LinkClicked += (s, e) => { try { Process.Start(url); } catch { } };
        return l;
    }
}
