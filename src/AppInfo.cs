using System;
using System.IO;
using System.Reflection;

[assembly: AssemblyTitle("Konbini")]
[assembly: AssemblyProduct("Konbini")]
[assembly: AssemblyDescription("A library of everyday file tools for Windows")]
[assembly: AssemblyVersion(Konbini.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(Konbini.AppInfo.Version + ".0")]

namespace Konbini
{
    /// <summary>Version and data locations. A "Konbini.portable" file next to the exe switches to portable mode: settings
    /// and downloaded components live in a "Konbini Data" folder beside the exe instead of the user profile (if that
    /// folder is writable; otherwise the normal locations are used).</summary>
    public static class AppInfo
    {
        public const string Version = "1.0.0";

        public static readonly string ExeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private static string portableDir;
        private static bool checkedPortable;

        /// <summary>The portable data folder, or null when not running portable.</summary>
        public static string PortableDataDir
        {
            get
            {
                if (checkedPortable) return portableDir;
                checkedPortable = true;
                try
                {
                    if (!File.Exists(Path.Combine(ExeDir, "Konbini.portable"))) return null;
                    string dir = Path.Combine(ExeDir, "Konbini Data");
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".write-test");
                    File.WriteAllText(probe, ""); File.Delete(probe);
                    portableDir = dir;
                }
                catch { portableDir = null; }  // read-only location (e.g. opened inside a zip): fall back to the profile
                return portableDir;
            }
        }

        public static bool Portable { get { return PortableDataDir != null; } }
    }
}
