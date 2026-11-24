using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Konbini
{
    // User preferences, stored as key=value lines in %APPDATA%\Konbini\settings.ini.
    public static class Settings
    {
        public const string ThemeSystem = "System", ThemeLight = "Light", ThemeDark = "Dark";
        public const string ViewList = "List", ViewThumbnails = "Thumbnails";
        public const string SizeLarge = "Large", SizeMedium = "Medium", SizeMaximized = "Maximized", SizeRemember = "Remember";

        public static string ThemeMode = ThemeSystem;
        public static string DefaultView = ViewList;
        public static string WindowSize = SizeLarge;
        public static string BgModel = "";   // "" = use the model recommended for this PC
        public static Rectangle LastBounds = Rectangle.Empty;   // restored size/position, for SizeRemember
        public static bool LastMaximized;

        public static event EventHandler Changed;

        public static readonly string FilePath = AppInfo.Portable ? Path.Combine(AppInfo.PortableDataDir, "settings.ini") : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Konbini", "settings.ini");

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    if (k == "theme" && (v == ThemeSystem || v == ThemeLight || v == ThemeDark)) ThemeMode = v;
                    else if (k == "defaultView" && (v == ViewList || v == ViewThumbnails)) DefaultView = v;
                    else if (k == "bgModel" && System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-z]{1,16}$")) BgModel = v;
                    else if (k == "windowSize" && (v == SizeLarge || v == SizeMedium || v == SizeMaximized || v == SizeRemember)) WindowSize = v;
                    else if (k == "lastMaximized") LastMaximized = v == "1";
                    else if (k == "lastBounds")
                    {
                        var n = v.Split(','); int x, y, w, h;
                        if (n.Length == 4 && int.TryParse(n[0], out x) && int.TryParse(n[1], out y) && int.TryParse(n[2], out w) && int.TryParse(n[3], out h) && w > 0 && h > 0)
                            LastBounds = new Rectangle(x, y, w, h);
                    }
                }
            }
            catch { } // a broken settings file must never stop the app from starting
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[] { "theme=" + ThemeMode, "defaultView=" + DefaultView, "windowSize=" + WindowSize, "bgModel=" + BgModel,
                    "lastBounds=" + LastBounds.X + "," + LastBounds.Y + "," + LastBounds.Width + "," + LastBounds.Height,
                    "lastMaximized=" + (LastMaximized ? "1" : "0") });
            }
            catch { }
            Theme.Refresh();
            var h = Changed; if (h != null) h(null, EventArgs.Empty);
        }
    }

}
