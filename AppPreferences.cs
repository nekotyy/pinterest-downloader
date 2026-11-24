using System.Text.Json;

namespace PinterestDownload;

record AppPreferences(string Theme = "System", bool AutoClose = false, bool AutoSelect = true, int PageSize = 100);

sealed class PreferencesStore
{
    readonly string file;
    public AppPreferences Value { get; private set; } = new();
    public string? LoadError { get; }

    public PreferencesStore(string file)
    {
        this.file = file;
        try {
            if (File.Exists(file)) {
                var value = JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(file)) ?? throw new InvalidDataException("Настройки пусты");
                Validate(value); Value = value;
            }
        } catch (Exception ex) { LoadError = ex.Message; }
    }

    public void Save(AppPreferences value)
    {
        if (LoadError != null) throw new InvalidOperationException("Не удалось прочитать настройки: " + LoadError);
        Validate(value);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, file, true); Value = value; }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    static void Validate(AppPreferences value)
    {
        if (value.Theme is not ("System" or "Light" or "Dark") || value.PageSize is not (50 or 100 or 200))
            throw new InvalidDataException("Недопустимые настройки приложения");
    }
}
