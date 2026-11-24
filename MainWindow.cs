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
using Avalonia.Automation;

namespace PinterestDownload;

sealed class MainWindow : Window
{
    const string ExtensionOrigin = "chrome-extension://alenpleailakepgbibnkkmhcmkoijmch";
    readonly HttpListener listener = new();
    readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinterestDownload", "settings.json");
    readonly string historyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinterestDownload", "history.json");
    readonly DownloadHistory history;
    readonly ImageDownloader downloader;
    readonly BrowserRegistry browsers = new();
    readonly CloseCommands closeCommands = new();
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
    readonly ProgressBar downloadProgress = new() { Minimum = 0, Height = 8, IsVisible = false };
    readonly Button cancelDownload = new() { Content = "Отменить", IsVisible = false };
    readonly ComboBox browserFilter = new() { MinWidth = 220 };
    readonly CheckBox repeat = new() { Content = "Скачивать повторно", VerticalAlignment = VerticalAlignment.Center };
    readonly Button more = new() { Content = "Следующие 100" };
    readonly Button previous = new() { Content = "Предыдущие 100" };
    readonly SemaphoreSlim previewLimit = new(6);
    readonly Dictionary<string, Task<Bitmap?>> previewCache = new();
    readonly Button download = new() { Content = "Скачать выбранные", Background = new SolidColorBrush(Color.Parse("#A52031")), Foreground = Brushes.White };
    readonly Button select = new() { Content = "Выбрать все" };
    readonly Button closeTabs = new() { Content = "Закрыть скачанные вкладки" };
    string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    bool busy;
    string lastList = "";
    string browserSignature = "";
    int visibleCount = 100;
    bool rebuildingBrowsers;
    CancellationTokenSource? downloadCancellation;

    public MainWindow()
    {
        if (string.IsNullOrEmpty(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { if (File.Exists(settingsFile)) folder = JsonSerializer.Deserialize<string>(File.ReadAllText(settingsFile)) ?? folder; } catch { }
        history = new DownloadHistory(historyFile);
        downloader = new ImageDownloader(client, history);
        if (history.LoadError != null) notice.Text = "Не удалось прочитать историю загрузок: " + history.LoadError;
        Title = "Pinterest Download"; Width = 1200; Height = 800; MinWidth = 700; MinHeight = 500;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PinterestDownload/docs/icon.png"))); } catch { }
        var choose = new Button { Content = "Изменить папку" };
        AutomationProperties.SetName(browserFilter, "Выбор браузера");
        choose.Click += async (_, _) => await ChooseFolder();
        download.Click += async (_, _) => await DownloadSelected();
        closeTabs.Click += (_, _) => CloseSavedTabs();
        cancelDownload.Click += (_, _) => { cancelDownload.IsEnabled = false; downloadCancellation?.Cancel(); };
        select.Click += (_, _) => { if (selected.Count == pins.Count) selected.Clear(); else foreach (var id in pins.Keys) selected.Add(id); RenderPins(); };
        more.Click += (_, _) => { visibleCount += 100; RenderPins(); };
        previous.Click += (_, _) => { visibleCount = Math.Max(100, visibleCount - 100); RenderPins(); };
        browserFilter.SelectionChanged += (_, _) => { if (!rebuildingBrowsers) { visibleCount = 100; ApplyBrowserFilter(); } };
        var top = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { choose, download } };
        DockPanel.SetDock(buttons, Dock.Right); top.Children.Add(buttons); top.Children.Add(folderLabel);
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Children = { countLabel, select, repeat, closeTabs } };
        foreach (Control control in toolbar.Children) control.Margin = new Thickness(0, 0, 14, 4);
        var headerText = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Pinterest Download", FontSize = 27, FontWeight = FontWeight.Bold }, new TextBlock { Text = "Ваши пины в одном месте", Opacity = .7 } } };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { headerText } };
        try { header.Children.Insert(0, new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://PinterestDownload/docs/icon.png"))), Width = 48, Height = 48 }); } catch { }
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"), Margin = new Thickness(28), RowSpacing = 12 };
        AddRow(layout, header, 0); AddRow(layout, top, 1);
        AddRow(layout, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 15, Children = { browserFilter, browserStatus } }, 2);
        var progressPanel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(cancelDownload, Dock.Right); progressPanel.Children.Add(cancelDownload);
        progressPanel.Children.Add(new StackPanel { Spacing = 5, Children = { downloadStatus, downloadProgress } });
        AddRow(layout, notice, 3); AddRow(layout, toolbar, 4); AddRow(layout, progressPanel, 5);
        AddRow(layout, new ScrollViewer { Content = gallery, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, 6);
        AddRow(layout, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { previous, more } }, 7);
        Content = layout; UpdateFolder(); RefreshBrowserList(); RenderPins();
        ActualThemeVariantChanged += (_, _) => ApplyTheme(); ApplyTheme();
        Opened += (_, _) => StartListener();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => RefreshBrowserList(); timer.Start();
        Closed += (_, _) => { downloadCancellation?.Cancel(); timer.Stop(); listener.Close(); client.Dispose(); };
    }

    static void AddRow(Grid grid, Control control, int row) { Grid.SetRow(control, row); grid.Children.Add(control); }
    void UpdateFolder() => folderLabel.Text = "Папка: " + folder;
    void ApplyTheme() { Background = new SolidColorBrush(Color.Parse(ActualThemeVariant == ThemeVariant.Dark ? "#17171B" : "#F7F5F3")); notice.Foreground = ActualThemeVariant == ThemeVariant.Dark ? Brushes.Salmon : Brushes.Firebrick; RenderPins(); }
    void UpdateCount() { countLabel.Text = $"Найдено: {pins.Count}, выбрано: {selected.Count}"; download.IsEnabled = !busy && selected.Count > 0; closeTabs.IsEnabled = !busy && pins.Count > 0; select.Content = pins.Count > 0 && selected.Count == pins.Count ? "Снять выбор" : "Выбрать все"; }

    void CloseSavedTabs()
    {
        var source = (browserFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        int queued = 0; var now = DateTimeOffset.UtcNow;
        var targets = CloseCommands.Targets(browsers, source, now.UtcDateTime, id => history.Find(id, folder) != null);
        foreach (var target in targets) if (closeCommands.Queue(target.Key, target.Value, now)) queued++;
        notice.Text = queued > 0 ? $"Ожидается ответ расширений: {queued}. Обновите вкладки в расширении, чтобы выполнить команду сразу." : "Нет скачанных вкладок в подключённых браузерах или предыдущая команда ещё выполняется.";
    }

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
            try { context = await listener.GetContextAsync().ConfigureAwait(false); } catch { break; }
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
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
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
            var now = DateTimeOffset.UtcNow;
            if (payload.Ack != null && closeCommands.Acknowledge(payload.Source, payload.Ack, now)) {
                var ack = payload.Ack;
                Dispatcher.UIThread.Post(() => notice.Text = ack.Failed < 0 ? "Расширение прервало операцию закрытия. Проверьте вкладки." : $"Закрыто вкладок: {ack.Closed}. Ошибок: {ack.Failed}.");
            }
            var command = closeCommands.Pending(payload.Source, now);
            Dispatcher.UIThread.Post(() => {
                if (!browsers.Update(payload, valid, DateTime.UtcNow)) return;
                RefreshBrowserList(); ApplyBrowserFilter();
            });
            if (command == null) res.StatusCode = 204;
            else {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(command, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                res.StatusCode = 200; res.ContentType = "application/json"; res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            }
        } catch { res.StatusCode = 400; } finally { res.Close(); }
    }

    static bool IsPin(string? value, string id) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinterest.com" || uri.Host.EndsWith(".pinterest.com", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.TrimEnd('/') == "/pin/" + id;
    static bool IsImage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && (uri.Host == "pinimg.com" || uri.Host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase));

    void RefreshBrowserList()
    {
        bool Online(BrowserSession session) => BrowserRegistry.Online(session, DateTime.UtcNow);
        var signature = string.Join("|", browsers.Sessions.Select(x => $"{x.Key}:{x.Value.Name}:{Online(x.Value)}"));
        if (signature != browserSignature || browserFilter.Items.Count == 0) {
            var previous = (browserFilter.SelectedItem as ComboBoxItem)?.Tag as string;
            rebuildingBrowsers = true; browserFilter.Items.Clear();
            browserFilter.Items.Add(new ComboBoxItem { Content = "Все браузеры", Tag = "" });
            foreach (var entry in browsers.Sessions) browserFilter.Items.Add(new ComboBoxItem { Content = $"{entry.Value.Name}, {(Online(entry.Value) ? "подключён" : "нет связи")}, {entry.Key[..Math.Min(4, entry.Key.Length)]}", Tag = entry.Key });
            browserFilter.SelectedItem = browserFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string?)x.Tag == previous) ?? browserFilter.Items[0];
            rebuildingBrowsers = false; browserSignature = signature;
        }
        var active = browsers.Sessions.Values.Count(Online);
        var pending = browsers.Sessions.Values.Where(Online).Where(x => x.Done < x.Total).Select(x => $"{x.Name}: {x.Done}/{x.Total}");
        browserStatus.Text = active == 0 ? "Расширение не подключено" : $"Подключено: {active}" + (pending.Any() ? ", Обработка: " + string.Join(", ", pending) : "");
    }

    void ApplyBrowserFilter()
    {
        if (rebuildingBrowsers) return;
        var source = (browserFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var old = pins.Keys.ToHashSet(); pins.Clear();
        foreach (var entry in browsers.Select(source)) pins[entry.Key] = entry.Value;
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

    string? DownloadedPath(string id) => history.Find(id, folder);

    void SetStatus(string id, string status) { statuses[id] = status; if (statusLabels.TryGetValue(id, out var label)) label.Text = status; }
    async Task DownloadSelected()
    {
        if (busy) return; busy = true; UpdateCount();
        var items = selected.Where(pins.ContainsKey).Select(id => pins[id]).ToArray(); int completed = 0;
        var targetFolder = folder; bool allowRepeat = repeat.IsChecked == true;
        using var cancellation = new CancellationTokenSource(); downloadCancellation = cancellation;
        downloadProgress.Maximum = Math.Max(items.Length, 1); downloadProgress.Value = 0; downloadProgress.IsVisible = true;
        cancelDownload.IsVisible = true; cancelDownload.IsEnabled = true;
        try {
            foreach (var pin in items) {
                if (cancellation.IsCancellationRequested) break;
                downloadStatus.Text = $"Обработано {completed} из {items.Length}";
                await Download(pin, completed, targetFolder, allowRepeat, cancellation.Token); completed++;
                downloadProgress.IsIndeterminate = false; downloadProgress.Value = completed;
            }
            downloadStatus.Text = cancellation.IsCancellationRequested ? $"Отменено. Обработано {completed} из {items.Length}" : $"Завершено: {completed} из {items.Length}";
        } finally { downloadCancellation = null; cancelDownload.IsVisible = false; downloadProgress.IsIndeterminate = false; busy = false; UpdateCount(); }
    }
    async Task Download(Pin pin, int completed, string targetFolder, bool allowRepeat, CancellationToken cancellationToken)
    {
        var result = await downloader.Download(pin, targetFolder, allowRepeat, progress => {
            downloadProgress.IsIndeterminate = progress.Total is not > 0;
            if (progress.Total is > 0) downloadProgress.Value = completed + Math.Min(1, (double)progress.Bytes / progress.Total.Value);
            SetStatus(pin.Id, $"Скачивание: {progress.Bytes / 1024} КБ" + (progress.Total is long total ? $" из {total / 1024} КБ" : ""));
        }, cancellationToken);
        if (result.Warning != null) notice.Text = result.Warning;
        SetStatus(pin.Id, result.Status);
    }
}
