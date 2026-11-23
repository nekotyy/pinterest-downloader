using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PinterestDownload;

record Pin(string Id, string Url, string Title, string Image);

sealed class MainWindow : Window
{
    const string ExtensionOrigin = "chrome-extension://alenpleailakepgbibnkkmhcmkoijmch";
    readonly HttpListener listener = new();
    readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinterestDownload", "settings.json");
    readonly Dictionary<string, Pin> pins = new();
    readonly HashSet<string> selected = new();
    readonly Dictionary<string, TextBlock> statusLabels = new();
    readonly Dictionary<string, string> statuses = new();
    readonly WrapPanel gallery = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock folderLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock countLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
    readonly Button download = new() { Content = "Скачать выбранные", Background = new SolidColorBrush(Color.Parse("#A52031")), Foreground = Brushes.White };
    readonly Button select = new() { Content = "Выбрать все" };
    string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    bool busy;
    string lastList = "";

    public MainWindow()
    {
        if (string.IsNullOrEmpty(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { if (File.Exists(settingsFile)) folder = JsonSerializer.Deserialize<string>(File.ReadAllText(settingsFile)) ?? folder; } catch { }
        Title = "Pinterest Download"; Width = 1200; Height = 800; MinWidth = 700; MinHeight = 500;
        Background = new SolidColorBrush(Color.Parse("#F7F5F3"));
        var choose = new Button { Content = "Изменить папку" };
        choose.Click += async (_, _) => await ChooseFolder();
        download.Click += async (_, _) => await DownloadSelected();
        select.Click += (_, _) => { if (selected.Count == pins.Count) selected.Clear(); else foreach (var id in pins.Keys) selected.Add(id); RenderPins(); };
        var top = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { choose, download } };
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons); top.Children.Add(folderLabel);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { countLabel, select } };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(28), RowSpacing = 16 };
        AddRow(layout, new TextBlock { Text = "Pinterest Download", FontSize = 27, FontWeight = FontWeight.Bold }, 0);
        AddRow(layout, top, 1); AddRow(layout, notice, 2); AddRow(layout, toolbar, 3);
        AddRow(layout, new ScrollViewer { Content = gallery, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, 4);
        Content = layout; UpdateFolder(); RenderPins();
        Opened += (_, _) => StartListener();
        Closed += (_, _) => { listener.Close(); client.Dispose(); };
    }

    static void AddRow(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    void UpdateFolder() => folderLabel.Text = "Папка: " + folder;
    void UpdateCount() { countLabel.Text = $"Найдено: {pins.Count} · выбрано: {selected.Count}"; download.IsEnabled = !busy && selected.Count > 0; select.Content = pins.Count > 0 && selected.Count == pins.Count ? "Снять выбор" : "Выбрать все"; }

    async Task ChooseFolder()
    {
        try {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка для изображений", AllowMultiple = false });
            var path = result.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            folder = path; Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(folder)); UpdateFolder();
        } catch (Exception ex) { notice.Text = "Не удалось выбрать папку: " + ex.Message; }
    }

    void StartListener()
    {
        try { listener.Prefixes.Add("http://127.0.0.1:17849/"); listener.Start(); _ = Listen(); }
        catch (Exception ex) { notice.Text = "Не удалось подключить расширение. Проверьте, не запущена ли другая копия приложения: " + ex.Message; }
    }

    async Task Listen()
    {
        while (listener.IsListening) {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); } catch { break; }
            _ = HandleRequest(context);
        }
    }

    async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request; var res = context.Response;
        try {
            var origin = req.Headers["Origin"];
            if ((origin != ExtensionOrigin && !Regex.IsMatch(origin ?? "", @"^moz-extension://[0-9a-f-]{36}$")) || req.Url?.AbsolutePath != "/pins") { res.StatusCode = 403; return; }
            res.Headers.Add("Access-Control-Allow-Origin", origin);
            res.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; return; }
            if (req.HttpMethod != "POST" || req.ContentLength64 < 0 || req.ContentLength64 > 1_000_000) { res.StatusCode = 400; return; }
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var incoming = JsonSerializer.Deserialize<List<Pin>>(await reader.ReadToEndAsync(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            var valid = incoming.Where(p => p.Id is not null && Regex.IsMatch(p.Id, @"^\d{1,30}$") && IsPin(p.Url, p.Id)).Take(500)
                .Select(p => p with { Title = (p.Title ?? "Пин")[..Math.Min((p.Title ?? "Пин").Length, 300)], Image = IsImage(p.Image) ? p.Image : "" }).ToList();
            Dispatcher.UIThread.Post(() => {
                var old = pins.Keys.ToHashSet(); pins.Clear(); foreach (var pin in valid) pins[pin.Id] = pin;
                selected.RemoveWhere(id => !pins.ContainsKey(id)); foreach (var id in pins.Keys) if (!old.Contains(id)) selected.Add(id);
                var current = JsonSerializer.Serialize(pins.Values); if (current != lastList) { lastList = current; RenderPins(); }
            });
            res.StatusCode = 204;
        } catch { res.StatusCode = 400; } finally { res.Close(); }
    }

    static bool IsPin(string? value, string id) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinterest.com" || uri.Host.EndsWith(".pinterest.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.TrimEnd('/') == "/pin/" + id;
    static bool IsImage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinimg.com" || uri.Host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase));

    void RenderPins()
    {
        gallery.Children.Clear(); statusLabels.Clear();
        if (pins.Count == 0) gallery.Children.Add(new TextBlock { Text = "Откройте пины в браузере и нажмите значок расширения.\nСписок также обновляется автоматически.", Margin = new Thickness(4, 36), Foreground = Brushes.Gray });
        foreach (var pin in pins.Values) {
            var image = new Image { Width = 200, Height = 180, Stretch = Stretch.Uniform };
            var check = new CheckBox { Content = pin.Title, IsChecked = selected.Contains(pin.Id), MaxWidth = 195 };
            check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) selected.Add(pin.Id); else selected.Remove(pin.Id); UpdateCount(); };
            var status = new TextBlock { Text = statuses.GetValueOrDefault(pin.Id, pin.Image == "" ? "Нет предпросмотра" : "Пин " + pin.Id), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 };
            statusLabels[pin.Id] = status;
            var link = new Button { Content = "Открыть пин ↗" };
            link.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pin.Url) { UseShellExecute = true }); } catch (Exception ex) { notice.Text = ex.Message; } };
            var stack = new StackPanel { Width = 200, Spacing = 8, Children = { image, check, status, link } };
            gallery.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 0, 16, 16), Child = stack });
            if (IsImage(pin.Image)) _ = LoadPreview(image, pin.Image);
        }
        UpdateCount();
    }

    async Task LoadPreview(Image image, string url)
    {
        try {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 10_000_000) return;
            await using var input = await response.Content.ReadAsStreamAsync(); using var buffer = new MemoryStream();
            var bytes = new byte[81920]; int n;
            while ((n = await input.ReadAsync(bytes)) > 0) { if (buffer.Length + n > 10_000_000) return; buffer.Write(bytes, 0, n); }
            buffer.Position = 0; image.Source = new Bitmap(buffer);
        } catch { }
    }

    void SetStatus(string id, string status) { statuses[id] = status; if (statusLabels.TryGetValue(id, out var label)) label.Text = status; }
    async Task DownloadSelected()
    {
        if (busy) return; busy = true; UpdateCount();
        try { foreach (var id in selected.ToArray()) if (pins.TryGetValue(id, out var pin)) await Download(pin); }
        finally { busy = false; UpdateCount(); }
    }
    async Task Download(Pin pin)
    {
        try {
            if (!IsImage(pin.Image)) throw new Exception("Изображение не найдено на странице");
            SetStatus(pin.Id, "Скачивание…");
            using var response = await client.GetAsync(pin.Image, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
            var ext = response.Content.Headers.ContentType?.MediaType switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => throw new Exception("Неподдерживаемый формат изображения") };
            if (response.Content.Headers.ContentLength > 50_000_000) throw new Exception("Изображение слишком большое");
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, "pinterest_" + pin.Id + ext);
            if (File.Exists(path)) { SetStatus(pin.Id, "Уже существует"); return; }
            var temp = Path.Combine(folder, ".pinterest_" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[81920]; long total = 0; int n;
                    while ((n = await input.ReadAsync(buffer)) > 0) { total += n; if (total > 50_000_000) throw new Exception("Изображение слишком большое"); await output.WriteAsync(buffer.AsMemory(0, n)); }
                }
                File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            SetStatus(pin.Id, "Сохранено");
        } catch (Exception ex) { SetStatus(pin.Id, "Ошибка: " + ex.Message); }
    }
}
