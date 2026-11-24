namespace PinterestDownload;

record CloseCommand(string Id, string[] Pins, long ExpiresAt);
record CloseAcknowledgement(string Id, int Closed, int Failed);
record CommandPoll(string Source, CloseAcknowledgement? Ack);

sealed class CloseCommands
{
    readonly Dictionary<string, CloseCommand> pending = new();
    readonly object gate = new();

    public static Dictionary<string, string[]> Targets(BrowserRegistry browsers, string? source, DateTime now, Func<string, bool> isSaved)
    {
        var result = new Dictionary<string, string[]>();
        foreach (var entry in browsers.Sessions) {
            if (!Guid.TryParse(entry.Key, out _) || (!string.IsNullOrEmpty(source) && source != entry.Key) || !BrowserRegistry.Online(entry.Value, now)) continue;
            var pins = entry.Value.Pins.Where(pin => isSaved(pin.Id)).Select(pin => pin.Id).Distinct().ToArray();
            if (pins.Length > 0) result[entry.Key] = pins;
        }
        return result;
    }

    public static Dictionary<string, string[]> AutomaticTargets(BrowserRegistry browsers, string? source, DateTime now, IReadOnlySet<string> saved, bool cancelled, Func<string, bool> isSaved)
        => cancelled ? new() : Targets(browsers, source, now, id => saved.Contains(id) && isSaved(id));

    public bool Queue(string source, IEnumerable<string> ids, DateTimeOffset now)
    {
        var pins = ids.Where(id => id.Length is > 0 and <= 30 && id.All(char.IsAsciiDigit)).Distinct().ToArray();
        if (pins.Length == 0) return false;
        lock (gate) {
            if (PendingLocked(source, now) != null) return false;
            pending[source] = new CloseCommand(Guid.NewGuid().ToString("N"), pins, now.AddMinutes(2).ToUnixTimeMilliseconds());
            return true;
        }
    }

    public CloseCommand? Pending(string source, DateTimeOffset now) { lock (gate) return PendingLocked(source, now); }
    public bool Acknowledge(string source, CloseAcknowledgement acknowledgement, DateTimeOffset now)
    {
        lock (gate) {
            if (PendingLocked(source, now)?.Id != acknowledgement.Id) return false;
            return pending.Remove(source);
        }
    }
    CloseCommand? PendingLocked(string source, DateTimeOffset now)
    {
        if (!pending.TryGetValue(source, out var command)) return null;
        if (command.ExpiresAt <= now.ToUnixTimeMilliseconds()) { pending.Remove(source); return null; }
        return command;
    }
}
