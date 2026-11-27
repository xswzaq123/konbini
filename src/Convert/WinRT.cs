using System;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Konbini.Conversion
{
    // Minimal bridge to Windows Runtime APIs (built into Windows 10/11) without the SDK-only helper assemblies.
    public static class WinRT
    {
        private static void Spin(IAsyncInfo i, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (i.Status == AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) { i.Cancel(); throw new TimeoutException("The operation took too long."); }
                Thread.Sleep(5);
            }
            if (i.Status == AsyncStatus.Error) throw i.ErrorCode ?? new Exception("Windows reported an error.");
            if (i.Status == AsyncStatus.Canceled) throw new OperationCanceledException();
        }

        // Block on the operation's Completed event (wakes immediately) rather than polling with Sleep, which at the
        // default 15.6 ms timer resolution added up to ~15 ms to every call (e.g. every AI tile).
        private static void Wait(IAsyncInfo i, ManualResetEventSlim done, int timeoutMs)
        {
            // Not disposed: after a timeout, Completed may still fire later and must not hit a disposed event.
            if (i.Status == AsyncStatus.Started && !done.Wait(timeoutMs) && i.Status == AsyncStatus.Started)
            { i.Cancel(); throw new TimeoutException("The operation took too long."); }
            Spin(i, timeoutMs); // status is final now; this just maps errors and cancellation
        }

        public static T Await<T>(IAsyncOperation<T> op)
        { var e = new ManualResetEventSlim(); op.Completed = (a, s) => e.Set(); Wait(op, e, 10 * 60 * 1000); return op.GetResults(); }
        public static T Await<T, P>(IAsyncOperationWithProgress<T, P> op)
        { var e = new ManualResetEventSlim(); op.Completed = (a, s) => e.Set(); Wait(op, e, 10 * 60 * 1000); return op.GetResults(); }
        public static void Await(IAsyncAction op)
        { var e = new ManualResetEventSlim(); op.Completed = (a, s) => e.Set(); Wait(op, e, 10 * 60 * 1000); op.GetResults(); }
        // Long media transcodes can legitimately take a while.
        public static void Await<P>(IAsyncActionWithProgress<P> op)
        { var e = new ManualResetEventSlim(); op.Completed = (a, s) => e.Set(); Wait(op, e, 3 * 60 * 60 * 1000); op.GetResults(); }

        public static StorageFile Open(string path) { return Await(StorageFile.GetFileFromPathAsync(Path.GetFullPath(path))); }

        public static StorageFile Create(string path)
        {
            path = Path.GetFullPath(path);
            var folder = Await(StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)));
            return Await(folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting));
        }

        public static byte[] ReadAll(IRandomAccessStream s)
        {
            uint size = (uint)s.Size;
            using (var reader = new DataReader(s.GetInputStreamAt(0)))
            {
                Await(reader.LoadAsync(size));
                var b = new byte[size];
                reader.ReadBytes(b);
                return b;
            }
        }

        public static InMemoryRandomAccessStream FromBytes(byte[] data)
        {
            var s = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(s.GetOutputStreamAt(0)))
            {
                w.WriteBytes(data);
                Await(w.StoreAsync());
                Await(w.FlushAsync());
                w.DetachStream();
            }
            s.Seek(0);
            return s;
        }
    }
}
