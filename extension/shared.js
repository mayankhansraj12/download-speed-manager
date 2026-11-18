// Shared by the background worker, the extension pages and the content script.
// eslint-disable-next-line no-var
var DSM =
  globalThis.DSM ??
  (globalThis.DSM = (() => {
    // Same categories as the Windows app.
    const CATEGORIES = {
      Video: "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob",
      Audio: "mp3 flac wav aac m4a ogg opus wma aiff",
      Documents: "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp epub csv",
      Archives: "zip rar 7z tar gz tgz bz2 xz zst iso cab",
      Software: "exe msi msix msixbundle appx appxbundle apk dmg pkg deb rpm appimage",
      Images: "jpg jpeg png gif webp bmp tif tiff svg heic psd",
    };
    const byExtension = new Map();
    for (const [category, list] of Object.entries(CATEGORIES)) {
      for (const ext of list.split(" ")) byExtension.set(ext, category);
    }
    // Extensions of scripts that usually *serve* a file rather than being one.
    const PAGE_LIKE = new Set(["php", "asp", "aspx", "jsp", "cgi", "html", "htm", "do", "action", "pl"]);

    const DEFAULTS = {
      enabled: true, // catch downloads started in the browser
      videoButton: true, // show "Download video" over videos
      fileTypes: [CATEGORIES.Video, CATEGORIES.Audio, CATEGORIES.Archives, CATEGORIES.Software, "pdf epub bin img"].join(" "),
      minSizeKb: 0, // leave smaller downloads to the browser
      localPort: 9614, // the cross-platform (Node.js) app's local API
    };

    const extensionOf = (name) => {
      const match = /\.([a-z0-9]{1,10})$/i.exec(name ?? "");
      const ext = match ? match[1].toLowerCase() : null;
      return ext && !PAGE_LIKE.has(ext) ? ext : null;
    };

    const nameFromUrl = (url) => {
      try {
        const last = new URL(url).pathname.split("/").filter(Boolean).pop() ?? "";
        return decodeURIComponent(last);
      } catch {
        return "";
      }
    };

    return {
      CATEGORIES,
      DEFAULTS,
      isHttp: (url) => /^https?:\/\//i.test(url ?? ""),
      baseName: (path) => (path ? path.split(/[\\/]/).pop() || "" : ""),
      extensionOf,
      nameFromUrl,
      categoryOf: (name) => byExtension.get(extensionOf(name) ?? "") ?? "Other",
      fileTypes: (text) => new Set(String(text).toLowerCase().split(/[\s,;.]+/).filter(Boolean)),
      async settings() {
        return { ...DEFAULTS, ...(await chrome.storage.local.get(Object.keys(DEFAULTS))) };
      },
      formatBytes(bytes) {
        if (!(bytes > 0)) return "";
        const units = ["B", "KB", "MB", "GB", "TB"];
        let value = bytes;
        let unit = 0;
        while (value >= 1024 && unit < units.length - 1) {
          value /= 1024;
          unit++;
        }
        return `${value.toFixed(unit ? 1 : 0)} ${units[unit]}`;
      },
    };
  })());
