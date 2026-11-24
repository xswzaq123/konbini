using System;
using System.IO;
using Windows.AI.MachineLearning;
using Konbini.Conversion;

namespace Konbini.Upscale
{
    /// <summary>Shared Windows ML helpers: load an embedded ONNX model on the GPU (falling back to CPU) and run it.</summary>
    public static class WinMl
    {
        /// <summary>For tests: forces every model onto the CPU device.</summary>
        public static bool ForceCpu;

        /// <summary>Loads a model dependency (embedded or installed by DepManager, verified before use).</summary>
        public static LearningModelSession Load(Konbini.Deps.Dependency dep, bool allowGpu, out string device)
        {
            return Load(dep, allowGpu ? GpuPolicy.Prefer : GpuPolicy.Never, out device);
        }

        public enum GpuPolicy { Prefer, Never }

        public static LearningModelSession Load(Konbini.Deps.Dependency dep, GpuPolicy gpu, out string device)
        {
            LearningModel model;
            using (Stream s = dep.Source == Konbini.Deps.DepSource.Embedded ? Konbini.Deps.DepManager.OpenEmbedded(dep) : (Stream)Konbini.Deps.DepManager.Acquire(dep))
            {
                byte[] bytes;
                using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                model = WinRT.Await(LearningModel.LoadFromStreamAsync(
                    Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(WinRT.FromBytes(bytes))));
            }
            if (gpu == GpuPolicy.Prefer && !ForceCpu)
            {
                try { device = "GPU"; return new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.DirectXHighPerformance)); }
                catch { }
            }
            device = "CPU";
            return new LearningModelSession(model, new LearningModelDevice(LearningModelDeviceKind.Cpu));
        }

        /// <summary>Largest dedicated video memory of any graphics adapter, in bytes (0 if unknown). Read from the display
        /// driver's registry entry, which needs no admin rights; Win32_VideoController caps this at 4 GB.</summary>
        public static long DedicatedVideoMemory
        {
            get
            {
                if (vram >= 0) return vram;
                long best = 0;
                try
                {
                    using (var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                        if (cls != null)
                            foreach (var n in cls.GetSubKeyNames())
                            {
                                try
                                {
                                    using (var k = cls.OpenSubKey(n))
                                    {
                                        if (k == null) continue;
                                        object q = k.GetValue("HardwareInformation.qwMemorySize");
                                        long v = q is long ? (long)q : q is byte[] && ((byte[])q).Length >= 8 ? BitConverter.ToInt64((byte[])q, 0) : 0;
                                        if (v == 0) { object m = k.GetValue("HardwareInformation.MemorySize"); v = m is int ? (uint)(int)m : m is byte[] && ((byte[])m).Length >= 4 ? BitConverter.ToUInt32((byte[])m, 0) : 0; }
                                        if (v > best) best = v;
                                    }
                                }
                                catch { }
                            }
                }
                catch { }
                return vram = best;
            }
        }
        private static long vram = -1;

        /// <summary>Total physical memory in bytes.</summary>
        public static long TotalRam
        {
            get
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
            }
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        /// <summary>Runs a single-input model; returns the named float output, flattened.</summary>
        public static float[] Run(LearningModelSession session, string input, long[] shape, float[] data, string output)
        {
            var binding = new LearningModelBinding(session);
            binding.Bind(input, TensorFloat.CreateFromArray(shape, data));
            // Models with several (or dynamically shaped) outputs, e.g. U2-Net's side outputs, need the wanted output
            // bound explicitly; binding only it also skips copying the others back.
            foreach (var f in session.Model.OutputFeatures)
            {
                var tf = f as TensorFeatureDescriptor;
                if (f.Name != output || tf == null) continue;
                var os = new long[tf.Shape.Count]; bool known = true;
                for (int i = 0; i < os.Length; i++) { os[i] = tf.Shape[i]; if (os[i] <= 0) known = false; }
                if (known) binding.Bind(output, TensorFloat.Create(os));
            }
            var result = WinRT.Await(session.EvaluateAsync(binding, "run"));
            var view = ((TensorFloat)result.Outputs[output]).GetAsVectorView();
            var arr = new float[view.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = view[i];
            return arr;
        }
    }
}
