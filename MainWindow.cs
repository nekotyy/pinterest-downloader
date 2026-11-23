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
using Avalonia.Platform;
using Avalonia.Styling;

namespace PinterestDownload;

record Pin(string Id, string Url, string Title, string Image);
record SyncPayload(string Source, string Browser, long Generation, int Done, int Total, List<Pin> Pins);
sealed class BrowserSession
{
    public string Name = "Браузер";
    public DateTime LastSeen;
    public long Generation;
    public int Done, Total;
    public List<Pin> Pins = new();
}

sealed class MainWindow : Window
{
    const string ExtensionOrigin = "chrome-extension://alenpleailakepgbibnkkmhcmkoijmch";
    readonly HttpListener listener = new();
    readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinterestDownload", "settings.json");
    readonly string historyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinterestDownload", "history.json");
    Dictionary<string, string> history = new();
    readonly Dictionary<string, BrowserSession> browsers = new();
    readonly Dictionary<string, Pin> pins = new();
    readonly HashSet<string> selected = new();
    readonly Dictionary<string, TextBlock> statusLabels = new();
    readonly Dictionary<string, string> statuses = new();
    readonly WrapPanel gallery = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock folderLabel = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock countLabel = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick };
    readonly TextBlock browserStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock downloadStatus = new() { TextWrapping = TextWrapping.Wrap };
    readonly ComboBox browserFilter = new() { MinWidth = 220 };
    readonly CheckBox repeat = new() { Content = "Скачивать повторно", VerticalAlignment = VerticalAlignment.Center };
    readonly Button more = new() { Content = "Следующие 100" };
    readonly Button previous = new() { Content = "Предыдущие 100" };
    readonly SemaphoreSlim previewLimit = new(6);
    readonly Dictionary<string, Task<Bitmap?>> previewCache = new();
    readonly Button download = new() { Content = "Скачать выбранные", Background = new SolidColorBrush(Color.Parse("#A52031")), Foreground = Brushes.White };
    readonly Button select = new() { Content = "Выбрать все" };
    string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    bool busy;
    string lastList = "";
    string browserSignature = "";
    int visibleCount = 100;
    bool rebuildingBrowsers;

    public MainWindow()
    {
        if (string.IsNullOrEmpty(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { if (File.Exists(settingsFile)) folder = JsonSerializer.Deserialize<string>(File.ReadAllText(settingsFile)) ?? folder; } catch { }
        try { if (File.Exists(historyFile)) history = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(historyFile)) ?? new(); } catch { }
        Title = "Pinterest Download"; Width = 1200; Height = 800; MinWidth = 700; MinHeight = 500;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PinterestDownload/docs/icon.png"))); } catch { }
        var choose = new Button { Content = "Изменить папку" };
        choose.Click += async (_, _) => await ChooseFolder();
        download.Click += async (_, _) => await DownloadSelected();
        select.Click += (_, _) => { if (selected.Count == pins.Count) selected.Clear(); else foreach (var id in pins.Keys) selected.Add(id); RenderPins(); };
        more.Click += (_, _) => { visibleCount += 100; RenderPins(); };
        previous.Click += (_, _) => { visibleCount = Math.Max(100, visibleCount - 100); RenderPins(); };
        browserFilter.SelectionChanged += (_, _) => { visibleCount = 100; ApplyBrowserFilter(); };
        var top = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { choose, download } };
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons); top.Children.Add(folderLabel);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { countLabel, select, repeat } };
        var headerText = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Pinterest Download", FontSize = 27, FontWeight = FontWeight.Bold }, new TextBlock { Text = "Ваши пины в одном месте", Opacity = .7 } } };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { headerText } };
        try { header.Children.Insert(0, new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://PinterestDownload/docs/icon.png"))), Width = 48, Height = 48 }); } catch { }
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(28), RowSpacing = 12 };
        AddRow(layout, header, 0); AddRow(layout, top, 1);
        AddRow(layout, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 15, Children = { browserFilter, browserStatus } }, 2);
        AddRow(layout, notice, 3); AddRow(layout, toolbar, 4); AddRow(layout, downloadStatus, 5);
        AddRow(layout, new ScrollViewer { Content = gallery, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, 6);
        AddRow(layout, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { previous, more } }, 7);
        Content = layout; UpdateFolder(); RefreshBrowserList(); RenderPins();
        ActualThemeVariantChanged += (_, _) => ApplyTheme(); ApplyTheme();
        Opened += (_, _) => StartListener();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => RefreshBrowserList(); timer.Start();
        Closed += (_, _) => { timer.Stop(); listener.Close(); client.Dispose(); };
    }

    static void AddRow(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    void UpdateFolder() => folderLabel.Text = "Папка: " + folder;
    void ApplyTheme() { Background = new SolidColorBrush(Color.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#17171B" : "#F7F5F3")); notice.Foreground = ActualThemeVariant == ThemeVariant.Dark ? Brushes.Salmon : Brushes.Firebrick; RenderPins(); }
    void UpdateCount() { countLabel.Text = $"Найдено: {pins.Count}, выбрано: {selected.Count}"; download.IsEnabled = !busy && selected.Count > 0; select.Content = pins.Count > 0 && selected.Count == pins.Count ? "Снять выбор" : "Выбрать все"; }

    async Task ChooseFolder()
    {
        try {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка для изображений", AllowMultiple = false });
            var path = result.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            folder = path; Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(folder)); UpdateFolder(); RenderPins();
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
            if (req.HttpMethod != "POST" || req.ContentLength64 < 0 || req.ContentLength64 > 10_000_000) { res.StatusCode = 400; return; }
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var document = JsonDocument.Parse(body);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            SyncPayload? payload;
            if (document.RootElement.ValueKind == JsonValueKind.Array) {
                var legacyPins = JsonSerializer.Deserialize<List<Pin>>(body, options) ?? new();
                payload = new SyncPayload("legacy-" + origin, "Расширение старой версии", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), legacyPins.Count, legacyPins.Count, legacyPins);
            } else {
                payload = JsonSerializer.Deserialize<SyncPayload>(body, options);
                if (payload == null || !Guid.TryParse(payload.Source, out _) || payload.Browser == null || payload.Pins == null || payload.Total < 0 || payload.Done < 0 || payload.Done > payload.Total) { res.StatusCode = 400; return; }
            }
            var valid = payload.Pins.Where(p => p != null && p.Id is not null && Regex.IsMatch(p.Id, @"^\d{1,30}$") && IsPin(p.Url, p.Id)).Take(10000)
                .Select(p => p with { Title = (p.Title ?? "Пин")[..Math.Min((p.Title ?? "Пин").Length, 300)], Image = IsImage(p.Image) ? p.Image : "" }).ToList();
            Dispatcher.UIThread.Post(() => {
                if (!browsers.TryGetValue(payload.Source, out var session)) browsers[payload.Source] = session = new BrowserSession();
                if (payload.Generation < session.Generation || (payload.Generation == session.Generation && payload.Done < session.Done)) return;
                session.Name = payload.Browser[..Math.Min(payload.Browser.Length, 50)]; session.LastSeen = DateTime.UtcNow;
                session.Generation = payload.Generation; session.Done = payload.Done; session.Total = payload.Total; session.Pins = valid;
                RefreshBrowserList(); ApplyBrowserFilter();
            });
            res.StatusCode = 204;
        } catch { res.StatusCode = 400; } finally { res.Close(); }
    }

    static bool IsPin(string? value, string id) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinterest.com" || uri.Host.EndsWith(".pinterest.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.TrimEnd('/') == "/pin/" + id;
    static bool IsImage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinimg.com" || uri.Host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase));

    void RefreshBrowserList()
    {
        bool Online(BrowserSession session) => DateTime.UtcNow - session.LastSeen < TimeSpan.FromMinutes(2);
        var signature = string.Join("|", browsers.Select(x => $"{x.Key}:{x.Value.Name}:{Online(x.Value)}"));
        if (signature != browserSignature || browserFilter.Items.Count == 0) {
            var previous = (browserFilter.SelectedItem as ComboBoxItem)?.Tag as string;
            rebuildingBrowsers = true; browserFilter.Items.Clear();
            browserFilter.Items.Add(new ComboBoxItem { Content = "Все браузеры", Tag = "" });
            foreach (var entry in browsers) browserFilter.Items.Add(new ComboBoxItem { Content = $"{entry.Value.Name}, {(Online(entry.Value) ? "подключён" : "нет связи")}, {entry.Key[..Math.Min(4, entry.Key.Length)]}", Tag = entry.Key });
            browserFilter.SelectedItem = browserFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string?)x.Tag == previous) ?? browserFilter.Items[0];
            rebuildingBrowsers = false; browserSignature = signature;
        }
        var active = browsers.Values.Count(Online);
        var pending = browsers.Values.Where(Online).Where(x => x.Done < x.Total).Select(x => $"{x.Name}: {x.Done}/{x.Total}");
        browserStatus.Text = active == 0 ? "Расширение не подключено" : $"Подключено: {active}" + (pending.Any() ? ", Обработка: " + string.Join(", ", pending) : "");
    }

    void ApplyBrowserFilter()
    {
        if (rebuildingBrowsers) return;
        var source = (browserFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var old = pins.Keys.ToHashSet(); pins.Clear();
        foreach (var entry in browsers) {
            if (!string.IsNullOrEmpty(source) && entry.Key != source) continue;
            foreach (var pin in entry.Value.Pins) if (!pins.TryGetValue(pin.Id, out var existing) || (string.IsNullOrEmpty(existing.Image) && !string.IsNullOrEmpty(pin.Image))) pins[pin.Id] = pin;
        }
        selected.RemoveWhere(id => !pins.ContainsKey(id)); foreach (var id in pins.Keys) if (!old.Contains(id)) selected.Add(id);
        var current = JsonSerializer.Serialize(pins.Values); if (current != lastList) { lastList = current; RenderPins(); }
    }

    void RenderPins()
    {
        gallery.Children.Clear(); statusLabels.Clear();
        if (pins.Count == 0) gallery.Children.Add(new TextBlock { Text = "Откройте пины в браузере и откройте расширение и нажмите «Обновить вкладки».\nСписок также обновляется автоматически.", Margin = new Thickness(4, 36), Foreground = Brushes.Gray });
        if (visibleCount > Math.Max(100, pins.Count)) visibleCount = Math.Max(100, ((pins.Count + 99) / 100) * 100);
        foreach (var pin in pins.Values.Skip(visibleCount - 100).Take(100)) {
            var image = new Image { Width = 200, Height = 180, Stretch = Stretch.Uniform };
            var check = new CheckBox { Content = pin.Title, IsChecked = selected.Contains(pin.Id), MaxWidth = 195 };
            check.IsCheckedChanged += (_, _) => { if (check.IsChecked == true) selected.Add(pin.Id); else selected.Remove(pin.Id); UpdateCount(); };
            var status = new TextBlock { Text = statuses.GetValueOrDefault(pin.Id, DownloadedPath(pin.Id) != null ? "Уже скачано" : pin.Image == "" ? "Откройте вкладку для загрузки изображения" : "Готово к скачиванию"), TextWrapping = TextWrapping.Wrap, Opacity = .75, FontSize = 12 };
            statusLabels[pin.Id] = status;
            var link = new Button { Content = "Открыть пин" };
            link.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pin.Url) { UseShellExecute = true }); } catch (Exception ex) { notice.Text = ex.Message; } };
            var stack = new StackPanel { Width = 200, Spacing = 8, Children = { image, check, status, link } };
            gallery.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#29292F" : "#FFFFFF")), CornerRadius = new CornerRadius(12), Padding = new Thickness(12), Margin = new Thickness(0, 0, 16, 16), Child = stack });
            if (IsImage(pin.Image)) _ = LoadPreview(image, pin.Image);
        }
        previous.IsVisible = visibleCount > 100; more.IsVisible = pins.Count > visibleCount; UpdateCount();
    }

    async Task LoadPreview(Image image, string url)
    {
        if (!previewCache.TryGetValue(url, out var task)) {
            if (previewCache.Count >= 300) previewCache.Clear();
            previewCache[url] = task = FetchPreview(url);
        }
        image.Source = await task;
    }

    async Task<Bitmap?> FetchPreview(string url)
    {
        await previewLimit.WaitAsync();
        try {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 10_000_000) return null;
            await using var input = await response.Content.ReadAsStreamAsync(); using var buffer = new MemoryStream();
            var bytes = new byte[81920]; int n;
            while ((n = await input.ReadAsync(bytes)) > 0) { if (buffer.Length + n > 10_000_000) return null; buffer.Write(bytes, 0, n); }
            buffer.Position = 0; return Bitmap.DecodeToWidth(buffer, 300);
        } catch { return null; } finally { previewLimit.Release(); }
    }

    string? DownloadedPath(string id)
    {
        if (history.TryGetValue(id, out var saved) && File.Exists(saved)) return saved;
        foreach (var ext in new[] { ".jpg", ".png", ".webp", ".gif" }) {
            var candidate = Path.Combine(folder, "pinterest_" + id + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    void SaveHistory()
    {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);
            var temp = historyFile + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(history)); File.Move(temp, historyFile, true);
        } catch (Exception ex) { notice.Text = "Файл сохранён, но историю сохранить не удалось: " + ex.Message; }
    }

    void SetStatus(string id, string status) { statuses[id] = status; if (statusLabels.TryGetValue(id, out var label)) label.Text = status; }
    async Task DownloadSelected()
    {
        if (busy) return; busy = true; UpdateCount();
        var items = selected.ToArray(); int completed = 0;
        try { foreach (var id in items) { downloadStatus.Text = $"Обработано {completed} из {items.Length}"; if (pins.TryGetValue(id, out var pin)) await Download(pin); completed++; } downloadStatus.Text = $"Завершено: {completed} из {items.Length}"; }
        finally { busy = false; UpdateCount(); }
    }
    async Task Download(Pin pin)
    {
        try {
            var oldPath = DownloadedPath(pin.Id);
            if (oldPath != null && repeat.IsChecked != true) { SetStatus(pin.Id, "Уже скачано: " + oldPath); return; }
            if (!IsImage(pin.Image)) throw new Exception("Откройте вкладку, чтобы загрузить изображение");
            SetStatus(pin.Id, "Скачивание…");
            using var response = await client.GetAsync(pin.Image, HttpCompletionOption.ResponseHeadersRead); response.EnsureSuccessStatusCode();
            var ext = response.Content.Headers.ContentType?.MediaType switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => throw new Exception("Неподдерживаемый формат изображения") };
            if (response.Content.Headers.ContentLength > 50_000_000) throw new Exception("Изображение слишком большое");
            Directory.CreateDirectory(folder); string path = Path.Combine(folder, "pinterest_" + pin.Id + ext);
            if (File.Exists(path)) {
                if (repeat.IsChecked != true) { SetStatus(pin.Id, "Уже скачано: " + path); return; }
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, "pinterest_" + pin.Id + "_" + i + ext);
            }
            var temp = Path.Combine(folder, ".pinterest_" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[81920]; long total = 0; int n;
                    while ((n = await input.ReadAsync(buffer)) > 0) { total += n; if (total > 50_000_000) throw new Exception("Изображение слишком большое"); await output.WriteAsync(buffer.AsMemory(0, n)); SetStatus(pin.Id, $"Скачивание: {total / 1024} КБ" + (response.Content.Headers.ContentLength is long length ? $" из {length / 1024} КБ" : "")); }
                }
                File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            history[pin.Id] = path; SaveHistory(); SetStatus(pin.Id, "Сохранено");
        } catch (Exception ex) { SetStatus(pin.Id, "Ошибка: " + ex.Message); }
    }
}
