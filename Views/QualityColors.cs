using System.Windows.Media;

namespace Modinator.Views;

// Single home for the Quality2 → accent-colour ladder (escalating gold
// above Ultimate). Consumed by MainWindow (sidebar/tracked quality dots),
// ForgeViewerView and HeroViewerView (card strips, equipment-row dots) —
// the tiers live in one place so they cannot drift between views.
internal static class QualityColors
{
    internal static Color Get(Quality2 q)
    {
        return q switch
        {
            Quality2.UltimatePlusPlus => Color.FromRgb(255, 225, 120),
            Quality2.UltimatePlus => Color.FromRgb(245, 195, 60),
            Quality2.Ultimate93 => Color.FromRgb(230, 170, 40),
            Quality2.Ultimate => Color.FromRgb(200, 140, 20),
            Quality2.Supreme => Color.FromRgb(130, 70, 190),
            Quality2.Transcendent => Color.FromRgb(40, 140, 200),
            Quality2.Mythical => Color.FromRgb(200, 50, 80),
            Quality2.Godly => Color.FromRgb(200, 160, 30),
            Quality2.Legendary => Color.FromRgb(200, 110, 30),
            Quality2.Epic => Color.FromRgb(140, 60, 200),
            Quality2.Amazing => Color.FromRgb(50, 160, 70),
            Quality2.Powerful => Color.FromRgb(80, 150, 90),
            Quality2.Shining => Color.FromRgb(170, 160, 50),
            Quality2.Polished or Quality2.Sturdy or Quality2.Solid or Quality2.Stocky
                => Color.FromRgb(120, 125, 135),
            Quality2.Worn or Quality2.Torn => Color.FromRgb(100, 100, 105),
            Quality2.Cursed => Color.FromRgb(80, 30, 80),
            _ => Color.FromRgb(120, 125, 135)
        };
    }
}
