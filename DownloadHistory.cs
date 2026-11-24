using System.Text.Json;

namespace PinterestDownload;

sealed class DownloadHistory
{
    readonly string file;
    Dictionary<string, string> entries = new();
    public string? LoadError { get; }

    public DownloadHistory(string file)
    {
        this.file = file;
        try {
            if (File.Exists(file)) entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? throw new InvalidDataException("История пуста");
        } catch (Exception ex) { LoadError = ex.Message; }
    }

    public string? Find(string id, string folder)
    {
        if (entries.TryGetValue(id, out var saved) && File.Exists(saved)) return saved;
        foreach (var extension in new[] { ".jpg", ".png", ".webp", ".gif" }) {
            var candidate = Path.Combine(folder, "pinterest_" + id + extension);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public string NextPath(string folder, string id, string extension)
    {
        var path = Path.Combine(folder, "pinterest_" + id + extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, "pinterest_" + id + "_" + i + extension);
        return path;
    }

    public void Record(string id, string path)
    {
        if (LoadError != null) throw new InvalidOperationException("Не удалось прочитать историю: " + LoadError);
        var updated = new Dictionary<string, string>(entries) { [id] = path };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(updated)); File.Move(temp, file, true); entries = updated; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
