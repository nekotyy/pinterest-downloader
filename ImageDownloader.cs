namespace PinterestDownload;

record DownloadResult(string Status, string? Warning = null, bool Saved = false);
record DownloadProgress(long Bytes, long? Total);

sealed class ImageDownloader(HttpClient client, DownloadHistory history)
{
    public async Task<DownloadResult> Download(Pin pin, string folder, bool repeat, Action<DownloadProgress> progress, CancellationToken cancellationToken = default)
    {
        try {
            var oldPath = history.Find(pin.Id, folder);
            if (oldPath != null && !repeat) return new("Уже скачано: " + oldPath);
            if (!Uri.TryCreate(pin.Image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || (uri.Host != "pinimg.com" && !uri.Host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Откройте вкладку, чтобы загрузить изображение");
            cancellationToken.ThrowIfCancellationRequested();
            progress(new(0, null));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            progress(new(0, response.Content.Headers.ContentLength));
            var ext = response.Content.Headers.ContentType?.MediaType switch { "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", _ => throw new Exception("Неподдерживаемый формат изображения") };
            if (response.Content.Headers.ContentLength > 50_000_000) throw new Exception("Изображение слишком большое");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "pinterest_" + pin.Id + ext);
            if (File.Exists(path)) {
                if (!repeat) return new("Уже скачано: " + path);
                path = history.NextPath(folder, pin.Id, ext);
            }
            var temp = Path.Combine(folder, ".pinterest_" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[81920]; long total = 0; int n;
                    while ((n = await input.ReadAsync(buffer, cancellationToken)) > 0) {
                        total += n;
                        if (total > 50_000_000) throw new Exception("Изображение слишком большое");
                        await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                        progress(new(total, response.Content.Headers.ContentLength));
                    }
                }
                cancellationToken.ThrowIfCancellationRequested(); File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            try { history.Record(pin.Id, path); }
            catch (Exception ex) { return new("Сохранено", "Файл сохранён, но историю сохранить не удалось: " + ex.Message, true); }
            return new("Сохранено", Saved: true);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("Отменено"); }
        catch (Exception ex) { return new("Ошибка: " + ex.Message); }
    }
}
