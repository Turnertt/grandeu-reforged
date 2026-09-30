namespace Modinator;

// What one item card shows. Plain data (no WPF) so the Forge Viewer, the
// Templates tab and the Item Dupe pickers all feed the same card renderer
// (Views/ItemCard) and therefore look the same.
internal class ItemCardData
{
    public int Address;
    public string Name = "";
    public string BaseName = "";
    public string Description = "";
    public string ForgerName = "";
    public string SearchText = "";
    public ItemUser User = new();

    // Browser / picker extras; the Forge Viewer's own list leaves them unset.
    public string Key = "";        // stable identity across refreshes
    public string Source = "";     // "Forge", "Hero", "Floor" or a player name
    public string? Folder;
    public string? Footer;         // small line under the stats
    public DateTime? Saved;        // templates: enables the "newest first" sort
    public object? Payload;        // ItemTemplate / MultiplayerItem; null = a local item
    // Dupe target picker: how close this item is to the source.
    // 2 = same base item (same archetype), 1 = same kind (same object class,
    // equipment type and, for weapons, weapon type), 0 = neither.
    public int Match;
    public bool Recommended => Match > 0;

    // One live item, read the way the Forge scan reads it. Throws when the
    // address no longer holds a readable item. Worker thread only.
    public static ItemCardData ReadLive(int address)
    {
        var native = DupeMemory.ReadItem(address);
        var user = Base.ItemToUser(native);
        string name = "";
        try { name = ItemNames.DisplayName(unchecked(address - 0x38)); } catch { }
        if (string.IsNullOrWhiteSpace(name)) name = DupeMemory.ItemName(address);
        return new ItemCardData
        {
            Address = address,
            Name = name,
            Description = ReadText(address, "Description"),
            ForgerName = ReadText(address, "ForgerName"),
            User = user,
        };
    }

    private static string ReadText(int address, string field)
    {
        try { return Base.ReadUni<ItemNative>(address, field) ?? ""; }
        catch { return ""; }
    }
}
