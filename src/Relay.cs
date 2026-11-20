// Relay between EdiabasLib and the MHD universal WiFi adapter.
//
// EdiabasLib connects to 127.0.0.1:2323 (ObdComPort = DEEPOBDWIFI:127.0.0.1:2323:RAW),
// the relay connects to the adapter (WiFi gateway, normally 192.168.4.1) on port 23.
//
// In CAN mode the MHD reads the first byte of KWP2000* "B8" telegrams (and DS2 telegrams)
// as a long length and swallows the following requests. That broke ISTA identification on
// D-CAN cars. On a "MHD CAN"/"MHD DCAN" network only well-formed BMW-FAST telegrams are
// forwarded; on any other network everything is passed through unchanged.

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

static class Relay
{
    public const int ListenPort = 2323;
    const string DefaultAdapterIp = "192.168.4.1";
    const int AdapterPort = 23;

    static readonly object upLock = new object();
    static TcpClient up;
    static NetworkStream us;
    static NetworkStream owner;
    static int ownerId;
    static int clients;
    static bool filterCan;

    public static bool Verbose;
    public static bool Running;
    public static volatile string Mode = "";        // "" = not connected, "K" or "D" while connected
    public static volatile string Ssid = "";
    public static event Action StateChanged;

    public static bool Start()
    {
        TcpListener l;
        try
        {
            l = new TcpListener(IPAddress.Loopback, ListenPort);
            l.Start();
        }
        catch (Exception ex)
        {
            Log.Write("Relay cannot listen on port " + ListenPort + ": " + ex.Message);
            return false;
        }
        new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "RelayAccept" }.Start();
        Log.Write("Relay listening on 127.0.0.1:" + ListenPort);
        Running = true;
        return true;
    }

    static void SetMode(string m)
    {
        if (Mode == m) return;
        Mode = m;
        Action h = StateChanged;
        if (h != null) try { h(); } catch { }
    }

    static string Hex(byte[] b, int off, int n)
    {
        return BitConverter.ToString(b, off, n).Replace("-", " ");
    }

    public static string CurrentSsid()
    {
        try
        {
            // full path: never a netsh.exe from the program's own folder
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "wlan show interfaces");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                // never wait for a hung netsh for more than a few seconds
                var read = p.StandardOutput.ReadToEndAsync();
                if (!read.Wait(4000)) { try { p.Kill(); } catch { } return ""; }
                string o = read.Result;
                p.WaitForExit(1000);
                Match mt = Regex.Match(o, @"^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.Multiline);
                return mt.Success ? mt.Groups[1].Value : "";
            }
        }
        catch { return ""; }
    }

    // The adapter name ends in a unit-specific ID (e.g. "MHD CAN 1A2B"); only the mode word matters
    public static bool IsCanSsid(string ssid) { return Regex.IsMatch(ssid, @"\bD?CAN\b", RegexOptions.IgnoreCase); }
    public static bool IsKlineSsid(string ssid) { return Regex.IsMatch(ssid, @"\bK-?LINE\b", RegexOptions.IgnoreCase); }
    public static bool IsEnetSsid(string ssid) { return Regex.IsMatch(ssid, @"\bENET\b", RegexOptions.IgnoreCase); }
    public static bool IsMhdSsid(string ssid) { return Regex.IsMatch(ssid, @"^MHD\b", RegexOptions.IgnoreCase); }

    // Gateway of the connected WiFi interface = the adapter. Falls back to the MHD default.
    static string AdapterAddress()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                        return g.Address.ToString();
            }
        }
        catch { }
        return DefaultAdapterIp;
    }

    // Length of a well-formed BMW-FAST telegram at buf[off], or -1.
    // 0x80 = physical, 0xC0 = functional addressing. KWP2000* "B8" telegrams fail the length check.
    static int FastLen(byte[] b, int off, int avail)
    {
        if (avail < 4) return -1;
        byte f = b[off];
        if ((f & 0x80) != 0x80) return -1;
        int l = f & 0x3F;
        int total = l > 0 ? 3 + l + 1 : 4 + b[off + 3] + 1;
        if (total > avail) return -1;
        int sum = 0;
        for (int i = 0; i < total - 1; i++) sum += b[off + i];
        if ((byte)sum != b[off + total - 1]) return -1;
        return total;
    }

    // True when buf[off] is the beginning of a BMW-FAST telegram whose rest has not arrived yet.
    // A complete KWP2000* telegram ("B8 <target> <source> <len> ... <checksum>") also starts with a byte
    // that looks like a long BMW-FAST telegram; it is complete as it is and must still be blocked.
    static bool Incomplete(byte[] b, int off, int avail)
    {
        if (avail <= 0) return false;
        byte f = b[off];
        if ((f & 0x80) != 0x80) return false;
        if (avail < 4) return true;
        if (f == 0xB8 && 4 + b[off + 3] + 1 == avail) return false;
        int l = f & 0x3F;
        int total = l > 0 ? 3 + l + 1 : 4 + b[off + 3] + 1;
        return total > avail && total <= 300;
    }

    // The D-CAN filter. Forwards every well-formed BMW-FAST telegram in buf[0..n) (after what was held
    // back from the previous read), blocks everything else, and returns the start of a telegram whose
    // rest has not arrived yet (TCP has no message boundaries, so a telegram can arrive in two pieces).
    public static byte[] Filter(byte[] held, byte[] buf, int n, Action<byte[], int, int> send, Action<string> log)
    {
        byte[] data = buf; int len = n, fromHeld = held.Length;
        if (fromHeld > 0)
        {
            data = new byte[fromHeld + n];
            Buffer.BlockCopy(held, 0, data, 0, fromHeld);
            Buffer.BlockCopy(buf, 0, data, fromHeld, n);
            len = fromHeld + n;
        }
        int off = 0;
        while (off < len)
        {
            int t = FastLen(data, off, len - off);
            if (t > 0) { send(data, off, t); off += t; continue; }
            if (Incomplete(data, off, len - off))
            {
                byte[] rest = new byte[len - off];
                Buffer.BlockCopy(data, off, rest, 0, rest.Length);
                return rest;
            }
            if (off < fromHeld)
            {
                // the held piece did not continue as expected: drop it and look at the new data alone
                log("Dropped an unfinished telegram: " + Hex(data, off, fromHeld - off));
                off = fromHeld;
                continue;
            }
            log("Blocked (not BMW-FAST, would confuse the MHD in CAN mode): " + Hex(data, off, len - off));
            break;
        }
        return new byte[0];
    }

    static void AcceptLoop(TcpListener l)
    {
        int id = 0;
        while (true)
        {
            TcpClient c;
            try { c = l.AcceptTcpClient(); } catch { Thread.Sleep(500); continue; }
            int myId = ++id;
            new Thread(() => Client(c, myId)) { IsBackground = true }.Start();
        }
    }

    // Called with upLock held
    static void EnsureUp()
    {
        if (up != null && up.Connected) return;
        try { if (up != null) up.Close(); } catch { }
        string ssid = CurrentSsid();
        Ssid = ssid;
        filterCan = IsCanSsid(ssid);
        // Only talk to an MHD adapter; on any other network the WiFi gateway is e.g. the home router
        if (!IsMhdSsid(ssid))
        {
            SetMode("");
            throw new IOException("not connected to an MHD adapter's WiFi (WiFi: " + (ssid == "" ? "none" : ssid) + ")");
        }
        string adapter = AdapterAddress();
        up = new TcpClient();
        up.NoDelay = true;
        IAsyncResult r = up.BeginConnect(adapter, AdapterPort, null, null);
        if (!r.AsyncWaitHandle.WaitOne(3000) || !up.Connected)
        {
            try { up.Close(); } catch { }
            up = null;
            SetMode("");
            throw new IOException("adapter " + adapter + ":" + AdapterPort + " not reachable (WiFi: " + (ssid == "" ? "none" : ssid) + ")");
        }
        up.EndConnect(r);
        us = up.GetStream();
        Thread.Sleep(300);
        byte[] tmp = new byte[4096];
        while (up.Available > 0)
        {
            int n = us.Read(tmp, 0, tmp.Length);
            Log.Write("Discarded leftover data: " + Hex(tmp, 0, n));
        }
        Log.Write("Adapter " + adapter + " connected, WiFi '" + ssid + "', " + (filterCan ? "D-CAN (filter on)" : "pass-through"));
        SetMode(filterCan ? "D" : "K");
        NetworkStream s = us;
        new Thread(() => UpReader(s)) { IsBackground = true }.Start();
    }

    static void UpReader(NetworkStream s)
    {
        byte[] buf = new byte[4096];
        try
        {
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                NetworkStream o; int oid;
                lock (upLock) { o = owner; oid = ownerId; }
                if (Verbose) Log.Write("MHD->#" + oid + ": " + Hex(buf, 0, n));
                if (o != null) { try { o.Write(buf, 0, n); } catch { } }
            }
        }
        catch { }
        lock (upLock)
        {
            if (us == s)
            {
                try { up.Close(); } catch { }
                up = null;
                Log.Write("Adapter connection closed");
                SetMode("");
            }
        }
    }

    static void Client(TcpClient c, int id)
    {
        lock (upLock) { clients++; }
        try
        {
            c.NoDelay = true;
            NetworkStream cs = c.GetStream();
            lock (upLock) { EnsureUp(); }
            byte[] buf = new byte[4096];
            byte[] held = new byte[0];      // start of a BMW-FAST telegram whose rest has not arrived yet
            while (true)
            {
                int n = cs.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                lock (upLock)
                {
                    EnsureUp();
                    owner = cs; ownerId = id;
                    if (!filterCan)
                    {
                        if (Verbose) Log.Write("#" + id + "->MHD: " + Hex(buf, 0, n));
                        us.Write(buf, 0, n);
                        continue;
                    }
                    NetworkStream target = us;
                    held = Filter(held, buf, n, (d, o, cnt) =>
                    {
                        if (Verbose) Log.Write("#" + id + "->MHD: " + Hex(d, o, cnt));
                        target.Write(d, o, cnt);
                    }, Log.Write);
                }
            }
        }
        catch (Exception ex) { Log.WriteOnce("Connection from INPA/ISTA refused: " + ex.Message); }
        try { c.Close(); } catch { }
        lock (upLock)
        {
            if (ownerId == id) owner = null;
            clients--;
            if (clients <= 0)
            {
                clients = 0;
                try { if (up != null) up.Close(); } catch { }
                up = null; us = null;
                SetMode("");
            }
        }
    }
}

// Small size-limited log in %LOCALAPPDATA%\EdiaHelper
static class Log
{
    const long MaxBytes = 5 * 1024 * 1024;
    static readonly object lk = new object();
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdiaHelper");
    public static readonly string PathName = System.IO.Path.Combine(Folder, "EdiaHelper.log");

    static string lastOnce; static DateTime lastOnceTime;

    // Earlier versions were called "Adapter Switch": take over their settings, log, NCS backup state
    // and "last seen" records once, so nothing has to be set up again.
    public static void MoveOldData()
    {
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdapterSwitch");
            if (Directory.Exists(Folder) || !Directory.Exists(old)) return;
            Directory.CreateDirectory(Folder);
            foreach (string f in Directory.GetFiles(old))
            {
                string name = Path.GetFileName(f).Replace("AdapterSwitch", "EdiaHelper");
                File.Copy(f, Path.Combine(Folder, name), false);
            }
            Write("Settings taken over from " + old);
        }
        catch { }
    }

    // Same message again within 10 minutes is not written again (INPA retries every few seconds)
    public static void WriteOnce(string s)
    {
        lock (lk)
        {
            if (s == lastOnce && (DateTime.Now - lastOnceTime).TotalMinutes < 10) return;
            lastOnce = s; lastOnceTime = DateTime.Now;
        }
        Write(s);
    }

    public static void Write(string s)
    {
        lock (lk)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                FileInfo fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = PathName + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(PathName, old);
                }
                File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + s + Environment.NewLine);
            }
            catch { }
        }
    }
}
