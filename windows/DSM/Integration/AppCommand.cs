namespace Dsm.Integration;

/// <summary>A request to the running app, from the command line, a second instance or the browser.</summary>
public sealed class AppCommand
{
    public string Type { get; set; } = "open"; // open | add | ping | exit | update
    public string? Url { get; set; }
    public string? FileName { get; set; }
    public string? Folder { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public bool Silent { get; set; }        // add without asking (/n)
    public bool StartNow { get; set; } = true; // false: add to the list paused (/a)
    public bool StartQueue { get; set; }    // resume everything waiting (/s)
    public bool Tray { get; set; }          // start hidden in the notification area

    public IReadOnlyDictionary<string, string> Headers()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(Cookies)) headers["cookie"] = Cookies;
        if (!string.IsNullOrEmpty(Referrer)) headers["referer"] = Referrer;
        if (!string.IsNullOrEmpty(UserAgent)) headers["user-agent"] = UserAgent;
        return headers;
    }
}

/// <summary>
/// Command-line parsing. Accepts the switches IDM documents for its command line
/// (/d URL, /p folder, /f file name, /n silent, /a add only, /s start queue) so existing
/// scripts work, plus --tray and a bare URL.
/// </summary>
public static class CommandLine
{
    public static AppCommand Parse(IReadOnlyList<string> args)
    {
        var command = new AppCommand();
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "/d" or "-d" or "--url": command.Url = Next(); break;
                case "/p" or "-p" or "--folder": command.Folder = Next(); break;
                case "/f" or "-f" or "--name": command.FileName = Next(); break;
                case "/n" or "--silent": command.Silent = true; break;
                case "/a" or "--add-only": command.StartNow = false; break;
                case "/s" or "--start-queue": command.StartQueue = true; break;
                case "/tray" or "--tray": command.Tray = true; break;
                case "/exit" or "--exit": command.Type = "exit"; break; // used by the installer before updating
                case "/update" or "--update": command.Type = "update"; break; // check now and install a newer version
                default:
                    if (Uri.TryCreate(args[i], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") command.Url = args[i];
                    break;
            }
        }
        if (command.Type is not ("exit" or "update") && !string.IsNullOrEmpty(command.Url)) command.Type = "add";
        return command;
    }
}
