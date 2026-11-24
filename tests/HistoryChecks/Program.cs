using PinterestDownload;

var root = Path.Combine(Path.GetTempPath(), "pinterest-history-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    var historyFile = Path.Combine(root, "settings", "history.json");
    var firstFolder = Path.Combine(root, "first");
    var secondFolder = Path.Combine(root, "second");
    Directory.CreateDirectory(firstFolder); Directory.CreateDirectory(secondFolder);
    var first = Path.Combine(firstFolder, "pinterest_123.jpg");
    File.WriteAllText(first, "original");

    var history = new DownloadHistory(historyFile);
    Assert(history.Find("123", firstFolder) == first, "existing file in the selected folder is detected");
    history.Record("123", first);
    history = new DownloadHistory(historyFile);
    Assert(history.Find("123", secondFolder) == first, "history is retained across restarts and folder changes");
    Assert(history.NextPath(firstFolder, "123", ".jpg") == Path.Combine(firstFolder, "pinterest_123_2.jpg"), "repeat download does not replace the original");
    File.WriteAllText(Path.Combine(firstFolder, "pinterest_123_2.jpg"), "copy");
    Assert(history.NextPath(firstFolder, "123", ".jpg") == Path.Combine(firstFolder, "pinterest_123_3.jpg"), "repeat download avoids existing copies");
    File.Delete(first);
    Assert(history.Find("123", secondFolder) == null, "history does not claim a deleted file exists");

    File.WriteAllText(historyFile, "not valid json");
    history = new DownloadHistory(historyFile);
    Assert(history.LoadError != null, "invalid history is reported");
    try { history.Record("123", first); throw new Exception("invalid history was overwritten"); }
    catch (InvalidOperationException) { }
    Assert(File.ReadAllText(historyFile) == "not valid json", "invalid history is preserved");

    var preferencesFile = Path.Combine(root, "settings", "preferences.json");
    var preferences = new PreferencesStore(preferencesFile);
    Assert(preferences.Value == new AppPreferences(), "default preferences preserve the existing behavior");
    var changed = new AppPreferences("Dark", true, false, 50);
    preferences.Save(changed);
    Assert(new PreferencesStore(preferencesFile).Value == changed, "preferences survive a restart");
    try { preferences.Save(changed with { PageSize = 0 }); throw new Exception("invalid page size was accepted"); }
    catch (InvalidDataException) { }
    Assert(new PreferencesStore(preferencesFile).Value == changed, "invalid preferences do not replace saved values");
    File.WriteAllText(preferencesFile, "invalid json");
    preferences = new PreferencesStore(preferencesFile);
    Assert(preferences.LoadError != null, "invalid preferences are reported");
    try { preferences.Save(changed); throw new Exception("invalid preferences were overwritten"); }
    catch (InvalidOperationException) { }
    Assert(File.ReadAllText(preferencesFile) == "invalid json", "invalid preferences remain intact");

    var registry = new BrowserRegistry();
    var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var missingImage = new Pin("123", "https://pinterest.com/pin/123/", "First", "");
    var withImage = missingImage with { Image = "https://i.pinimg.com/123.jpg" };
    var other = new Pin("456", "https://pinterest.com/pin/456/", "Other", "");
    var chrome = new SyncPayload("chrome", "Chrome", 100, 2, 2, new() { missingImage, other });
    var firefox = new SyncPayload("firefox", "Firefox", 101, 1, 1, new() { withImage });
    Assert(registry.Update(chrome, chrome.Pins, now), "Chrome is accepted");
    Assert(registry.Update(firefox, firefox.Pins, now), "Firefox is accepted");
    Assert(registry.Select(null).Count == 2 && registry.Select(null)["123"].Image == withImage.Image, "all browsers are combined and the available preview is preferred");
    Assert(registry.Select("firefox").Count == 1 && registry.Select("chrome").Count == 2, "browser filtering keeps sources separate");
    Assert(!registry.Update(chrome with { Generation = 99, Done = 0 }, new(), now), "an old synchronization does not replace a newer result");
    Assert(!registry.Update(chrome with { Done = 1 }, new(), now), "out-of-order progress does not replace a newer result");
    Assert(registry.Select("chrome").Count == 2, "out-of-order results leave the visible pins intact");
    Assert(BrowserRegistry.Online(registry.Sessions["chrome"], now.AddSeconds(119)), "recent browser is online");
    Assert(!BrowserRegistry.Online(registry.Sessions["chrome"], now.AddSeconds(121)), "inactive browser becomes offline");
    Assert(registry.Touch("chrome", now.AddMinutes(3)), "heartbeat updates a known browser");
    Assert(BrowserRegistry.Online(registry.Sessions["chrome"], now.AddMinutes(4)), "heartbeats keep a manually synchronized browser online");
    Assert(!registry.Touch("unknown", now), "a heartbeat does not create an unknown browser");

    var queue = new CloseCommands();
    var start = new DateTimeOffset(now);
    Assert(queue.Queue("chrome", new[] { "123", "123", "456" }, start), "a close request is queued");
    var command = queue.Pending("chrome", start)!;
    Assert(command.Pins.SequenceEqual(new[] { "123", "456" }), "close requests are deduplicated");
    Assert(queue.Pending("firefox", start) == null, "requests do not leak between browsers");
    Assert(!queue.Queue("chrome", new[] { "789" }, start), "an unfinished request cannot be overwritten");
    Assert(!queue.Acknowledge("chrome", new CloseAcknowledgement("wrong", 0, 0), start), "unknown acknowledgements do not remove requests");
    Assert(queue.Acknowledge("chrome", new CloseAcknowledgement(command.Id, 2, 0), start), "matching acknowledgement completes a request");
    Assert(!queue.Acknowledge("chrome", new CloseAcknowledgement(command.Id, 2, 0), start), "an acknowledgement is processed only once");
    Assert(queue.Queue("chrome", new[] { "789" }, start), "another request can be queued after completion");
    Assert(queue.Pending("chrome", start.AddMinutes(3)) == null, "old requests expire instead of closing tabs later");
    var source1 = "11111111-1111-1111-1111-111111111111";
    var source2 = "22222222-2222-2222-2222-222222222222";
    var closeRegistry = new BrowserRegistry();
    closeRegistry.Update(chrome with { Source = source1 }, chrome.Pins, now);
    closeRegistry.Update(firefox with { Source = source2 }, firefox.Pins, now);
    var targets = CloseCommands.Targets(closeRegistry, null, now, id => id == "123");
    Assert(targets.Count == 2 && targets[source1].SequenceEqual(new[] { "123" }) && targets[source2].SequenceEqual(new[] { "123" }), "only downloaded pins are targeted in each connected browser");
    targets = CloseCommands.Targets(closeRegistry, source1, now, id => id == "123");
    Assert(targets.Count == 1 && targets.ContainsKey(source1), "the browser filter limits tab closing");
    Assert(CloseCommands.Targets(closeRegistry, null, now.AddMinutes(3), _ => true).Count == 0, "offline browsers do not receive close requests");
    var searchable = new[] { withImage with { Title = "Mountain" }, other with { Title = "Coast" } };
    Assert(PinFilter.Apply(searchable, "mount", false, _ => false).Single().Id == "123", "search is case-insensitive");
    Assert(PinFilter.Apply(searchable, "456", false, _ => false).Single().Id == "456", "search matches pin IDs");
    Assert(PinFilter.Apply(searchable, "", true, id => id == "123").Single().Id == "456", "downloaded pins can be hidden");

    var downloadFolder = Path.Combine(root, "downloads");
    var downloadHistory = new DownloadHistory(Path.Combine(root, "downloads-history.json"));
    var handler = new FakeImageHandler();
    using var client = new HttpClient(handler);
    var downloader = new ImageDownloader(client, downloadHistory);
    var downloadable = new Pin("789", "https://pinterest.com/pin/789/", "Image", "https://i.pinimg.com/image.jpg");
    var result = await downloader.Download(downloadable, downloadFolder, false, _ => { });
    Assert(result.Status == "Сохранено" && result.Saved && File.Exists(Path.Combine(downloadFolder, "pinterest_789.jpg")), "image is saved to the selected folder");
    result = await downloader.Download(downloadable, secondFolder, false, _ => { });
    Assert(result.Status.StartsWith("Уже скачано") && !result.Saved && handler.Requests == 1, "an existing download is reported without another network request");
    result = await downloader.Download(downloadable, downloadFolder, true, _ => { });
    Assert(result.Status == "Сохранено" && File.Exists(Path.Combine(downloadFolder, "pinterest_789_2.jpg")), "repeat download saves a separate copy");
    Assert(File.ReadAllBytes(Path.Combine(downloadFolder, "pinterest_789.jpg")).SequenceEqual(new byte[] { 1, 2, 3 }), "repeat download leaves the original intact");
    handler.Fail = true;
    result = await downloader.Download(downloadable with { Id = "999" }, downloadFolder, false, _ => { });
    Assert(result.Status.StartsWith("Ошибка:") && !Directory.EnumerateFiles(downloadFolder, "pinterest_999*").Any(), "network failure does not leave an incomplete file");
    handler.Fail = false; handler.BreakStream = true;
    result = await downloader.Download(downloadable with { Id = "998" }, downloadFolder, false, _ => { });
    Assert(result.Status.StartsWith("Ошибка:") && !Directory.EnumerateFiles(downloadFolder, ".pinterest_*.tmp").Any(), "interrupted downloads remove temporary files");
    var requestCount = handler.Requests;
    result = await downloader.Download(downloadable with { Id = "997", Image = "https://example.com/image.jpg" }, downloadFolder, false, _ => { });
    Assert(result.Status.StartsWith("Ошибка:") && handler.Requests == requestCount, "non-Pinterest image hosts are rejected");
    handler.BreakStream = false; handler.Oversize = true;
    result = await downloader.Download(downloadable with { Id = "996" }, downloadFolder, false, _ => { });
    Assert(result.Status.StartsWith("Ошибка:") && !File.Exists(Path.Combine(downloadFolder, "pinterest_996.jpg")), "oversized images are rejected");
    handler.Oversize = false;
    var concurrentPath = Path.Combine(downloadFolder, "pinterest_995.jpg");
    result = await downloader.Download(downloadable with { Id = "995" }, downloadFolder, false, progress => { if (progress.Bytes > 0 && !File.Exists(concurrentPath)) File.WriteAllText(concurrentPath, "other file"); });
    Assert(result.Status.StartsWith("Ошибка:") && File.ReadAllText(concurrentPath) == "other file", "a file created during download is not overwritten");
    Assert(!Directory.EnumerateFiles(downloadFolder, ".pinterest_*.tmp").Any(), "a conflicting download cleans its temporary file");
    using var cancellation = new CancellationTokenSource();
    result = await downloader.Download(downloadable with { Id = "994" }, downloadFolder, false, progress => { if (progress.Bytes > 0) cancellation.Cancel(); }, cancellation.Token);
    Assert(result.Status == "Отменено" && !File.Exists(Path.Combine(downloadFolder, "pinterest_994.jpg")), "cancelled downloads do not create a completed file");
    Assert(!Directory.EnumerateFiles(downloadFolder, ".pinterest_*.tmp").Any(), "cancelled downloads remove temporary files");
    Console.WriteLine("Download history, browser status and image downloads: OK");
} finally { Directory.Delete(root, true); }

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

sealed class FakeImageHandler : HttpMessageHandler
{
    public int Requests;
    public bool Fail;
    public bool BreakStream;
    public bool Oversize;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        HttpContent content = BreakStream ? new StreamContent(new FailingStream()) : new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        if (Oversize) content.Headers.ContentLength = 50_000_001;
        return Task.FromResult(new HttpResponseMessage(Fail ? System.Net.HttpStatusCode.BadGateway : System.Net.HttpStatusCode.OK) { Content = content });
    }
}

sealed class FailingStream : MemoryStream
{
    bool first = true;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!first) return ValueTask.FromException<int>(new IOException("Interrupted"));
        first = false;
        buffer.Span[0] = 1;
        return ValueTask.FromResult(1);
    }
}
