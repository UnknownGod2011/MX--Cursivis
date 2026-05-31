using Cursivis.Companion.Infrastructure;
using Cursivis.Companion.Models;
using System.Diagnostics;

namespace Cursivis.Companion.Services;

public sealed class SelectionDetector
{
    private readonly ClipboardService _clipboardService;

    public SelectionDetector(ClipboardService clipboardService)
    {
        _clipboardService = clipboardService;
    }

    public async Task<SelectionCaptureResult> CaptureSelectionAsync(IntPtr targetHandle, CancellationToken cancellationToken)
    {
        var backup = await _clipboardService.CaptureAsync();
        var sentinel = $"__CURSIVIS_SENTINEL_{Guid.NewGuid()}__";
        string? selectedText = null;
        string? selectedImageBase64 = null;
        string? selectedImageMimeType = null;
        var sentinelSequence = 0u;
        var sawFreshClipboardWrite = false;

        try
        {
            await _clipboardService.SetTextAsync(sentinel);
            sentinelSequence = NativeMethods.GetCurrentClipboardSequenceNumber();

            if (targetHandle != IntPtr.Zero)
            {
                NativeMethods.BringToFront(targetHandle);
                await Task.Delay(110, cancellationToken);

                if (NativeMethods.GetActiveWindowHandle() != targetHandle)
                {
                    NativeMethods.BringToFront(targetHandle);
                    await Task.Delay(70, cancellationToken);
                }
            }

            NativeMethods.SendCtrlC();
            var resendThresholdsMs = new Queue<long>([260, 520]);

            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 950)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await Task.Delay(25, cancellationToken);

                if (!sawFreshClipboardWrite)
                {
                    var currentSequence = NativeMethods.GetCurrentClipboardSequenceNumber();
                    sawFreshClipboardWrite = currentSequence != 0 && currentSequence != sentinelSequence;
                }

                if (!sawFreshClipboardWrite)
                {
                    if (resendThresholdsMs.Count > 0 &&
                        timer.ElapsedMilliseconds >= resendThresholdsMs.Peek())
                    {
                        NativeMethods.SendCtrlC();
                        resendThresholdsMs.Dequeue();
                    }

                    continue;
                }

                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    var clipboardText = await _clipboardService.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(clipboardText) &&
                        !string.Equals(clipboardText, sentinel, StringComparison.Ordinal))
                    {
                        selectedText = clipboardText;
                    }
                }

                if (string.IsNullOrWhiteSpace(selectedImageBase64))
                {
                    var clipboardImage = await _clipboardService.GetImageAsync();
                    if (!string.IsNullOrWhiteSpace(clipboardImage.ImageBase64) &&
                        !string.IsNullOrWhiteSpace(clipboardImage.MimeType))
                    {
                        selectedImageBase64 = clipboardImage.ImageBase64;
                        selectedImageMimeType = clipboardImage.MimeType;
                    }
                }

                if (resendThresholdsMs.Count > 0 &&
                    string.IsNullOrWhiteSpace(selectedText) &&
                    string.IsNullOrWhiteSpace(selectedImageBase64) &&
                    timer.ElapsedMilliseconds >= resendThresholdsMs.Peek())
                {
                    NativeMethods.SendCtrlC();
                    resendThresholdsMs.Dequeue();
                }

                if (!string.IsNullOrWhiteSpace(selectedText) && !string.IsNullOrWhiteSpace(selectedImageBase64))
                {
                    break;
                }

                if (timer.ElapsedMilliseconds >= 220 &&
                    (!string.IsNullOrWhiteSpace(selectedText) || !string.IsNullOrWhiteSpace(selectedImageBase64)))
                {
                    break;
                }
            }
        }
        finally
        {
            await _clipboardService.RestoreAsync(backup, sentinel);
        }

        return new SelectionCaptureResult
        {
            Text = string.IsNullOrWhiteSpace(selectedText) ? null : selectedText,
            ImageBase64 = string.IsNullOrWhiteSpace(selectedImageBase64) ? null : selectedImageBase64,
            ImageMimeType = string.IsNullOrWhiteSpace(selectedImageMimeType) ? null : selectedImageMimeType
        };
    }

    public async Task<string?> TryCaptureSelectedTextAsync(IntPtr targetHandle, CancellationToken cancellationToken)
    {
        var selection = await CaptureSelectionAsync(targetHandle, cancellationToken);
        return selection.Text;
    }
}
