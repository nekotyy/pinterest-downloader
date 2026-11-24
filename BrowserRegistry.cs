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

sealed class BrowserRegistry
{
    readonly Dictionary<string, BrowserSession> sessions = new();
    public IReadOnlyDictionary<string, BrowserSession> Sessions => sessions;

    public bool Update(SyncPayload payload, List<Pin> pins, DateTime now)
    {
        if (!sessions.TryGetValue(payload.Source, out var session)) sessions[payload.Source] = session = new BrowserSession();
        if (payload.Generation < session.Generation || (payload.Generation == session.Generation && payload.Done < session.Done)) return false;
        session.Name = payload.Browser[..Math.Min(payload.Browser.Length, 50)]; session.LastSeen = now;
        session.Generation = payload.Generation; session.Done = payload.Done; session.Total = payload.Total; session.Pins = pins;
        return true;
    }

    public Dictionary<string, Pin> Select(string? source)
    {
        var result = new Dictionary<string, Pin>();
        foreach (var entry in sessions) {
            if (!string.IsNullOrEmpty(source) && entry.Key != source) continue;
            foreach (var pin in entry.Value.Pins)
                if (!result.TryGetValue(pin.Id, out var existing) || (string.IsNullOrEmpty(existing.Image) && !string.IsNullOrEmpty(pin.Image))) result[pin.Id] = pin;
        }
        return result;
    }

    public static bool Online(BrowserSession session, DateTime now) => now - session.LastSeen < TimeSpan.FromMinutes(2);
}
