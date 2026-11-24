using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Konbini.Deps
{
    /// <summary>
    /// Installs, verifies and locates dependencies. Rules (see CLAUDE.md "External dependencies"):
    /// - only Konbini's own copy in %LOCALAPPDATA%\Konbini\deps\&lt;id&gt; counts, and only if its SHA-256 matches;
    /// - a verified copy is never downloaded again or overwritten;
    /// - downloads are HTTPS-only, from fixed URLs on allow-listed hosts, written to .partial (resumable), hash-checked
    ///   before extraction, and the extracted file is hash-checked again before it is moved into place or executed;
    /// - one install per dependency at a time, across threads (lock) and processes (named mutex);
    /// - per-user folder: never needs administrator rights.
    /// </summary>
    public static class DepManager
    {
        public static string Root
        {
            get
            {
                if (AppInfo.Portable) return Path.Combine(AppInfo.PortableDataDir, "deps");
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Konbini", "deps");
            }
        }

        /// <summary>For tests: overrides the install root.</summary>
        public static string RootOverride;
        private static string RootDir { get { return RootOverride ?? Root; } }

        public static event Action<Dependency> StatusChanged;

        private static readonly Dictionary<string, object> locks = new Dictionary<string, object>();
        // Files already hashed this session: path -> (length, last write). Re-hashed if either changes.
        private static readonly Dictionary<string, Tuple<long, DateTime>> verified = new Dictionary<string, Tuple<long, DateTime>>(StringComparer.OrdinalIgnoreCase);

        private static object LockFor(Dependency d) { lock (locks) { object o; if (!locks.TryGetValue(d.Id, out o)) locks[d.Id] = o = new object(); return o; } }

        public static string InstallPath(Dependency d)
        {
            if (d.Source == DepSource.Embedded) return null;
            return Path.Combine(RootDir, d.Id, d.FileName);
        }

        // ------------------------------------------------------------------ status

        public static DepStatus Status(Dependency d)
        {
            if (d.Source == DepSource.Embedded)
                return typeof(DepManager).Assembly.GetManifestResourceInfo(d.Resource) != null ? DepStatus.Installed : DepStatus.Missing;
            string p = InstallPath(d);
            if (!File.Exists(p)) return DepStatus.Missing;
            return IsVerified(p, d.Sha256) ? DepStatus.Installed : DepStatus.Damaged;
        }

        public static bool IsInstalled(Dependency d) { return Status(d) == DepStatus.Installed; }

        private static bool IsVerified(string path, string sha)
        {
            try
            {
                var fi = new FileInfo(path);
                var key = Tuple.Create(fi.Length, fi.LastWriteTimeUtc);
                lock (verified) { Tuple<long, DateTime> v; if (verified.TryGetValue(path, out v) && v.Equals(key)) return true; }
                if (HashFile(path) != sha) return false;
                lock (verified) verified[path] = key;
                return true;
            }
            catch { return false; }
        }

        public static string HashFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return HashStream(fs);
        }
        private static string HashStream(Stream s)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(s)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ using an installed dependency

        /// <summary>Embedded dependency contents.</summary>
        public static Stream OpenEmbedded(Dependency d)
        {
            if (d.Source != DepSource.Embedded) throw new ArgumentException("Not an embedded dependency.");
            var s = typeof(DepManager).Assembly.GetManifestResourceStream(d.Resource);
            if (s == null) throw new DependencyException(d, d.DisplayName + " is missing from this copy of Konbini — please download Konbini again.");
            return d.Gzipped ? (Stream)new GZipStream(s, CompressionMode.Decompress) : s;
        }

        /// <summary>
        /// Opens the verified installed file and keeps it open with sharing that forbids writes and deletes, so it can't be
        /// swapped between the hash check and its use. Dispose the returned handle when done (after the process exits).
        /// Throws <see cref="DependencyException"/> if it isn't installed or fails verification.
        /// </summary>
        public static FileStream Acquire(Dependency d)
        {
            string p = InstallPath(d);
            if (!File.Exists(p)) throw new DependencyException(d, d.DisplayName + " isn't installed yet.", true);
            FileStream hold;
            try { hold = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException ex) { throw new DependencyException(d, d.DisplayName + " couldn't be opened (" + ex.Message + ")."); }
            if (!IsVerified(p, d.Sha256))
            {
                hold.Dispose();
                throw new DependencyException(d, d.DisplayName + " is damaged and needs to be downloaded again.", true);
            }
            return hold;
        }

        // ------------------------------------------------------------------ installing

        /// <summary>
        /// Makes sure the dependency is installed. Does nothing if a verified copy exists. progress gets (done, total) bytes.
        /// Throws OperationCanceledException on cancel and DependencyException with a friendly message on failure.
        /// </summary>
        public static void EnsureInstalled(Dependency d, Action<long, long, string> progress, Func<bool> cancelled)
        {
            if (d.Source == DepSource.Embedded) { if (!IsInstalled(d)) OpenEmbedded(d).Dispose(); return; }
            if (IsInstalled(d)) return;
            WithInstallLock(d, cancelled, () =>
            {
                if (IsInstalled(d)) return;                    // another thread/process finished it meanwhile
                string dir = Path.GetDirectoryName(InstallPath(d));
                Directory.CreateDirectory(dir);
                CleanStale(dir);
                string partial = Path.Combine(dir, "download.partial");
                if (File.Exists(partial) && File.Exists(partial + ".url") && File.ReadAllText(partial + ".url") != d.Url) { TryDelete(partial); }
                Download(d, partial, progress, cancelled);
                if (progress != null) progress(d.DownloadSize, d.DownloadSize, "Verifying…");
                if (HashFile(partial) != d.DownloadSha256)
                {
                    TryDelete(partial); TryDelete(partial + ".url");
                    throw new DependencyException(d, "The download of " + d.DisplayName + " was corrupted or has changed on the server, so it wasn't used. Please try again.");
                }
                InstallVerifiedDownload(d, partial, dir);
                TryDelete(partial); TryDelete(partial + ".url");
            });
            Raise(d);
        }

        /// <summary>Installs from a file the user already has (the original download or the finished file). Verified by hash.</summary>
        public static void InstallFromFile(Dependency d, string file)
        {
            if (d.Source == DepSource.Embedded) return;
            string h = HashFile(file);
            if (h != d.DownloadSha256 && h != d.Sha256)
                throw new DependencyException(d, "That file isn't the expected " + d.DisplayName + " (its checksum doesn't match), so it wasn't used.");
            if (IsInstalled(d)) return;
            WithInstallLock(d, null, () =>
            {
                if (IsInstalled(d)) return;
                string dir = Path.GetDirectoryName(InstallPath(d));
                Directory.CreateDirectory(dir);
                CleanStale(dir);
                string staged = Path.Combine(dir, "import.tmp");
                File.Copy(file, staged, true);
                if (HashFile(staged) != h) { TryDelete(staged); throw new DependencyException(d, "The file changed while it was being copied."); }
                if (h == d.Sha256) Place(d, staged); else InstallVerifiedDownload(d, staged, dir);
                TryDelete(staged);
            });
            Raise(d);
        }

        /// <summary>Removes an installed (downloaded) dependency. Fails if it's in use.</summary>
        public static void Uninstall(Dependency d)
        {
            if (d.Source == DepSource.Embedded) return;
            WithInstallLock(d, null, () =>
            {
                string dir = Path.GetDirectoryName(InstallPath(d));
                if (!Directory.Exists(dir)) return;
                try { Directory.Delete(dir, true); }
                catch (IOException) { throw new DependencyException(d, d.DisplayName + " is in use. Finish what's running and try again."); }
                catch (UnauthorizedAccessException) { throw new DependencyException(d, d.DisplayName + " is in use. Finish what's running and try again."); }
                lock (verified) verified.Remove(InstallPath(d));
            });
            Raise(d);
        }

        private static void Raise(Dependency d) { var h = StatusChanged; if (h != null) try { h(d); } catch { } }

        private static void WithInstallLock(Dependency d, Func<bool> cancelled, Action body)
        {
            lock (LockFor(d))
            using (var m = new Mutex(false, @"Local\Konbini-dep-" + d.Id))
            {
                bool owned = false;
                try
                {
                    while (!owned)
                    {
                        try { owned = m.WaitOne(250); } catch (AbandonedMutexException) { owned = true; } // previous holder crashed: its partial file is resumable
                        if (!owned && cancelled != null && cancelled()) throw new OperationCanceledException();
                    }
                    body();
                }
                catch (DependencyException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (IOException ex) { throw new DependencyException(d, Friendly(d, ex)); }
                catch (UnauthorizedAccessException) { throw new DependencyException(d, "Konbini isn't allowed to write to " + RootDir + "."); }
                catch (WebException ex) { throw new DependencyException(d, Friendly(d, ex)); }
                finally { if (owned) m.ReleaseMutex(); }
            }
        }

        private static string Friendly(Dependency d, Exception ex)
        {
            int hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex) & 0xFFFF;
            if (hr == 112 || hr == 39) return "There isn't enough free disk space to install " + d.DisplayName + ".";
            var we = ex as WebException;
            if (we != null)
            {
                if (we.Status == WebExceptionStatus.NameResolutionFailure || we.Status == WebExceptionStatus.ConnectFailure)
                    return "Couldn't reach the download server. Check your internet connection and try again.";
                if (we.Status == WebExceptionStatus.Timeout) return "The download timed out. Try again — it will continue where it stopped.";
                if (we.Status == WebExceptionStatus.TrustFailure || we.Status == WebExceptionStatus.SecureChannelFailure)
                    return "The secure connection to the download server couldn't be verified, so nothing was downloaded.";
                return "The download failed (" + we.Message + "). Try again — it will continue where it stopped.";
            }
            return "Couldn't install " + d.DisplayName + " (" + ex.Message + ").";
        }

        // Leftovers from crashed or interrupted installs. download.partial is kept (it's resumed); everything else temporary goes.
        private static void CleanStale(string dir)
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                string n = Path.GetFileName(f);
                if (n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            }
        }

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        // ------------------------------------------------------------------ download

        private static void CheckUrl(Dependency d, Uri u)
        {
            if (u.Scheme != Uri.UriSchemeHttps || !Registry.AllowedHosts.Contains(u.Host.ToLowerInvariant()))
                throw new DependencyException(d, "The download address (" + u.Scheme + "://" + u.Host + ") isn't an approved secure source, so nothing was downloaded.");
        }

        private static void Download(Dependency d, string partial, Action<long, long, string> progress, Func<bool> cancelled)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var uri = new Uri(d.Url);
            CheckUrl(d, uri);
            File.WriteAllText(partial + ".url", d.Url);

            // Disk space: need the download plus the extracted file.
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (have > d.DownloadSize) { TryDelete(partial); have = 0; }
            long need = (d.DownloadSize - have) + (d.ArchiveEntry != null ? d.Size : 0) + 16 * 1024 * 1024;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(partial)));
                if (drive.AvailableFreeSpace < need)
                    throw new DependencyException(d, "Installing " + d.DisplayName + " needs about " + (need / 1048576) + " MB of free space on " + drive.Name + ".");
            }
            catch (ArgumentException) { }
            if (have == d.DownloadSize) return;

            for (int attempt = 0; ; attempt++)
            {
                try { DownloadOnce(d, uri, partial, progress, cancelled); return; }
                catch (WebException ex)
                {
                    var resp = ex.Response as HttpWebResponse;
                    if (resp != null && (int)resp.StatusCode == 416) { TryDelete(partial); }   // server rejected the resume range: start over
                    else if (attempt >= 2 || resp != null && (int)resp.StatusCode == 404) throw;
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    Thread.Sleep(1000 * (attempt + 1));
                }
            }
        }

        private static void DownloadOnce(Dependency d, Uri uri, string partial, Action<long, long, string> progress, Func<bool> cancelled)
        {
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            var req = (HttpWebRequest)WebRequest.Create(uri);
            req.UserAgent = "Konbini";
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 5;
            req.Timeout = 30000; req.ReadWriteTimeout = 30000;
            if (have > 0) req.AddRange(have);
            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                CheckUrl(d, resp.ResponseUri);
                bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) have = 0;
                using (var src = resp.GetResponseStream())
                using (var dst = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[1 << 16];
                    long done = have; int n; var lastReport = DateTime.MinValue;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException(); // .partial is kept for resuming
                        if (done + n > d.DownloadSize) throw new DependencyException(d, "The server sent more data than expected, so the download was stopped.");
                        dst.Write(buf, 0, n); done += n;
                        if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                        { lastReport = DateTime.UtcNow; progress(done, d.DownloadSize, "Downloading…"); }
                    }
                    if (done != d.DownloadSize) throw new WebException("The connection closed early.", WebExceptionStatus.ConnectionClosed);
                }
            }
        }

        // ------------------------------------------------------------------ extract + place

        // `download` has already passed DownloadSha256.
        private static void InstallVerifiedDownload(Dependency d, string download, string dir)
        {
            if (d.ArchiveEntry == null) { string tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp"); File.Copy(download, tmp); Place(d, tmp); return; }
            string outTmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var zip = ZipFile.OpenRead(download))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName == d.ArchiveEntry);
                    if (entry == null) throw new DependencyException(d, "The downloaded archive doesn't contain " + d.FileName + ".");
                    SafeEntryPath(d, dir, entry.FullName);   // defence in depth: we only extract one known entry, to a fixed name
                    if (entry.Length != d.Size) throw new DependencyException(d, "The downloaded archive has an unexpected " + d.FileName + ".");
                    using (var s = entry.Open())
                    using (var f = new FileStream(outTmp, FileMode.CreateNew, FileAccess.Write)) s.CopyTo(f);
                }
                Place(d, outTmp);
            }
            catch (InvalidDataException) { throw new DependencyException(d, "The downloaded archive is damaged."); }
            finally { TryDelete(outTmp); }
        }

        /// <summary>Resolves a ZIP entry under root, rejecting absolute paths, drive letters and "..".</summary>
        public static string SafeEntryPath(Dependency d, string root, string entryName)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (entryName.Contains(":") || entryName.StartsWith("/") || entryName.StartsWith("\\"))
                throw new DependencyException(d, "The archive contains an unsafe path and was rejected.");
            string target = Path.GetFullPath(Path.Combine(fullRoot, entryName.Replace('/', '\\')));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new DependencyException(d, "The archive contains an unsafe path and was rejected.");
            return target;
        }

        // Hash-checks a staged file and atomically moves it to the install path. A verified existing copy is never replaced.
        private static void Place(Dependency d, string staged)
        {
            if (HashFile(staged) != d.Sha256) { TryDelete(staged); throw new DependencyException(d, "The extracted " + d.FileName + " doesn't match the expected version, so it wasn't installed."); }
            string target = InstallPath(d);
            if (File.Exists(target))
            {
                if (IsVerified(target, d.Sha256)) { TryDelete(staged); return; }
                File.Replace(staged, target, null);              // damaged copy: replace atomically
            }
            else File.Move(staged, target);
            lock (verified) verified.Remove(target);
        }
    }

    public class DependencyException : Exception
    {
        public readonly Dependency Dependency;
        public readonly bool NeedsInstall;
        public DependencyException(Dependency d, string message, bool needsInstall = false) : base(message) { Dependency = d; NeedsInstall = needsInstall; }
    }
}
