using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LifeScanner;

/// <summary>
/// The save bridge from macos/serve.py, for Windows: the page calls POST /api/* with its per-launch token.
/// Files are written only into folders the user picked in the native dialog (or one procedure-type subfolder
/// level inside them), only as new .pdf files (never overwriting), and only with PDF content.
/// UI work (folder dialog, Explorer) is passed in so this class has no WinForms dependency.
/// </summary>
public sealed class Bridge
{
    public const long MaxPdfBytes = 300L * 1024 * 1024;

    readonly string configPath;
    readonly object gate = new();
    readonly HashSet<string> savedFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<string?, string?> pickFolder;
    readonly Action<string> reveal;

    public string Token { get; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public Bridge(string configDir, Func<string?, string?> pickFolder, Action<string> reveal)
    {
        configPath = Path.Combine(configDir, "bridge.json");
        this.pickFolder = pickFolder;
        this.reveal = reveal;
    }

    sealed record Config(List<string> Folders, string? Last);

    Config Load()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8));
            var folders = node?["folders"]?.AsArray().Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? new();
            var last = node?["last"]?.GetValue<string>();
            return new Config(folders, last);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return new Config(new(), null);
        }
    }

    void Save(Config cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var tmp = configPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { folders = cfg.Folders, last = cfg.Last }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(tmp, configPath, true);
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    static object FolderInfo(string path) => new { path, name = Path.GetFileName(path) is { Length: > 0 } n ? n : path };

    string? AllowedFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full;
        try { full = Normalize(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var cfg = Load();
        return cfg.Folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase)) && Directory.Exists(full) ? full : null;
    }

    /// <summary>Handles one bridge call; returns the HTTP status and the JSON body.</summary>
    public (int Status, string Json) Handle(string action, IReadOnlyDictionary<string, string> query, byte[] body)
    {
        try
        {
            switch (action)
            {
                case "state":
                    {
                        var last = AllowedFolder(Load().Last);
                        return Ok(new { folder = last is null ? null : FolderInfo(last) });
                    }
                case "choose-folder":
                    {
                        var cfg = Load();
                        var start = cfg.Last is not null && Directory.Exists(cfg.Last) ? cfg.Last : null;
                        var picked = pickFolder(start);
                        if (picked is null || !Directory.Exists(picked)) return Ok(new { folder = (object?)null });
                        var full = Normalize(picked);
                        lock (gate)
                        {
                            cfg = Load();
                            if (!cfg.Folders.Any(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase))) cfg.Folders.Add(full);
                            Save(cfg with { Last = full });
                        }
                        return Ok(new { folder = FolderInfo(full) });
                    }
                case "clear-folder":
                    lock (gate) Save(Load() with { Last = null });
                    return Ok(new { ok = true });
                case "save":
                    {
                        var folder = AllowedFolder(query.GetValueOrDefault("folder"));
                        if (folder is null) return Err(400, "folder not allowed");
                        if (body.Length < 5 || Encoding.ASCII.GetString(body, 0, 5) != "%PDF-") return Err(400, "not a pdf");
                        folder = ProcedureFolder(folder, query.GetValueOrDefault("sub"));
                        string target;
                        if (query.TryGetValue("auto", out var auto))
                        {
                            var name = SafePdfName(auto);
                            var stem = name[..^4];
                            target = WriteSequenced(folder, stem.Length > 100 ? stem[..100] : stem.Length > 0 ? stem : "document", body);
                        }
                        else target = WriteUnique(folder, SafePdfName(query.GetValueOrDefault("name")), body);
                        lock (gate) savedFiles.Add(target);
                        return Ok(new { path = target, name = Path.GetFileName(target) });
                    }
                case "reveal":
                    {
                        var target = query.GetValueOrDefault("path");
                        bool known; lock (gate) known = target is not null && savedFiles.Contains(target);
                        if (!known || !File.Exists(target)) return Err(404, "unknown file");
                        reveal(target!);
                        return Ok(new { ok = true });
                    }
                default:
                    return Err(404, "not found");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Err(500, e.Message);
        }
    }

    static (int, string) Ok(object value) => (200, JsonSerializer.Serialize(value));
    static (int, string) Err(int status, string message) => (status, JsonSerializer.Serialize(new { error = message }));

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    static string Clean(string? value) => new string((value ?? "").Normalize(NormalizationForm.FormC)
        .Select(c => "/\\:*?\"<>|".Contains(c) || c < 32 ? '_' : c).ToArray());

    // Windows also refuses device names (CON, NUL, ...) and names ending in a dot or space.
    static string AvoidReserved(string stem) => Reserved.Contains(stem.Split('.')[0].TrimEnd()) ? "_" + stem : stem;

    public static string SafePdfName(string? name)
    {
        var s = Clean(Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last())).Trim().TrimStart('.').TrimEnd('.', ' ');
        if (!s.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) s += ".pdf";
        var stem = s[..^4].TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "document";
        stem = AvoidReserved(stem);
        if (stem.Length > 116) stem = stem[..116];
        return stem + ".pdf";
    }

    public static string SafeDirName(string? name)
    {
        var s = Clean(name).Trim('.', ' ', '\t');
        if (s.Length > 80) s = s[..80].TrimEnd('.', ' ');
        return s.Length == 0 ? "" : AvoidReserved(s);
    }

    /// <summary>One subfolder level inside an allowed folder: reuse a case-insensitive match or create it.</summary>
    public static string ProcedureFolder(string folder, string? sub)
    {
        var name = SafeDirName(sub);
        if (name.Length == 0) return folder;
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var info = new DirectoryInfo(dir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (string.Equals(info.Name.Normalize(NormalizationForm.FormC), name, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        var target = Path.Combine(folder, name);
        Directory.CreateDirectory(target);
        return target;
    }

    /// <summary>Write data to target only if it does not exist yet; false when the name is taken.</summary>
    static bool CreateNew(string target, byte[] data)
    {
        FileStream stream;
        try { stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) when (File.Exists(target)) { return false; }
        try { using (stream) stream.Write(data); }
        catch { try { File.Delete(target); } catch (IOException) { } throw; }
        return true;
    }

    public static string WriteUnique(string folder, string name, byte[] data)
    {
        var stem = name[..^4];
        for (var i = 1; i < 1000; i++)
        {
            var target = Path.Combine(folder, i == 1 ? name : $"{stem} ({i}).pdf");
            if (CreateNew(target, data)) return target;
        }
        throw new IOException("no free file name");
    }

    /// <summary>Save as &lt;base&gt;_&lt;n&gt;.pdf, continuing after the highest number already in the folder.</summary>
    public static string WriteSequenced(string folder, string stem, byte[] data)
    {
        var pattern = new Regex("^" + Regex.Escape(stem.Normalize(NormalizationForm.FormC)) + @"_(\d+)\.pdf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var highest = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.pdf"))
        {
            var m = pattern.Match(Path.GetFileName(file).Normalize(NormalizationForm.FormC));
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) highest = Math.Max(highest, n);
        }
        for (var n = highest + 1; n < highest + 1000; n++)
        {
            var target = Path.Combine(folder, $"{stem}_{n}.pdf");
            if (CreateNew(target, data)) return target;
        }
        throw new IOException("no free file name");
    }

    /// <summary>The page as served: same-origin fetches allowed and the bridge token injected, like serve.py.</summary>
    public string PageHtml(string html)
    {
        const string cspOld = "connect-src 'none'";
        if (Count(html, cspOld) != 1 || Count(html, "<head>") != 1) return html;
        html = html.Replace(cspOld, "connect-src 'self'");
        html = html.Replace("<head>", $"<head>\n  <meta name=\"scanner-bridge\" content=\"{Token}\">");
        // The only macOS-specific wording in the page.
        return html.Replace("Mở trong Finder", "Mở trong Explorer").Replace("Show in Finder", "Show in Explorer");
    }

    static int Count(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
