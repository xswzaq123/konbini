# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Konbini is a Windows-only desktop app (Windows 10/11) that hosts a growing library of tools: Image Resizer, Image Compressor, Image Upscaler, Background Remover, File Converter and Video Compressor. **Hard rule: the user must never have to install anything.** Use only what ships with Windows: .NET Framework 4.x, GDI+ and the Windows Runtime APIs. Don't add NuGet packages, native DLLs or external executables. **The exception is the dependency manager** (approved by the user): small dependencies are embedded in the exe, and large ones (ffmpeg and the Standard, High and Max background models) are downloaded on first use, after the user agrees. See "External dependencies" below. Don't target mobile or the browser.

## Commands

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1          # -> dist\Konbini.exe
powershell -NoProfile -ExecutionPolicy Bypass -File package.ps1        # -> dist\release\Konbini-Setup-<v>.exe + Konbini-<v>-portable.zip
powershell -NoProfile -ExecutionPolicy Bypass -File tests\smoke.ps1    # UI tests (resize/compress/thumbnails/lock/settings)
powershell -MTA -NoProfile -ExecutionPolicy Bypass -File tests\convert.ps1   # converter tests; -MTA is required (WinRT)
powershell -NoProfile -ExecutionPolicy Bypass -File tests\edge.ps1 <folder with a sample image>   # locked/read-only/Unicode/overwrite/close-mid-run
```
`tests\upscale.ps1` (run with `-MTA`; add `-cpu` to test without a GPU) covers the AI and Fast upscalers.

**Video Compressor / ffmpeg:**
- ffmpeg is a downloadable dependency (`Registry.Ffmpeg`): the gyan.dev essentials 9.1 zip from GitHub, which includes libx264, libx265 and AAC (GPL). It is pinned in `tools\ffmpeg.txt`.
- `Ffmpeg.Run` and `Ffmpeg.Probe` hold `DepManager.Acquire` for as long as the process runs, and every process goes into a kill-on-close Job Object.
- `Ffmpeg.Probe` parses `ffmpeg -i`, because ffprobe isn't shipped. Before ffmpeg is installed, the tool's `Describe` falls back to WinRT `GetVideoPropertiesAsync`.
- `VideoCompress.Templates` holds the quick templates. `BuildArgs` never upscales, forces `yuv420p` and `+faststart`, tags H.265 output as `hvc1`, and does two-pass encodes for the target-size modes.
- `tests\videocompress.ps1` installs ffmpeg from `models\ffmpeg.exe` if that file is present (it isn't embedded, and is only kept for tests), and otherwise downloads it.

**AI upscaler model:** `models\realesr-general-x4v3.onnx` is Real-ESRGAN (BSD-3), embedded into the exe by `build.ps1` and run through the in-box Windows ML (`Windows.AI.winmd`). It was generated once by `tools\export-model.py` in a throwaway Python virtual environment. The ONNX input is fixed at 464×464 (a 384-pixel tile plus a 40-pixel margin, wider than the model's 34-pixel receptive field so there are no seams). `TILE` in the script must match `Tile`/`Margin` in `src\Upscale\AiUpscaler.cs`.

**Background remover:** the user picks one of four models (`src\BgRemove\BgModels.cs`), each a dependency in `Registry`.

| Model | Dependency | Size | Provided as | Input and normalisation |
|---|---|---|---|---|
| Lite: U²-Net-P | `BgLite` | 4.6 MB | Embedded | 320², ImageNet mean and std |
| Standard: IS-Net | `BgFast` (fp16), plus `BgFull` (fp32) | 88 MB / 170 MB | Downloaded (fp16 from the user's own GitHub release, `Registry.StandardModelUrl`; fp32 from rembg) | 1024², `x/255−0.5` |
| High quality: BiRefNet-lite | `BgHq` | 214 MB | Downloaded | 1024², ImageNet, sigmoid on the output |
| Maximum quality: BiRefNet | `BgMax` | 928 MB | Downloaded | 1024², ImageNet, sigmoid on the output |

- **Default:** `BgModels.Recommended` picks Lite on PCs with under 6 GB of RAM or no graphics card, otherwise Standard. The user's choice is saved as `Settings.BgModel`.
- **Downloading:** every download, including models picked in the tool and Download in Settings → Components, first asks Yes/No (`DepPrompt.Ensure`), showing the size, source and licence. Nothing is fetched without a yes.
- **BiRefNet hardware limits:**
  - `UsesGpu` runs it on the GPU only with at least 8 GB (High) or 12 GB (Max) of dedicated video memory, read by `WinMl.DedicatedVideoMemory` from the registry.
  - A 4 GB RTX 3050 runs out of memory, or the driver resets (TDR), under DirectML. On the CPU it takes about 12 s and 7 GB of RAM for lite, or 21 s and 10 GB for full.
  - `CannotRunReason` blocks it below 12 or 16 GB of RAM.
  - `RunModel` falls back to the CPU if a GPU run fails.
- **Testing:** **don't run BiRefNet inference on the user's machine**, as they asked. `tests\bgremove.ps1` tests only Lite by default; `-models high,max` is opt-in. `tests\deps.ps1` checks only the download.
- **Shared pipeline:** after inference, every model goes through the same steps: min-max normalisation, guided-filter refinement, decontamination and `Compose`.
- **Memory:** `Memory.ReleaseAfterRun` (Core.cs) compacts the heap when a batch ends and unloads the AI models after 60 s idle (`RunStarting` cancels that). `NeedsFull` and the tool's status text must never load a model or hash a file on the UI thread: use `lastDev16` and `DepManager.LooksInstalled`. Downloaded models load with `LearningModel.LoadFromFilePath` while the `Acquire` handle is held; `WinMl.Run` copies outputs in one block through `IMemoryBufferByteAccess`.
- **Tensor names:** for non-Standard models they're read from `session.Model`. `WinMl.Run` binds the wanted output explicitly, because U²-Net has seven outputs.
- **Standard precision:** the half-precision copy was made by `tools\convert-fp16.py`. That script must keep `Resize` in full precision and use onnxruntime's converter. `BgPrecision` applies to Standard only: Automatic uses fp16 on a GPU and fp32 on a CPU; before fp16 is installed, `NeedsFull` decides from `WinMl.DedicatedVideoMemory`. Only the needed file is downloaded. The file to upload, its hash and the steps are in `hosting\README.md`.
- **Adding a model:**
  1. Download it and check that Windows ML loads *and runs* it on both GPU and CPU.
  2. Pin its hash in `Registry`.
  3. Add a `BgModel` entry with its input size, normalisation, sigmoid setting and hardware text.

`edge.ps1` answers message boxes from a timer, so tests can trigger dialogs. It temporarily denies write access on its own temp folders with `icacls` and restores it afterwards.

- There is no lint step and no project file. `build.ps1` calls the in-box compiler `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` directly. It references the .NET Framework assemblies, `System.Runtime(.WindowsRuntime).dll`, and the `.winmd` files in `C:\Windows\System32\WinMetadata`. To use a new assembly or WinRT namespace, add it there.
- The test scripts load `dist\Konbini.exe` in-process by reflection, so build first. To run one check, copy the relevant block into a scratch script, using the same `LoadFile` / `Find-C` / `Pump` pattern. Reach private members such as `AddPaths` through `BindingFlags.NonPublic`.
- `smoke.ps1` must stay saved as UTF-8 **with BOM**, because Windows PowerShell 5.1 needs it. It backs up the real settings file and restores it afterwards.
- `tests\make-video.cs` builds the sample MP4 that `convert.ps1` uses.

## Releases

Double-click `make-release.bat` (it runs `package.ps1`, then opens `dist\release`).

The version lives in `src\AppInfo.cs` (`AppInfo.Version`) and in `installer\Setup.cs`. They must match, and `package.ps1` checks this.

- **Installer** (`installer\Setup.cs`): a WinForms app that embeds gzipped copies of `Konbini.exe`, `Uninstall.exe` and `THIRD-PARTY-NOTICES.txt`. The same source compiled with `/define:UNINSTALLER` produces `Uninstall.exe`.
  - **Per user:** it installs to `%LOCALAPPDATA%\Programs\Konbini` with no admin rights, and refuses Program Files and Windows. It adds a Start menu shortcut, an optional desktop shortcut, and an HKCU Apps & features entry.
  - **Switches:** `/S` for silent, `/D=<dir>` to choose the folder, and `/KEEPDATA` to keep settings and downloads when uninstalling.
  - **Uninstall:** it relaunches itself from `%TEMP%`, waits for the original process to exit, and deletes only the files it installed, and only from the registered install folder.
- **Portable zip:** contains `Konbini.exe` plus a `Konbini.portable` marker file. With that marker present, `AppInfo.PortableDataDir` puts settings and dependencies in `Konbini Data\` next to the exe. If that folder isn't writable, it falls back to the user profile.
- **Icon:** `installer\konbini.ico` is generated by `tools\make-icon.ps1` and used by both the app and the installer.
- **Signing:** builds aren't code-signed, so Windows SmartScreen warns the first time someone runs them.

## Language and API constraints

- **C# 5 only.** The in-box csc doesn't support `$""` strings, `?.`, `nameof`, expression-bodied members or `out var`.
- **WinRT async: never use `AsTask()`.** It needs the SDK-only `Windows.winmd`. Use the `WinRT.Await(...)` helpers (they block on the operation's `Completed` event) in `src/Convert/WinRT.cs`, plus `WinRT.Open`, `WinRT.Create`, `WinRT.ReadAll` and `WinRT.FromBytes`.
- File formats are hand-written; there are no libraries for them:
  - PDF writer: `PdfWriter.cs`
  - OpenXML docx/xlsx: `Documents.cs`, `Data.cs`
  - tar: `Archives.cs`
  - RTF: `Documents.cs`
  - JSON uses `JavaScriptSerializer`, and `Json.Parse` normalizes `object[]` to `ArrayList`.
  - Audio/video, PDF rendering, and WebP/HEIC decoding use Windows' own `MediaTranscoder`, `Windows.Data.Pdf` and `BitmapDecoder`. They depend on the codecs present on the machine, so report a missing codec with a clear message.

## Architecture

- **Shell:** `src/Program.cs`. `MainForm` has a sidebar (search box, owner-drawn nav `ListBox` grouped by `ITool.Category`, Settings button) and a content panel. Each page is built once with `Page(title, desc, body)`, cached in `views`, and recoloured in place when the theme changes. The page's `TableLayoutPanel` columns must use `Percent`, never the default AutoSize, so content can't push the page wider than the window.
- **Home page** (`src/Home.cs`): the startup page, and the first sidebar item. `HomeTool` is an `ITool`, so the busy guard, clear-on-leave and flicker-free switching all apply to it.
  - `HomeSuggest.Analyse` expands folders (up to 10,000 files) and classifies each file off the UI thread. It uses the same rules the tools' batches use: `ImageUtil.IsImage` for the image tools, `FileDetector` category Video for Video Compressor, and `Converters.TargetsFor` for File Converter.
  - **Simple tools run right on Home.** These are the ones with a `Quick` verb in `HomeSuggest.Info` (Compress, Upscale, Cut out).
    - `HomeView.RunQuick` borrows the tool's own `BatchImagePanel` through `MainForm.PanelFor`, which builds the page off-screen.
    - It uses the panel's `AddFiles`, `StartRun`, `RunProgress`, `RunCompleted`, `StopRun` and `LastOutputs`, so the tool's real processing and current settings are used.
    - Output goes next to the originals, using the tool's suffix.
    - The batch is cleared afterwards. `HomeView.Busy` blocks switching pages while it runs.
  - **Tools with real choices** (Resizer, Video Compressor, File Converter) show "Open tool". Every quick card also has a "More options" link. Both call `MainForm.OpenToolWith`, which opens the tool and passes the files to `AddFiles`.
  - **When adding a tool,** give it a reason and an order in `HomeSuggest.Info`, and a classification rule in `Analyse`.
  - Tested by `tests\home.ps1`.
- **Adding a tool:** implement `ITool` (`Name`, `Category`, `Description`, `CreateView()`), then register it in `ToolRegistry.All()` in `src/Core.cs`.
- **`BatchImagePanel`** (`src/Core.cs`) is the shared batch UI that every tool reuses. It provides:
  - a toolbar for add, add folder, remove and clear
  - a ListView that switches between list and thumbnail views, with up to 3 background thumbnail workers. Thumbnails are decoded by Windows Imaging at thumbnail size (`ImageUtil.WicThumb`), because GDI+ decoding holds a process-wide lock that freezes the UI's painting; GDI+ is only the fallback. Results are applied in batches (`FlushThumbs`), and image-list slots are reserved by doubling (`AddThumbImages`), because `ImageList.Images.Add` is O(n²) for big lists
  - Options and Output cards
  - a progress bar, and a run button that processes files in parallel (`Parallel.ForEach`, at most 4 workers)

  Tools customise it through these hooks:

  | Hook | What it does |
  |---|---|
  | `Accept` | Filters which files are accepted |
  | `Describe` | Fills column 2 |
  | `Noun`, `Suffix`, `WarnOnEmptySuffix` | Set wording and the output suffix |
  | `ItemsChanged` | Fires when the file list changes |
  | `UniformSize`, `SizesPending` | Report image sizes |
  | `BeforeRun` | Runs on the UI thread before processing; copy UI values into locals here, because workers must not touch controls |
  | `Process(src, baseOut) -> status string` | Converts one file |
  | `ReservePath(path)` | Returns a unique output path; always use it, and never overwrite the source |
  | `AutoSuffix` | Builds a suffix that describes the settings, e.g. `_upscaled-4x`, `_compressed-q75`, `_resized-fit-1920x1080`. Changing any option control refreshes it (`HookOptionChanges`). Typing in the box makes the suffix custom, and "Use automatic" switches back. Call `RefreshSuffix()` for changes the hook can't see, such as a colour picker |
| `Thumbnail`, `MaxWorkers`, `DetailsHeader` | Custom thumbnail (video frames), parallelism limit (video uses 1), and the column-2 header |

- **Converter pipeline** (`src/Convert/`):
  1. `FileDetector.Detect` identifies each file by its **content**, not its extension, and returns a `FileKind`.
  2. `Converters.TargetsFor(kind)` lists the formats that kind can become.
  3. `Converters.Convert(...)` sends the file to the matching handler: `ImageConv`, `PdfConv`, `MediaConv`, `DocReaders`/`DocWriters` (through the shared `Doc` model), `Tables`, `Subtitles` or `Archives`.

  To add a format, add detection in `FileKind.cs`, targets in `TargetsFor` and `ExtensionFor`, and a handler, then add a check to `tests\convert.ps1`.
- **Theming** (`src/Theme.cs`, `src/Settings.cs`):
  - `Theme.Apply(control)` walks the control tree and colours each control by its type and its `Tag` marker (`primary`, `muted`, `sidebar`, `toggle-on`, `toggle-off`). After building controls dynamically, call `Theme.Apply` on their container.
  - Dark mode also uses `SetWindowTheme` (`DarkMode_ItemsView`, `DarkMode_CFD`) and the DWM title-bar attributes.
  - Settings (theme mode and default batch view) are saved to a file. `Settings.Save()` calls `Theme.Refresh()` and raises `Changed`.
- **Custom controls:**
  - `NumBox` replaces `NumericUpDown`, which can't be themed dark. Its base constructor runs before `Inner` exists, so keep the null guards.
  - `Card`: a rounded panel with a small heading.
  - `SlimProgress`: a thin progress bar.

## Windows desktop requirements (from the user)

- Support DPI scaling at 100%, 125%, 150% and 200%, and resizing, maximizing and restoring the window, with **no clipping and no horizontal scrolling**. ListView columns refit in `FitColumns()` whenever the list or the window size changes.
- Keep the UI responsive. Do heavy work off the UI thread and marshal updates back with `BeginInvoke`.
- Dispose bitmaps, streams and WinRT objects promptly. Clean up temporary files, and leave no background work running after the app closes.
- Files and paths:
  - Handle Unicode names, names with spaces, locked files, read-only folders and low disk space, with clear error messages.
  - Never overwrite or delete user files without the user confirming.
  - Never execute input files.
  - Don't require administrator rights.

## External dependencies (from the user)

`src\Deps\` is the central dependency manager, and every external dependency must go through it.

| File | What it does |
|---|---|
| `Dependency.cs` | `Registry`: every dependency, with its source (embedded or downloaded), a hard-coded HTTPS URL, size, SHA-256 of both the download and the installed file, and for a ZIP the single entry to extract. It also holds the allowed hosts. |
| `DepManager.cs` | `Status`, `EnsureInstalled`, `InstallFromFile`, `Uninstall`, `Acquire`, `OpenEmbedded`, `SafeEntryPath`, and `RootOverride` as the test switch. |
| `DepPrompt.cs` | `Ensure(owner, dep)`: asks the user, shows the size, source and licence, then runs a cancellable progress dialog. `AutoAnswer` is the test switch. |

- **Small dependencies are embedded:** the upscaler model and the Lite background model. Large ones (ffmpeg, the full background model) are downloaded to `%LOCALAPPDATA%\Konbini\deps\<id>\` when a tool first needs them. A tool calls `DepPrompt.Ensure` in `BeforeRun`.
- **Settings → Components** lists every dependency and its status, with Download, Remove, Repair and From file… buttons. Status is computed off the UI thread.
- **Adding a dependency:** add a `Registry` entry with pinned hashes, measured from a file you downloaded and checked yourself. Embed it in `build.ps1` only if it's small. Call `DepPrompt.Ensure` before using it, then get it from `DepManager.Acquire` or `OpenEmbedded`.

Rules. All of these are enforced in code and covered by `tests\deps.ps1`, which uses a temp root; add `-offline` to skip the real download.
- **Detection:** "installed" means Konbini's own copy exists and its SHA-256 matches. The hash also pins the version.
  - Copies on `PATH` or anywhere else are never used.
  - A verified copy is never downloaded again or overwritten. Only a damaged copy is replaced, using `File.Replace`.
- **Downloads:**
  - HTTPS only, with TLS 1.2 forced, from fixed `Registry` URLs only. Both the start URL and the final URL after redirects must be on `Registry.AllowedHosts`.
  - URLs never come from the UI, files or servers.
  - Free disk space is checked first.
  - Data goes to `download.partial`, which is resumed with a `Range` request after an interruption or cancel.
  - The download is hash-checked before extraction, and the extracted file is checked again before it's moved into place. Nothing is executed before both checks pass.
- **Archives:** only the single named entry is extracted, to a fixed file name, and its size is checked. `SafeEntryPath` rejects `..`, absolute paths and drive letters. Never use `ExtractToDirectory`.
- **Concurrency:** an in-process lock plus a named `Mutex` (`Local\Konbini-dep-<id>`). Whoever acquires second re-checks the status and does nothing if the install already happened.
- **Cleanup:** temp files go in `finally`. Stale `*.tmp` files from a crash are deleted on the next attempt, but `download.partial` is kept so it can resume.
- **Using a dependency:** `Acquire` opens the file with `FileShare.Read` and verifies it while holding that handle. Keep the handle open while the dependency is in use, so the file can't be swapped or deleted.
- **Permissions:** per-user folder only. No admin rights, registry or PATH changes.
- **Errors:** `DependencyException` carries a friendly message, and `NeedsInstall` is set when a download would fix it.
- **Licensing:** ffmpeg is GPL. Because it's downloaded from its official release rather than shipped inside Konbini, it isn't part of the Konbini binary.
