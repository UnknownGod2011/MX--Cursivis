#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.IO;
using OpenCvSharp;

public sealed class WebcamCaptureResult
{
    public bool Success { get; init; }
    public string Path { get; init; } = "";
    public string Message { get; init; } = "";
    public int CameraIndex { get; init; } = -1;
    public string Backend { get; init; } = "";
}

public sealed class WebcamCaptureService
{
    private static readonly (VideoCaptureAPIs Api, string Name)[] Backends =
    [
        (VideoCaptureAPIs.DSHOW, "DirectShow"),
        (VideoCaptureAPIs.MSMF, "Media Foundation"),
        (VideoCaptureAPIs.ANY, "Windows default"),
    ];

    public async Task<WebcamCaptureResult> CapturePhotoAsync(CancellationToken token)
    {
        LiveModeState.SetUi(LiveModeVoicePhase.Executing, "Opening camera", "Waiting for the webcam...");
        return await Task.Run(() => CapturePhoto(token), token);
    }

    private static WebcamCaptureResult CapturePhoto(CancellationToken token)
    {
        Exception lastError = null;
        for (var cameraIndex = 0; cameraIndex < 4; cameraIndex++)
        {
            foreach (var backend in Backends)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var capture = new VideoCapture();
                    if (!capture.Open(cameraIndex, backend.Api) || !capture.IsOpened())
                        continue;

                    capture.Set(VideoCaptureProperties.BufferSize, 1);
                    capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                    capture.Set(VideoCaptureProperties.FrameHeight, 720);
                    LiveModeState.SetUi(LiveModeVoicePhase.Executing, "Camera ready", "Capturing one approved photo...");

                    using var frame = new Mat();
                    var deadline = DateTime.UtcNow.AddSeconds(6);
                    while (DateTime.UtcNow < deadline)
                    {
                        token.ThrowIfCancellationRequested();
                        if (capture.Read(frame) && !frame.Empty() && frame.Width >= 160 && frame.Height >= 120)
                        {
                            var folder = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                                "Cursivis",
                                "Camera");
                            Directory.CreateDirectory(folder);
                            var path = UniquePhotoPath(folder);
                            if (!Cv2.ImWrite(path, frame))
                                throw new IOException("The webcam frame could not be saved.");

                            LiveModeState.SetUi(LiveModeVoicePhase.Done, "Photo saved", path);
                            return new WebcamCaptureResult
                            {
                                Success = true,
                                Path = path,
                                CameraIndex = cameraIndex,
                                Backend = backend.Name,
                                Message = $"Photo saved to {path}.",
                            };
                        }

                        Thread.Sleep(90);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    LiveModeLog.Warning(ex, $"Webcam capture failed for camera {cameraIndex} using {backend.Name}");
                }
            }
        }

        var detail = lastError?.Message;
        return new WebcamCaptureResult
        {
            Success = false,
            Message = string.IsNullOrWhiteSpace(detail)
                ? "No usable webcam was found. Check Windows Settings > Privacy & security > Camera and enable desktop app access."
                : $"The webcam could not capture a photo: {detail}. Check Windows camera privacy settings and close other apps using the camera.",
        };
    }

    private static string UniquePhotoPath(string folder)
    {
        var stem = $"photo-{DateTime.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(folder, stem + ".jpg");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(folder, $"{stem}-{suffix}.jpg");
        return path;
    }
}

