// Trace level (IfhTrace) for INPA / NCS Expert and ISTA.
//
// The level is kept in EDIABAS.INI in each EDIABAS folder. Both BMW's EDIABAS (K+DCAN cable) and
// EdiabasLib (MHD) read it from there: EdiaHelper's EdiabasLib.config files do not set it, so they do
// not override it. EdiaHelper always sets both folders (INPA and ISTA) to the same level.
//
// The levels build on each other (from EdiabasLib's source):
//   0  off
//   1  every message sent to and received from the car, connection steps, job arguments
//   2  level 1 plus error details
//   3  level 2 plus every single instruction of the diagnostic scripts: gigabytes, very slow
//
// A level above 0 is set back to 0 when EdiaHelper starts, so a forgotten trace cannot fill the disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

static class Trace
{
    public static readonly string[] Choices =
    {
        "0 - Off (normal use)",
        "1 - Messages to and from the car (troubleshooting)",
        "2 - Level 1 + error details",
        "3 - Every script step (script debugging only, gigabytes!)",
    };

    public static readonly string[] Help =
    {
        "No trace files are written. The setting for everyday diagnostics and coding.",
        "For \"it does not connect\" or \"it stops with an error\": shows what goes to the car and what comes back. " +
            "Turn it on, repeat the one failing action, turn it off. A few MB per session.",
        "Like level 1, plus the details behind error messages (e.g. IFH-0003). Barely bigger than level 1.",
        "Every step inside the diagnostic scripts. Only for a few seconds of one specific job, never for a whole " +
            "ISTA identification: it writes gigabytes and makes everything very slow.",
    };

    // The level now: the highest one found in the EDIABAS folders
    public static int Level()
    {
        int max = 0;
        foreach (string bin in Switcher.EdiabasBins())
        {
            int v;
            if (int.TryParse(Switcher.IniValue(bin, "IfhTrace"), out v) && v > max) max = v;
        }
        return Math.Min(max, 3);
    }

    // Writes IfhTrace into EDIABAS.INI in every EDIABAS folder (INPA and ISTA)
    public static void Set(int level)
    {
        if (level < 0 || level > 3) throw new ArgumentOutOfRangeException("level");
        List<string> done = new List<string>();
        foreach (string bin in Switcher.EdiabasBins())
        {
            string ini = Path.Combine(bin, "EDIABAS.INI");
            Switcher.NotLink(ini);
            string t = File.ReadAllText(ini, Encoding.Default);
            Regex r = new Regex(@"^([ \t]*IfhTrace[ \t]*=[ \t]*)\S*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (r.IsMatch(t)) t = r.Replace(t, "${1}" + level, 1);
            else
            {
                Match c = Regex.Match(t, @"^[ \t]*\[Configuration\][^\r\n]*\r?\n", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                t = c.Success ? t.Insert(c.Index + c.Length, "IfhTrace         = " + level + "\r\n")
                              : "[Configuration]\r\nIfhTrace         = " + level + "\r\n" + t;
            }
            File.WriteAllText(ini, t, Encoding.Default);
            done.Add(bin);
        }
        Log.Write("Trace level set to " + level + " in " + string.Join(", ", done.ToArray()));
    }

    // At start: a trace that was left on is turned off
    public static void ResetAtStart()
    {
        try
        {
            int l = Level();
            if (l > 0)
            {
                Set(0);
                Log.Write("Trace level was " + l + "; set back to 0 when EdiaHelper started");
            }
        }
        catch (Exception ex) { Log.Write("Could not reset the trace level: " + ex.Message); }
    }

    // Where trace files are written: EdiabasLib (MHD) uses <folder>\Trace, or its own folder in
    // LocalAppData if that is not writable; BMW's EDIABAS (cable) uses TracePath from EDIABAS.INI
    public static List<string> Folders()
    {
        List<string> list = new List<string>();
        Action<string> add = p =>
        {
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                p = Path.GetFullPath(p.Trim()).TrimEnd('\\');
                if (Directory.Exists(p) && !list.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) list.Add(p);
            }
            catch { }
        };
        foreach (string bin in Switcher.EdiabasBins())
        {
            add(Path.Combine(bin, "Trace"));
            add(Switcher.IniValue(bin, "TracePath"));
        }
        add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"EdiabasLib\Trace"));
        return list;
    }

    public static long Size(string folder)
    {
        long n = 0;
        try { foreach (FileInfo f in new DirectoryInfo(folder).GetFiles("*", SearchOption.AllDirectories)) n += f.Length; }
        catch { }
        return n;
    }

    public static string SizeText(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
        return (bytes / 1024.0).ToString("0") + " KB";
    }
}
