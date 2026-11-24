namespace PinterestDownload;

record DownloadResult(string Status, string? Warning = null);

sealed class ImageDownloader(HttpClient client, DownloadHistory history)
{
    public async Task<DownloadResult> Download(Pin pin, string folder, bool repeat, Action<string> progress)
    {
        try {
            var oldPath = history.Find(pin.Id, folder);
            if (oldPath != null && !repeat) return new("Уже скачано: " + oldPath);
            if (!Uri.TryCreate(pin.Image, UriKind.Absolute, out var uri) || uri.Scheme != "https" || (uri.Host != "pinimg.com" && !uri.Host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Откройте вкладку, чтобы загрузить изображение");
            progress("Скачивание...");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
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
                await using (var input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[81920]; long total = 0; int n;
                    while ((n = await input.ReadAsync(buffer)) > 0) {
                        total += n;
                        if (total > 50_000_000) throw new Exception("Изображение слишком большое");
                        await output.WriteAsync(buffer.AsMemory(0, n));
                        progress($"Скачивание: {total / 1024} КБ" + (response.Content.Headers.ContentLength is long length ? $" из {length / 1024} КБ" : ""));
                    }
                }
                File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            try { history.Record(pin.Id, path); }
            catch (Exception ex) { return new("Сохранено", "Файл сохранён, но историю сохранить не удалось: " + ex.Message); }
            return new("Сохранено");
        } catch (Exception ex) { return new("Ошибка: " + ex.Message); }
    }
}
