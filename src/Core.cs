using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Konbini
{
    // Every tool in the library implements this. Register new tools in ToolRegistry.
    public interface ITool
    {
        string Name { get; }
        string Category { get; }
        string Description { get; }
        Control CreateView();
    }

    public static class ToolRegistry
    {
        public static List<ITool> All()
        {
            return new List<ITool>
            {
                new ImageResizerTool(),
                new ImageCompressorTool(),
                new ImageUpscalerTool(),
                new BackgroundRemoverTool(),
                new VideoCompressorTool(),
                new FileConverterTool(),
            };
        }
    }

    public enum OutFormat { Keep, Jpeg, Png, Bmp, Tiff, Gif }

    public static class ImageUtil
    {
        public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

        public static bool IsImage(string path)
        {
            return Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        // Loads without locking the file and applies EXIF rotation.
        public static Bitmap Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
            {
                var bmp = new Bitmap(img);
                var r = Orientation(img);
                if (r != RotateFlipType.RotateNoneFlipNone) bmp.RotateFlip(r);
                return bmp;
            }
        }

        // Thumbnail for non-image files: a file card coloured by type, with the extension on it.
        public static Bitmap IconThumb(string path, int box)
        {
            string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            if (ext.Length == 0) ext = "FILE";
            if (ext.Length > 5) ext = ext.Substring(0, 5);
            Color c;
            switch (ext)
            {
                case "PDF": c = Color.FromArgb(220, 38, 38); break;
                case "MP3": case "WAV": case "FLAC": case "M4A": case "AAC": case "WMA": case "OGG": c = Color.FromArgb(168, 85, 247); break;
                case "MP4": case "MOV": case "AVI": case "MKV": case "WMV": case "WEBM": case "M4V": case "3GP": c = Color.FromArgb(236, 72, 153); break;
                case "DOC": case "DOCX": case "RTF": case "ODT": case "TXT": case "MD": c = Color.FromArgb(37, 99, 235); break;
                case "XLS": case "XLSX": case "CSV": case "TSV": case "ODS": c = Color.FromArgb(22, 163, 74); break;
                case "PPT": case "PPTX": c = Color.FromArgb(234, 88, 12); break;
                case "ZIP": case "GZ": case "TAR": case "TGZ": case "7Z": case "RAR": c = Color.FromArgb(202, 138, 4); break;
                case "JSON": case "XML": case "YAML": case "YML": case "HTML": case "HTM": c = Color.FromArgb(13, 148, 136); break;
                default: c = Color.FromArgb(100, 116, 139); break;
            }
            var bmp = new Bitmap(box, box, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float w = box * 0.62f, h = box * 0.8f, x = (box - w) / 2, y = (box - h) / 2, fold = w * 0.28f;
                using (var p = new GraphicsPath())
                {
                    p.AddLine(x, y, x + w - fold, y);
                    p.AddLine(x + w - fold, y, x + w, y + fold);
                    p.AddLine(x + w, y + fold, x + w, y + h);
                    p.AddLine(x + w, y + h, x, y + h);
                    p.CloseFigure();
                    using (var b = new SolidBrush(c)) g.FillPath(b, p);
                }
                using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                    g.FillPolygon(b, new[] { new PointF(x + w - fold, y), new PointF(x + w - fold, y + fold), new PointF(x + w, y + fold) });
                using (var f = new Font("Segoe UI Semibold", box * (ext.Length > 3 ? 0.11f : 0.14f), GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(ext, f, Brushes.White, new RectangleF(x, y + h * 0.35f, w, h * 0.5f), sf);
            }
            return bmp;
        }

        public static RotateFlipType Orientation(Image img)
        {
            const int OrientationId = 0x0112;
            if (!img.PropertyIdList.Contains(OrientationId)) return RotateFlipType.RotateNoneFlipNone;
            switch (img.GetPropertyItem(OrientationId).Value[0])
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.Rotate180FlipX;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        private static bool SwapsAxes(RotateFlipType r)
        {
            return r == RotateFlipType.Rotate90FlipNone || r == RotateFlipType.Rotate270FlipNone ||
                   r == RotateFlipType.Rotate90FlipX || r == RotateFlipType.Rotate270FlipX;
        }

        // Fast thumbnail: decodes without validation and draws straight to a small canvas.
        // Also returns the true (orientation-corrected) dimensions. Returns null if unreadable.
        public static Bitmap MakeThumb(string path, int box, out Size size)
        {
            // Windows Imaging first: it decodes straight to thumbnail size (JPEG scales in the DCT, ~5-10x less work)
            // and, unlike GDI+, doesn't hold a process-wide lock that freezes the UI thread's painting meanwhile.
            Bitmap wic = WicThumb(path, box, out size);
            if (wic != null) return wic;
            size = Size.Empty;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                using (var img = Image.FromStream(fs, false, false))
                {
                    var r = Orientation(img);
                    size = SwapsAxes(r) ? new Size(img.Height, img.Width) : img.Size;
                    int iw = img.Width, ih = img.Height;
                    double s = Math.Min(1.0, Math.Min((double)box / iw, (double)box / ih));
                    if (SwapsAxes(r)) s = Math.Min(1.0, Math.Min((double)box / ih, (double)box / iw));
                    int w = Math.Max(1, (int)(iw * s)), h = Math.Max(1, (int)(ih * s));
                    using (var small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(small))
                        {
                            // Bilinear is ~3x faster than bicubic and indistinguishable at 128px.
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(img, 0, 0, w, h);
                        }
                        if (r != RotateFlipType.RotateNoneFlipNone) small.RotateFlip(r);
                        var t = new Bitmap(box, box, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(t))
                            g.DrawImageUnscaled(small, (box - small.Width) / 2, (box - small.Height) / 2);
                        return t;
                    }
                }
            }
            catch { return null; }
        }

        private static unsafe Bitmap WicThumb(string path, int box, out Size size)
        {
            size = Size.Empty;
            try
            {
                var file = Konbini.Conversion.WinRT.Open(path);
                using (var ras = Konbini.Conversion.WinRT.Await(file.OpenAsync(Windows.Storage.FileAccessMode.Read)))
                {
                    var dec = Konbini.Conversion.WinRT.Await(Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras));
                    uint pw = dec.PixelWidth, ph = dec.PixelHeight, ow = dec.OrientedPixelWidth, oh = dec.OrientedPixelHeight;
                    if (pw == 0 || ph == 0) return null;
                    double sc = Math.Min(1.0, Math.Min((double)box / ow, (double)box / oh));
                    // The transform scales the stored pixels; orientation is applied afterwards.
                    var tr = new Windows.Graphics.Imaging.BitmapTransform
                    {
                        ScaledWidth = (uint)Math.Max(1, (int)(pw * sc)), ScaledHeight = (uint)Math.Max(1, (int)(ph * sc)),
                        InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
                    };
                    bool swap = ow != pw;
                    int w = (int)(swap ? tr.ScaledHeight : tr.ScaledWidth), h = (int)(swap ? tr.ScaledWidth : tr.ScaledHeight);
                    var pd = Konbini.Conversion.WinRT.Await(dec.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Straight, tr,
                        Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                        Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb));
                    byte[] bytes = pd.DetachPixelData();
                    if (bytes.Length < w * h * 4) return null;
                    var t = new Bitmap(box, box, PixelFormat.Format32bppArgb);
                    var d = t.LockBits(new Rectangle(0, 0, box, box), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        byte* dst = (byte*)d.Scan0; int ox = (box - w) / 2, oy = (box - h) / 2;
                        for (int y = 0; y < box; y++)
                        {
                            byte* row = dst + y * d.Stride;
                            for (int i = 0; i < box * 4; i++) row[i] = 0; // transparent padding
                            if (y >= oy && y < oy + h)
                                System.Runtime.InteropServices.Marshal.Copy(bytes, (y - oy) * w * 4, (IntPtr)(row + ox * 4), w * 4);
                        }
                    }
                    finally { t.UnlockBits(d); }
                    size = new Size((int)ow, (int)oh);
                    return t;
                }
            }
            catch { size = Size.Empty; return null; }
        }

        private static readonly ImageCodecInfo JpegCodec =
            ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        public static Size ReadSize(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var img = Image.FromStream(fs, false, false))
                    return img.Size;
            }
            catch { return Size.Empty; }
        }

        public static Bitmap Resize(Bitmap src, int w, int h)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var attr = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                attr.SetWrapMode(WrapMode.TileFlipXY); // avoids dark edge halos
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attr);
            }
            return dst;
        }

        public static OutFormat FromExtension(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png": return OutFormat.Png;
                case ".bmp": return OutFormat.Bmp;
                case ".gif": return OutFormat.Gif;
                case ".tif": case ".tiff": return OutFormat.Tiff;
                default: return OutFormat.Jpeg;
            }
        }

        public static string ExtensionFor(OutFormat f)
        {
            switch (f)
            {
                case OutFormat.Png: return ".png";
                case OutFormat.Bmp: return ".bmp";
                case OutFormat.Gif: return ".gif";
                case OutFormat.Tiff: return ".tif";
                default: return ".jpg";
            }
        }

        public static void Save(Bitmap bmp, string path, OutFormat f, int jpegQuality)
        {
            using (var fs = File.Create(path)) Save(bmp, fs, f, jpegQuality);
        }

        public static void Save(Bitmap bmp, Stream path, OutFormat f, int jpegQuality)
        {
            if (f == OutFormat.Jpeg)
            {
                // JPEG has no alpha: flatten onto white.
                using (var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(flat))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                    }
                    using (var ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(1, Math.Min(100, jpegQuality)));
                        flat.Save(path, JpegCodec, ep);
                    }
                }
                return;
            }
            ImageFormat fmt = f == OutFormat.Png ? ImageFormat.Png : f == OutFormat.Bmp ? ImageFormat.Bmp
                            : f == OutFormat.Gif ? ImageFormat.Gif : ImageFormat.Tiff;
            bmp.Save(path, fmt);
        }

        public static string FormatBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.0") + " KB";
            return (b / 1024.0 / 1024.0).ToString("0.00") + " MB";
        }
    }

    // Reusable batch file list + output settings + run button. Tools supply options and a per-file processor.
    public class BatchImagePanel : UserControl
    {
        public readonly ListView List;
        private readonly TextBox outDir;
        private readonly CheckBox sameFolder;
        private readonly TextBox suffix;
        private readonly SlimProgress progress;
        private readonly Button runBtn;
        private readonly Label status;
        private readonly HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int ThumbSize = 128;
        private readonly ImageList thumbs;
        // Path -> index in thumbs. The image list only grows (cleared when the list empties), so indexes stay valid;
        // this avoids ImageList's linear key search, which made big batches O(n²).
        private readonly Dictionary<string, int> thumbIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Finished thumbnails waiting to be applied in one batch on the UI thread.
        private readonly Queue<Tuple<string, Bitmap, Size, string>> thumbDone = new Queue<Tuple<string, Bitmap, Size, string>>();
        private bool thumbFlushPosted;
        // Native image-list slots in use; capacity beyond this is reserved, blank and unreferenced.
        private int thumbUsed;
        private readonly Queue<string> thumbQueue = new Queue<string>();
        private int thumbWorkers;
        // Windows Imaging decodes without GDI+'s process-wide lock, so a few workers genuinely run in parallel.
        private static readonly int MaxThumbWorkers = Math.Max(1, Math.Min(3, Environment.ProcessorCount / 2));
        private Label dropHint;
        private readonly Dictionary<string, ListViewItem> itemsByPath = new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Button> toolbarButtons = new List<Button>();
        private readonly Dictionary<string, Size> sizesByPath = new Dictionary<string, Size>(StringComparer.OrdinalIgnoreCase);

        // Raised when images are added/removed or their dimensions become known.
        public event EventHandler ItemsChanged;
        private void RaiseItemsChanged()
        {
            // Item count decides whether the vertical scrollbar shows; refit columns once it has.
            if (List.IsHandleCreated) List.BeginInvoke((Action)FitColumns);
            var h = ItemsChanged; if (h != null) h(this, EventArgs.Empty);
        }

        // Last column fills the visible width (excluding any vertical scrollbar) so the list never scrolls sideways.
        private void FitColumns()
        {
            if (List.View != View.Details || List.Columns.Count == 0) return;
            int avail = List.ClientSize.Width, n = List.Columns.Count;
            int used = 0; for (int i = 0; i < n - 1; i++) used += List.Columns[i].Width;
            // Narrow window: give up name-column width (down to 140) before overflowing sideways.
            if (n > 1 && used + 80 > avail)
            {
                int first = List.Columns[0].Width, nf = Math.Max(140, first - (used + 80 - avail));
                List.Columns[0].Width = nf; used += nf - first;
            }
            else if (n > 1 && List.Columns[0].Width < 260 && used + 80 + (260 - List.Columns[0].Width) <= avail)
            {
                used += 260 - List.Columns[0].Width; List.Columns[0].Width = 260;  // grow back when room returns
            }
            int w = Math.Max(80, avail - used);
            var last = List.Columns[n - 1];
            if (last.Width == w) return;
            last.Width = w;
            // The native list keeps a stale horizontal scrollbar after shrinking; a deferred re-set clears it.
            if (List.IsHandleCreated) List.BeginInvoke((Action)delegate { last.Width = w - 1; last.Width = w; });
        }

        public bool SizesPending { get { return sizesByPath.Count < files.Count; } }

        // The size shared by every readable image, once all dimensions are known; Size.Empty otherwise.
        public Size UniformSize
        {
            get
            {
                if (files.Count == 0 || sizesByPath.Count < files.Count) return Size.Empty;
                var readable = sizesByPath.Values.Where(s => !s.IsEmpty).Distinct().ToList();
                return readable.Count == 1 ? readable[0] : Size.Empty;
            }
        }

        // Returns the result text for the "Result" column. Throw to report an error.
        public Func<string, string, string> Process; // (inputPath, outputPathWithoutExtension) => result
        public Action BeforeRun;
        // Which files the list accepts (images by default) and an optional per-file description for column 2.
        public Func<string, bool> Accept = ImageUtil.IsImage;
        public Func<string, string> Describe;
        public Func<string, int, Bitmap> Thumbnail; // optional custom thumbnail (e.g. a video frame); null = image thumbnail
        public int MaxWorkers = 4;
        // OLE drag & drop only works on STA threads. Konbini.exe's Main is [STAThread]; when the panel is hosted on an MTA
        // thread (e.g. scripts/tests that also use WinRT), registering would throw, so drag & drop is simply turned off.
        private static bool CanDrop { get { return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA; } }
        public string DetailsHeader { set { List.Columns[1].Text = value; List.Columns[1].Width = 200; FitColumns(); } }
        public string[] Files { get { return files.ToArray(); } }
        public bool WarnOnEmptySuffix = true;
        private Button addFilesBtn;
        private string noun = "image";
        // What the list holds ("image", "file"): used in button and status text.
        public string Noun { get { return noun; } set { noun = value; addFilesBtn.Text = "Add " + value + "s…"; if (dropHint != null) dropHint.Text = "Drag & drop " + value + "s or folders here"; UpdateStatus(); } }

        public BatchImagePanel(string runText, Control options)
        {
            Dock = DockStyle.Fill;
            Font = Theme.Body;
            BackColor = Theme.Bg;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // Toolbar
            var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
            var addFiles = addFilesBtn = Theme.SecondaryButton("Add images…");
            var addFolder = Theme.SecondaryButton("Add folder…");
            var remove = Theme.SecondaryButton("Remove selected");
            var clear = Theme.SecondaryButton("Clear");
            var listViewBtn = Theme.SecondaryButton("☰ List");
            var thumbViewBtn = Theme.SecondaryButton("▦ Thumbnails");
            listViewBtn.Margin = new Padding(24, 3, 3, 3);
            bar.Controls.AddRange(new Control[] { addFiles, addFolder, remove, clear, listViewBtn, thumbViewBtn });
            toolbarButtons.AddRange(new[] { addFiles, addFolder, remove, clear });
            root.Controls.Add(bar, 0, 0);

            List = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, AllowDrop = CanDrop,
                BorderStyle = BorderStyle.None, HideSelection = false };
            // Double buffering removes flicker while thumbnails stream in.
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(List, true, null);
            List.Columns.Add("File", 260);
            List.Columns.Add("Dimensions", 100);
            List.Columns.Add("Size", 80);
            List.Columns.Add("Result", 300);
            List.ShowItemToolTips = true;
            // Custom header: the system header ignores dark mode. Rows still use default drawing.
            List.OwnerDraw = true;
            List.DrawItem += (s, e) => e.DrawDefault = true;
            List.DrawSubItem += (s, e) => e.DrawDefault = true;
            List.DrawColumnHeader += (s, e) =>
            {
                using (var bg = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(bg, e.Bounds);
                using (var line = new Pen(Theme.Border))
                    e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.Small,
                    new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height), Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            // Last column fills the width so no unpainted system header strip is left on the right.
            List.Resize += delegate { FitColumns(); };
            List.ClientSizeChanged += delegate { FitColumns(); };
            thumbs = new ImageList { ImageSize = new Size(ThumbSize, ThumbSize), ColorDepth = ColorDepth.Depth32Bit };
            List.LargeImageList = thumbs;
            var listCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 6, 8), Margin = new Padding(0, 4, 0, 0) };
            listCard.Controls.Add(List);
            root.Controls.Add(listCard, 0, 1);

            Action<bool> setView = thumbMode =>
            {
                List.View = thumbMode ? View.LargeIcon : View.Details;
                if (!thumbMode) FitColumns();
                // In Details view items resolve ImageKey against SmallImageList (none), so they cached "no image".
                // Re-resolve against the thumbnail list now that it's active.
                if (thumbMode)
                {
                    List.BeginUpdate();
                    foreach (ListViewItem it in List.Items) RefreshImage(it);
                    List.EndUpdate();
                }
                listViewBtn.Tag = thumbMode ? Theme.ToggleOff : Theme.ToggleOn;
                thumbViewBtn.Tag = thumbMode ? Theme.ToggleOn : Theme.ToggleOff;
                Theme.Apply(listViewBtn); Theme.Apply(thumbViewBtn);
            };
            setView(Settings.DefaultView == Settings.ViewThumbnails);
            listViewBtn.Click += delegate { setView(false); };
            thumbViewBtn.Click += delegate { setView(true); };
            // Changing the default in Settings switches open batches too, so the change is visible right away.
            string appliedDefault = Settings.DefaultView;
            EventHandler onSettings = delegate
            {
                if (Settings.DefaultView == appliedDefault) return;
                appliedDefault = Settings.DefaultView;
                setView(appliedDefault == Settings.ViewThumbnails);
            };
            Settings.Changed += onSettings;
            Disposed += delegate { Settings.Changed -= onSettings; };

            var hint = new Label { Text = "Drag & drop images or folders here", ForeColor = Theme.Muted, AutoSize = true,
                BackColor = Theme.Card, UseMnemonic = false, Tag = Theme.MutedTag };
            List.Controls.Add(hint);
            List.Resize += delegate { hint.Location = new Point((List.Width - hint.Width) / 2, List.Height / 2); };
            dropHint = hint;

            // Options + output
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

            var optBox = new Card { Title = "Options", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 6, 0) };
            options.Dock = DockStyle.Fill;
            optBox.Controls.Add(options);
            bottom.Controls.Add(optBox, 0, 0);

            var outBox = new Card { Title = "Output", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            var outFlow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
            outFlow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outFlow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sameFolder = new CheckBox { Text = "Save next to originals", Checked = true, AutoSize = true };
            outFlow.Controls.Add(sameFolder, 0, 0); outFlow.SetColumnSpan(sameFolder, 2);
            outDir = new TextBox { Dock = DockStyle.Fill, Visible = false,
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Konbini") };
            var browse = Theme.SecondaryButton("Browse…"); browse.Visible = false;
            outFlow.Controls.Add(outDir, 0, 1); outFlow.Controls.Add(browse, 1, 1);
            var sfxRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            sfxRow.Controls.Add(new Label { Text = "File name suffix:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            suffix = new TextBox { Width = 170 };
            sfxRow.Controls.Add(suffix);
            // "Automatic" suffixes describe the settings (e.g. _upscaled-4x). Typing makes it custom; the link goes back.
            autoLink = new LinkLabel { Text = "Use automatic", AutoSize = true, Visible = false, Margin = new Padding(6, 6, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline };
            autoLink.LinkClicked += delegate { customSuffix = false; RefreshSuffix(); };
            sfxRow.Controls.Add(autoLink);
            suffix.TextChanged += delegate
            {
                if (settingSuffix || autoSuffix == null) return;
                customSuffix = true; autoLink.Visible = true;
            };
            HookOptionChanges(options);
            outFlow.Controls.Add(sfxRow, 0, 2); outFlow.SetColumnSpan(sfxRow, 2);
            outBox.Controls.Add(outFlow);
            bottom.Controls.Add(outBox, 1, 0);
            root.Controls.Add(bottom, 0, 2);
            bottomPart = bottom;

            // Run row
            var runRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 8, 0, 0) };
            runRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            runRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            runRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left, Text = "No images added.", Tag = Theme.MutedTag };
            progress = new SlimProgress { Dock = DockStyle.Fill, Visible = false, Margin = new Padding(8, 0, 12, 0) };
            runBtn = Theme.PrimaryButton(runText);
            runRow.Controls.Add(status, 0, 0); runRow.Controls.Add(progress, 1, 0); runRow.Controls.Add(runBtn, 2, 0);
            root.Controls.Add(runRow, 0, 3);
            runPart = runRow;

            // Events
            sameFolder.CheckedChanged += delegate { outDir.Visible = browse.Visible = !sameFolder.Checked; };
            browse.Click += delegate
            {
                using (var d = new FolderBrowserDialog { SelectedPath = outDir.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) outDir.Text = d.SelectedPath;
            };
            addFiles.Click += delegate
            {
                using (var d = new OpenFileDialog { Multiselect = true,
                    Filter = Describe == null ? "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" : "All files|*.*" })
                    if (d.ShowDialog(this) == DialogResult.OK) AddPaths(d.FileNames);
            };
            addFolder.Click += delegate
            {
                using (var d = new FolderBrowserDialog())
                    if (d.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { d.SelectedPath });
            };
            remove.Click += delegate { RemoveItems(List.SelectedItems.Cast<ListViewItem>().ToList()); };
            clear.Click += delegate { ClearFiles(); };
            List.KeyDown += (s, e) =>
            {
                if (!runBtn.Enabled) return;
                if (e.KeyCode == Keys.Delete) RemoveItems(List.SelectedItems.Cast<ListViewItem>().ToList());
                else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem it in List.Items) it.Selected = true;
            };
            List.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            List.DragDrop += (s, e) => AddPaths((string[])e.Data.GetData(DataFormats.FileDrop));
            runBtn.Click += delegate { Run(); };
        }

        private Control bottomPart, runPart;

        /// <summary>Just the file list and its toolbar (add, remove, clear, list/thumbnails): no options, output or Run.
        /// The Home page uses this to show what was dropped.</summary>
        public void ShowListOnly() { bottomPart.Visible = false; runPart.Visible = false; }

        /// <summary>Adds files or folders to the batch (same rules as dropping them on the list). Used by the Home page.</summary>
        public void AddFiles(IEnumerable<string> paths) { AddPaths(paths); }

        /// <summary>Removes every file from the batch and frees its thumbnails (Clear button; also when leaving the tool).</summary>
        public void ClearFiles()
        {
            lock (thumbQueue) thumbQueue.Clear();
            RemoveItems(List.Items.Cast<ListViewItem>().ToList());
        }

        public string Suffix { get { return suffix.Text; } set { settingSuffix = true; suffix.Text = value; settingSuffix = false; } }

        private LinkLabel autoLink;
        private Func<string> autoSuffix;
        private bool customSuffix, settingSuffix;

        /// <summary>Builds a suffix that says what the current settings do ("_upscaled-4x"). The suffix box follows it
        /// whenever an option changes, until the user types their own; "Use automatic" switches back.</summary>
        public Func<string> AutoSuffix { get { return autoSuffix; } set { autoSuffix = value; RefreshSuffix(); } }
        /// <summary>True when the user typed their own suffix.</summary>
        public bool CustomSuffix { get { return customSuffix; } }

        public void RefreshSuffix()
        {
            if (autoSuffix == null || customSuffix) { if (autoLink != null) autoLink.Visible = customSuffix && autoSuffix != null; return; }
            string s;
            try { s = SafeSuffix(autoSuffix()); } catch (Exception) { return; }
            Suffix = s;
            autoLink.Visible = false;
        }

        /// <summary>Lower-case, file-name-safe: spaces become dashes, anything else odd is dropped.</summary>
        public static string SafeSuffix(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant())
            {
                if (c == ' ') { if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-'); }
                else if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') sb.Append(c);
            }
            return sb.ToString().TrimEnd('-', '.');
        }

        // Any option change (combo, checkbox, number, slider, text) refreshes the automatic suffix.
        private void HookOptionChanges(Control c)
        {
            if (c == null) return;
            EventHandler h = delegate { RefreshSuffix(); };
            if (c is ComboBox) ((ComboBox)c).SelectedIndexChanged += h;
            else if (c is CheckBox) ((CheckBox)c).CheckedChanged += h;
            else if (c is RadioButton) ((RadioButton)c).CheckedChanged += h;
            else if (c is TrackBar) ((TrackBar)c).ValueChanged += h;
            else if (c is NumBox) { ((NumBox)c).ValueChanged += h; return; }
            else if (c is TextBox) ((TextBox)c).TextChanged += h;
            foreach (Control child in c.Controls) HookOptionChanges(child);
        }

        private void AddPaths(IEnumerable<string> paths)
        {
            var newItems = new List<ListViewItem>();
            foreach (var p in paths)
            {
                IEnumerable<string> list;
                try
                {
                    list = Directory.Exists(p)
                        ? Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(f => Accept(f)).ToArray()
                        : (Accept(p) && File.Exists(p) ? new[] { p } : new string[0]);
                }
                catch (Exception) { continue; } // unreadable folder
                foreach (var f in list)
                {
                    if (!files.Add(f)) continue;
                    long len = 0;
                    try { len = new FileInfo(f).Length; } catch { }
                    // Dimensions and thumbnail are filled in by the background worker.
                    var it = new ListViewItem(new[] { Path.GetFileName(f), "…", ImageUtil.FormatBytes(len), "" })
                        { Tag = f, ToolTipText = f };
                    itemsByPath[f] = it;
                    newItems.Add(it);
                }
            }
            if (newItems.Count == 0) return;
            List.BeginUpdate();
            List.Items.AddRange(newItems.ToArray());
            List.EndUpdate();
            lock (thumbQueue) foreach (var it in newItems) thumbQueue.Enqueue((string)it.Tag);
            UpdateStatus();
            StartThumbWorker();
        }

        // Builds thumbnails + reads dimensions off the UI thread so large batches stay responsive.
        private void StartThumbWorker()
        {
            int start;
            lock (thumbQueue) { start = Math.Min(MaxThumbWorkers, thumbQueue.Count) - thumbWorkers; if (start <= 0) return; thumbWorkers += start; }
            for (int n = 0; n < start; n++) new Thread(() =>
            {
                while (true)
                {
                    string f;
                    lock (thumbQueue)
                    {
                        if (thumbQueue.Count == 0) { thumbWorkers--; return; }
                        f = thumbQueue.Dequeue();
                    }
                    Size size;
                    Bitmap thumb = null; size = Size.Empty;
                    if (Thumbnail != null) { try { thumb = Thumbnail(f, ThumbSize); } catch { thumb = null; } }
                    else thumb = ImageUtil.MakeThumb(f, ThumbSize, out size);
                    string desc = null;
                    if (Describe != null)
                    {
                        try { desc = Describe(f); } catch (Exception ex) { desc = "Error: " + ex.Message; }
                        if (thumb == null) thumb = ImageUtil.IconThumb(f, ThumbSize);
                    }
                    if (IsDisposed) return;
                    bool post;
                    lock (thumbDone) { thumbDone.Enqueue(Tuple.Create(f, thumb, size, desc)); post = !thumbFlushPosted; thumbFlushPosted = true; }
                    if (post)
                    {
                        try { BeginInvoke((Action)FlushThumbs); }
                        catch (InvalidOperationException) { DisposePendingThumbs(); return; }
                    }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        }

        // Applies every finished thumbnail in one list update and raises ItemsChanged once, instead of a repaint,
        // column refit and ItemsChanged per file.
        private void FlushThumbs()
        {
            Tuple<string, Bitmap, Size, string>[] batch;
            lock (thumbDone) { batch = thumbDone.ToArray(); thumbDone.Clear(); thumbFlushPosted = false; }
            if (batch.Length == 0) return;
            if (IsDisposed) { foreach (var t in batch) if (t.Item2 != null) t.Item2.Dispose(); return; }
            AddThumbImages(batch);
            List.BeginUpdate();
            try { foreach (var t in batch) ApplyThumb(t.Item1, t.Item2, t.Item3, t.Item4); }
            finally { List.EndUpdate(); }
            RaiseItemsChanged();
        }

        [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool ImageList_SetImageCount(IntPtr himl, uint count);
        [System.Runtime.InteropServices.DllImport("comctl32.dll")] private static extern bool ImageList_Replace(IntPtr himl, int i, IntPtr image, IntPtr mask);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

        // ImageList.Images.Add grows the native list 4 slots at a time (copying every image each time) and makes the
        // ListView re-walk all items per add: O(n²) for big batches. Reserve the whole batch's slots once and fill them
        // natively, using the same colour/mask conversion WinForms uses.
        private void AddThumbImages(Tuple<string, Bitmap, Size, string>[] batch)
        {
            var fresh = new List<Tuple<string, Bitmap, Size, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in batch)
            {
                ListViewItem it;
                if (t.Item2 != null && !thumbIndex.ContainsKey(t.Item1) && seen.Add(t.Item1)
                    && itemsByPath.TryGetValue(t.Item1, out it) && it.ListView != null) fresh.Add(t);
            }
            if (fresh.Count == 0) return;
            IntPtr h = thumbs.Handle;
            int start = thumbUsed, need = start + fresh.Count, cap = thumbs.Images.Count;
            // Double the capacity when it runs out, so growing to n images costs O(n) copying in total.
            if (need > cap && !ImageList_SetImageCount(h, (uint)Math.Max(need, Math.Max(16, cap * 2)))) return; // ApplyThumb falls back
            thumbUsed = need;
            for (int k = 0; k < fresh.Count; k++)
            {
                IntPtr mask = IntPtr.Zero, color = IntPtr.Zero;
                try
                {
                    mask = ControlPaint.CreateHBitmapTransparencyMask(fresh[k].Item2);
                    color = ControlPaint.CreateHBitmapColorMask(fresh[k].Item2, mask);
                    if (ImageList_Replace(h, start + k, color, mask)) thumbIndex[fresh[k].Item1] = start + k;
                }
                finally { if (color != IntPtr.Zero) DeleteObject(color); if (mask != IntPtr.Zero) DeleteObject(mask); }
            }
        }

        private void DisposePendingThumbs()
        {
            lock (thumbDone) { foreach (var t in thumbDone) if (t.Item2 != null) t.Item2.Dispose(); thumbDone.Clear(); thumbFlushPosted = false; }
        }

        private void ApplyThumb(string f, Bitmap thumb, Size size, string desc)
        {
            ListViewItem it;
            if (!itemsByPath.TryGetValue(f, out it) || it.ListView == null) { if (thumb != null) thumb.Dispose(); return; }
            it.SubItems[1].Text = desc ?? (size.IsEmpty ? "unreadable" : size.Width + " × " + size.Height);
            sizesByPath[f] = size;
            if (thumb == null) return;
            if (!thumbIndex.ContainsKey(f)) { thumbs.Images.Add(thumb); thumbUsed = thumbs.Images.Count; thumbIndex[f] = thumbUsed - 1; } // fallback path
            thumb.Dispose(); // ImageList keeps its own copy
            RefreshImage(it);
        }

        // The native item caches its image index; re-assign so it picks up the current thumbnail.
        private void RefreshImage(ListViewItem it)
        {
            int idx;
            if (!thumbIndex.TryGetValue((string)it.Tag, out idx)) return;
            it.ImageIndex = -1;
            it.ImageIndex = idx;
        }

        // Gives each output a unique path within a run (e.g. a.png and a.jpg both converted to a.jpg).
        private readonly HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Existing files are never overwritten unless the user confirmed overwriting originals (empty suffix).
        /// <summary>True once the user asked to stop; long Process callbacks should poll it.</summary>
        public bool CancelRequested { get { return cancelRequested; } }
        /// <summary>Shows interim progress (e.g. "AI 40%") in a file's status cell; callable from workers.</summary>
        public void ReportProgress(string src, string text)
        {
            try
            {
                BeginInvoke((Action)(() =>
                {
                    ListViewItem it;
                    if (itemsByPath.TryGetValue(src, out it) && it.ListView != null) it.SubItems[3].Text = text;
                }));
            }
            catch (InvalidOperationException) { }
        }

        public string ReservePath(string path)
        {
            string candidate;
            lock (claimed)
            {
                string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
                candidate = path; int n = 2;
                while (claimed.Contains(candidate) || (!allowOverwrite && File.Exists(candidate)))
                    candidate = Path.Combine(dir, name + " (" + n++ + ")" + ext);
                claimed.Add(candidate);
            }
            if (!File.Exists(candidate) && itemOutputs.Value != null) itemOutputs.Value.Add(candidate);
            return candidate;
        }
        private volatile bool allowOverwrite, cancelRequested;
        // Outputs created for the item a worker is currently processing, deleted again if that item fails.
        private readonly ThreadLocal<List<string>> itemOutputs = new ThreadLocal<List<string>>();
        public bool Busy { get; private set; }
        /// <summary>Stop after the files in progress and close the window (used when quitting mid-run).</summary>
        public void RequestCancel() { closeWhenStopped = true; cancelRequested = true; status.Text = "Stopping…"; }
        /// <summary>Stop after the files in progress; the window stays open.</summary>
        public void StopRun() { if (Busy) { cancelRequested = true; status.Text = "Stopping…"; } }
        private volatile bool closeWhenStopped;

        /// <summary>Raised on the UI thread after each file: (files done, total).</summary>
        public event Action<int, int> RunProgress;
        /// <summary>Raised on the UI thread when a run ends: (succeeded, failed, stopped early).</summary>
        public event Action<int, int, bool> RunCompleted;
        /// <summary>Output files written by the last run.</summary>
        public List<string> LastOutputs { get; private set; }

        /// <summary>Runs the batch with the current settings (as the Run button does). False if it didn't start.</summary>
        public bool StartRun() { Run(); return Busy; }

        public static string FriendlyError(Exception ex)
        {
            int hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex) & 0xFFFF;
            if (hr == 112 || hr == 39) return "Not enough disk space.";
            if (hr == 32 || hr == 33) return "The file is in use by another program. Close it and try again.";
            if (ex is UnauthorizedAccessException) return "Access denied: the file or folder is read-only or protected.";
            if (ex is DirectoryNotFoundException) return "The folder no longer exists.";
            if (ex is FileNotFoundException) return "The file no longer exists.";
            if (ex is PathTooLongException) return "The path is too long for Windows.";
            return ex.Message;
        }

        private void RemoveItems(IList<ListViewItem> toRemove)
        {
            List.BeginUpdate();
            foreach (var it in toRemove)
            {
                string f = (string)it.Tag;
                files.Remove(f); itemsByPath.Remove(f); sizesByPath.Remove(f); List.Items.Remove(it);
            }
            List.EndUpdate();
            if (List.Items.Count == 0) { thumbs.Images.Clear(); thumbIndex.Clear(); thumbUsed = 0; } // safe only when no item references an index
            UpdateStatus();
            RaiseItemsChanged();
        }

        private void UpdateStatus()
        {
            dropHint.Visible = List.Items.Count == 0;
            status.Text = List.Items.Count == 0 ? "No " + noun + "s added." : List.Items.Count + " " + noun + (List.Items.Count == 1 ? "" : "s") + " ready.";
        }

        // Removes half-written outputs of a failed item (only files this run created).
        private void DeletePartial()
        {
            foreach (var p in itemOutputs.Value) try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        private void SetBusy(bool busy)
        {
            Busy = busy;
            runBtn.Enabled = !busy;
            foreach (var b in toolbarButtons) b.Enabled = !busy;
            List.AllowDrop = !busy && CanDrop;
            progress.Visible = busy;
        }

        private void Run()
        {
            if (List.Items.Count == 0) { MessageBox.Show(this, "Add some " + noun + "s first.", "Konbini"); return; }
            // BeforeRun may throw OperationCanceledException to abort (e.g. after telling the user what's missing).
            try { if (BeforeRun != null) BeforeRun(); } catch (OperationCanceledException) { return; }
            string dir = sameFolder.Checked ? null : outDir.Text.Trim();
            string sfx = suffix.Text;
            if (sfx.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { MessageBox.Show(this, "The file name suffix contains characters that aren't allowed in file names.", "Konbini"); return; }
            if (dir != null)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".konbini-write-test-" + Guid.NewGuid().ToString("N"));
                    File.WriteAllBytes(probe, new byte[0]); File.Delete(probe);
                }
                catch (Exception ex) { MessageBox.Show(this, "Can't save to the output folder:\n" + FriendlyError(ex), "Konbini", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            if (WarnOnEmptySuffix && dir == null && string.IsNullOrEmpty(sfx) &&
                MessageBox.Show(this, "No suffix is set, so originals with the same format will be overwritten. Continue?",
                    "Konbini", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            allowOverwrite = WarnOnEmptySuffix && dir == null && string.IsNullOrEmpty(sfx);
            cancelRequested = false; closeWhenStopped = false;
            Memory.RunStarting();

            var items = List.Items.Cast<ListViewItem>().Select(i => new KeyValuePair<ListViewItem, string>(i, (string)i.Tag)).ToList();
            foreach (var kv in items) kv.Key.SubItems[3].Text = "Waiting…";
            SetBusy(true); progress.Maximum = items.Count; progress.Value = 0;
            status.Text = "Processing…";
            int ok = 0, fail = 0;
            var proc = Process;
            lock (claimed) claimed.Clear();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            new Thread(() =>
            {
                // A few images at a time: big speedup on multi-core PCs without exhausting memory on huge photos.
                var po = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(MaxWorkers, Environment.ProcessorCount)) };
                System.Threading.Tasks.Parallel.ForEach(items, po, kv =>
                {
                    string src = kv.Value, result;
                    if (cancelRequested) result = "Cancelled";
                    else
                    {
                        itemOutputs.Value = new List<string>();
                        try
                        {
                            string baseOut = Path.Combine(dir ?? Path.GetDirectoryName(src), Path.GetFileNameWithoutExtension(src) + sfx);
                            result = proc(src, baseOut); Interlocked.Increment(ref ok);
                        }
                        catch (OperationCanceledException) { result = "Cancelled"; DeletePartial(); }
                        catch (OutOfMemoryException) { result = "Error: not a valid file or too large"; Interlocked.Increment(ref fail); DeletePartial(); }
                        catch (Exception ex) { result = "Error: " + FriendlyError(ex); Interlocked.Increment(ref fail); DeletePartial(); }
                        itemOutputs.Value = null;
                    }
                    string r = result;
                    try { BeginInvoke((Action)(() => { kv.Key.SubItems[3].Text = r; kv.Key.ToolTipText = src + "\n" + r; progress.Value++; if (RunProgress != null) RunProgress(progress.Value, progress.Maximum); })); }
                    catch (InvalidOperationException) { }
                });
                // Give the memory back now the batch is done (still on the worker thread, so the UI doesn't stall).
                Memory.ReleaseAfterRun();
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        SetBusy(false);
                        lock (claimed) LastOutputs = claimed.Where(File.Exists).ToList();
                        bool stopped = cancelRequested;
                        if (stopped && closeWhenStopped) { var form = FindForm(); if (form != null) form.Close(); return; }
                        status.Text = (stopped ? "Stopped: " : "Done: ") + ok + " succeeded" + (fail > 0 ? ", " + fail + " failed" : "") +
                                      " in " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.0") + " s.";
                        if (RunCompleted != null) RunCompleted(ok, fail, stopped);
                    }));
                }
                catch (InvalidOperationException) { }
            }) { IsBackground = true }.Start();
        }
    }

    public static class Ui
    {
        public static Label L(string t) { return new Label { Text = t, AutoSize = true, Margin = new Padding(0, 7, 6, 0) }; }

        public static NumBox Num(int min, int max, int val, int width)
        {
            return new NumBox { Maximum = max, Minimum = min, Value = val, Width = width };
        }

        public static ComboBox Formats()
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            c.Items.AddRange(new object[] { "Same as original", "JPEG", "PNG", "BMP", "TIFF", "GIF" });
            c.SelectedIndex = 0;
            return c;
        }

        public static OutFormat Resolve(ComboBox c, string src)
        {
            return c.SelectedIndex == 0 ? ImageUtil.FromExtension(src) : (OutFormat)c.SelectedIndex;
        }
    }

    /// <summary>Returns memory to Windows after work: unloads AI models, compacts the large-object heap
    /// (image and tensor buffers live there and .NET never compacts it by itself) and trims the working set.</summary>
    public static class Memory
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        // Models stay loaded this long after a run, so back-to-back runs don't reload them (seconds on a GPU).
        private const int UnloadAfterMs = 60 * 1000;
        private static readonly System.Threading.Timer unloadTimer = new System.Threading.Timer(_ => UnloadModels(), null, Timeout.Infinite, Timeout.Infinite);
        private static int running;

        /// <summary>Call when a batch starts: keeps the models loaded while it runs.</summary>
        public static void RunStarting()
        {
            Interlocked.Increment(ref running);
            unloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Call when a batch ends: frees its buffers now and the AI models once the app has been idle a minute.</summary>
        public static void ReleaseAfterRun()
        {
            if (Interlocked.Decrement(ref running) < 0) Interlocked.Exchange(ref running, 0);
            Compact();
            unloadTimer.Change(UnloadAfterMs, Timeout.Infinite);
        }

        private static void UnloadModels()
        {
            if (Volatile.Read(ref running) > 0) return;
            try { Konbini.Upscale.AiUpscaler.Unload(); } catch { }
            try { Konbini.BgRemove.BackgroundRemover.Unload(); } catch { }
            Compact();
        }

        /// <summary>Compaction off the UI thread (it waits for finalizers and trims the working set).</summary>
        public static void CompactInBackground() { ThreadPool.QueueUserWorkItem(_ => Compact()); }

        public static void Compact()
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers(); // WinRT/GDI+ wrappers release native memory in finalizers
            GC.Collect();
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                    SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }
    }
}
