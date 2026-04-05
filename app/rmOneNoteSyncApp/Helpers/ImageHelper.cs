using System;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace rmOneNoteSyncApp.Helpers;

public static class ImageHelper
{
    /// <summary>
    /// Load a <see cref="Bitmap"/> from an Avalonia embedded resource URI
    /// (e.g. "avares://rmOneNoteSyncApp/Assets/rmpp.png").
    /// Returns null and swallows the exception if the asset cannot be found.
    /// </summary>
    public static Bitmap? LoadFromResource(Uri resourceUri)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(resourceUri));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Convenience overload accepting a plain string URI.</summary>
    public static Bitmap? LoadFromResource(string resourceUri) =>
        LoadFromResource(new Uri(resourceUri));
}
