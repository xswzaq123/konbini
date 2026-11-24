using System;
using Konbini.Deps;
using Konbini.Upscale;

namespace Konbini.BgRemove
{
    public enum BgNorm { HalfOffset, ImageNet }   // x/255-0.5 (IS-Net)  |  (x/255-mean)/std (U²-Net, BiRefNet)

    /// <summary>One background-removal model the user can pick. Tensor names are read from the model at load time.</summary>
    public sealed class BgModel
    {
        public string Id;          // saved in settings
        public string Name;        // combo text
        public string For;         // what it's good at
        public string Hardware;    // what it needs
        public Dependency Dep;     // the model file (Standard also uses Registry.BgFull)
        public int Input;          // square input size
        public BgNorm Norm;
        public bool Sigmoid;       // output is logits
        public long GpuMinVram;    // use the GPU only with at least this much dedicated video memory (0 = any GPU; long.MaxValue = never)
        public long MinRam;        // refuse to run below this much physical memory (0 = no limit)
        public string SizeText { get { return ImageUtil.FormatBytes(Dep.Source == DepSource.Download ? Dep.DownloadSize : Dep.Size); } }
        public override string ToString() { return Name; }
    }

    public static class BgModels
    {
        private const long GB = 1L << 30;

        public static readonly BgModel Lite = new BgModel
        {
            Id = "lite", Name = "Lite — old or low-end PCs", Dep = Registry.BgLite, Input = 320, Norm = BgNorm.ImageNet,
            For = "Quick cut-outs of clear, simple subjects. Edges are softer.",
            Hardware = "Any PC; about a second per photo even without a graphics card.",
        };
        public static readonly BgModel Standard = new BgModel
        {
            Id = "standard", Name = "Standard — everyday photos", Dep = Registry.BgFast, Input = 1024, Norm = BgNorm.HalfOffset,
            For = "People, products and pets. Good all-rounder.",
            Hardware = "Any PC; fast with a graphics card (86 MB download). PCs without one, or Full precision, use a 170 MB download.",
        };
        public static readonly BgModel High = new BgModel
        {
            Id = "high", Name = "High quality — hair & fine detail", Dep = Registry.BgHq, Input = 1024, Norm = BgNorm.ImageNet, Sigmoid = true,
            GpuMinVram = 8 * GB, MinRam = 12 * GB,
            For = "Hair, fur, lace and fine detail; cleaner edges than Standard.",
            Hardware = "Needs 12 GB+ RAM. Uses graphics cards with 8 GB+ video memory; otherwise runs on the processor (about 10–15 s per photo).",
        };
        public static readonly BgModel Max = new BgModel
        {
            Id = "max", Name = "Maximum quality — best edges", Dep = Registry.BgMax, Input = 1024, Norm = BgNorm.ImageNet, Sigmoid = true,
            GpuMinVram = 12 * GB, MinRam = 16 * GB,
            For = "The most accurate cut-outs, for large or difficult photos.",
            Hardware = "Needs 16 GB+ RAM. Uses graphics cards with 12 GB+ video memory; otherwise runs on the processor (about 20–30 s per photo).",
        };

        public static readonly BgModel[] All = { Lite, Standard, High, Max };

        public static BgModel ById(string id)
        {
            foreach (var m in All) if (m.Id == id) return m;
            return Standard;
        }

        /// <summary>Lite on PCs without a usable graphics card or with little memory, otherwise Standard.</summary>
        public static BgModel Recommended
        {
            get
            {
                long ram = WinMl.TotalRam;
                if (ram > 0 && ram < 6 * GB) return Lite;
                return WinMl.DedicatedVideoMemory >= 1 * GB ? Standard : Lite;
            }
        }

        /// <summary>Null if the model can run here, otherwise a friendly reason it can't.</summary>
        public static string CannotRunReason(BgModel m)
        {
            long ram = WinMl.TotalRam;
            if (m.MinRam > 0 && ram > 0 && ram < m.MinRam)
                return m.Name.Split('—')[0].Trim() + " needs at least " + (m.MinRam / GB) + " GB of memory; this PC has " + Math.Round(ram / (double)GB, 1) + " GB. Choose Standard or Lite.";
            return null;
        }

        public static bool UsesGpu(BgModel m)
        {
            if (WinMl.ForceCpu || m.GpuMinVram == long.MaxValue) return false;
            return m.GpuMinVram == 0 || WinMl.DedicatedVideoMemory >= m.GpuMinVram;
        }
    }
}
