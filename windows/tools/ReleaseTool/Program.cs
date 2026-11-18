using System.Security.Cryptography;
using System.Text.Json;
using Dsm.Updates;

// Release helper for the update pipeline (used by .github/workflows/release.yml and by you locally):
//   keygen    --private <key.pem> --public <public-key.txt>      create the update-signing key pair
//   manifest  --version 1.2.3 --setup <Setup.exe> --repository owner/repo [--base-url url] [--minimum 1.0.0] --out latest.json
//   sign      --file latest.json [--key-file key.pem]  (or env UPDATE_SIGNING_KEY) [--out latest.json.sig]
//   verify    --file latest.json --signature latest.json.sig --public-key-file <public-key.txt>
try
{
    var options = Options(args.Skip(1).ToArray());
    return args.FirstOrDefault() switch
    {
        "keygen" => KeyGen(options),
        "manifest" => Manifest(options),
        "sign" => Sign(options),
        "verify" => Verify(options),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: ReleaseTool keygen|manifest|sign|verify [--option value ...] (see Program.cs)");
    return 2;
}

static Dictionary<string, string> Options(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--")) throw new ArgumentException($"Expected an --option, got \"{args[i]}\".");
        options[args[i][2..]] = args[i + 1];
    }
    return options;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new ArgumentException($"--{name} is required.");

static int KeyGen(Dictionary<string, string> options)
{
    var privatePath = Path.GetFullPath(Required(options, "private"));
    var publicPath = Path.GetFullPath(Required(options, "public"));
    if (File.Exists(privatePath)) throw new IOException($"{privatePath} already exists; refusing to overwrite a signing key.");

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem() + "\n");
    var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    File.WriteAllText(publicPath,
        $"# Update-signing public keys (ECDSA P-256, base64 SubjectPublicKeyInfo), one per line.\n" +
        $"# The app installs an update only if latest.json is signed by the matching private key.\n" +
        $"# Added {DateTime.UtcNow:yyyy-MM-dd}.\n{publicKey}\n");
    Console.WriteLine($"private key: {privatePath}  (keep it secret: add it to the UPDATE_SIGNING_KEY repository secret and back it up)");
    Console.WriteLine($"public key:  {publicPath}  (commit it; the app embeds it)");
    return 0;
}

static int Manifest(Dictionary<string, string> options)
{
    var version = Required(options, "version").TrimStart('v');
    if (!UpdateManifest.TryParseVersion(version, out _)) throw new ArgumentException($"\"{version}\" is not a release version like 1.2.3.");
    var setup = Required(options, "setup");
    options.TryGetValue("repository", out var repository);
    var baseUrl = options.GetValueOrDefault("base-url")
        ?? (repository is { Length: > 0 } ? $"https://github.com/{repository}/releases/download/v{version}" : throw new ArgumentException("--repository or --base-url is required."));
    var bytes = File.ReadAllBytes(setup);
    var manifest = new UpdateManifest
    {
        Version = version,
        PublishedAt = DateTimeOffset.UtcNow,
        NotesUrl = options.GetValueOrDefault("notes-url") ?? (repository is { Length: > 0 } ? $"https://github.com/{repository}/releases/tag/v{version}" : null),
        MinimumVersion = options.GetValueOrDefault("minimum") is { Length: > 0 } minimum
            ? (UpdateManifest.TryParseVersion(minimum, out _) ? minimum.TrimStart('v') : throw new ArgumentException($"--minimum \"{minimum}\" is not a version like 1.2.3."))
            : null,
        Setup = new UpdateFile
        {
            Url = $"{baseUrl.TrimEnd('/')}/{Path.GetFileName(setup)}",
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Size = bytes.LongLength,
        },
    };
    var output = Required(options, "out");
    File.WriteAllText(output, JsonSerializer.Serialize(manifest, UpdateManifest.JsonOptions) + "\n");
    Console.WriteLine($"{output}: version {manifest.Version}, {manifest.Setup.Size:N0} bytes, sha256 {manifest.Setup.Sha256[..16]}…");
    return 0;
}

static int Sign(Dictionary<string, string> options)
{
    var file = Required(options, "file");
    var pem = options.TryGetValue("key-file", out var keyFile) ? File.ReadAllText(keyFile) : Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY");
    if (string.IsNullOrWhiteSpace(pem)) throw new ArgumentException("No signing key: pass --key-file or set the UPDATE_SIGNING_KEY environment variable.");
    using var key = ECDsa.Create();
    key.ImportFromPem(pem);
    var signature = key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256);
    var output = options.GetValueOrDefault("out") ?? file + ".sig";
    File.WriteAllText(output, Convert.ToBase64String(signature) + "\n");
    Console.WriteLine($"signed {file} -> {output}");
    return 0;
}

static int Verify(Dictionary<string, string> options)
{
    var data = File.ReadAllBytes(Required(options, "file"));
    var signature = Convert.FromBase64String(File.ReadAllText(Required(options, "signature")).Trim());
    var keys = File.ReadAllLines(Required(options, "public-key-file")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
    foreach (var publicKey in keys)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (key.VerifyData(data, signature, HashAlgorithmName.SHA256))
        {
            Console.WriteLine("signature OK: installed apps will accept this update");
            return 0;
        }
    }
    Console.Error.WriteLine("signature does NOT match any public key the app trusts; installed apps would ignore this update");
    return 1;
}
