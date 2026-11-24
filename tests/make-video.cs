// Test helper: renders a short MP4 (solid colour + the given WAV as audio) using Windows' built-in editing APIs.
using System;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;

static class MakeVideo
{
    static void Spin(IAsyncInfo i) { while (i.Status == AsyncStatus.Started) Thread.Sleep(10); if (i.Status != AsyncStatus.Completed) throw i.ErrorCode; }
    static T A<T>(IAsyncOperation<T> o) { Spin(o); return o.GetResults(); }
    static T A<T, P>(IAsyncOperationWithProgress<T, P> o) { Spin(o); return o.GetResults(); }

    static int Main(string[] args)
    {
        string wav = Path.GetFullPath(args[0]), mp4 = Path.GetFullPath(args[1]);
        var comp = new MediaComposition();
        comp.Clips.Add(MediaClip.CreateFromColor(Windows.UI.Color.FromArgb(255, 40, 120, 220), TimeSpan.FromSeconds(2)));
        comp.BackgroundAudioTracks.Add(A(BackgroundAudioTrack.CreateFromFileAsync(A(StorageFile.GetFileFromPathAsync(wav)))));
        var folder = A(StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(mp4)));
        var file = A(folder.CreateFileAsync(Path.GetFileName(mp4), CreationCollisionOption.ReplaceExisting));
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);
        var result = A(comp.RenderToFileAsync(file, MediaTrimmingPreference.Fast, profile));
        Console.WriteLine("render: " + result);
        return result == Windows.Media.Transcoding.TranscodeFailureReason.None ? 0 : 1;
    }
}
