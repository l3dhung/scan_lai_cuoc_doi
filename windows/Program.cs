using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LifeScanner;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

/// <summary>
/// The page runs at https://life-scanner.local/, a virtual origin answered from memory (a secure context, so the
/// camera works). GET / returns the embedded dist/index.html; POST /api/* goes to <see cref="Bridge"/>.
/// </summary>
sealed class MainForm : Form
{
    const string Origin = "https://life-scanner.local";
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Life-scanner");

    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly Bridge bridge;
    readonly string pageHtml;

    public MainForm()
    {
        Text = "Life-scanner";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 860);
        Size = new Size(Math.Min(1280, area.Width - 40), Math.Min(900, area.Height - 40));
        MinimumSize = new Size(420, 560);
        Controls.Add(web);

        bridge = new Bridge(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Life-scanner"), PickFolder, Reveal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        pageHtml = bridge.PageHtml(reader.ReadToEnd());

        Load += async (_, _) => await StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            // Profile next to the user's data, not next to the .exe (which may sit on a read-only USB stick or share).
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show(this,
                "Máy này chưa có Microsoft Edge WebView2 Runtime (Windows 10/11 thường có sẵn).\n\nBấm OK để mở trang tải về của Microsoft, cài xong thì mở lại Life-scanner.",
                "Life-scanner", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.OK) OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            Close();
            return;
        }

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequest;
        core.PermissionRequested += (_, e) =>
        {
            // Only our own page asks; the camera is what it needs.
            if (e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase) && e.PermissionKind == CoreWebView2PermissionKind.Camera)
                e.State = CoreWebView2PermissionState.Allow;
        };
        core.NavigationStarting += (_, e) =>
        {
            // Our page, plus blob:/data: URLs the page uses for downloads; anything else opens in the default browser.
            if (e.Uri.StartsWith(Origin + "/", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) OpenExternal(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) || core.DocumentTitle.StartsWith(Origin) ? "Life-scanner" : core.DocumentTitle;
        core.Navigate(Origin + "/");
    }

    void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = web.CoreWebView2.Environment;
        var uri = new Uri(e.Request.Uri);
        var path = uri.AbsolutePath;

        if (e.Request.Method == "GET" && (path == "/" || path == "/index.html"))
        {
            e.Response = Respond(env, 200, "OK", Encoding.UTF8.GetBytes(pageHtml), "text/html; charset=utf-8");
            return;
        }
        if (e.Request.Method != "POST" || !path.StartsWith("/api/", StringComparison.Ordinal))
        {
            e.Response = Respond(env, 404, "Not Found", Array.Empty<byte>(), "text/plain");
            return;
        }
        if (!string.Equals(e.Request.Headers.GetHeader("X-Scanner-Token"), bridge.Token, StringComparison.Ordinal))
        {
            e.Response = Json(env, 403, "{\"error\":\"forbidden\"}");
            return;
        }

        var body = ReadBody(e.Request.Content);
        if (body is null)
        {
            e.Response = Json(env, 413, "{\"error\":\"too large\"}");
            return;
        }
        var query = HttpUtility.ParseQueryString(uri.Query);
        var args = query.AllKeys.OfType<string>().ToDictionary(k => k, k => query[k] ?? "");
        var action = path["/api/".Length..];

        // Run after the event returns: choose-folder opens a modal dialog, which must not block the WebView2 callback.
        var deferral = e.GetDeferral();
        BeginInvoke(() =>
        {
            try
            {
                var (status, json) = bridge.Handle(action, args, body);
                e.Response = Json(env, status, json);
            }
            catch (Exception ex)
            {
                e.Response = Json(env, 500, System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
            }
            finally { deferral.Complete(); }
        });
    }

    static byte[]? ReadBody(Stream? content)
    {
        if (content is null) return Array.Empty<byte>();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > Bridge.MaxPdfBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    static CoreWebView2WebResourceResponse Json(CoreWebView2Environment env, int status, string json) =>
        Respond(env, status, status == 200 ? "OK" : "Error", Encoding.UTF8.GetBytes(json), "application/json; charset=utf-8");

    static CoreWebView2WebResourceResponse Respond(CoreWebView2Environment env, int status, string reason, byte[] data, string type) =>
        env.CreateWebResourceResponse(new MemoryStream(data), status, reason,
            $"Content-Type: {type}\r\nContent-Length: {data.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");

    string? PickFolder(string? start)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Chọn thư mục lưu PDF",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (start is not null) dialog.InitialDirectory = start;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    static void Reveal(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = false });

    static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { }
    }
}
