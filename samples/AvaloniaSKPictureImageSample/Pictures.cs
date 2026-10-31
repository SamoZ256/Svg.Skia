using SkiaSharp;

namespace AvaloniaSKPictureImageSample;

// The generated classes keep no picture; keeping these for the life of the app is this sample's choice.
internal static class Pictures
{
    public static SKPicture Tiger { get; } = AvaloniaSKPictureImageSample.Tiger.Record();

    public static SKPicture Logo { get; } = AvaloniaSKPictureImageSample.Logo.Record();

    public static SKPicture Camera { get; } = AvaloniaSKPictureImageSample.Camera.Record();

    public static SKPicture Pattern { get; } = AvaloniaSKPictureImageSample.Pattern.Record();
}
