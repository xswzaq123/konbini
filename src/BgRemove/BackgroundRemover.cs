using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using Windows.AI.MachineLearning;
using Konbini.Upscale;

namespace Konbini.BgRemove
{
    public enum BgMode { Transparent, Color, Blur, Image, Mask }
    public enum BgPrecision { Automatic, Fast, Full }

    /// <summary>
    /// AI background removal: IS-Net (DIS, Apache-2.0) via Windows ML predicts a 1024² mask, a fast guided filter
    /// snaps its edges to the full-resolution photo (hair, fur), and colour decontamination removes background
    /// fringes from semi-transparent edge pixels.
    /// </summary>
    public static class BackgroundRemover
    {
        private const int WorkMax = 1536;       // guided filter coefficients are computed at <= this size, applied at full size
        private static readonly object gate = new object();
        private static LearningModelSession s16, s32;
        private static string dev16, dev32, err16, err32;
        // Device the fast model loaded on most recently; kept across Unload so NeedsFull can answer without loading.
        private static volatile string lastDev16;

        public static string LastDevice { get; private set; }

        /// <summary>Picks (and lazily loads) the session for a precision. Throws a friendly error if none can run.</summary>
        private static LearningModelSession Session(BgPrecision p)
        {
            if (p != BgPrecision.Full)
            {
                if (s16 == null && err16 == null && Konbini.Deps.DepManager.IsInstalled(Konbini.Deps.Registry.BgFast))
                {
                    try { s16 = WinMl.Load(Konbini.Deps.Registry.BgFast, true, out dev16); lastDev16 = dev16; }
                    catch (Exception ex) { err16 = ex.Message; lastDev16 = "failed"; }
                }
                // Half precision only pays off on a GPU; on CPU, Automatic uses the full-precision model.
                if (s16 != null && (p == BgPrecision.Fast || dev16 == "GPU")) { LastDevice = dev16 + " · fast"; return s16; }
            }
            bool fullInstalled = Konbini.Deps.DepManager.IsInstalled(Konbini.Deps.Registry.BgFull);
            if (s32 == null && err32 == null && fullInstalled)
            {
                try { s32 = WinMl.Load(Konbini.Deps.Registry.BgFull, true, out dev32); }
                catch (Exception ex) { err32 = ex.Message; }
            }
            if (s32 != null) { LastDevice = dev32 + " · full"; return s32; }
            if (s16 != null && p != BgPrecision.Full) { LastDevice = dev16 + " · fast"; return s16; }
            if (p != BgPrecision.Full && !Konbini.Deps.DepManager.IsInstalled(Konbini.Deps.Registry.BgFast) && (p == BgPrecision.Fast || !NeedsFull(p)))
                throw new Konbini.Deps.DependencyException(Konbini.Deps.Registry.BgFast, "The Standard background model isn't installed yet.", true);
            if (!fullInstalled)
                throw new Konbini.Deps.DependencyException(Konbini.Deps.Registry.BgFull, "The full-precision background model isn't installed yet.", true);
            throw new InvalidOperationException("Windows ML couldn't load the background removal model (" + (err32 ?? err16) + ").");
        }

        /// <summary>True when this precision will use the downloadable full-precision model on this PC.
        /// Automatic needs it only when the fast model can't run on a graphics card.</summary>
        public static bool NeedsFull(BgPrecision p)
        {
            if (p == BgPrecision.Full) return true;
            if (p == BgPrecision.Fast) return false;
            // Never loads the model: this runs on the UI thread (the tool's options), and loading 86 MB there froze it.
            // Use the device the fast model actually got last time; before it has ever loaded, decide from the hardware.
            string dev = lastDev16;
            if (dev != null) return dev != "GPU";
            return WinMl.ForceCpu || WinMl.DedicatedVideoMemory < 1L << 30;
        }

        private static readonly System.Collections.Generic.Dictionary<string, LearningModelSession> other = new System.Collections.Generic.Dictionary<string, LearningModelSession>();
        private static readonly System.Collections.Generic.Dictionary<string, string> otherDev = new System.Collections.Generic.Dictionary<string, string>();

        // Non-Standard models: one cached session each, on the GPU only when it has enough video memory (BgModels.UsesGpu).
        private static LearningModelSession SessionFor(BgModel m, bool forceCpu)
        {
            LearningModelSession s; string dev;
            if (!forceCpu && other.TryGetValue(m.Id, out s)) { LastDevice = otherDev[m.Id]; return s; }
            if (m.Dep.Source == Konbini.Deps.DepSource.Download && !Konbini.Deps.DepManager.IsInstalled(m.Dep))
                throw new Konbini.Deps.DependencyException(m.Dep, m.Dep.DisplayName + " isn't installed yet.", true);
            if (other.TryGetValue(m.Id, out s)) { other.Remove(m.Id); try { s.Dispose(); } catch { } }
            s = WinMl.Load(m.Dep, !forceCpu && BgModels.UsesGpu(m) ? WinMl.GpuPolicy.Prefer : WinMl.GpuPolicy.Never, out dev);
            other[m.Id] = s; otherDev[m.Id] = dev; LastDevice = dev;
            return s;
        }

        private static float[] RunModel(BgModel m, BgPrecision p, float[] input)
        {
            long[] shape = { 1, 3, m.Input, m.Input };
            if (m == BgModels.Standard) return WinMl.Run(Session(p), "input_image", shape, input, "output_image");
            var s = SessionFor(m, false);
            try { return WinMl.Run(s, s.Model.InputFeatures[0].Name, shape, input, s.Model.OutputFeatures[0].Name); }
            catch (Exception)
            {
                if (otherDev[m.Id] != "GPU") throw;
                // The graphics card couldn't run it (out of memory, driver reset): fall back to the processor for good.
                s = SessionFor(m, true);
                return WinMl.Run(s, s.Model.InputFeatures[0].Name, shape, input, s.Model.OutputFeatures[0].Name);
            }
        }

        /// <summary>Frees every loaded model (they hold hundreds of MB to GBs). The next run loads them again.</summary>
        public static void Unload()
        {
            lock (gate)
            {
                if (s16 != null) { try { s16.Dispose(); } catch { } s16 = null; }
                if (s32 != null) { try { s32.Dispose(); } catch { } s32 = null; }
                err16 = err32 = null;
                foreach (var s in other.Values) { try { s.Dispose(); } catch { } }
                other.Clear(); otherDev.Clear();
            }
        }

        public static bool Available(BgPrecision p)
        {
            lock (gate) { try { Session(p); return true; } catch { return false; } }
        }

        /// <summary>Full-resolution alpha (0..1) for the image's foreground.</summary>
        public static float[] Alpha(Bitmap img, BgPrecision precision, bool refine, Func<bool> cancelled, Action<int> progress)
        {
            return Alpha(img, BgModels.Standard, precision, refine, cancelled, progress);
        }

        public static float[] Alpha(Bitmap img, BgModel model, BgPrecision precision, bool refine, Func<bool> cancelled, Action<int> progress)
        {
            int w = img.Width, h = img.Height;
            int S = model.Input;
            byte[] px = Pixels(img);
            if (progress != null) progress(10);

            // 1. Model input: S x S RGB planes, normalised the way the model was trained.
            var input = new float[3 * S * S];
            using (var small = ImageUtil.Resize(img, S, S))
            {
                byte[] sp = Pixels(small);
                bool inet = model.Norm == BgNorm.ImageNet;
                for (int i = 0; i < S * S; i++)
                {
                    float r = sp[i * 4 + 2] / 255f, g = sp[i * 4 + 1] / 255f, b = sp[i * 4] / 255f;
                    if (inet) { input[i] = (r - 0.485f) / 0.229f; input[S * S + i] = (g - 0.456f) / 0.224f; input[2 * S * S + i] = (b - 0.406f) / 0.225f; }
                    else { input[i] = r - 0.5f; input[S * S + i] = g - 0.5f; input[2 * S * S + i] = b - 0.5f; }
                }
            }
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            float[] mask;
            lock (gate) mask = RunModel(model, precision, input);
            if (model.Sigmoid) for (int i = 0; i < mask.Length; i++) mask[i] = 1f / (1f + (float)Math.Exp(-mask[i]));
            if (progress != null) progress(60);
            if (cancelled != null && cancelled()) throw new OperationCanceledException();

            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < mask.Length; i++) { if (mask[i] < mn) mn = mask[i]; if (mask[i] > mx) mx = mask[i]; }
            float range = Math.Max(1e-6f, mx - mn);
            for (int i = 0; i < mask.Length; i++) mask[i] = (mask[i] - mn) / range;

            // 2. Work resolution for the guided filter.
            double ws = Math.Min(1.0, (double)WorkMax / Math.Max(w, h));
            int ww = Math.Max(1, (int)Math.Round(w * ws)), wh = Math.Max(1, (int)Math.Round(h * ws));
            float[] p = Bilinear(mask, S, S, ww, wh);
            var alpha = new float[w * h];
            if (!refine)
            {
                var sp = new Sampler(p, ww, wh, w, h);
                Parallel.For(0, h, y => { for (int x = 0; x < w; x++) alpha[y * w + x] = Remap(sp.At(x, y)); });
            }
            else
            {
                float[] I;
                using (var work = ImageUtil.Resize(img, ww, wh)) I = Gray(Pixels(work), ww * wh);
                int r = Math.Max(2, (int)Math.Round(Math.Max(ww, wh) / 192.0)); const float eps = 1e-3f;
                // He et al. guided filter: q = a*I + b with a = cov(I,p)/(var(I)+eps), b = mean(p) - a*mean(I).
                float[] mI = Box(I, ww, wh, r), mP = Box(p, ww, wh, r);
                var Ip = new float[ww * wh]; var II = new float[ww * wh];
                for (int i = 0; i < Ip.Length; i++) { Ip[i] = I[i] * p[i]; II[i] = I[i] * I[i]; }
                float[] mIp = Box(Ip, ww, wh, r), mII = Box(II, ww, wh, r);
                var a = Ip; var b = II; // reuse buffers
                for (int i = 0; i < a.Length; i++)
                {
                    float cov = mIp[i] - mI[i] * mP[i], vr = mII[i] - mI[i] * mI[i];
                    a[i] = cov / (vr + eps); b[i] = mP[i] - a[i] * mI[i];
                }
                a = Box(a, ww, wh, r); b = Box(b, ww, wh, r);
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                // Apply the (smooth) coefficients to the full-resolution guide: detail comes from the real photo.
                var sa = new Sampler(a, ww, wh, w, h); var sb = new Sampler(b, ww, wh, w, h);
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x, k = i * 4;
                        float g = (0.114f * px[k] + 0.587f * px[k + 1] + 0.299f * px[k + 2]) / 255f;
                        alpha[i] = Remap(sa.At(x, y) * g + sb.At(x, y));
                    }
                });
            }
            if (progress != null) progress(85);
            return alpha;
        }

        // Pushes the faint tails to fully clear / fully solid so backgrounds are clean and subjects opaque.
        private static float Remap(float v)
        {
            v = (v - 0.04f) / 0.92f;
            return v <= 0 ? 0 : v >= 1 ? 1 : v;
        }

        /// <summary>Builds the final image for the chosen background.</summary>
        public static unsafe Bitmap Compose(Bitmap img, float[] alpha, BgMode mode, Color color, int blurStrength, Bitmap bgImage)
        {
            int w = img.Width, h = img.Height;
            byte[] px = Pixels(img);
            var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            if (mode == BgMode.Mask)
            {
                WriteRows(outBmp, (y, row) => { for (int x = 0; x < w; x++) { byte v = Lanczos.Clamp(alpha[y * w + x] * 255f); row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = v; row[x * 4 + 3] = 255; } });
                return outBmp;
            }
            byte[] fg = Decontaminate(px, alpha, w, h);   // foreground colour with background spill removed
            byte[] bg = null;
            if (mode == BgMode.Blur) bg = BlurredBackground(img, blurStrength);
            else if (mode == BgMode.Image && bgImage != null) bg = Cover(bgImage, w, h);
            WriteRows(outBmp, (y, row) =>
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x; float a = alpha[i];
                        float fb = fg[i * 3], fgc = fg[i * 3 + 1], fr = fg[i * 3 + 2];
                    if (mode == BgMode.Transparent)
                    {
                        row[x * 4] = Lanczos.Clamp(fb); row[x * 4 + 1] = Lanczos.Clamp(fgc); row[x * 4 + 2] = Lanczos.Clamp(fr);
                        row[x * 4 + 3] = Lanczos.Clamp(a * 255f);
                        continue;
                    }
                    float bb, bgc, br;
                    if (bg != null) { bb = bg[i * 4]; bgc = bg[i * 4 + 1]; br = bg[i * 4 + 2]; }
                    else { bb = color.B; bgc = color.G; br = color.R; }
                    row[x * 4] = Lanczos.Clamp(fb * a + bb * (1 - a)); row[x * 4 + 1] = Lanczos.Clamp(fgc * a + bgc * (1 - a));
                    row[x * 4 + 2] = Lanczos.Clamp(fr * a + br * (1 - a)); row[x * 4 + 3] = 255;
                }
            });
            return outBmp;
        }

        // Semi-transparent pixels are a mix C = a*F + (1-a)*B. Estimate the local background B and foreground F
        // (alpha-weighted blurs at work resolution) and solve for F, so edges carry no background colour.
        private static byte[] Decontaminate(byte[] px, float[] alpha, int w, int h)
        {
            double ws = Math.Min(1.0, 768.0 / Math.Max(w, h));
            int ww = Math.Max(1, (int)Math.Round(w * ws)), wh = Math.Max(1, (int)Math.Round(h * ws));
            float[] aw = Area(i => alpha[i], w, h, ww, wh);
            int r = Math.Max(3, Math.Max(ww, wh) / 48);
            var res = new byte[w * h * 3];
            var invA = new float[ww * wh];
            for (int i = 0; i < invA.Length; i++) invA[i] = 1 - aw[i];
            float[] sumWb = Box(invA, ww, wh, r), sumWf = Box(aw, ww, wh, r);
            for (int c = 0; c < 3; c++)
            {
                int cc = c;
                float[] cw = Area(i => px[i * 4 + cc], w, h, ww, wh);
                var cb = new float[cw.Length]; var cf = new float[cw.Length];
                for (int i = 0; i < cw.Length; i++) { cb[i] = cw[i] * invA[i]; cf[i] = cw[i] * aw[i]; }
                cb = Box(cb, ww, wh, r); cf = Box(cf, ww, wh, r);
                for (int i = 0; i < cw.Length; i++) { cb[i] = sumWb[i] > 1e-4f ? cb[i] / sumWb[i] : cw[i]; cf[i] = sumWf[i] > 1e-4f ? cf[i] / sumWf[i] : cw[i]; }
                var sBg = new Sampler(cb, ww, wh, w, h); var sFe = new Sampler(cf, ww, wh, w, h);
                Parallel.For(0, h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x; float a = alpha[i], C = px[i * 4 + cc];
                        if (a >= 0.98f || a <= 0.002f) { res[i * 3 + cc] = (byte)C; continue; }
                        float solved = (C - (1 - a) * sBg.At(x, y)) / Math.Max(a, 0.15f);
                        float t = Math.Min(1f, a / 0.5f);                 // trust the solve more as alpha grows
                        res[i * 3 + cc] = Lanczos.Clamp(sFe.At(x, y) * (1 - t) + solved * t);
                    }
                });
            }
            return res;
        }

        /// <summary>Bilinear lookup of a small map at full-resolution coordinates, without building the full map.</summary>
        private sealed class Sampler
        {
            private readonly float[] s; private readonly int sw, sh; private readonly double fx, fy;
            public Sampler(float[] s, int sw, int sh, int dw, int dh) { this.s = s; this.sw = sw; this.sh = sh; fx = (double)sw / dw; fy = (double)sh / dh; }
            public float At(int x, int y)
            {
                double px = Math.Max(0, Math.Min(sw - 1, (x + 0.5) * fx - 0.5)), py = Math.Max(0, Math.Min(sh - 1, (y + 0.5) * fy - 0.5));
                int x0 = (int)px, y0 = (int)py, x1 = Math.Min(sw - 1, x0 + 1), y1 = Math.Min(sh - 1, y0 + 1);
                float tx = (float)(px - x0), ty = (float)(py - y0);
                float top = s[y0 * sw + x0] * (1 - tx) + s[y0 * sw + x1] * tx, bot = s[y1 * sw + x0] * (1 - tx) + s[y1 * sw + x1] * tx;
                return top * (1 - ty) + bot * ty;
            }
        }

        private static byte[] BlurredBackground(Bitmap img, int strength)
        {
            int w = img.Width, h = img.Height;
            double ws = Math.Min(1.0, 1024.0 / Math.Max(w, h));
            int ww = Math.Max(1, (int)Math.Round(w * ws)), wh = Math.Max(1, (int)Math.Round(h * ws));
            int r = Math.Max(1, (int)Math.Round(Math.Max(ww, wh) * Math.Max(1, Math.Min(100, strength)) / 2000.0));
            byte[] sp; using (var s = ImageUtil.Resize(img, ww, wh)) sp = Pixels(s);
            var outPx = new byte[w * h * 4];
            for (int c = 0; c < 3; c++)
            {
                var ch = new float[ww * wh];
                for (int i = 0; i < ch.Length; i++) ch[i] = sp[i * 4 + c];
                for (int k = 0; k < 3; k++) ch = Box(ch, ww, wh, r); // 3 box passes ≈ Gaussian
                var sm = new Sampler(ch, ww, wh, w, h); int cc = c;
                Parallel.For(0, h, y => { for (int x = 0; x < w; x++) outPx[(y * w + x) * 4 + cc] = Lanczos.Clamp(sm.At(x, y)); });
            }
            return outPx;
        }

        // Scales the background picture to cover the frame (centred crop).
        private static byte[] Cover(Bitmap bg, int w, int h)
        {
            using (var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    double s = Math.Max((double)w / bg.Width, (double)h / bg.Height);
                    int dw = (int)Math.Ceiling(bg.Width * s), dh = (int)Math.Ceiling(bg.Height * s);
                    g.DrawImage(bg, (w - dw) / 2, (h - dh) / 2, dw, dh);
                }
                return Pixels(dst);
            }
        }

        // ---- helpers -------------------------------------------------------------------------------------

        public static byte[] Pixels(Bitmap b)
        {
            int w = b.Width, h = b.Height; var px = new byte[w * h * 4];
            var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, px, y * w * 4, w * 4); }
            finally { b.UnlockBits(d); }
            return px;
        }

        private unsafe delegate void RowFill(int y, byte* row);
        private static unsafe void WriteRows(Bitmap b, RowFill fill)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan = (byte*)d.Scan0; int stride = d.Stride;
                Parallel.For(0, b.Height, y => fill(y, scan + (long)y * stride));
            }
            finally { b.UnlockBits(d); }
        }

        private static float[] Gray(byte[] px, int n)
        {
            var g = new float[n];
            for (int i = 0; i < n; i++) g[i] = (0.114f * px[i * 4] + 0.587f * px[i * 4 + 1] + 0.299f * px[i * 4 + 2]) / 255f;
            return g;
        }

        /// <summary>Mean over a (2r+1)² window, edge-clamped; O(1) per pixel via running sums.</summary>
        public static float[] Box(float[] s, int w, int h, int r)
        {
            var tmp = new float[w * h]; var d = new float[w * h];
            Parallel.For(0, h, y =>
            {
                int o = y * w; double sum = 0;
                for (int x = -r; x <= r; x++) sum += s[o + Clamp(x, w)];
                for (int x = 0; x < w; x++)
                {
                    tmp[o + x] = (float)(sum / (2 * r + 1));
                    sum += s[o + Clamp(x + r + 1, w)] - s[o + Clamp(x - r, w)];
                }
            });
            Parallel.For(0, w, x =>
            {
                double sum = 0;
                for (int y = -r; y <= r; y++) sum += tmp[Clamp(y, h) * w + x];
                for (int y = 0; y < h; y++)
                {
                    d[y * w + x] = (float)(sum / (2 * r + 1));
                    sum += tmp[Clamp(y + r + 1, h) * w + x] - tmp[Clamp(y - r, h) * w + x];
                }
            });
            return d;
        }

        private static int Clamp(int i, int n) { return i < 0 ? 0 : i >= n ? n - 1 : i; }

        public static float[] Bilinear(float[] s, int sw, int sh, int dw, int dh)
        {
            var d = new float[dw * dh];
            double sx = (double)sw / dw, sy = (double)sh / dh;
            Parallel.For(0, dh, y =>
            {
                double fy = Math.Max(0, Math.Min(sh - 1, (y + 0.5) * sy - 0.5));
                int y0 = (int)fy, y1 = Math.Min(sh - 1, y0 + 1); float ty = (float)(fy - y0);
                for (int x = 0; x < dw; x++)
                {
                    double fx = Math.Max(0, Math.Min(sw - 1, (x + 0.5) * sx - 0.5));
                    int x0 = (int)fx, x1 = Math.Min(sw - 1, x0 + 1); float tx = (float)(fx - x0);
                    float top = s[y0 * sw + x0] * (1 - tx) + s[y0 * sw + x1] * tx, bot = s[y1 * sw + x0] * (1 - tx) + s[y1 * sw + x1] * tx;
                    d[y * dw + x] = top * (1 - ty) + bot * ty;
                }
            });
            return d;
        }

        // Area-average downscale (no aliasing) used to build work-resolution maps.
        private static float[] Area(Func<int, float> s, int sw, int sh, int dw, int dh)
        {
            var d = new float[dw * dh];
            Parallel.For(0, dh, y =>
            {
                int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw); double sum = 0;
                    for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) sum += s(yy * sw + xx);
                    d[y * dw + x] = (float)(sum / ((y1 - y0) * (x1 - x0)));
                }
            });
            return d;
        }
    }
}
