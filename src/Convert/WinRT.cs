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

        public static T Await<T>(IAsyncOperation<T> op) { Spin(op, 10 * 60 * 1000); return op.GetResults(); }
        public static T Await<T, P>(IAsyncOperationWithProgress<T, P> op) { Spin(op, 10 * 60 * 1000); return op.GetResults(); }
        public static void Await(IAsyncAction op) { Spin(op, 10 * 60 * 1000); op.GetResults(); }
        // Long media transcodes can legitimately take a while.
        public static void Await<P>(IAsyncActionWithProgress<P> op) { Spin(op, 3 * 60 * 60 * 1000); op.GetResults(); }

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
