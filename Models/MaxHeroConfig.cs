using System.IO;
using System.Text.Json;

namespace Modinator;

// Per-field "max" values for the MAX button in HeroEditView. Nullable means
// "skip this field when MAX runs". Apply rules live in HeroEditView.
public class MaxHeroConfig
{
    // Hero stats — always applied.
    public int? HeroHealth { get; set; }
    public int? HeroSpeed { get; set; }
    public int? HeroDamage { get; set; }
    public int? HeroCasting { get; set; }
    public int? HeroSkill1 { get; set; }
    public int? HeroSkill2 { get; set; }

    // Tower stats — always applied.
    public int? TowerHealth { get; set; }
    public int? TowerSpeed { get; set; }
    public int? TowerDamage { get; set; }
    public int? TowerRange { get; set; }

    // Level / experience — only-if-nonzero.
    public int? Level { get; set; }
    public int? Experience { get; set; }

    // Name — always applied if non-empty.
    public string? HeroName { get; set; }

    private static string Path =>
        System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "Modinator", "max_hero.json");

    public static MaxHeroConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<MaxHeroConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new MaxHeroConfig();
    }

    public void Save()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
