using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.AI.MachineLearning;
using Konbini.Conversion;

namespace Konbini.Upscale
{
    /// <summary>
    /// Real-ESRGAN (realesr-general-x4v3, BSD-3) run through Windows ML, which ships with Windows 10 1809+ / 11.
    /// The model is embedded in the exe. Images are processed in fixed 384 px tiles with a 40 px context margin (wider than the 34 px receptive field),
    /// so memory stays bounded and there are no seams.
    /// </summary>
    public static class AiUpscaler
    {
        private const int Tile = 384, Margin = 40, In = Tile + 2 * Margin, Native = 4, Out = In * Native;
        public const long MaxOutputPixels = 150L * 1000 * 1000;
        public const int MaxOutputSide = 16384;

        private static readonly object gate = new object();
        private static LearningModelSession session;
        private static string loadError;
        /// <summary>For tests: forces the CPU device instead of the GPU.</summary>
        public static bool ForceCpu;
        public static string DeviceName { get; private set; }

        /// <summary>Loads the model once. Returns false (with Error set) if Windows ML can't run it.</summary>
        public static bool Available
        {
            get { lock (gate) { EnsureLoaded(); return session != null; } }
        }
        public static string Error { get { return loadError; } }

        private static void EnsureLoaded()
        {
            if (session != null || loadError != null) return;
            try { string dev; WinMl.ForceCpu |= ForceCpu; session = WinMl.Load(Konbini.Deps.Registry.Upscaler, true, out dev); DeviceName = dev; }
            catch (Exception ex) { session = null; loadError = "Windows ML couldn't load the AI model (" + ex.Message + ")."; }
        }

        /// <summary>Upscales by 2, 3 or 4. progress gets 0-100; cancelled is polled between tiles.</summary>
        public static unsafe Bitmap Upscale(Bitmap src, int scale, Action<int> progress, Func<bool> cancelled)
        {
            if (scale < 2 || scale > 4) throw new ArgumentOutOfRangeException("scale");
            int w = src.Width, h = src.Height, ow = w * scale, oh = h * scale;
            CheckSize(ow, oh);

            // Source pixels (straight BGRA bytes).
            var srcPx = new byte[w * h * 4]; bool hasAlpha = false;
            var sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(sd.Scan0 + y * sd.Stride, srcPx, y * w * 4, w * 4);
            }
            finally { src.UnlockBits(sd); }
            for (int i = 3; i < srcPx.Length; i += 4) if (srcPx[i] != 255) { hasAlpha = true; break; }

            var result = new Bitmap(ow, oh, PixelFormat.Format32bppArgb);
            try
            {
                int tilesX = (w + Tile - 1) / Tile, tilesY = (h + Tile - 1) / Tile, done = 0, total = tilesX * tilesY;
                var input = new float[3 * In * In];
                int scaled = Out * scale / Native, keep0 = Margin * scale; // after downscaling to the target scale
                var rd = result.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int ty = 0; ty < tilesY; ty++)
                        for (int tx = 0; tx < tilesX; tx++)
                        {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException("Cancelled");
                            int x0 = tx * Tile - Margin, y0 = ty * Tile - Margin;
                            for (int y = 0; y < In; y++)
                            {
                                int sy = Mirror(y0 + y, h);
                                for (int x = 0; x < In; x++)
                                {
                                    int si = (sy * w + Mirror(x0 + x, w)) * 4, o = y * In + x;
                                    input[o] = srcPx[si + 2] / 255f; input[In * In + o] = srcPx[si + 1] / 255f; input[2 * In * In + o] = srcPx[si] / 255f;
                                }
                            }
                            float[] tile = Run(input);                 // planar RGB, Out x Out, 0..1
                            float[] bgra;                               // interleaved B,G,R,A(=255) * 255, scaled x scaled
                            if (scale == Native) bgra = Interleave(tile, Out);
                            else
                            {
                                var full = Interleave(tile, Out);
                                bgra = Lanczos.Pass(Lanczos.Pass(full, Out, Out, scaled, true), scaled, Out, scaled, false);
                            }
                            // Copy the tile's centre into the output.
                            int dx = tx * Tile * scale, dy = ty * Tile * scale;
                            int cw = Math.Min(Tile * scale, ow - dx), ch = Math.Min(Tile * scale, oh - dy);
                            for (int y = 0; y < ch; y++)
                            {
                                byte* row = (byte*)rd.Scan0 + (dy + y) * rd.Stride + dx * 4;
                                int bi = ((keep0 + y) * scaled + keep0) * 4;
                                for (int x = 0; x < cw; x++, bi += 4)
                                {
                                    row[x * 4] = Lanczos.Clamp(bgra[bi]); row[x * 4 + 1] = Lanczos.Clamp(bgra[bi + 1]);
                                    row[x * 4 + 2] = Lanczos.Clamp(bgra[bi + 2]); row[x * 4 + 3] = 255;
                                }
                            }
                            done++;
                            if (progress != null) progress(done * 100 / total);
                        }
                }
                finally { result.UnlockBits(rd); }
                if (hasAlpha) CopyAlpha(src, result);
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public static void CheckSize(int ow, int oh)
        {
            if (ow > MaxOutputSide || oh > MaxOutputSide || (long)ow * oh > MaxOutputPixels)
                throw new InvalidOperationException("The result would be " + ow + "×" + oh + ", which is too large. Choose a smaller scale.");
        }

        private static float[] Run(float[] input)
        {
            lock (gate)
            {
                EnsureLoaded();
                if (session == null) throw new InvalidOperationException(loadError);
                return WinMl.Run(session, "input", new long[] { 1, 3, In, In }, input, "output");
            }
        }

        private static float[] Interleave(float[] planar, int n)
        {
            var d = new float[n * n * 4]; int plane = n * n;
            for (int i = 0; i < plane; i++)
            {
                d[i * 4] = planar[2 * plane + i] * 255f; d[i * 4 + 1] = planar[plane + i] * 255f; d[i * 4 + 2] = planar[i] * 255f; d[i * 4 + 3] = 255f;
            }
            return d;
        }

        // Alpha is upscaled smoothly with high-quality bicubic, then copied into the AI result.
        private static unsafe void CopyAlpha(Bitmap src, Bitmap dst)
        {
            using (var a = ImageUtil.Resize(src, dst.Width, dst.Height))
            {
                var ad = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var dd = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < dst.Height; y++)
                    {
                        byte* ar = (byte*)ad.Scan0 + y * ad.Stride, dr = (byte*)dd.Scan0 + y * dd.Stride;
                        for (int x = 0; x < dst.Width; x++) dr[x * 4 + 3] = ar[x * 4 + 3];
                    }
                }
                finally { a.UnlockBits(ad); dst.UnlockBits(dd); }
            }
        }

        private static int Mirror(int i, int n)
        {
            if (n == 1) return 0;
            while (i < 0 || i >= n) i = i < 0 ? -i : 2 * n - 2 - i;
            return i;
        }
    }
}
