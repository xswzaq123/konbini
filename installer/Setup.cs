// Konbini installer / uninstaller. One source, two builds (package.ps1):
//   Konbini-Setup.exe  - embeds Konbini.exe + Uninstall.exe, installs per user (no admin) to %LOCALAPPDATA%\Programs\Konbini
//   Uninstall.exe      - compiled with /define:UNINSTALLER; removes the app, shortcuts and the Apps & features entry
// Switches: /S (silent), /D=<folder> (install folder, install only), /KEEPDATA (uninstall: keep settings and downloads).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Konbini Setup")]
[assembly: AssemblyProduct("Konbini")]
[assembly: AssemblyVersion(Konbini.Setup.Program.Version + ".0")]

namespace Konbini.Setup
{
    static class Program
    {
        public const string Version = "1.1.0";
        public const string AppName = "Konbini";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Konbini";

        static bool silent;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase));
            string dirArg = args.Where(a => a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(3)).FirstOrDefault();
            try
            {
#if UNINSTALLER
                return Uninstall(args.Any(a => a.Equals("/KEEPDATA", StringComparison.OrdinalIgnoreCase)), args);
#else
                return Install(dirArg);
#endif
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                return 1;
            }
        }

        static void Fail(string msg)
        {
            if (!silent) MessageBox.Show(msg, AppName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else Console.Error.WriteLine(msg);
        }

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
        }

        static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); }
        }
        static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); }
        }

        // Waits for running copies of Konbini started from `dir` to close (asks the user unless silent).
        static bool EnsureClosed(string dir)
        {
            while (true)
            {
                var running = Process.GetProcessesByName("Konbini").Where(p =>
                {
                    try { return string.Equals(Path.GetDirectoryName(p.MainModule.FileName), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }).ToList();
                if (running.Count == 0) return true;
                if (silent) { foreach (var p in running) { try { p.CloseMainWindow(); p.WaitForExit(10000); } catch { } } if (running.All(p => { try { return p.HasExited; } catch { return true; } })) return true; return false; }
                var r = MessageBox.Show("Konbini is running. Close it, then click Retry.", AppName + " Setup", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.Retry) return false;
            }
        }

        static void CreateShortcut(string link, string target, string workDir, string description)
        {
            // WScript.Shell ships with every Windows 10/11; late-bound so no interop assembly is needed.
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

#if !UNINSTALLER
        // ------------------------------------------------------------------------------------------------ install
        static int Install(string dirArg)
        {
            string dir = dirArg ?? ExistingInstallDir() ?? DefaultDir;
            bool desktop = true, launch = !silent;
            if (!silent)
            {
                using (var f = new InstallForm(dir))
                {
                    if (f.ShowDialog() != DialogResult.OK) return 2;
                    dir = f.Folder; desktop = f.Desktop; launch = f.Launch;
                }
            }
            dir = Path.GetFullPath(dir);
            if (!EnsureClosed(dir)) return 3;
            DoInstall(dir, desktop);
            if (launch) Process.Start(new ProcessStartInfo(Path.Combine(dir, "Konbini.exe")) { WorkingDirectory = dir, UseShellExecute = true });
            else if (!silent) MessageBox.Show("Konbini " + Version + " is installed.", AppName + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static string ExistingInstallDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var v = k == null ? null : k.GetValue("InstallLocation") as string;
                return !string.IsNullOrEmpty(v) && Directory.Exists(v) ? v : null;
            }
        }

        public static void DoInstall(string dir, bool desktop)
        {
            Directory.CreateDirectory(dir);
            // Write each file to a temp name, then swap it in, so a failure never leaves a half-written Konbini.exe.
            foreach (var name in new[] { "Konbini.exe", "Uninstall.exe", "THIRD-PARTY-NOTICES.txt" })
            {
                string target = Path.Combine(dir, name), tmp = target + ".new";
                using (var res = typeof(Program).Assembly.GetManifestResourceStream("payload." + name))
                {
                    if (res == null) throw new InvalidOperationException("This installer is damaged (missing " + name + "). Please download it again.");
                    using (var gz = new GZipStream(res, CompressionMode.Decompress))
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write)) gz.CopyTo(fs);
                }
                if (File.Exists(target)) File.Replace(tmp, target, null); else File.Move(tmp, target);
            }
            CreateShortcut(StartMenuLink, Path.Combine(dir, "Konbini.exe"), dir, "Everyday file tools");
            if (desktop) CreateShortcut(DesktopLink, Path.Combine(dir, "Konbini.exe"), dir, "Everyday file tools");
            else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                string un = Path.Combine(dir, "Uninstall.exe");
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", AppName);
                k.SetValue("DisplayIcon", Path.Combine(dir, "Konbini.exe") + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + un + "\"");
                k.SetValue("QuietUninstallString", "\"" + un + "\" /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = new DirectoryInfo(dir).GetFiles().Sum(f => f.Length) / 1024;
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        class InstallForm : Form
        {
            readonly TextBox folder; readonly CheckBox desk, run;
            public string Folder { get { return folder.Text.Trim(); } }
            public bool Desktop { get { return desk.Checked; } }
            public bool Launch { get { return run.Checked; } }

            public InstallForm(string dir)
            {
                Text = "Install Konbini " + Version; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen; Font = SystemFonts.MessageBoxFont; AutoScaleMode = AutoScaleMode.Font;
                ClientSize = new Size(520, 250);
                var title = new Label { Text = "Konbini " + Version, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), AutoSize = true, Location = new Point(18, 16) };
                var blurb = new Label { Text = "Everyday file tools: resize, compress, upscale and convert images, remove backgrounds, convert files and compress videos.\nInstalled for your Windows account only — no administrator rights needed.", Location = new Point(20, 52), Size = new Size(480, 48) };
                var fl = new Label { Text = "Install to:", AutoSize = true, Location = new Point(20, 112) };
                folder = new TextBox { Text = dir, Location = new Point(20, 132), Width = 390 };
                var browse = new Button { Text = "Browse…", Location = new Point(418, 130), Width = 82 };
                browse.Click += delegate
                {
                    using (var d = new FolderBrowserDialog { Description = "Choose where to install Konbini", SelectedPath = Folder })
                        if (d.ShowDialog(this) == DialogResult.OK) folder.Text = Path.Combine(d.SelectedPath, d.SelectedPath.EndsWith(AppName, StringComparison.OrdinalIgnoreCase) ? "" : AppName);
                };
                desk = new CheckBox { Text = "Create a desktop shortcut", Checked = true, AutoSize = true, Location = new Point(20, 168) };
                run = new CheckBox { Text = "Open Konbini when done", Checked = true, AutoSize = true, Location = new Point(20, 192) };
                var ok = new Button { Text = "Install", Location = new Point(332, 210), Width = 82, DialogResult = DialogResult.None };
                var cancel = new Button { Text = "Cancel", Location = new Point(418, 210), Width = 82, DialogResult = DialogResult.Cancel };
                ok.Click += delegate
                {
                    string err = null;
                    try
                    {
                        string full = Path.GetFullPath(Folder);
                        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                        if (full.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || (pf86.Length > 0 && full.StartsWith(pf86, StringComparison.OrdinalIgnoreCase)) || full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                            err = "That folder needs administrator rights. Konbini installs for your account only — please choose a folder in your user profile.";
                        else if (Path.GetPathRoot(full) == full) err = "Please choose a folder, not a whole drive.";
                    }
                    catch { err = "That isn't a valid folder."; }
                    if (err != null) { MessageBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    DialogResult = DialogResult.OK;
                };
                Controls.AddRange(new Control[] { title, blurb, fl, folder, browse, desk, run, ok, cancel });
                AcceptButton = ok; CancelButton = cancel;
            }
        }
#else
        // ------------------------------------------------------------------------------------------------ uninstall
        static int Uninstall(bool keepData, string[] args)
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            // A running exe can't delete itself: re-launch a copy from %TEMP% that removes the install folder.
            if (!args.Any(a => a.StartsWith("/FROMTEMP=", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(Path.Combine(dir, "Konbini.exe")) && !silent)
                {
                    MessageBox.Show("Konbini doesn't seem to be installed here.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                if (!silent)
                {
                    var r = MessageBox.Show("Remove Konbini from this PC?\n\nClick Yes to also delete your Konbini settings and downloaded components (AI models, video engine), or No to keep them.",
                        "Uninstall Konbini", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
                    if (r == DialogResult.Cancel) return 2;
                    keepData = r == DialogResult.No;
                }
                // Remove copies left in %TEMP% by earlier uninstalls (a running exe can't delete itself).
                foreach (var old in Directory.GetFiles(Path.GetTempPath(), "Konbini-Uninstall-*.exe")) { try { File.Delete(old); } catch { } }
                string tmp = Path.Combine(Path.GetTempPath(), "Konbini-Uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                File.Copy(Assembly.GetExecutingAssembly().Location, tmp);
                var psi = new ProcessStartInfo(tmp, "/FROMTEMP=\"" + dir + "\" /PARENT=" + Process.GetCurrentProcess().Id + " /S" + (keepData ? " /KEEPDATA" : "") + (silent ? "" : " /NOTIFY")) { UseShellExecute = false };
                Process.Start(psi);
                return 0;   // exit now so the copy in %TEMP% can delete this Uninstall.exe
            }
            string target = args.First(a => a.StartsWith("/FROMTEMP=", StringComparison.OrdinalIgnoreCase)).Substring(10).Trim('"');
            bool notify = args.Any(a => a.Equals("/NOTIFY", StringComparison.OrdinalIgnoreCase));
            // Only ever delete a folder that really is a Konbini install (the registered one, containing Konbini.exe).
            string registered;
            using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) registered = k == null ? null : k.GetValue("InstallLocation") as string;
            if (!File.Exists(Path.Combine(target, "Konbini.exe")) || (registered != null && !string.Equals(Path.GetFullPath(registered).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Refusing to remove " + target + ": it isn't the registered Konbini install.");
            // Wait for the launcher (the installed Uninstall.exe) to exit so its file can be deleted.
            string parent = args.Where(a => a.StartsWith("/PARENT=", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(8)).FirstOrDefault();
            int pid;
            if (parent != null && int.TryParse(parent, out pid)) { try { Process.GetProcessById(pid).WaitForExit(15000); } catch (ArgumentException) { } }
            Thread.Sleep(200);
            if (!EnsureClosed(target)) { if (notify) MessageBox.Show("Konbini is still running, so it wasn't removed. Close it and try again.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return 3; }

            foreach (var name in new[] { "Konbini.exe", "Uninstall.exe", "THIRD-PARTY-NOTICES.txt", "Konbini.exe.new", "Uninstall.exe.new", "THIRD-PARTY-NOTICES.txt.new" })
                for (int i = 0; i < 20; i++) { try { string f = Path.Combine(target, name); if (File.Exists(f)) File.Delete(f); break; } catch { Thread.Sleep(250); } }
            try { if (!Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target); } catch { }   // never delete files we didn't install
            foreach (var l in new[] { StartMenuLink, DesktopLink }) try { if (File.Exists(l)) File.Delete(l); } catch { }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (!keepData)
            {
                foreach (var d in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName),
                                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName) })
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }
            if (notify) MessageBox.Show("Konbini has been removed.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
#endif
    }
}
