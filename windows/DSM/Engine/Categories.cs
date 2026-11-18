namespace Dsm.Engine;

/// <summary>File categories decide the save folder and the main window's category tree.</summary>
public static class Categories
{
    public const string Other = "Other";
    public static readonly string[] All = ["Video", "Audio", "Documents", "Archives", "Software", "Images", Other];

    private static readonly Dictionary<string, string> ByExtension = Build();

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string extensions)
        {
            foreach (var ext in extensions.Split(' ')) map["." + ext] = category;
        }
        Add("Video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob");
        Add("Audio", "mp3 flac wav aac m4a ogg opus wma aiff");
        Add("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp epub csv");
        Add("Archives", "zip rar 7z tar gz tgz bz2 xz zst iso cab");
        Add("Software", "exe msi msix msixbundle appx appxbundle apk dmg pkg deb rpm appimage");
        Add("Images", "jpg jpeg png gif webp bmp tif tiff svg heic psd");
        return map;
    }

    public static string For(string? fileName) =>
        fileName is not null && ByExtension.TryGetValue(Path.GetExtension(fileName), out var category) ? category : Other;

    /// <summary>Links worth offering to download when they appear on the clipboard.</summary>
    public static bool IsDownloadableLink(Uri uri) => ByExtension.ContainsKey(Path.GetExtension(uri.AbsolutePath));
}
