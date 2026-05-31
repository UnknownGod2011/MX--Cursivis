namespace Cursivis.Companion.Models;

public sealed class SelectionCaptureResult
{
    public string? Text { get; init; }

    public string? ImageBase64 { get; init; }

    public string? ImageMimeType { get; init; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool HasImage => !string.IsNullOrWhiteSpace(ImageBase64) && !string.IsNullOrWhiteSpace(ImageMimeType);

    public bool HasAnyContent => HasText || HasImage;
}
