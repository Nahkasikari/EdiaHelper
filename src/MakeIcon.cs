// Build helper: writes EdiaHelper.ico (and optional PNG previews) from IconArt.
// Usage: MakeIcon.exe <out.ico> [previewFolder]

using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static void Main(string[] args)
    {
        IconArt.WriteIco(args.Length > 0 ? args[0] : "EdiaHelper.ico", IconArt.DrawAppIcon);
        if (args.Length > 1)
        {
            Directory.CreateDirectory(args[1]);
            using (var b = IconArt.DrawAppIcon(256)) b.Save(Path.Combine(args[1], "app256.png"), ImageFormat.Png);
            using (var b = IconArt.DrawAppIcon(32)) b.Save(Path.Combine(args[1], "app32.png"), ImageFormat.Png);
            foreach (string l in new[] { "K", "D", "E", "C", "I" })
                using (var b = IconArt.DrawTray(64, true, l)) b.Save(Path.Combine(args[1], "tray_" + l + ".png"), ImageFormat.Png);
            using (var b = IconArt.DrawTray(64, false, null)) b.Save(Path.Combine(args[1], "tray_idle.png"), ImageFormat.Png);
            using (var b = IconArt.DrawTray(16, true, "D")) b.Save(Path.Combine(args[1], "tray_D16.png"), ImageFormat.Png);
            using (var b = IconArt.DrawTray(16, false, null)) b.Save(Path.Combine(args[1], "tray_idle16.png"), ImageFormat.Png);
        }
    }
}
