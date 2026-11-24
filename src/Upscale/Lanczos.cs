using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace Konbini.Upscale
{
    /// <summary>Separable Lanczos-3 resampling in premultiplied ARGB, plus an optional light unsharp mask.</summary>
    public static class Lanczos
    {
        public static Bitmap Resize(Bitmap src, int w, int h, bool sharpen)
        {
            float[] px = ToPremultiplied(src);
            int sw = src.Width, sh = src.Height;
            float[] tmp = Pass(px, sw, sh, w, true);       // horizontal: sw x sh -> w x sh
            float[] dst = Pass(tmp, w, sh, h, false);      // vertical:   w x sh -> w x h
            if (sharpen) dst = Unsharp(dst, w, h, 0.35f);
            return FromPremultiplied(dst, w, h);
        }

        private static double Kernel(double x)
        {
            x = Math.Abs(x);
            if (x < 1e-8) return 1;
            if (x >= 3) return 0;
            double px = Math.PI * x;
            return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
        }

        // Resamples along one axis. Data is 4 floats (B,G,R,A premultiplied) per pixel, row-major.
        internal static float[] Pass(float[] s, int sw, int sh, int dLen, bool horizontal)
        {
            int sLen = horizontal ? sw : sh, other = horizontal ? sh : sw;
            int ow = horizontal ? dLen : sw, oh = horizontal ? sh : dLen;
            var d = new float[ow * oh * 4];
            double scale = (double)sLen / dLen, support = 3 * Math.Max(1, scale);
            // Precompute weights per destination coordinate.
            var starts = new int[dLen]; var weights = new float[dLen][];
            for (int i = 0; i < dLen; i++)
            {
                double center = (i + 0.5) * scale - 0.5;
                int a = (int)Math.Floor(center - support) + 1, b = (int)Math.Floor(center + support);
                var wts = new float[b - a + 1]; double sum = 0;
                for (int j = a; j <= b; j++) { double v = Kernel((j - center) / Math.Max(1, scale)); wts[j - a] = (float)v; sum += v; }
                for (int k = 0; k < wts.Length; k++) wts[k] = (float)(wts[k] / sum);
                starts[i] = a; weights[i] = wts;
            }
            Parallel.For(0, other, o =>
            {
                for (int i = 0; i < dLen; i++)
                {
                    float b0 = 0, g0 = 0, r0 = 0, a0 = 0; var wts = weights[i]; int a = starts[i];
                    for (int k = 0; k < wts.Length; k++)
                    {
                        int j = a + k; if (j < 0) j = 0; else if (j >= sLen) j = sLen - 1;
                        int si = horizontal ? (o * sw + j) * 4 : (j * sw + o) * 4;
                        float wt = wts[k];
                        b0 += s[si] * wt; g0 += s[si + 1] * wt; r0 += s[si + 2] * wt; a0 += s[si + 3] * wt;
                    }
                    int di = horizontal ? (o * ow + i) * 4 : (i * ow + o) * 4;
                    d[di] = b0; d[di + 1] = g0; d[di + 2] = r0; d[di + 3] = a0;
                }
            });
            return d;
        }

        // out = px + amount * (px - blur3x3(px)); light enough not to halo.
        private static float[] Unsharp(float[] s, int w, int h, float amount)
        {
            var d = new float[s.Length];
            Parallel.For(0, h, y =>
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                    for (int c = 0; c < 4; c++)
                    {
                        float blur = (s[(y0 * w + x0) * 4 + c] + 2 * s[(y0 * w + x) * 4 + c] + s[(y0 * w + x1) * 4 + c] +
                                      2 * s[(y * w + x0) * 4 + c] + 4 * s[(y * w + x) * 4 + c] + 2 * s[(y * w + x1) * 4 + c] +
                                      s[(y1 * w + x0) * 4 + c] + 2 * s[(y1 * w + x) * 4 + c] + s[(y1 * w + x1) * 4 + c]) / 16f;
                        float v = s[(y * w + x) * 4 + c];
                        d[(y * w + x) * 4 + c] = v + amount * (v - blur);
                    }
                }
            });
            return d;
        }

        public static unsafe float[] ToPremultiplied(Bitmap src)
        {
            int w = src.Width, h = src.Height; var px = new float[w * h * 4];
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        float a = row[x * 4 + 3] / 255f; int i = (y * w + x) * 4;
                        px[i] = row[x * 4] * a; px[i + 1] = row[x * 4 + 1] * a; px[i + 2] = row[x * 4 + 2] * a; px[i + 3] = a * 255f;
                    }
                }
            }
            finally { src.UnlockBits(data); }
            return px;
        }

        public static unsafe Bitmap FromPremultiplied(float[] px, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4; float a = px[i + 3];
                        if (a <= 0.5f) { row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = row[x * 4 + 3] = 0; continue; }
                        float inv = 255f / a;
                        row[x * 4] = Clamp(px[i] * inv); row[x * 4 + 1] = Clamp(px[i + 1] * inv); row[x * 4 + 2] = Clamp(px[i + 2] * inv);
                        row[x * 4 + 3] = Clamp(a);
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        public static byte Clamp(float v) { return v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f); }
    }
}
