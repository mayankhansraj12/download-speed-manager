using Dsm.Engine;

namespace Dsm.Integration;

/// <summary>
/// Decides whether text that just appeared on the clipboard is a new download link worth offering.
///
/// Dictation tools (Wispr Flow and similar), clipboard managers and remote-desktop clients put text
/// on the clipboard briefly, paste it, and then restore what was there before. To a clipboard monitor
/// that restore looks like a fresh copy of the old link. So a link is offered only:
///  - the first time it appears on the clipboard while the app is running,
///  - if it was not already on the clipboard when the app started,
///  - if it is not already in the download list, and
///  - if the app that put it there did not mark it as private to clipboard monitors.
/// </summary>
public sealed class ClipboardLinkFilter
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>True when a link is already in the download list (it was downloaded or is downloading).</summary>
    public Func<string, bool>? AlreadyInList { get; set; }

    /// <summary>Remember text without offering it: clipboard content at startup, or text the app copied itself.</summary>
    public void MarkSeen(string? text)
    {
        if (AsLink(text) is { } url) _seen.Add(url);
    }

    /// <summary>The link to offer, or null. Every link is remembered, even when it isn't offered.</summary>
    public string? Offer(string? text, bool excludedByOwner = false, bool enabled = true)
    {
        if (AsLink(text) is not { } url) return null;
        var firstTime = _seen.Add(url);
        if (!firstTime || excludedByOwner || !enabled) return null;
        if (AlreadyInList?.Invoke(url) == true) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && Categories.IsDownloadableLink(uri) ? url : null;
    }

    /// <summary>A single http(s) URL (no surrounding text), normalised; otherwise null.</summary>
    private static string? AsLink(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace)) return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }
}
