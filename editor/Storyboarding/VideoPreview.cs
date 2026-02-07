using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StorybrewEditor.Storyboarding
{
    public class VideoPreview : IDisposable
    {
        private static readonly string FfmpegExecutable = "ffmpeg";
        private static readonly string FfprobeExecutable = "ffprobe";

        private string videoPath;
        private readonly string cacheFolderPath;

        private double duration;
        private double frameRate;
        private int videoWidth;
        private int videoHeight;
        private bool probed;

        private readonly ConcurrentDictionary<int, string> frameCache = new ConcurrentDictionary<int, string>();

        private volatile bool isExtracting;
        private CancellationTokenSource extractionCts;

        public double OffsetMs { get; set; }

        public bool IsLoaded => probed && duration > 0;
        public bool HasVideo => !string.IsNullOrEmpty(videoPath) && File.Exists(videoPath);
        public double Duration => duration;
        public int Width => videoWidth;
        public int Height => videoHeight;

        public bool Enabled { get; set; } = true;

        public VideoPreview(string projectFolderPath)
        {
            cacheFolderPath = Path.Combine(projectFolderPath, ".sbrew", "videocache");
        }

        public void LoadVideo(string path, double startTimeMs)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Trace.WriteLine($"VideoPreview: Video file not found: {path}");
                return;
            }

            if (videoPath == path && OffsetMs == startTimeMs && probed)
                return;

            ClearCache();

            videoPath = path;
            OffsetMs = startTimeMs;
            probed = false;

            Directory.CreateDirectory(cacheFolderPath);
            Task.Run(() => ProbeVideo());
        }

        private void ProbeVideo()
        {
            if (!HasVideo || !IsFfmpegAvailable())
            {
                Trace.WriteLine("VideoPreview: FFmpeg not found on PATH. Video preview disabled.");
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = FfprobeExecutable,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate,duration -show_entries format=duration -of csv=p=0 \"{videoPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10000);
                    ParseProbeOutput(output);
                }

                if (duration <= 0)
                {
                    var startInfo2 = new ProcessStartInfo
                    {
                        FileName = FfprobeExecutable,
                        Arguments = $"-v error -show_entries format=duration -of csv=p=0 \"{videoPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using (var process = Process.Start(startInfo2))
                    {
                        var output = process.StandardOutput.ReadToEnd();
                        process.WaitForExit(10000);

                        if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var dur))
                            duration = dur;
                    }
                }

                if (frameRate <= 0)
                    frameRate = 30;

                probed = true;
                Trace.WriteLine($"VideoPreview: Probed {Path.GetFileName(videoPath)} - {videoWidth}x{videoHeight} @ {frameRate:F2}fps, {duration:F2}s");
            }
            catch (Exception e)
            {
                Trace.WriteLine($"VideoPreview: Probe failed: {e.Message}");
            }
        }

        private void ParseProbeOutput(string output)
        {
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Trim().Split(',');
                if (parts.Length >= 3)
                {
                    if (int.TryParse(parts[0], out var w) && w > 0)
                        videoWidth = w;
                    if (int.TryParse(parts[1], out var h) && h > 0)
                        videoHeight = h;

                    var fpsMatch = Regex.Match(parts[2], @"(\d+)/(\d+)");
                    if (fpsMatch.Success)
                    {
                        var num = double.Parse(fpsMatch.Groups[1].Value);
                        var den = double.Parse(fpsMatch.Groups[2].Value);
                        if (den > 0) frameRate = num / den;
                    }
                    else if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var fps))
                        frameRate = fps;

                    if (parts.Length >= 4 && double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 0)
                        duration = dur;
                }
                else if (parts.Length == 1 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var singleDur) && singleDur > 0)
                {
                    duration = singleDur;
                }
            }
        }

        public int TimeToFrameIndex(double timeSeconds)
        {
            if (frameRate <= 0) return 0;

            var adjustedTime = timeSeconds - (OffsetMs / 1000.0);
            if (adjustedTime < 0) return -1;
            if (adjustedTime >= duration) return -1;

            return (int)(adjustedTime * frameRate);
        }

        public string GetFramePath(double timeSeconds)
        {
            if (!IsLoaded || !Enabled) return null;

            var frameIndex = TimeToFrameIndex(timeSeconds);
            if (frameIndex < 0) return null;

            if (frameCache.TryGetValue(frameIndex, out var framePath) && File.Exists(framePath))
                return framePath;

            for (var offset = 1; offset <= 30; offset++)
            {
                if (frameCache.TryGetValue(frameIndex - offset, out var nearPath) && File.Exists(nearPath))
                {
                    RequestFrameExtraction(timeSeconds, frameIndex);
                    return nearPath;
                }
            }

            RequestFrameExtraction(timeSeconds, frameIndex);
            return null;
        }

        private void RequestFrameExtraction(double timeSeconds, int frameIndex)
        {
            if (isExtracting) return;

            extractionCts?.Cancel();
            extractionCts = new CancellationTokenSource();
            var token = extractionCts.Token;

            isExtracting = true;
            Task.Run(() =>
            {
                try
                {
                    var adjustedTime = timeSeconds - (OffsetMs / 1000.0);
                    var startTime = TimeSpan.FromSeconds(Math.Max(0, adjustedTime));
                    var chunkSeconds = (int)Math.Ceiling(duration);
                    var chunkFrames = (int)(frameRate * chunkSeconds);

                    var pattern = Path.Combine(cacheFolderPath, $"chunk_{frameIndex:D6}_%06d.png");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = FfmpegExecutable,
                        Arguments = $"-ss {startTime:hh\\:mm\\:ss\\.fff} -i \"{videoPath}\" -t {chunkSeconds} -vf \"fps={frameRate},scale=1280:720\" -pix_fmt bgra -y \"{pattern}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        process.StandardError.ReadToEnd();
                        process.WaitForExit(30000);
                    }

                    for (var i = 0; i < chunkFrames; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var file = Path.Combine(cacheFolderPath, $"chunk_{frameIndex:D6}_{(i + 1):D6}.png");
                        if (File.Exists(file))
                            frameCache[frameIndex + i] = file;
                    }

                    Trace.WriteLine($"VideoPreview: Extracted chunk at frame {frameIndex}, cached {frameCache.Count} frames");
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Trace.WriteLine($"VideoPreview: Chunk extraction error: {e.Message}");
                }
                finally
                {
                    isExtracting = false;
                }
            }, token);
        }

        public void ClearCache()
        {
            extractionCts?.Cancel();
            frameCache.Clear();

            try
            {
                if (Directory.Exists(cacheFolderPath))
                {
                    foreach (var file in Directory.GetFiles(cacheFolderPath, "*.png"))
                    {
                        try { File.Delete(file); }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"VideoPreview: Cache cleanup error: {e.Message}");
            }
        }

        public static bool IsFfmpegAvailable()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = FfmpegExecutable,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit(3000);
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        #region IDisposable

        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            extractionCts?.Cancel();
            extractionCts?.Dispose();

            try
            {
                if (Directory.Exists(cacheFolderPath))
                    Directory.Delete(cacheFolderPath, true);
            }
            catch (Exception e)
            {
                Trace.WriteLine($"VideoPreview: Cleanup error: {e.Message}");
            }
        }

        #endregion
    }
}