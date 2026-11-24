using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace Konbini.Conversion
{
    public static class ImageConv
    {
        private static readonly string[] GdiReadable = { "png", "jpg", "gif", "bmp", "tif", "ico" };

        // Loads any image Windows can decode (incl. WebP/HEIC/AVIF/JPEG XR when their codecs are present).
        public static Bitmap Load(string path, string id)
        {
            if (GdiReadable.Contains(id))
            {
                try { return ImageUtil.Load(path); } catch { /* fall back to the Windows decoder */ }
            }
            byte[] png = WinDecodeToPng(File.ReadAllBytes(path));
            using (var ms = new MemoryStream(png)) using (var img = Image.FromStream(ms)) return new Bitmap(img);
        }

        public static byte[] WinDecodeToPng(byte[] data)
        {
            using (var input = WinRT.FromBytes(data))
            {
                WinBitmapDecoder dec;
                try { dec = WinRT.Await(WinBitmapDecoder.CreateAsync(input)); }
                catch (Exception) { throw new NotSupportedException("Windows has no decoder for this image type (install the codec from the Microsoft Store, e.g. HEIF/WebP/AV1 Image Extensions)."); }
                using (var sb = WinRT.Await(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                           new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)))
                using (var output = new InMemoryRandomAccessStream())
                {
                    var enc = WinRT.Await(WinBitmapEncoder.CreateAsync(WinBitmapEncoder.PngEncoderId, output));
                    enc.SetSoftwareBitmap(sb);
                    WinRT.Await(enc.FlushAsync());
                    return WinRT.ReadAll(output);
                }
            }
        }

        public static bool CanEncodeHeif()
        {
            try { return WinBitmapEncoder.GetEncoderInformationEnumerator().Any(e => e.CodecId == WinBitmapEncoder.HeifEncoderId); }
            catch { return false; }
        }

        public static void Save(Bitmap bmp, string path, string target, int jpegQuality)
        {
            switch (target)
            {
                case "jpg": ImageUtil.Save(bmp, path, OutFormat.Jpeg, jpegQuality); break;
                case "png": ImageUtil.Save(bmp, path, OutFormat.Png, 0); break;
                case "bmp": ImageUtil.Save(bmp, path, OutFormat.Bmp, 0); break;
                case "gif": ImageUtil.Save(bmp, path, OutFormat.Gif, 0); break;
                case "tif": ImageUtil.Save(bmp, path, OutFormat.Tiff, 0); break;
                case "ico": SaveIco(bmp, path); break;
                case "pdf":
                    {
                        var pdf = new PdfWriter();
                        pdf.AddJpegPage(JpegBytes(bmp, jpegQuality), bmp.Width, bmp.Height);
                        pdf.Save(path); break;
                    }
                case "jxr": WinEncode(bmp, path, WinBitmapEncoder.JpegXREncoderId, jpegQuality); break;
                case "heic": WinEncode(bmp, path, WinBitmapEncoder.HeifEncoderId, jpegQuality); break;
                default: throw new NotSupportedException("Can't save images as ." + target);
            }
        }

        public static byte[] JpegBytes(Bitmap bmp, int quality)
        {
            using (var ms = new MemoryStream()) { ImageUtil.Save(bmp, ms, OutFormat.Jpeg, quality); return ms.ToArray(); }
        }

        private static void WinEncode(Bitmap bmp, string path, Guid encoderId, int quality)
        {
            byte[] png; using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); png = ms.ToArray(); }
            using (var input = WinRT.FromBytes(png))
            {
                var dec = WinRT.Await(WinBitmapDecoder.CreateAsync(input));
                using (var sb = WinRT.Await(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)))
                using (var output = new InMemoryRandomAccessStream())
                {
                    var props = new BitmapPropertySet();
                    props.Add("ImageQuality", new BitmapTypedValue(quality / 100f, Windows.Foundation.PropertyType.Single));
                    WinBitmapEncoder enc;
                    try { enc = WinRT.Await(WinBitmapEncoder.CreateAsync(encoderId, output, props)); }
                    catch { enc = WinRT.Await(WinBitmapEncoder.CreateAsync(encoderId, output)); }
                    enc.SetSoftwareBitmap(sb);
                    WinRT.Await(enc.FlushAsync());
                    File.WriteAllBytes(path, WinRT.ReadAll(output));
                }
            }
        }

        // Multi-size .ico with PNG-compressed entries (supported since Windows Vista).
        private static void SaveIco(Bitmap src, string path)
        {
            var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 }.Where(s => s <= Math.Max(16, Math.Max(src.Width, src.Height))).ToList();
            var images = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (var canvas = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        double k = Math.Min((double)s / src.Width, (double)s / src.Height);
                        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
                        g.DrawImage(src, (s - w) / 2, (s - h) / 2, w, h);
                    }
                    using (var ms = new MemoryStream()) { canvas.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                }
            }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }

    public static class PdfConv
    {
        // Renders every page to an image. Returns the output files.
        public static List<string> ToImages(string src, string target, string baseOut, int jpegQuality, Func<string, string> reserve)
        {
            var outputs = new List<string>();
            var file = WinRT.Open(src);
            Windows.Data.Pdf.PdfDocument doc;
            try { doc = WinRT.Await(Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file)); }
            catch (Exception ex) { throw new InvalidDataException("Couldn't open the PDF (it may be damaged or password-protected). " + ex.Message); }
            uint count = doc.PageCount;
            for (uint i = 0; i < count; i++)
            {
                using (var page = doc.GetPage(i))
                using (var stream = new InMemoryRandomAccessStream())
                {
                    // ~150 dpi: crisp text, reasonable file size.
                    var opts = new Windows.Data.Pdf.PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(1, page.Size.Width * 150 / 72),
                        BitmapEncoderId = WinBitmapEncoder.PngEncoderId,
                        BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255)
                    };
                    WinRT.Await(page.RenderToStreamAsync(stream, opts));
                    byte[] png = WinRT.ReadAll(stream);
                    string outPath = reserve(baseOut + (count > 1 ? "_page" + (i + 1) : "") + Converters.ExtensionFor(target));
                    if (target == "png") File.WriteAllBytes(outPath, png);
                    else using (var ms = new MemoryStream(png)) using (var img = Image.FromStream(ms)) using (var bmp = new Bitmap(img))
                        ImageConv.Save(bmp, outPath, target, jpegQuality);
                    outputs.Add(outPath);
                }
            }
            return outputs;
        }

        public static void ImagesToPdf(IEnumerable<Bitmap> images, string path, int quality)
        {
            var pdf = new PdfWriter();
            foreach (var b in images) pdf.AddJpegPage(ImageConv.JpegBytes(b, quality), b.Width, b.Height);
            pdf.Save(path);
        }
    }

    public static class MediaConv
    {
        public static readonly string[] AudioTargets = { "mp3", "m4a", "wav", "wma", "flac" };
        public static readonly string[] VideoTargets = { "mp4", "wmv", "avi" };

        // quality: 0 = high, 1 = medium, 2 = low
        private static MediaEncodingProfile Profile(string target, int quality)
        {
            var aq = quality == 0 ? AudioEncodingQuality.High : quality == 1 ? AudioEncodingQuality.Medium : AudioEncodingQuality.Low;
            var vq = quality == 0 ? VideoEncodingQuality.HD1080p : quality == 1 ? VideoEncodingQuality.HD720p : VideoEncodingQuality.Vga;
            switch (target)
            {
                case "mp3": return MediaEncodingProfile.CreateMp3(aq);
                case "m4a": return MediaEncodingProfile.CreateM4a(aq);
                case "wav": return MediaEncodingProfile.CreateWav(aq);
                case "wma": return MediaEncodingProfile.CreateWma(aq);
                case "flac": return MediaEncodingProfile.CreateFlac(aq);
                case "mp4": return MediaEncodingProfile.CreateMp4(vq);
                case "wmv": return MediaEncodingProfile.CreateWmv(vq);
                case "avi": return MediaEncodingProfile.CreateAvi(vq);
                default: throw new NotSupportedException("Can't encode ." + target);
            }
        }

        public static void Transcode(string src, string dst, string target, int quality, bool sourceHasVideo)
        {
            var profile = Profile(target, quality);
            bool videoOut = VideoTargets.Contains(target);
            if (videoOut && sourceHasVideo)
            {
                // Keep the source's size when it's smaller than the chosen preset (avoids upscaling).
                try
                {
                    var props = WinRT.Await(WinRT.Open(src).Properties.GetVideoPropertiesAsync());
                    if (props.Width > 0 && props.Height > 0 && props.Width <= profile.Video.Width)
                    {
                        profile.Video.Width = props.Width - props.Width % 2;
                        profile.Video.Height = props.Height - props.Height % 2;
                    }
                    else if (props.Width > 0 && props.Height > 0)
                    {
                        uint w = profile.Video.Width, h = (uint)(w * (double)props.Height / props.Width);
                        profile.Video.Height = h - h % 2;
                    }
                }
                catch { }
            }
            if (!videoOut) profile.Video = null;

            var input = WinRT.Open(src);
            var output = WinRT.Create(dst);
            var tc = new MediaTranscoder { HardwareAccelerationEnabled = true };
            PrepareTranscodeResult prep = WinRT.Await(tc.PrepareFileTranscodeAsync(input, output, profile));
            if (!prep.CanTranscode)
            {
                try { WinRT.Await(output.DeleteAsync()); } catch { }
                string why = prep.FailureReason == TranscodeFailureReason.CodecNotFound
                    ? "Windows doesn't have a codec for this file (some formats need a free extension from the Microsoft Store)."
                    : prep.FailureReason == TranscodeFailureReason.InvalidProfile ? "This conversion isn't supported for this file."
                    : "Windows couldn't convert this file (" + prep.FailureReason + ").";
                throw new NotSupportedException(why);
            }
            WinRT.Await(prep.TranscodeAsync());
        }
    }
}
