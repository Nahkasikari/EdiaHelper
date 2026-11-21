// "Check connection": goes through everything the current choice depends on and reports
// each point as OK / warning / problem in plain words.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

static class Check
{
    public enum Level { Ok, Warn, Fail, Info }

    public class Item
    {
        public Level Level;
        public string Text;
        public Item(Level l, string t) { Level = l; Text = t; }
    }

    static List<Item> items;
    static void Ok(string t) { items.Add(new Item(Level.Ok, t)); }
    static void Warn(string t) { items.Add(new Item(Level.Warn, t)); }
    static void Fail(string t) { items.Add(new Item(Level.Fail, t)); }
    static void Info(string t) { items.Add(new Item(Level.Info, t)); }

    public static List<Item> Run()
    {
        items = new List<Item>();
        Info("EdiaHelper " + Program.Version + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        // ---- folders
        bool inpaOk = Switcher.IsEdiabasDir(Settings.EdiabasBin);
        bool istaOk = Switcher.IsEdiabasDir(Settings.IstaBin);
        if (inpaOk) Ok("INPA / NCS Expert folder: " + Settings.EdiabasBin);
        else { Fail("INPA / NCS Expert folder not found (" + Settings.EdiabasBin + "). Set it at the top of the window."); return items; }
        if (istaOk) Ok("ISTA folder: " + Settings.IstaBin);
        else Warn("ISTA folder not found (" + Settings.IstaBin + "). Only INPA / NCS Expert are switched.");

        // ---- current choice
        int c = Switcher.Current();
        if (c < 0)
        {
            Fail("The files do not match any choice (INPA interface is '" + Switcher.InpaInterface() + "'). Choose the adapter and press Apply.");
            return items;
        }
        Ok("Current choice: " + Switcher.Names[c]);
        bool lib = c == Switcher.MhdKD || c == Switcher.MhdEnet;

        CheckDlls(lib, istaOk);
        string running = Switcher.DiagRunning();
        if (running != null) Info(running + " is running.");

        switch (c)
        {
            case Switcher.MhdKD: CheckMhdKD(istaOk); break;
            case Switcher.MhdEnet: CheckMhdEnet(istaOk); break;
            case Switcher.Cable: CheckCable(istaOk); break;
            case Switcher.EnetCable: CheckEnetCable(); break;
            case Switcher.Icom: CheckIcom(); break;
        }
        return items;
    }

    static string Sha(string path)
    {
        if (!File.Exists(path)) return "";
        using (SHA256 s = SHA256.Create())
        using (FileStream f = File.OpenRead(path))
            return BitConverter.ToString(s.ComputeHash(f));
    }

    // The Interface in EDIABAS.INI, for INPA and for ISTA's own EDIABAS
    static void CheckInterface(string want, bool istaOk)
    {
        bool istaSeparate = istaOk && !Settings.SameFolder(Settings.IstaBin, Settings.EdiabasBin);
        foreach (string bin in istaSeparate ? new[] { Settings.EdiabasBin, Settings.IstaBin } : new[] { Settings.EdiabasBin })
        {
            string label = bin == Settings.EdiabasBin ? "INPA" : "ISTA";
            string have = Switcher.IniInterface(bin);
            if (have.ToUpperInvariant() == want) Ok(label + " interface: " + want);
            else Fail(label + " interface is " + (have == "" ? "not set" : have) + ", expected " + want + ". Press Apply.");
        }
    }

    static void CheckDlls(bool lib, bool istaOk)
    {
        string[] bins = istaOk ? new[] { Settings.EdiabasBin, Settings.IstaBin } : new[] { Settings.EdiabasBin };
        if (lib)
        {
            string want = Sha(Path.Combine(Switcher.Profiles, "EdiabasLib8", "Api32.dll"));
            if (want == "") { Fail("The EdiabasLib 8.0 files are missing from " + Path.Combine(Switcher.Profiles, "EdiabasLib8") + ". Reinstall EdiaHelper."); return; }
            foreach (string bin in bins)
            {
                string label = bin == Settings.EdiabasBin ? "INPA" : "ISTA";
                if (Sha(Path.Combine(bin, "Api32.dll")) == want) Ok(label + ": EdiabasLib 8.0 Api32.dll is active");
                else Fail(label + ": Api32.dll is not the EdiabasLib 8.0 version. Press Apply to put it back.");
            }
            return;
        }
        // BMW originals: each folder has its own (INPA and ISTA can have different EDIABAS versions)
        foreach (string bin in bins)
        {
            string label = bin == Settings.EdiabasBin ? "INPA" : "ISTA";
            string api = Path.Combine(bin, "Api32.dll");
            if (File.Exists(api) && !Switcher.IsEdiabasLib(api))
                Ok(label + ": BMW original Api32.dll is active (version " + System.Diagnostics.FileVersionInfo.GetVersionInfo(api).FileVersion + ")");
            else Fail(label + ": Api32.dll is not BMW's original. Press Apply to put it back.");
            if (Switcher.OriginalSource(bin, "Api32.dll") == null)
                Warn(label + ": no saved copy of BMW's original Api32.dll for this folder. Run Install.cmd again.");
        }
    }

    static string ConfigValue(string bin, string key)
    {
        string f = Path.Combine(bin, "EdiabasLib.config");
        if (!File.Exists(f)) return null;
        Match m = Regex.Match(File.ReadAllText(f), "<add\\s+key=\"" + key + "\"\\s+value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    static void CheckLibConfig(bool istaOk)
    {
        foreach (string bin in istaOk ? new[] { Settings.EdiabasBin, Settings.IstaBin } : new[] { Settings.EdiabasBin })
        {
            string label = bin == Settings.EdiabasBin ? "INPA" : "ISTA";
            string port = ConfigValue(bin, "ObdComPort");
            if (port == null) { Fail(label + ": EdiabasLib.config is missing. Press Apply."); continue; }
            if (string.Equals(port, "DEEPOBDWIFI:127.0.0.1:" + Relay.ListenPort + ":RAW", StringComparison.OrdinalIgnoreCase))
                Ok(label + ": EdiabasLib goes through the relay (" + port + ")");
            else
                Fail(label + ": EdiabasLib ObdComPort is '" + port + "', expected DEEPOBDWIFI:127.0.0.1:" + Relay.ListenPort + ":RAW. Press Apply.");
            int t;
            if (int.TryParse(ConfigValue(bin, "IfhTrace"), out t) && t >= 2)
                Warn(label + ": EdiabasLib logging is on (IfhTrace=" + t + "). Level 3 writes gigabytes and slows everything down.");
        }
    }

    static string WifiGateway()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) return g.Address.ToString();
            }
        }
        catch { }
        return "";
    }

    static bool PortOpen(string ip, int port, int ms)
    {
        try
        {
            using (TcpClient t = new TcpClient())
            {
                IAsyncResult r = t.BeginConnect(ip, port, null, null);
                bool ok = r.AsyncWaitHandle.WaitOne(ms) && t.Connected;
                if (ok) t.EndConnect(r);
                return ok;
            }
        }
        catch { return false; }
    }

    static bool Listening(int port)
    {
        try
        {
            foreach (IPEndPoint ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == port) return true;
        }
        catch { }
        return false;
    }

    static bool Connected(string ip, int port)
    {
        try
        {
            foreach (TcpConnectionInformation c in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                if (c.State == TcpState.Established && c.RemoteEndPoint.Port == port && c.RemoteEndPoint.Address.ToString() == ip) return true;
        }
        catch { }
        return false;
    }

    static bool CheckMhdWifi(string modeWord, Func<string, bool> isMode)
    {
        string ssid = Relay.CurrentSsid();
        if (ssid == "") { Fail("WiFi is not connected. Connect to the adapter's network (\"MHD " + modeWord + " ...\")."); return false; }
        if (!Relay.IsMhdSsid(ssid)) { Fail("WiFi is '" + ssid + "', not an MHD adapter. Connect to the adapter's network."); return false; }
        if (!isMode(ssid)) Warn("WiFi is '" + ssid + "', but this choice is for " + modeWord + ". The adapter picks its mode by the car; choose the matching adapter setting.");
        else Ok("WiFi: " + ssid);
        return true;
    }

    // Runtimes EdiabasLib 8.0 needs. INPA / ISTA's EDIABAS are 32-bit, so the x86 versions count.
    static void CheckRuntimes()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                object r = k != null ? k.GetValue("Release") : null;
                if (r is int && (int)r >= 533320) Ok(".NET Framework 4.8.1 is installed");
                else Fail(".NET Framework 4.8.1 is missing (needed by EdiabasLib): https://dotnet.microsoft.com/download/dotnet-framework/net481");
            }
        }
        catch { }

        bool desk = false;
        try
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"dotnet\shared\Microsoft.WindowsDesktop.App");
            if (Directory.Exists(d))
                foreach (string v in Directory.GetDirectories(d))
                {
                    int major;
                    if (int.TryParse(Path.GetFileName(v).Split('.')[0], out major) && major >= 10) desk = true;
                }
        }
        catch { }
        if (desk) Ok(".NET Desktop Runtime 10 (x86) is installed");
        else Fail(".NET 10 Desktop Runtime, x86, is missing (needed by EdiabasLib): https://dotnet.microsoft.com/download/dotnet/10.0");

        bool vc = false;
        foreach (string key in new[] { @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86", @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86" })
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                    if (k != null && k.GetValue("Installed") is int && (int)k.GetValue("Installed") == 1) vc = true;
            }
            catch { }
        }
        if (vc) Ok("Visual C++ Runtime (x86) is installed");
        else Fail("Visual C++ Runtime 2015-2022, x86, is missing (needed by EdiabasLib): https://aka.ms/vs/17/release/vc_redist.x86.exe");
    }

    static void CheckMhdKD(bool istaOk)
    {
        CheckRuntimes();
        CheckLibConfig(istaOk);
        CheckInterface("STD:OBD", istaOk);

        // Looked up in the system's connection list, so this also works from "EdiaHelper.exe /check"
        if (Listening(Relay.ListenPort)) Ok("Relay is running on 127.0.0.1:" + Relay.ListenPort);
        else Fail("The relay is not running. Start EdiaHelper (it should start with Windows).");

        if (!CheckMhdWifi("CAN / KLINE", s => Relay.IsCanSsid(s) || Relay.IsKlineSsid(s))) return;
        string gw = WifiGateway();
        if (gw == "") { Fail("The WiFi has no gateway address; the adapter did not give the laptop an address."); return; }
        Ok("Adapter address (WiFi gateway): " + gw);
        if (Connected(gw, 23)) Ok("The relay is talking to the adapter right now (" + (Relay.IsCanSsid(Relay.CurrentSsid()) ? "D-CAN, filter on" : "K-line, pass-through") + ")");
        else if (PortOpen(gw, 23, 2000)) Ok("The adapter answers on port 23");
        else Fail("The adapter does not answer on " + gw + ":23. Is it plugged into the car with the ignition on?");
    }

    static void CheckMhdEnet(bool istaOk)
    {
        CheckRuntimes();
        CheckInterface("ENET", istaOk);
        if (!CheckMhdWifi("ENET", Relay.IsEnetSsid)) return;
        string gw = WifiGateway();
        if (gw == "") { Fail("The WiFi has no gateway address; the adapter did not give the laptop an address."); return; }
        Ok("Adapter address (WiFi gateway): " + gw);
        string host = ConfigValue(Settings.EdiabasBin, "EnetRemoteHost") ?? "";
        if (host != "" && host != gw && !host.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            Warn("INPA's EdiabasLib looks for the car at " + host + ", but the adapter is " + gw + ". INPA may not find it (ISTA finds it by itself).");
        if (PortOpen(gw, 6801, 2000)) Ok("The car answers on port 6801 (ENET diagnostics)");
        else Fail("Nothing answers on " + gw + ":6801. Is the adapter in an ENET car with the ignition on?");
    }

    static void CheckCable(bool istaOk)
    {
        Cable.Info c = Cable.Current();
        if (c == null) { Fail("No K+DCAN cable found. Plug it in; if Windows does not show a COM port for it, install the FTDI driver."); return; }
        if (c.Present) Ok("Cable found on " + c.Port);
        else Fail("The cable (" + c.Port + ") is not plugged in.");
        if (c.Latency == 1) Ok("Latency timer: 1 ms");
        else Warn("Latency timer is " + (c.Latency < 0 ? "unknown" : c.Latency + " ms") + ", should be 1 ms. Press \"Match settings to cable\".");
        int n;
        if (int.TryParse(c.Port.Substring(3), out n) && n > 9) Warn(c.Port + " is above COM9; some EDIABAS / INPA versions only work with COM1-COM9.");

        foreach (string bin in istaOk ? new[] { Settings.EdiabasBin, Settings.IstaBin } : new[] { Settings.EdiabasBin })
        {
            string label = bin == Settings.EdiabasBin ? "INPA" : "ISTA";
            string f = Path.Combine(bin, "obd.ini");
            if (!File.Exists(f)) { Fail(label + ": obd.ini is missing. Press \"Match settings to cable\"."); continue; }
            Match m = Regex.Match(File.ReadAllText(f, Encoding.Default), @"^[ \t]*Port[ \t]*=[ \t]*(COM\d+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            string p = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
            if (p == c.Port) Ok(label + ": obd.ini uses " + p);
            else Fail(label + ": obd.ini uses " + (p == "" ? "no port" : p) + ", the cable is on " + c.Port + ". Press \"Match settings to cable\".");
        }
        CheckInterface("STD:OBD", istaOk);
    }

    static void CheckEnetCable()
    {
        CheckInterface("ENET", Switcher.IsEdiabasDir(Settings.IstaBin));
        bool found = false;
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet || ni.OperationalStatus != OperationalStatus.Up) continue;
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (d.Contains("virtual") || d.Contains("bluetooth") || d.Contains("rndis") || d.Contains("vpn")) continue;
                found = true;
                IPInterfaceProperties props = ni.GetIPProperties();
                string ip = "";
                foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) ip = a.Address.ToString();
                Ok("Ethernet connected: " + ni.Name + (ip != "" ? ", address " + ip : ""));
                try
                {
                    IPv4InterfaceProperties v4 = props.GetIPv4Properties();
                    if (v4 != null && !v4.IsDhcpEnabled)
                        Warn(ni.Name + " has a fixed address. For an ENET cable it should get its address automatically.");
                }
                catch { }
                if (ip.StartsWith("169.254.")) Info("A 169.254.x.x address is normal for an ENET cable (it takes up to a minute after plugging in).");
            }
        }
        catch { }
        if (!found) Fail("No Ethernet connection. Plug the ENET cable into the laptop and the car (ignition on).");
    }

    static void CheckIcom()
    {
        string iface = Switcher.InpaInterface().ToUpperInvariant();
        Ok("INPA interface: " + iface + " (" + (iface == "REMOTE" ? "address in EDIABAS.INI [TCP]" : "address in RPLUS.INI [ICOM_P]") + ")");
        string ip, port;
        Switcher.ReadIcom(out ip, out port);
        IPAddress a;
        if (!IPAddress.TryParse(ip, out a)) { Fail("No valid ICOM address is set. Enter it in the ICOM section and press Apply."); return; }
        Ok("ICOM address: " + ip + ", port " + port);
        bool ping = false;
        try { using (Ping p = new Ping()) ping = p.Send(a, 1000).Status == IPStatus.Success; } catch { }
        if (ping) Ok("The ICOM answers at " + ip);
        else Fail("The ICOM does not answer at " + ip + ". Check the cable / network and the ICOM's address.");
        Info("Remember to reserve the ICOM before use, as its instructions describe.");
    }

    // ---- result window

    public static void Show(IWin32Window owner)
    {
        List<Item> result;
        Cursor.Current = Cursors.WaitCursor;
        try { result = Run(); } finally { Cursor.Current = Cursors.Default; }

        Form f = new Form
        {
            Text = "Check connection", StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9.5f),
            ClientSize = new Size(640, 360), MinimizeBox = false, ShowInTaskbar = false
        };
        ListView list = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None, Location = new Point(10, 10), Size = new Size(620, 300), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        list.Columns.Add("", 34);
        list.Columns.Add("", 570);
        StringBuilder text = new StringBuilder();
        foreach (Item i in result)
        {
            string mark = i.Level == Level.Ok ? "OK" : i.Level == Level.Warn ? "!" : i.Level == Level.Fail ? "X" : "i";
            ListViewItem li = new ListViewItem(mark);
            li.SubItems.Add(i.Text);
            li.ForeColor = i.Level == Level.Ok ? Color.ForestGreen : i.Level == Level.Warn ? Color.DarkOrange : i.Level == Level.Fail ? Color.Firebrick : Color.DimGray;
            list.Items.Add(li);
            text.AppendLine(mark.PadRight(3) + i.Text);
        }
        int fails = result.FindAll(x => x.Level == Level.Fail).Count, warns = result.FindAll(x => x.Level == Level.Warn).Count;
        Label sum = new Label
        {
            Location = new Point(10, 322), AutoSize = true, Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            Text = fails > 0 ? fails + " problem(s) found." : warns > 0 ? "No problems, " + warns + " warning(s)." : "Everything looks right.",
            ForeColor = fails > 0 ? Color.Firebrick : warns > 0 ? Color.DarkOrange : Color.ForestGreen
        };
        Button copy = new Button { Text = "Copy", Location = new Point(440, 316), Size = new Size(90, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        Button close = new Button { Text = "Close", Location = new Point(540, 316), Size = new Size(90, 30), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
        copy.Click += (s, e) => { try { Clipboard.SetText(text.ToString()); copy.Text = "Copied"; } catch { } };
        f.Controls.AddRange(new Control[] { list, sum, copy, close });
        f.AcceptButton = close;
        Log.Write("Check connection: " + sum.Text);
        f.ShowDialog(owner);
    }
}
