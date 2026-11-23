using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.WinForms;

namespace PinterestDownload;

record Pin(string Id, string Url, string Title, string Image);
static class Program
{
    [STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}

sealed class MainForm : Form
{
    const string ExtensionOrigin = "chrome-extension://alenpleailakepgbibnkkmhcmkoijmch";
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly HttpListener listener = new();
    readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinterestDownload", "settings.json");
    readonly Dictionary<string, Pin> pins = new();
    string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    bool downloading;

    public MainForm()
    {
        Text = "Pinterest Download"; Width = 1200; Height = 800; MinimumSize = new(700, 500);
        Controls.Add(web); Shown += async (_, _) => await Start();
        FormClosed += (_, _) => { listener.Close(); client.Dispose(); };
    }

    async Task Start()
    {
        try { if (File.Exists(settingsFile)) folder = JsonSerializer.Deserialize<string>(File.ReadAllText(settingsFile)) ?? folder; } catch { }
        try {
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("about:blank", StringComparison.Ordinal)) e.Cancel = true; };
            web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            web.CoreWebView2.WebMessageReceived += async (_, e) => await Message(e.WebMessageAsJson);
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("PinterestDownload.ui.html")!;
            using var reader = new StreamReader(stream);
            web.NavigateToString(reader.ReadToEnd());
            listener.Prefixes.Add("http://127.0.0.1:17849/"); listener.Start();
            _ = Listen();
        } catch (Exception ex) { MessageBox.Show("Не удалось запустить приложение. Установите Microsoft Edge WebView2 Runtime и убедитесь, что приложение уже не запущено.\n\n" + ex.Message, "Pinterest Download", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
    }

    async Task Listen()
    {
        while (listener.IsListening) {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); } catch { break; }
            _ = Handle(context);
        }
    }

    async Task Handle(HttpListenerContext context)
    {
        var req = context.Request; var res = context.Response;
        try {
            if (req.Headers["Origin"] != ExtensionOrigin || req.Url?.AbsolutePath != "/pins") { res.StatusCode = 403; return; }
            res.Headers.Add("Access-Control-Allow-Origin", ExtensionOrigin);
            res.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; return; }
            if (req.HttpMethod != "POST" || req.ContentLength64 < 0 || req.ContentLength64 > 1_000_000) { res.StatusCode = 400; return; }
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var incoming = JsonSerializer.Deserialize<List<Pin>>(await reader.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var valid = incoming.Where(p => p.Id is not null && Regex.IsMatch(p.Id, @"^\d{1,30}$") && IsPin(p.Url, p.Id)).Take(500)
                .Select(p => p with { Title = (p.Title ?? "Пин")[..Math.Min((p.Title ?? "Пин").Length, 300)], Image = IsImage(p.Image) ? p.Image : "" }).ToList();
            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { pins.Clear(); foreach (var pin in valid) pins[pin.Id] = pin; Send(new { type = "pins", pins = pins.Values }); }));
            res.StatusCode = 204;
        } catch { res.StatusCode = 400; } finally { res.Close(); }
    }

    static bool IsPin(string? value, string id) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinterest.com" || uri.Host.EndsWith(".pinterest.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.TrimEnd('/') == "/pin/" + id;
    static bool IsImage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinimg.com" || uri.Host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase));
    void Send(object data) { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data)); }
    async Task Message(string json)
    {
        try {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement; var action = root.GetProperty("action").GetString();
            if (action == "ready") { Send(new { type = "settings", folder }); Send(new { type = "pins", pins = pins.Values }); }
            else if (action == "folder") {
                using var dialog = new FolderBrowserDialog { SelectedPath = folder, Description = "Папка для изображений" };
                if (dialog.ShowDialog(this) == DialogResult.OK) { folder = dialog.SelectedPath; Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(folder)); Send(new { type = "settings", folder }); }
            } else if (action == "open") {
                var id = root.GetProperty("id").GetString();
                if (id != null && pins.TryGetValue(id, out var pin)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pin.Url) { UseShellExecute = true });
            } else if (action == "download" && !downloading) {
                var ids = root.GetProperty("ids").EnumerateArray().Select(x => x.GetString()).Where(x => x != null && pins.ContainsKey(x)).Distinct().ToList();
                downloading = true; Send(new { type = "busy", value = true });
                try { foreach (var id in ids) if (pins.TryGetValue(id!, out var pin)) await Download(pin); }
                finally { downloading = false; Send(new { type = "busy", value = false }); }
            }
        } catch (Exception ex) { Send(new { type = "error", message = ex.Message }); }
    }

    async Task Download(Pin pin)
    {
        try {
            if (!IsImage(pin.Image)) throw new Exception("Изображение не найдено на странице");
            Send(new { type = "status", id = pin.Id, status = "Скачивание…" });
            using var response = await client.GetAsync(pin.Image, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var ext = response.Content.Headers.ContentType?.MediaType switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => throw new Exception("Неподдерживаемый формат изображения") };
            if (response.Content.Headers.ContentLength > 50_000_000) throw new Exception("Изображение слишком большое");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "pinterest_" + pin.Id + ext);
            if (File.Exists(path)) { Send(new { type = "status", id = pin.Id, status = "Уже существует" }); return; }
            var temp = Path.Combine(folder, ".pinterest_" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[81920]; long total = 0; int n;
                    while ((n = await input.ReadAsync(buffer)) > 0) { total += n; if (total > 50_000_000) throw new Exception("Изображение слишком большое"); await output.WriteAsync(buffer.AsMemory(0,n)); }
                }
                File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            Send(new { type = "status", id = pin.Id, status = "Сохранено" });
        } catch (Exception ex) { Send(new { type = "status", id = pin.Id, status = "Ошибка: " + ex.Message }); }
    }
}

