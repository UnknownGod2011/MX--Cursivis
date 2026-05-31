using Cursivis.Companion.Models;
using Cursivis.Companion.Views;

namespace Cursivis.Companion.Services;

public sealed class LassoSelectionService
{
    public Task<LassoSelectionResult> CaptureSelectionAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<LassoSelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new LassoOverlayWindow();
        LassoSelectionResult? pendingResult = null;

        cancellationToken.Register(() =>
        {
            if (!tcs.Task.IsCompleted)
            {
                pendingResult = new LassoSelectionResult
                {
                    IsCanceled = true,
                    Region = default,
                    CancelPoint = null
                };
                window.Dispatcher.BeginInvoke(window.Close);
            }
        });

        window.SelectionCompleted += (_, result) =>
        {
            pendingResult = result;
        };

        window.Closed += (_, _) =>
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.TrySetResult(pendingResult ?? new LassoSelectionResult
                {
                    IsCanceled = true,
                    Region = default,
                    CancelPoint = null
                });
            }
        };

        window.Show();
        window.Focus();
        return tcs.Task;
    }
}
