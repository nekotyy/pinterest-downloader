namespace PinterestDownload;

static class PinFilter
{
    public static Pin[] Apply(IEnumerable<Pin> pins, string query, bool hideSaved, Func<string, bool> isSaved)
    {
        query = query.Trim();
        return pins.Where(pin => (query.Length == 0 || pin.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || pin.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) && (!hideSaved || !isSaved(pin.Id))).ToArray();
    }

    public static Pin[] Page(IEnumerable<Pin> pins, int page, int pageSize) => pins.Skip(page * pageSize).Take(pageSize).ToArray();
}
