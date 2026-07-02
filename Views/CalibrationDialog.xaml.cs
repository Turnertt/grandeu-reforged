using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Modinator.Views;

// Guided calibration wizard (Settings → Diagnostics → CALIBRATE).
// Walks the user through deriving every version-fragile address from the
// live game: (1) game running + 32-bit check, (2) structural seed derive
// + pin from the Tavern/any level, (3) forge-chain reachability, and an
// optional (4) combat-wave pawn-chain check. Read-only against the game;
// the only write is the overrides.json pin (via Tunables).
public partial class CalibrationDialog : Window
{
    private readonly MainWindow _main;
    private int _step;          // 0=game check, 1=derive, 2=combat (optional), 3=done
    private bool _busy;

    public CalibrationDialog(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        ShowStep0();
    }

    // ── Step content ─────────────────────────────────────────────────

    private void ShowStep0()
    {
        _step = 0;
        LblStep.Text = "Step 1 of 4 — game check";
        LblInstruction.Text =
            "Start Dungeon Defenders (the 32-bit Steam build) and load your save. " +
            "Press CONTINUE when the game is running.";
        BtnSkip.Visibility = Visibility.Collapsed;
    }

    private void ShowStep1()
    {
        _step = 1;
        LblStep.Text = "Step 2 of 4 — derive addresses";
        LblInstruction.Text =
            "Go to the TAVERN (or straight into any level) with your hero spawned, " +
            "then press CONTINUE. The scan takes a few seconds.";
        BtnSkip.Visibility = Visibility.Collapsed;
    }

    private void ShowStep2()
    {
        _step = 2;
        LblStep.Text = "Step 3 of 4 — combat check (optional)";
        LblInstruction.Text =
            "Optional: verify Auto-Kill can see enemies. This needs a LIVE MISSION " +
            "WAVE — it can't be checked from the Tavern. If you're in the Tavern, " +
            "just press SKIP (item features are already fully calibrated). Otherwise " +
            "start a wave and press CONTINUE.";
        BtnSkip.Visibility = Visibility.Visible;
    }

    private void ShowDone(bool combatChecked)
    {
        _step = 3;
        LblStep.Text = "Step 4 of 4 — done";
        LblInstruction.Text = combatChecked
            ? "Calibration complete. All derived values are pinned — they re-derive automatically after the next game patch."
            : "Calibration complete (combat check skipped). Derived values are pinned — they re-derive automatically after the next game patch.";
        BtnSkip.Visibility = Visibility.Collapsed;
        BtnContinue.Visibility = Visibility.Collapsed;
    }

    // ── Step actions ─────────────────────────────────────────────────

    private async void BtnContinue_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        switch (_step)
        {
            case 0: RunGameCheck(); break;
            case 1: await RunDeriveAsync(); break;
            case 2: await RunCombatCheckAsync(); break;
        }
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_step == 2) ShowDone(combatChecked: false);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void RunGameCheck()
    {
        bool? is32 = GameChain.GameIs32Bit();
        if (is32 == null)
        {
            AddResult(false, "Game process", "DunDefGame.exe is not running.");
            return; // stay on step 0
        }
        if (is32 == false)
        {
            AddResult(false, "Game bitness",
                "The running game is 64-bit. This tool only works on the 32-bit " +
                "version of Dungeon Defenders — switch builds and start over.");
            return; // hard stop; user must fix and retry
        }
        AddResult(true, "Game process", "running, 32-bit build");
        ShowStep1();
    }

    private async Task RunDeriveAsync()
    {
        SetBusy(true, "Scanning game memory...");
        try
        {
            MainWindow.CalibrationProbe probe =
                await Task.Run(() => _main.ForceStructuralReseed());

            if (probe.WorldInfo == 0)
            {
                AddResult(false, "World scan",
                    "No character found — the game looks like it's in a menu or " +
                    "loading screen. Get your hero into the Tavern or a level, " +
                    "then press CONTINUE again.");
                return; // stay on step 1
            }
            AddResult(true, "World scan",
                $"WorldInfo 0x{probe.WorldInfo:X8}, {probe.ChainLength} character{(probe.ChainLength == 1 ? "" : "s")} visible");

            if (!probe.Pinned)
            {
                AddResult(false, "Address pin",
                    "Found the world but couldn't verify the player character " +
                    "(it may still be loading). Wait a few seconds and press " +
                    "CONTINUE again.");
                return; // stay on step 1
            }
            AddResult(true, "Address pin",
                $"player-pawn vtable 0x{probe.Seed:X8} derived and saved — future launches start instantly");

            // The wizard claims "saved" — verify the pin actually landed on
            // disk so a blocked write (permissions/AV) surfaces here instead
            // of as a mystery on the next launch.
            if (!System.IO.File.Exists(Tunables.FilePath))
                AddResult(false, "Address save",
                    $"the derived address couldn't be written to {Tunables.FilePath} — " +
                    "scans still work this session, but the next launch will re-learn. " +
                    "Check folder permissions / antivirus.");

            // Forge chain probe — runs automatically off the fresh pawn.
            // Attach the Scanner first, ON THE UI THREAD: GameChain reads
            // via Base.Instance, which is unattached if no Forge/Hero scan
            // ran this session (same fix the Hero Viewer scan needed) — and
            // Base.OpenProcess can raise the choose-process/message dialogs,
            // which must not be constructed on a thread-pool (MTA) thread.
            Base.OpenProcess();
            // Discover the forge-box offset from live memory (a DD1 patch
            // moved ItemBoxEquipments 0x39C → 0x3A8) and pin it, so the
            // Forge Viewer relocates the box without the user touching
            // anything. Only a fingerprint-verified candidate (the
            // ItemBoxEntries parallel array next door — see GameChain) is
            // pinned; offset==0 means "couldn't positively locate it" —
            // usually an empty box (the forge only populates in the Tavern),
            // in which case ReadItemBox reports reachability at the current
            // offset without saving anything.
            (int count, int offset) forge = await Task.Run(() =>
            {
                int pawn = _main.ResolvePlayerPawnAddress();
                int heroMgr = GameChain.ResolveHeroManager(pawn);
                if (heroMgr == 0) return (-1, 0);
                (int found, bool verified, int n) = GameChain.DiscoverItemBox(heroMgr);
                if (verified)
                {
                    Tunables.PinItemBoxOffset(found);
                    return (n, found);
                }
                return (GameChain.ReadItemBox(heroMgr).Count, 0);
            });

            if (forge.count < 0)
                AddResult(false, "Forge chain",
                    "Couldn't reach the item manager. Rescan from the Forge Viewer " +
                    "once you're in the Tavern; if it persists, the game may have " +
                    "changed in a way that needs a tool update.");
            else if (forge.offset != 0)
                AddResult(true, "Forge chain",
                    $"located at HeroManager +0x{forge.offset:X} — {forge.count} item{(forge.count == 1 ? "" : "s")} in the box; " +
                    "offset pinned (re-derives automatically after a patch)");
            else if (forge.count == 0)
                AddResult(true, "Forge chain",
                    "reachable — item box reports 0 items (the forge box only exists " +
                    "in the Tavern; this is normal mid-mission)");
            else
                AddResult(true, "Forge chain", $"reachable — {forge.count} items in the box");

            ShowStep2();
        }
        finally { SetBusy(false, null); }
    }

    private async Task RunCombatCheckAsync()
    {
        SetBusy(true, "Checking the enemy list...");
        try
        {
            MainWindow.CalibrationProbe probe =
                await Task.Run(() => _main.ForceStructuralReseed());

            // None of the "not yet" states below are FAILURES — by the time
            // the user is on this optional step, everything item-related has
            // already calibrated successfully. Render them as ℹ info rows,
            // not ✕, so "you're in the Tavern" doesn't read as "calibration
            // can't detect my character".
            if (probe.WorldInfo == 0 || probe.ChainLength == 0)
            {
                AddInfo("Combat check",
                    "Couldn't see the level just now (loading screens are normal). " +
                    "Make sure a wave is active, then press CONTINUE again — or " +
                    "press SKIP; your items are already fully calibrated.");
                return;
            }
            // ChainLength > 1 alone is true in the TAVERN too (shop NPCs /
            // practice dummies are pawns) — that would falsely certify
            // "Auto-Kill can see the wave" in exactly the place Auto-Kill
            // auto-disables. Require a real gameplay level first.
            if (!probe.InGameplayLevel)
            {
                AddInfo("Combat check",
                    "You're in the Tavern, so there are no enemies to check — " +
                    "that's expected, and everything item-related is already " +
                    "calibrated. Press SKIP to finish, or start a mission wave " +
                    "and press CONTINUE to also verify Auto-Kill.");
                return;
            }
            if (probe.ChainLength <= 1)
            {
                AddInfo("Combat check",
                    "Only your hero is visible — no enemies spawned yet. Start a " +
                    "wave and press CONTINUE again, or press SKIP to finish.");
                return;
            }
            AddResult(true, "Combat check",
                $"{probe.ChainLength} characters visible (hero + enemies) — Auto-Kill can see the wave");
            ShowDone(combatChecked: true);
        }
        finally { SetBusy(false, null); }
    }

    // ── Plumbing ─────────────────────────────────────────────────────

    private void SetBusy(bool busy, string? note)
    {
        _busy = busy;
        BtnContinue.IsEnabled = !busy;
        BtnSkip.IsEnabled = !busy;
        if (busy && note != null) LblStep.Text = note;
    }

    private enum ResultKind { Pass, Fail, Info }

    private void AddResult(bool ok, string title, string detail)
        => AddResult(ok ? ResultKind.Pass : ResultKind.Fail, title, detail);

    // Neutral "not done yet / nothing wrong" row — the optional combat
    // check uses it so a Tavern run doesn't render a scary ✕.
    private void AddInfo(string title, string detail)
        => AddResult(ResultKind.Info, title, detail);

    private void AddResult(ResultKind kind, string title, string detail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock
        {
            Text = kind switch { ResultKind.Pass => "✓", ResultKind.Fail => "✕", _ => "ℹ" },
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Width = 22,
            Foreground = (Brush)FindResource(kind switch
            {
                ResultKind.Pass => "SuccessBrush",
                ResultKind.Fail => "DangerBrush",
                _ => "AccentBrush",
            }),
            VerticalAlignment = VerticalAlignment.Top
        });
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
        text.Inlines.Add(new System.Windows.Documents.Run(title + "  ")
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        });
        text.Inlines.Add(new System.Windows.Documents.Run(detail)
        {
            Foreground = (Brush)FindResource("TextSecondaryBrush")
        });
        row.Children.Add(text);
        ResultsPanel.Children.Add(row);
    }
}
