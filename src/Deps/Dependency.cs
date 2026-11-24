using System;

namespace Konbini.Deps
{
    public enum DepSource { Embedded, Download }
    public enum DepStatus { Installed, Missing, Damaged }

    /// <summary>One external dependency. All values are fixed in <see cref="Registry"/>; nothing here ever comes from
    /// user input, files or a server, so the manager can only ever fetch these exact, hash-pinned files.</summary>
    public sealed class Dependency
    {
        public string Id;            // stable key, also the install folder name and mutex suffix
        public string DisplayName;   // "Video engine (ffmpeg 9.1)"
        public string Purpose;       // shown in the download prompt
        public DepSource Source;
        public string FileName;      // installed file name
        public string Sha256;        // hash of the installed file; pins the exact version
        public long Size;            // installed size in bytes
        public bool Executable;

        // Embedded
        public string Resource;      // manifest resource name
        public bool Gzipped;

        // Download (HTTPS only, host must be in Registry.AllowedHosts)
        public string Url;
        public string DownloadSha256; // hash of the downloaded file (archive or the file itself)
        public long DownloadSize;
        public string ArchiveEntry;   // when the download is a ZIP: the one entry to extract
        public string SourceName;     // "GitHub · GyanD/codexffmpeg" — shown to the user
        public string Licence;

        public override string ToString() { return DisplayName; }
    }

    public static class Registry
    {
        // Downloads may only come from (and redirect to) these hosts.
        public static readonly string[] AllowedHosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

        public static readonly Dependency Upscaler = new Dependency
        {
            Id = "upscale-model", DisplayName = "Upscaler AI model (Real-ESRGAN)", Purpose = "AI upscaling",
            Source = DepSource.Embedded, Resource = "Konbini.upscale.onnx", Size = 4866481, Licence = "BSD-3-Clause",
        };

        // Where you host hosting\isnet-general-use-fp16.onnx (a GitHub release asset of your own repo).
        // TODO: replace OWNER/REPO with your repository once the file is uploaded. Until then the download fails
        // with a 404 and the model can be installed with Settings → Components → From file….
        public const string StandardModelUrl = "https://github.com/xswzaq123/konbini/releases/download/models-v1/isnet-general-use-fp16.onnx";

        public static readonly Dependency BgFast = new Dependency
        {
            Id = "bg-model-fast", DisplayName = "Background model, standard (IS-Net fp16)", Purpose = "background removal (Standard model)",
            Source = DepSource.Download, FileName = "isnet-general-use-fp16.onnx",
            Sha256 = "a55546e585780fec35bb250d97944e7562b6d28575d9c36a6ccd173e508cfbc7", Size = 90667592,
            Url = StandardModelUrl,
            DownloadSha256 = "a55546e585780fec35bb250d97944e7562b6d28575d9c36a6ccd173e508cfbc7", DownloadSize = 90667592,
            SourceName = "GitHub · Konbini release (IS-Net by xuebinqin, converted to fp16)", Licence = "Apache-2.0",
        };

        public static readonly Dependency BgLite = new Dependency
        {
            Id = "bg-model-lite", DisplayName = "Background model, lite (U²-Net-P)", Purpose = "background removal on older PCs",
            Source = DepSource.Embedded, Resource = "Konbini.bglite.onnx", Size = 4574861, Licence = "Apache-2.0",
        };

        public static readonly Dependency BgHq = new Dependency
        {
            Id = "bg-model-hq", DisplayName = "Background model, high quality (BiRefNet-lite)", Purpose = "high-quality background removal",
            Source = DepSource.Download, FileName = "birefnet-general-lite.onnx",
            Sha256 = "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", Size = 224005088,
            Url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/BiRefNet-general-bb_swin_v1_tiny-epoch_232.onnx",
            DownloadSha256 = "5600024376f572a557870a5eb0afb1e5961636bef4e1e22132025467d0f03333", DownloadSize = 224005088,
            SourceName = "GitHub · danielgatis/rembg (BiRefNet, ZhengPeng7)", Licence = "MIT",
        };

        public static readonly Dependency BgMax = new Dependency
        {
            Id = "bg-model-max", DisplayName = "Background model, maximum quality (BiRefNet)", Purpose = "maximum-quality background removal",
            Source = DepSource.Download, FileName = "birefnet-general.onnx",
            Sha256 = "58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c", Size = 972666916,
            Url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/BiRefNet-general-epoch_244.onnx",
            DownloadSha256 = "58f621f00f5d756097615970a88a791584600dcf7c45b18a0a6267535a1ebd3c", DownloadSize = 972666916,
            SourceName = "GitHub · danielgatis/rembg (BiRefNet, ZhengPeng7)", Licence = "MIT",
        };

        public static readonly Dependency BgFull = new Dependency
        {
            Id = "bg-model-full", DisplayName = "Background model, full precision (IS-Net)",
            Purpose = "full-precision background removal (used on PCs without a suitable graphics card, or when you pick Full precision)",
            Source = DepSource.Download, FileName = "isnet-general-use.onnx",
            Sha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a", Size = 178648008,
            Url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx",
            DownloadSha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a", DownloadSize = 178648008,
            SourceName = "GitHub · danielgatis/rembg", Licence = "Apache-2.0",
        };

        public static readonly Dependency Ffmpeg = new Dependency
        {
            Id = "ffmpeg", DisplayName = "Video engine (ffmpeg 9.1)", Purpose = "video compression",
            Source = DepSource.Download, FileName = "ffmpeg.exe", Executable = true,
            Sha256 = "ed8f66cb877087a96f5bbb40a2944f0e764f2ad8870d5cb647360d912e62bc07", Size = 110590976,
            Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip",
            DownloadSha256 = "b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd", DownloadSize = 119467219,
            ArchiveEntry = "ffmpeg-9.1-essentials_build/bin/ffmpeg.exe",
            SourceName = "GitHub · GyanD/codexffmpeg (gyan.dev build)", Licence = "GPL-3.0",
        };

        public static readonly Dependency[] All = { Upscaler, BgLite, BgFast, BgFull, BgHq, BgMax, Ffmpeg };
    }
}
