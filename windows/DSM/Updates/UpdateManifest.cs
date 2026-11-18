using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dsm.Updates;

/// <summary>
/// latest.json, published with every release next to the installer. The app trusts it only when
/// latest.json.sig verifies against the public key built into the app. Shared with the release tool.
/// </summary>
public sealed partial class UpdateManifest
{
    public string Version { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
    public string? NotesUrl { get; set; }

    /// <summary>Copies older than this should update right away (for example after a security fix). Optional.</summary>
    public string? MinimumVersion { get; set; }

    public UpdateFile Setup { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex SemVer();

    /// <summary>"1.2.3" or "v1.2.3" as a comparable version (release versions only, no pre-release tags).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var match = SemVer().Match(text ?? "");
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out var major)
            || !int.TryParse(match.Groups[2].Value, out var minor)
            || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        version = new Version(major, minor, patch);
        return true;
    }
}

public sealed class UpdateFile
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}
