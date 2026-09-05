using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Modinator.Themes;

namespace Modinator;

public partial class MainWindow : Window
{
    private DispatcherTimer _timer = null!;
    private Button? _activeNavButton;
    private Base.Genus _currentGenus = Base.Genus.None;
    private ObservableCollection<TrackedItem> _trackedItems = new();

    // Remembers the Tracked panel's row height across hide/show cycles so a
    // user-dragged splitter size survives collapsing it. Seeded to the XAML
    // default (220).
    private GridLength _trackedRowHeight = new GridLength(220);

    // ── Hotkeys ─────────────────────────────────────────────────────
    // Global hotkey registration (RegisterHotKey) so combos work while DD1
    // has focus. Bindings live in HotkeyConfig / hotkeys.json.
    public HotkeyConfig Hotkeys { get; private set; } = new();
    private HotkeyManager? _hotkeyMgr;
    private const int HK_ID_AUTOKILL = 1;
    private const int HK_ID_AUTOG = 2;
    private const int HK_ID_ALWAYS_ON_TOP = 3;
    private const int HK_ID_UNLIMITED_MANA = 4;
    private const int HK_ID_MAX_TOWER_UNITS = 5;

    // Title bar quick toggles (built in code so they can be injected into the
    // per-window slot on ModernWindowStyle without tripping XAML's root-content rules).
    internal ToggleButton TbAlwaysOnTop { get; private set; } = null!;
    internal ToggleButton TbAutoKill { get; private set; } = null!;
    internal ToggleButton TbSimulateG { get; private set; } = null!;
    internal ToggleButton TbUnlimitedMana { get; private set; } = null!;
    internal ToggleButton TbMaxTowerUnits { get; private set; } = null!;

    public MainWindow()
    {
        InitializeComponent();
        WindowExtensions.SetTitleBarContent(this, BuildTitleBarToggles());
        Tunables.EnsureLoaded();   // optional offline override file (idempotent)
        Hotkeys = HotkeyConfig.Load();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _hotkeyMgr?.Dispose();
    }

    private UIElement BuildTitleBarToggles()
    {
        var style = (Style)FindResource("TitleBarToggle");

        TbAlwaysOnTop = new ToggleButton
        {
            Style = style,
            Content = "\uE840",
            ToolTip = "Always On Top",
        };
        TbAlwaysOnTop.Checked += TbAlwaysOnTop_Toggled;
        TbAlwaysOnTop.Unchecked += TbAlwaysOnTop_Toggled;

        TbAutoKill = new ToggleButton
        {
            Style = style,
            Content = "\uE945",
            ToolTip = "Auto Kill — enemies, enemy towers, and crystals",
        };
        TbAutoKill.Checked += TbAutoKill_Toggled;
        TbAutoKill.Unchecked += TbAutoKill_Toggled;

        TbSimulateG = new ToggleButton
        {
            Style = style,
            Content = "G",
            FontFamily = (System.Windows.Media.FontFamily)FindResource("DefaultFontFamily"),
            FontWeight = FontWeights.Bold,
            ToolTip = "Automate 'G'",
        };
        TbSimulateG.Checked += TbSimulateG_Toggled;
        TbSimulateG.Unchecked += TbSimulateG_Toggled;

        TbUnlimitedMana = new ToggleButton
        {
            Style = style,
            Content = "M",
            FontFamily = (System.Windows.Media.FontFamily)FindResource("DefaultFontFamily"),
            FontWeight = FontWeights.Bold,
            ToolTip = "Unlimited Mana — build/upgrade towers",
        };
        TbUnlimitedMana.Checked += TbUnlimitedMana_Toggled;
        TbUnlimitedMana.Unchecked += TbUnlimitedMana_Toggled;

        TbMaxTowerUnits = new ToggleButton
        {
            Style = style,
            Content = "T",
            FontFamily = (System.Windows.Media.FontFamily)FindResource("DefaultFontFamily"),
            FontWeight = FontWeights.Bold,
            ToolTip = "Max Tower Units — raise the map's DU budget cap",
        };
        TbMaxTowerUnits.Checked += TbMaxTowerUnits_Toggled;
        TbMaxTowerUnits.Unchecked += TbMaxTowerUnits_Toggled;

        var separator = new Border
        {
            Width = 1,
            Height = 14,
            Background = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            Margin = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(TbAlwaysOnTop);
        panel.Children.Add(TbAutoKill);
        panel.Children.Add(TbSimulateG);
        panel.Children.Add(TbUnlimitedMana);
        panel.Children.Add(TbMaxTowerUnits);
        panel.Children.Add(separator);
        return panel;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // First-launch disclaimer: not for online / Ranked play, use at your
        // own risk. This runs FIRST, before the 50 ms timer and before global
        // hotkeys are registered — a modal dialog still pumps the message
        // loop, so a registered hotkey could otherwise flip Auto-Kill or the
        // speed multiplier on (and start writing to the game) while the
        // notice was still sitting unaccepted on screen.
        if (Prefs.Current.DisclaimerAcceptedVersion < Prefs.CurrentDisclaimerVersion)
        {
            var dlg = new Views.DisclaimerDialog { Owner = this };
            if (dlg.ShowDialog() != true)
            {
                Application.Current.Shutdown();
                return;
            }
            Prefs.Current.DisclaimerAcceptedVersion = Prefs.CurrentDisclaimerVersion;
            Prefs.Current.Save();
        }

        // Default landing: Welcome screen
        ShowHome();

        // Bind tracked items list
        TrackedList.ItemsSource = _trackedItems;

        // Tracked panel stays reachable whenever it holds items (even off a
        // search tab) so active freezes can be managed — react to every
        // add/remove, including the timer's auto-prune of unloaded items.
        _trackedItems.CollectionChanged += (_, _) => UpdatePanelVisibility();
        UpdatePanelVisibility();

        // Subscribe to backend events
        Base.OnProgressChanged += OnProgressChanged;
        Base.OnResultsChanged += OnResultsChanged;

        // Timer for freeze/renew/simulate
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // Global hotkeys
        _hotkeyMgr = new HotkeyManager(this);
        ApplyHotkeys();

        TxtGameSpeed.Text = _speedMultiplier.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture);

        // Startup snapshot of the DD1 save (de-duplicated against the newest
        // backup, so this is a no-op when nothing changed). Off the UI thread
        // — it touches the filesystem and the registry, never the game.
        _ = System.Threading.Tasks.Task.Run(SaveBackup.OnStartup);
    }

    // Called on startup AND whenever the user rebinds a combo in SettingsView.
    public void ApplyHotkeys()
    {
        if (_hotkeyMgr == null) return;
        _hotkeyMgr.Register(HK_ID_AUTOKILL, Hotkeys.AutoKill,
            () => SetAutoKillEnabled(!AutoKillEnabled));
        _hotkeyMgr.Register(HK_ID_AUTOG, Hotkeys.AutoG,
            () => SetSimulateG(!Base.SimulateG));
        _hotkeyMgr.Register(HK_ID_ALWAYS_ON_TOP, Hotkeys.AlwaysOnTop,
            () => SetAlwaysOnTop(!Topmost));
        _hotkeyMgr.Register(HK_ID_UNLIMITED_MANA, Hotkeys.UnlimitedMana,
            () => SetUnlimitedMana(!UnlimitedManaEnabled));
        _hotkeyMgr.Register(HK_ID_MAX_TOWER_UNITS, Hotkeys.MaxTowerUnits,
            () => SetMaxTowerUnits(!MaxTowerUnitsEnabled));
    }

    public void SaveHotkeys()
    {
        Hotkeys.Save();
        ApplyHotkeys();
    }

    // ── Progress ────────────────────────────────────────────────────

    private void OnProgressChanged(int value)
    {
        // BeginInvoke (async) so the scanner thread doesn't block waiting
        // on the UI to paint — matters for large result sets.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (value < 100)
            {
                ScanProgress.Visibility = Visibility.Visible;
                ScanProgress.Value = value;
                StatusText.Text = "Scanning...";
            }
            else
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                StatusText.Text = "Ready";
            }
        }));
    }

    // ── Results list ────────────────────────────────────────────────

    private void OnResultsChanged(List<int> results)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ResultsList.Items.Clear();
            int count = Math.Min(results.Count, 5000);
            var genus = _currentGenus;
            int structSize = 0;
            if (genus == Base.Genus.Item) structSize = Marshal.SizeOf(typeof(ItemNative));
            else if (genus == Base.Genus.Hero) structSize = Marshal.SizeOf(typeof(HeroNative));

            for (int i = 0; i < count; i++)
            {
                int addr = results[i];
                var ri = new ResultItem
                {
                    Address = addr,
                    Display = Base.AddressToString(addr)
                };

                if (genus == Base.Genus.Misc && i < Base.MiscFloatTracks.Count)
                    ri.IsFloat = Base.MiscFloatTracks[i];

                // Read extra info for the first 500 results
                if (i < 500)
                {
                    try
                    {
                        if (genus == Base.Genus.Item && structSize > 0)
                        {
                            byte[] data = Base.Instance.ReadMemory(addr, structSize);
                            var native = Base.Push<ItemNative>(data);
                            var user = Base.ItemToUser(native);
                            ri.Name = Base.ReadUni<ItemNative>(addr, "EquipmentName");
                            if (string.IsNullOrEmpty(ri.Name))
                                ri.Name = Base.ReadUni<ItemNative>(addr, "BaseEquipmentName");
                            ri.Quality = QualityDisplay.Name(user.Quality2);
                            // Weapon class gate (EWeaponType) lives outside the
                            // marshaled ItemNative — a 1-byte read at
                            // addr + MaxCompat.WeaponTypeOffset. Weapons only;
                            // inner catch so a failed read just omits the tag.
                            string clsSuffix = "";
                            if (user.EquipmentType == EquipmentType.Weapon)
                            {
                                try
                                {
                                    byte[]? wb = Base.Instance.ReadMemory(addr + MaxCompat.WeaponTypeOffset, 1);
                                    if (wb != null && wb.Length > 0)
                                        clsSuffix = "  " + WeaponClass.Name(wb[0]);
                                }
                                catch { }
                            }
                            ri.Extra = $"Lv {user.Level}/{user.MaxLevel}  {user.EquipmentType}{clsSuffix}";
                            ri.QualityColor = new System.Windows.Media.SolidColorBrush(GetQualityWpfColor(user.Quality2));
                        }
                        else if (genus == Base.Genus.Hero && structSize > 0)
                        {
                            ri.Name = Base.ReadUni<HeroNative>(addr, "HeroName");
                            byte[] data = Base.Instance.ReadMemory(addr, structSize);
                            var native = Base.Push<HeroNative>(data);
                            ri.Extra = $"Lv {native.Level}";
                        }
                        else if (genus == Base.Genus.Misc)
                        {
                            byte[] val = Base.Instance.ReadMemory(addr, 4);
                            ri.Name = ri.IsFloat
                                ? BitConverter.ToSingle(val, 0).ToString("N2")
                                : BitConverter.ToInt32(val, 0).ToString("N0");
                        }
                        else if (genus == Base.Genus.Location)
                        {
                            byte[] val = Base.Instance.ReadMemory(addr, 12);
                            float x = BitConverter.ToSingle(val, 0);
                            float y = BitConverter.ToSingle(val, 4);
                            float z = BitConverter.ToSingle(val, 8);
                            ri.Name = $"X:{x:N0} Y:{y:N0} Z:{z:N0}";
                        }
                    }
                    catch { }
                }

                ResultsList.Items.Add(ri);
            }
            ResultsCountText.Text = results.Count.ToString("N0");
        }));
    }

    private static System.Windows.Media.Color GetQualityWpfColor(Quality2 q)
        => Views.QualityColors.Get(q);

    // ── Sidebar navigation ──────────────────────────────────────────

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string view = btn.Tag?.ToString() ?? "";
        NavigateToView(view, btn);
    }

    private void BtnHome_Click(object sender, RoutedEventArgs e) => ShowHome();

    // Advanced section = the raw memory-search tools (Item/Hero/Misc),
    // kept as a fallback but collapsed by default so they don't crowd the
    // sidebar. Toggle button expands/collapses the sub-list.
    private void BtnAdvanced_Click(object sender, RoutedEventArgs e)
        => SetAdvancedExpanded(AdvancedPanel.Visibility != Visibility.Visible);

    private void SetAdvancedExpanded(bool expanded)
    {
        AdvancedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        AdvancedChevron.Text = expanded ? "\uE70E" : "\uE70D"; // ChevronUp / ChevronDown
    }

    // Called by sidebar buttons AND by WelcomeView tiles. `source` is the
    // sidebar button to highlight; null when the call came from a tile, in
    // which case we look up the matching sidebar button ourselves.
    internal void NavigateToView(string view, Button? source = null)
    {
        Button? target = source ?? view switch
        {
            "ItemSearch" => BtnSearchItem,
            "HeroSearch" => BtnSearchHero,
            "MiscSearch" => BtnSearchMisc,
            "ForgeViewer" => BtnForgeViewer,
            "HeroViewer" => BtnHeroViewer,
            "ItemDupe" => BtnItemDupe,
            "Settings" => BtnSettings,
            _ => null,
        };
        SetActiveNavButton(target);

        // The search tools live in the collapsed Advanced section; expand
        // it when one of them is opened so the active highlight is visible.
        if (view is "ItemSearch" or "HeroSearch" or "MiscSearch")
            SetAdvancedExpanded(true);

        // Location search view was removed — tracked Location items still
        // open their editor via double-click in the results list, which
        // uses Base.Genus.Location, so the enum itself stays in use.
        _currentGenus = view switch
        {
            "ItemSearch" => Base.Genus.Item,
            "HeroSearch" => Base.Genus.Hero,
            "MiscSearch" => Base.Genus.Misc,
            _ => Base.Genus.None,
        };
        StatusText.Text = view;

        ContentArea.Content = view switch
        {
            "ItemSearch" => _itemSearchView ??= new Views.ItemSearchView(),
            "HeroSearch" => _heroSearchView ??= new Views.HeroSearchView(),
            "MiscSearch" => _miscSearchView ??= new Views.MiscSearchView(),
            "ForgeViewer" => _forgeViewerView ??= new Views.ForgeViewerView(),
            "HeroViewer" => _heroViewerView ??= new Views.HeroViewerView(),
            "ItemDupe" => _itemDupeView ??= new Views.ItemDupeView(),
            "Settings" => new Views.SettingsView(), // fresh so it re-syncs state each time
            _ => null,
        };

        UpdatePanelVisibility();
    }

    private void ShowHome()
    {
        SetActiveNavButton(null);
        _currentGenus = Base.Genus.None;
        _lastContentBeforeEditor = null;
        StatusText.Text = "Home";
        ContentArea.Content = _welcomeView ??= new Views.WelcomeView();
        UpdatePanelVisibility();
    }

    // Results list (sidebar) + Tracked Items panel (bottom of main content)
    // are search-context UI. Results shows only while a search tab
    // (Item/Hero/Misc) is active — _currentGenus is the existing signal:
    // NavigateToView sets it for those tabs and clears it (None) for
    // Home/Forge/Dupe/Settings. The Tracked panel additionally stays up
    // whenever it still holds items, so freezes remain manageable off-tab.
    // Collapsing the tracked row (height 0 + hidden splitter) lets the
    // active view take the full height; the splitter-resized height is
    // preserved across hide/show in _trackedRowHeight.
    private void UpdatePanelVisibility()
    {
        bool searching = _currentGenus != Base.Genus.None;
        bool showTracked = searching || _trackedItems.Count > 0;
        bool trackedShown = TrackedPanel.Visibility == Visibility.Visible;

        ResultsSection.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;

        if (showTracked == trackedShown) return; // no tracked-panel transition

        if (showTracked)
        {
            TrackedRow.Height = _trackedRowHeight;
            TrackedRow.MinHeight = 80;
        }
        else
        {
            _trackedRowHeight = TrackedRow.Height; // remember a splitter resize
            TrackedRow.Height = new GridLength(0);
            TrackedRow.MinHeight = 0;
        }
        TrackedSplitter.Visibility = showTracked ? Visibility.Visible : Visibility.Collapsed;
        TrackedPanel.Visibility = showTracked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetActiveNavButton(Button? btn)
    {
        if (_activeNavButton != null && _activeNavButton != btn)
        {
            _activeNavButton.Background = System.Windows.Media.Brushes.Transparent;
            _activeNavButton.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }
        _activeNavButton = btn;
        if (btn != null)
        {
            btn.Background = (System.Windows.Media.Brush)FindResource("SurfaceLightBrush");
            btn.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        }
    }

    internal void NavigateBackFromEditor()
    {
        if (_lastContentBeforeEditor != null)
        {
            ContentArea.Content = _lastContentBeforeEditor;
            _lastContentBeforeEditor = null;
        }
        else
        {
            ShowHome();
        }
    }

    // Cached view instances
    private Views.WelcomeView? _welcomeView;
    private Views.ItemSearchView? _itemSearchView;
    private Views.HeroSearchView? _heroSearchView;
    private Views.MiscSearchView? _miscSearchView;
    private Views.ForgeViewerView? _forgeViewerView;
    private Views.HeroViewerView? _heroViewerView;
    private Views.ItemDupeView? _itemDupeView;
    private object? _lastContentBeforeEditor;

    // ── Show editor ─────────────────────────────────────────────────

    internal void ShowEditor(int address, Base.Genus genus, string name, bool isFloat = false)
    {
        // Remember where we came from so the editor's BACK button can return to it.
        _lastContentBeforeEditor = ContentArea.Content;
        ContentArea.Content = genus switch
        {
            Base.Genus.Item => new Views.ItemEditView(address, name),
            Base.Genus.Hero => new Views.HeroEditView(address, name),
            Base.Genus.Misc => new Views.MiscEditView(address, isFloat, name),
            Base.Genus.Location => new Views.LocationEditView(address),
            Base.Genus.Tower => new Views.TowerEditView(address, name),
            _ => null
        };
    }

    // ── Options (called from SettingsView and title bar toggles) ────

    public bool AutoKillEnabled => _autoKillEnabled;
    public bool UnlimitedManaEnabled => _unlimitedMana;
    public bool MaxTowerUnitsEnabled => _maxTowerUnits;

    // Read-only diagnostics surface (Settings → Diagnostics card).
    public uint CurrentPawnVtable => _pawnVtable;
    public uint LiveGameStamp => _liveGameStamp;

    // ── Calibration (Settings wizard) ──────────────────────────────
    // Drop the seed + caches and re-derive everything from live memory,
    // then pin from the PRI-verified chain TAIL only (the local player
    // pawn) — same rule as the AutoKillTick auto-pin; the structural
    // scan's first match is never persisted (DECISIONS.md). Read-only
    // against the game except the overrides.json pin. Safe to call from
    // a background task (same tolerated raciness as ResolvePlayerPawnAddress).
    public sealed class CalibrationProbe
    {
        public int  WorldInfo;
        public int  PlayerPawn;
        public int  ChainLength;
        public uint Seed;
        public bool Pinned;
        // GRI reports a gameplay level (not tavern/lobby/loading). Lets the
        // wizard's combat check tell tavern NPCs apart from a real wave —
        // ChainLength > 1 alone is true in the tavern too (shop NPCs).
        public bool InGameplayLevel;
    }

    public CalibrationProbe ForceStructuralReseed()
    {
        var probe = new CalibrationProbe();
        // Own the caches for the whole sweep: blocks until an in-flight AK
        // tick drains; ticks arriving meanwhile skip (Monitor.TryEnter in
        // AutoKillTick). Also serializes two concurrent reseeds (Calibrate
        // + a viewer's auto-recalibrate) instead of letting them clobber
        // each other's _pawnVtable/_cachedWorldInfo mid-derivation.
        lock (_scanStateGate)
        {
            _pawnVtable = 0;
            _cachedWorldInfo = 0;
            _cachedPlayerPawn = 0;

            int wi = FindWorldInfoViaPawnScan();
            probe.WorldInfo = wi;
            if (wi == 0) return probe;
            _cachedWorldInfo = wi;
            probe.InGameplayLevel = IsInGameplayLevel();

            // Walk the pawn list, collecting each pawn + its PRI, then
            // select the LOCAL PLAYER by verification — not by position
            // (see SelectLocalPlayerPawn: the tail is an NPC in the tavern).
            byte[]? plData = AKRead(wi + 0x041C, 4);
            if (plData == null) return probe;
            int cur = BitConverter.ToInt32(plData, 0);
            var pawns = new List<(int addr, uint pri)>(16);
            var visited = new HashSet<int>();
            while (IsHeapPtr(cur) && visited.Add(cur) && visited.Count <= AK_CHAIN_MAX)
            {
                byte[]? pd = AKRead(cur, 0x32C);
                if (pd == null || pd.Length < 0x32C) break;
                uint pri = 0;
                byte[]? priB = AKRead(cur + OFF_PAWN_PRI, 4);
                if (priB != null && priB.Length >= 4)
                    pri = BitConverter.ToUInt32(priB, 0);
                pawns.Add((cur, pri));
                int np = BitConverter.ToInt32(pd, 0x0230);
                if (np == 0) break;
                cur = np;
            }
            probe.ChainLength = pawns.Count;
            int player = SelectLocalPlayerPawn(pawns);
            probe.PlayerPawn = player;
            if (player == 0) return probe;
            _cachedPlayerPawn = player;

            // Pin only from a verified player pawn (PRI check), never the
            // scan's first match.
            uint pv = TryPinPlayerSeed(player);
            if (pv != 0) { probe.Seed = pv; probe.Pinned = true; }
            return probe;
        }
    }

    // ── Diagnostic report (read-only) ───────────────────────────────
    //
    // Release builds compile out Base.Log, so when a feature misbehaves on
    // someone else's machine there is nothing to look at. This walks the
    // whole chain once and renders every link as text the user can copy and
    // send back. Read-only: no game writes, no pins — the same reads the
    // scans already do, taken under the scan gate so it can't race the AK
    // tick. Deliberately reports EVERY PRI-verified pawn, because the
    // "which pawn is the local player" step is the one that silently
    // degrades (mana + Forge + Hero all fail together when it picks wrong,
    // while Auto-Kill and Max Tower Units keep working — they never touch
    // the controller).
    public string BuildDiagnosticReport()
    {
        var sb = new System.Text.StringBuilder(2048);
        void L(string s) => sb.AppendLine(s);

        L("Grandeu: Reforged — diagnostic report");
        L(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            L($"app        : v{v?.ToString(3) ?? "?"}  {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}" +
              $"  on {Environment.OSVersion.Version}  {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        }
        catch { }

        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
            if (procs.Length == 0) L("game       : NOT RUNNING");
            else
            {
                bool? is32 = GameChain.GameIs32Bit();
                L($"game       : running  pid={procs[0].Id}  instances={procs.Length}  " +
                  (is32 == true ? "32-bit" : is32 == false ? "64-BIT (UNSUPPORTED)" : "bitness unknown"));
            }
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        catch (Exception ex) { L("game       : lookup failed — " + ex.Message); }

        L($"attached   : scanner pid={Base.AttachedPid}  (0 = not attached)");
        L($"log file   : {Base.LogPath}");
        L($"             {(System.IO.File.Exists(Base.LogPath) ? "exists — DEBUG build, send this file too" : "not present — this is a RELEASE build (no logging)")}");

        lock (_scanStateGate)
        {
            try
            {
                if (GetAKHandle() == IntPtr.Zero)
                {
                    L($"handle     : COULD NOT OPEN the game process (win32 error " +
                      $"{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}). " +
                      "If this is 5 (access denied), run the tool as administrator.");
                    L("(nothing below can be read without a handle)");
                    return sb.ToString();
                }
                L($"handle     : open  (targeting pid {_akTargetPid})");

                EnsureGameBuildStampChecked();
                L($"build stamp: live=0x{_liveGameStamp:X8}  saved=0x{Tunables.GameTimeDateStamp:X8}" +
                  (Tunables.GameTimeDateStamp != 0 && _liveGameStamp != 0
                      ? (Tunables.GameTimeDateStamp == _liveGameStamp ? "  (match)" : "  (GAME UPDATED — will re-learn)")
                      : ""));
                L($"seed       : 0x{_pawnVtable:X8}  ({(_pawnVtable == 0 ? "not learned yet" : "in use")})");

                static string Off(int cur, int def) => $"0x{cur:X}({(cur == def ? "default" : "learned")})";
                L($"offsets    : box {Off(GameChain.ItemBoxOffset, Tunables.DefaultItemBoxOffset)}  " +
                  $"heroes {Off(GameChain.LocalHeroesOffset, Tunables.DefaultLocalHeroesOffset)}  " +
                  $"manager {Off(GameChain.HeroManagerOffset, Tunables.DefaultHeroManagerOffset)}");
                // The learned-offset file verbatim: a pin carried over from a
                // different game build, or one that landed on a look-alike,
                // is invisible in the summary line above.
                try
                {
                    L($"overrides  : {Tunables.FilePath}");
                    L(System.IO.File.Exists(Tunables.FilePath)
                        ? "             " + System.IO.File.ReadAllText(Tunables.FilePath)
                              .Replace("\r", "").Replace("\n", "\n             ")
                        : "             (no file — everything is at compiled defaults)");
                }
                catch (Exception ex) { L("             (unreadable: " + ex.Message + ")"); }

                int wi = _cachedWorldInfo;
                if (wi == 0 || !ValidateCachedWorldInfo()) wi = FindWorldInfoViaPawnScan();
                if (wi == 0) { L("WorldInfo  : NOT FOUND (menu / loading screen, or the scan failed)"); return sb.ToString(); }
                _cachedWorldInfo = wi;
                L($"WorldInfo  : 0x{wi:X8}   gameplay level: {(IsInGameplayLevel() ? "yes" : "no (tavern/lobby/loading)")}");

                // AWorldInfo.Game (AGameInfo*) exists ONLY on the server/host
                // in UE3 — a client's copy is None. This is the cheapest
                // host-vs-client tell we can read without a new offset, and
                // it matters: a client also loses Pawn.Controller (below),
                // which is the exact hop Mana/Forge/Hero depend on.
                int gameInfo = ReadU32(wi + OFF_WI_GAME);
                int griPtr = ReadU32(wi + OFF_WI_GRI);
                L($"WI.Game    : 0x{gameInfo:X8}  ({(IsHeapPtr(gameInfo) ? "present — this machine is the HOST/solo" : "NULL — this machine is a CLIENT (not hosting)")})");
                L($"WI.GRI     : 0x{griPtr:X8}");

                // Walk the pawn list exactly as the scans do.
                int head = ReadU32(wi + 0x041C);
                var pawns = new List<(int addr, uint pri)>(32);
                var seen = new HashSet<int>();
                int cur = head;
                while (IsHeapPtr(cur) && seen.Add(cur) && seen.Count <= AK_CHAIN_MAX)
                {
                    byte[]? pd = AKRead(cur, 0x32C);
                    if (pd == null || pd.Length < 0x32C) break;
                    pawns.Add((cur, (uint)ReadU32(cur + OFF_PAWN_PRI)));
                    int np = BitConverter.ToInt32(pd, 0x0230);
                    if (np == 0) break;
                    cur = np;
                }
                L($"pawn chain : {pawns.Count} pawns (head 0x{head:X8})");

                int chosen = SelectLocalPlayerPawn(pawns, out var pick);
                L($"selected   : 0x{chosen:X8}   confidence={pick}" +
                  (pick < PlayerPawnPick.LocalPlayer
                      ? "   <-- PROBLEM: no pawn had a ULocalPlayer, so this is a GUESS"
                      : ""));

                // EVERY pawn, not just PRI-verified ones. On a client
                // Pawn.Controller (+0x22C) is commonly null for all pawns
                // including your own, which kills Mana/Forge/Hero while
                // leaving Auto-Kill and Max Tower Units working — so the
                // count of non-null controllers is the key number here.
                int shown = 0, priCount = 0, ctlCount = 0, chainCount = 0;
                L("pawns      : (pawn / PRI / controller+0x22C / ULocalPlayer / viewport / heromanager)");
                foreach (var p in pawns)
                {
                    bool priOk = IsVerifiedPri(p.pri);
                    if (priOk) priCount++;
                    int ctl = ReadU32(p.addr + OFF_PAWN_CONTROLLER);
                    if (IsHeapPtr(ctl)) ctlCount++;
                    int lp = IsHeapPtr(ctl) ? ReadU32(ctl + GameChain.OFF_CONTROLLER_PLAYER) : 0;
                    int vp = IsHeapPtr(lp) ? ReadU32(lp + GameChain.OFF_PLAYER_VIEWPORT) : 0;
                    int hmgr = IsHeapPtr(vp) ? ReadU32(vp + GameChain.HeroManagerOffset) : 0;
                    if (IsHeapPtr(hmgr)) chainCount++;
                    if (shown < 16)
                    {
                        L($"             0x{p.addr:X8}  pri=0x{p.pri:X8}{(priOk ? "*" : " ")}  ctl=0x{ctl:X8}  " +
                          $"lp=0x{lp:X8}  vp=0x{vp:X8}  hm=0x{hmgr:X8}" +
                          (p.addr == chosen ? "  <-- selected" : ""));
                        shown++;
                    }
                }
                if (pawns.Count > shown) L($"             ...{pawns.Count - shown} more not listed");
                L($"totals     : {priCount} PRI-verified (2+ means multiplayer), {ctlCount} with a Controller, " +
                  $"{chainCount} reaching a HeroManager");
                if (ctlCount == 0 && pawns.Count > 0)
                    L("             ^^ NO pawn has a Controller. That is the classic CLIENT signature: " +
                      "UE3 does not replicate Pawn.Controller, so Mana/Forge/Hero (which all start at " +
                      "pawn+0x22C) cannot work this way while Auto-Kill and Max Tower Units still do.");

                // Raw mana values for the selected pawn, whether or not the
                // toggle is on — "mana doesn't work" is usually the sanity
                // gate refusing an implausible MaxManaPower, and the only way
                // to tell that from a dead pointer is to see both numbers.
                int mctl = IsHeapPtr(chosen) ? ReadU32(chosen + OFF_PAWN_CONTROLLER) : 0;
                if (IsHeapPtr(mctl))
                    L($"mana       : controller=0x{mctl:X8}  cur(+0x{OFF_PC_MANAPOWER:X})={ReadU32(mctl + OFF_PC_MANAPOWER)}  " +
                      $"max(+0x{OFF_PC_MAXMANAPOWER:X})={ReadU32(mctl + OFF_PC_MAXMANAPOWER)}  (gate wants max in 1..1000000)");
                else
                    L("mana       : no controller on the selected pawn");
                L($"mana write : {(_unlimitedMana ? (_manaGateReason ?? "writing normally") : "toggle is off")}");

                // Forge / Hero payload, read through the selected pawn.
                int hmSel = IsHeapPtr(chosen) ? ReadU32(ReadU32(ReadU32(chosen + OFF_PAWN_CONTROLLER)
                                + GameChain.OFF_CONTROLLER_PLAYER) + GameChain.OFF_PLAYER_VIEWPORT) : 0;
                hmSel = IsHeapPtr(hmSel) ? ReadU32(hmSel + GameChain.HeroManagerOffset) : 0;
                if (!IsHeapPtr(hmSel))
                {
                    L("HeroManager: NOT REACHABLE from the selected pawn — this is why Forge/Hero find nothing");
                }
                else
                {
                    L($"HeroManager: 0x{hmSel:X8}");
                    L($"  item box  : +0x{GameChain.ItemBoxOffset:X} num={ReadU32(hmSel + GameChain.ItemBoxOffset + 4)}" +
                      $"   next-field num={ReadU32(hmSel + GameChain.ItemBoxOffset + 0x10)} (the ItemBoxEntries fingerprint)");
                    L($"  heroes    : +0x{GameChain.LocalHeroesOffset:X} local num={ReadU32(hmSel + GameChain.LocalHeroesOffset + 4)}" +
                      $"   active num={ReadU32(hmSel + GameChain.ActiveHeroesOffset + 4)}");
                    // The counts above are only "what we read at the pinned
                    // offsets". THIS is what differs between two saves: the
                    // real shape of every array in the window, classified by
                    // the same gates discovery uses. An empty box, a roster
                    // that fails the dense+sparse pair test, or a box sitting
                    // at an offset we never learned all show up here.
                    L("  window    : (every TArray-shaped field off the HeroManager)");
                    sb.Append(GameChain.DescribeArrayWindow(hmSel));
                    if (Base.AttachedPid == 0)
                        L("    NOTE: scanner not attached — window read may be empty; open a scan tab once and retry");
                }
            }
            catch (Exception ex) { L("report failed part-way: " + ex.GetType().Name + ": " + ex.Message); }
        }
        return sb.ToString();
    }

    public void SetAlwaysOnTop(bool on)
    {
        Topmost = on;
        if (TbAlwaysOnTop.IsChecked != on) TbAlwaysOnTop.IsChecked = on;
        SyncSettingsView();
    }

    public void SetSimulateG(bool on)
    {
        Base.SimulateG = on;
        if (TbSimulateG.IsChecked != on) TbSimulateG.IsChecked = on;
        SyncSettingsView();
    }

    // Unlimited mana (build/upgrade towers). Shares the Auto-Kill background
    // loop — RefreshAkLoop starts it when this is the only active feature,
    // and AutoKillTick tops the player controller's ManaPower up to its
    // MaxManaPower each tick. Not persisted (parity with
    // GrandeuReforged-Source, which has no AppPrefs); default off.
    public void SetUnlimitedMana(bool on)
    {
        _unlimitedMana = on;
        if (TbUnlimitedMana.IsChecked != on) TbUnlimitedMana.IsChecked = on;
        RefreshAkLoop();
        SyncSettingsView();
    }

    // Max tower units — raises ADunDefGameReplicationInfo.MaxTowerUnits (the
    // per-map DU budget cap) so far more towers can be placed. Shares the
    // Auto-Kill loop like the other passive toggles. Not persisted; default
    // off (parity with GrandeuReforged-Source).
    public void SetMaxTowerUnits(bool on)
    {
        _maxTowerUnits = on;
        if (TbMaxTowerUnits.IsChecked != on) TbMaxTowerUnits.IsChecked = on;
        RefreshAkLoop();
        SyncSettingsView();
    }

    public void SetAutoKillEnabled(bool on)
    {
        _autoKillEnabled = on;
        if (TbAutoKill.IsChecked != on) TbAutoKill.IsChecked = on;
        if (on)
        {
            _heroClasses.Clear();  // re-learn from scratch each time we turn on
            _loggedUnkilledPawns.Clear();
            _akTicksSinceEnable = 0;
            StatusText.Text = "Auto Kill: starting...";
        }
        else
        {
            StatusText.Text = "Ready";
        }
        RefreshAkLoop();
        SyncSettingsView();
    }

    // Starts/stops the background world-walk loop based on whether any
    // feature that uses it is active: auto-kill, or a non-default speed
    // multiplier that needs continuous TimeDilation writes.
    private void RefreshAkLoop()
    {
        bool shouldRun = _autoKillEnabled || _speedMultiplier != 1.0f || _unlimitedMana || _maxTowerUnits;
        if (shouldRun)
        {
            _cachedWorldInfo = 0;
            _cachedPlayerPawn = 0;
            _akLastValidated = DateTime.MinValue;
            ResetAutoKillHandle();
            StartAutoKillLoop();
        }
        else
        {
            StopAutoKillLoop();
        }
    }

    // Forge Viewer calls this between resolve attempts. A single stale read
    // anywhere in the pawn→HeroManager chain makes the resolve return null;
    // dropping the cached WorldInfo/pawn and the AK handle forces a fully
    // fresh re-resolve on the retry (ResolvePlayerPawnAddress re-finds
    // WorldInfo when _cachedWorldInfo==0; GetAKHandle reopens a zeroed
    // handle). No offsets/chains touched — same cache reset RefreshAkLoop
    // already performs, minus the loop start/stop.
    public void InvalidatePawnScanCache()
    {
        // Under the scan gate: this CLOSES the shared AK handle, and without
        // the lock it could do so in the middle of a tick's reads (or a
        // structural sweep) on the AK task — same ownership rule as
        // ForceStructuralReseed, which also drops these fields.
        lock (_scanStateGate)
        {
            _cachedWorldInfo = 0;
            _cachedPlayerPawn = 0;
            _akLastValidated = DateTime.MinValue;
            ResetAutoKillHandle();
        }
    }

    private void BtnGameSpeedApply_Click(object sender, RoutedEventArgs e)
        => ApplySpeedFromTextBox();

    private void TxtGameSpeed_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            ApplySpeedFromTextBox();
            e.Handled = true;
        }
    }

    private void BtnSpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn
            && btn.Tag is string tag
            && float.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v))
        {
            ApplySpeed(v);
        }
    }

    private void ApplySpeedFromTextBox()
    {
        if (!float.TryParse((TxtGameSpeed.Text ?? "").Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v))
        {
            StatusText.Text = "Speed: invalid number";
            return;
        }
        ApplySpeed(v);
    }

    // Clamp to a wide but still-sane range. Values above ~3 start causing
    // physics/animation glitches, and above ~10 AI pathing visibly desyncs
    // from the deltatime, but the cap is 15 so power users can blitz
    // farming. Below ~0.05 some Kismet sequences stop firing entirely.
    // Writing 1.0 explicitly once on reset so the game leaves slo-mo/fast
    // cleanly even if the tick loop is about to stop.
    private void ApplySpeed(float v)
    {
        v = Math.Clamp(v, 0.05f, 15.0f);
        TxtGameSpeed.Text = v.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture);
        _speedMultiplier = v;
        if (v == 1.0f && _cachedWorldInfo != 0)
            AKWrite(_cachedWorldInfo + OFF_WI_TIMEDILATION, FloatBits(1.0f));
        RefreshAkLoop();
        StatusText.Text = $"Speed: {v:0.##}x";
    }

    private static int FloatBits(float f)
        => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    // If Settings is the current content, re-pull state into its switches so
    // hotkey toggles visibly flip the sliders.
    private void SyncSettingsView()
    {
        if (ContentArea.Content is Views.SettingsView sv) sv.SyncFromState();
    }

    public void ShowMoreOptionsDialog()
    {
        var dlg = new Views.MoreOptionsDialog();
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    // Route title-bar toggles through the Set* methods so state, title-bar
    // icon, and Settings switches all stay in sync from one code path.

    private void TbAlwaysOnTop_Toggled(object sender, RoutedEventArgs e)
        => SetAlwaysOnTop(TbAlwaysOnTop.IsChecked == true);

    private void TbAutoKill_Toggled(object sender, RoutedEventArgs e)
        => SetAutoKillEnabled(TbAutoKill.IsChecked == true);

    private void TbSimulateG_Toggled(object sender, RoutedEventArgs e)
        => SetSimulateG(TbSimulateG.IsChecked == true);

    private void TbUnlimitedMana_Toggled(object sender, RoutedEventArgs e)
        => SetUnlimitedMana(TbUnlimitedMana.IsChecked == true);

    private void TbMaxTowerUnits_Toggled(object sender, RoutedEventArgs e)
        => SetMaxTowerUnits(TbMaxTowerUnits.IsChecked == true);

    // ── Results double-click → add to tracked items ─────────────────

    private void ResultsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not ResultItem item) return;
        if (_currentGenus == Base.Genus.None) return;

        AddToTrackedItems(item.Address, _currentGenus, item.IsFloat);
    }

    private void AddToTrackedItems(int address, Base.Genus genus, bool isFloat = false)
    {
        // Don't add duplicates — if already tracked, just highlight the existing row
        foreach (var existing in _trackedItems)
        {
            if (existing.Address == address && existing.Genus == genus)
            {
                TrackedList.SelectedItem = existing;
                TrackedList.ScrollIntoView(existing);
                return;
            }
        }

        // Get description
        string? desc = null;
        try { desc = Base.GetDescription(address, genus, isFloat); }
        catch { }
        // Strip DD1 <color:r,g,b> runs for display only — null still means
        // "unreadable" and must survive the check below.
        if (desc != null) desc = Watermark.StripColorTags(desc);

        if (desc == null)
        {
            Base.RaiseMessage("Could not read item at that address — it may have been unloaded.", "Error");
            return;
        }

        var tracked = new TrackedItem
        {
            Address = address,
            Genus = genus,
            IsFloat = isFloat,
            AddressText = Base.AddressToString(address),
            TypeText = genus.ToString(),
            Description = desc
        };

        // Read extra fields for Item/Hero types
        try
        {
            if (genus == Base.Genus.Item)
            {
                int size = Marshal.SizeOf(typeof(ItemNative));
                byte[] data = Base.Instance.ReadMemory(address, size);
                var native = Base.Push<ItemNative>(data);
                var user = Base.ItemToUser(native);
                tracked.Quality = QualityDisplay.Name(user.Quality2);
                tracked.Level = $"{user.Level} / {user.MaxLevel}";
                tracked.ForgerName = Base.ReadUni<ItemNative>(address, "ForgerName") ?? "";
            }
            else if (genus == Base.Genus.Hero)
            {
                int size = Marshal.SizeOf(typeof(HeroNative));
                byte[] data = Base.Instance.ReadMemory(address, size);
                var native = Base.Push<HeroNative>(data);
                var user = Base.HeroToUser(native);
                tracked.Level = user.Level.ToString();
            }
        }
        catch { }

        _trackedItems.Add(tracked);
        SelectionText.Text = $"{_trackedItems.Count} tracked";
        TrackedList.SelectedItem = tracked;
        TrackedList.ScrollIntoView(tracked);
    }

    // ── Tracked list double-click → open editor ─────────────────────

    private void TrackedList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackedList.SelectedItem is not TrackedItem item) return;
        ShowEditor(item.Address, item.Genus, item.Description, item.IsFloat);
    }

    // ── Tracked list context menu ───────────────────────────────────

    private void TrackedItem_OpenEditor(object sender, RoutedEventArgs e)
    {
        if (TrackedList.SelectedItem is not TrackedItem item) return;
        ShowEditor(item.Address, item.Genus, item.Description, item.IsFloat);
    }

    private void TrackedItem_Remove(object sender, RoutedEventArgs e)
    {
        var selected = TrackedList.SelectedItems.Cast<TrackedItem>().ToList();
        foreach (var item in selected)
        {
            // Remove freeze if active
            if (item.Genus == Base.Genus.Misc && Base.MiscFreeze.ContainsKey(item.Address))
                Base.MiscFreeze.Remove(item.Address);
            if (item.Genus == Base.Genus.Location && Base.LocationFreeze.ContainsKey(item.Address))
                Base.LocationFreeze.Remove(item.Address);

            _trackedItems.Remove(item);
        }
        SelectionText.Text = $"{_trackedItems.Count} tracked";
    }

    private void TrackedItem_CopyAddress(object sender, RoutedEventArgs e)
    {
        if (TrackedList.SelectedItem is TrackedItem item)
        {
            try { Clipboard.SetText(item.Address.ToString("X")); }
            catch { }
        }
    }

    private void TrackedItem_Freeze(object sender, RoutedEventArgs e)
    {
        if (TrackedList.SelectedItem is not TrackedItem item) return;
        if (item.Genus != Base.Genus.Misc && item.Genus != Base.Genus.Location) return;

        item.IsFrozen = !item.IsFrozen;

        try
        {
            if (item.Genus == Base.Genus.Misc)
                Base.HandleManaFreeze(item.Address, item.IsFrozen);
            else
                Base.HandleLocationFreeze(item.Address, item.IsFrozen);
        }
        catch
        {
            _trackedItems.Remove(item);
        }

        // Update type text to show freeze state
        item.TypeText = item.IsFrozen ? item.Genus + " *" : item.Genus.ToString();
        TrackedList.Items.Refresh();
    }

    // ── Timer — renew descriptions, freeze values, simulate G ───────

    private bool _inTimerTick;

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // Re-entrancy guard. Anything inside a tick that pumps the message
        // loop — a modal dialog, most obviously — lets this 50 ms timer fire
        // again on top of the tick already in progress. With the guard, a
        // future caller that shows a dialog from the tick degrades to one
        // dialog instead of an unclosable stack of them.
        if (_inTimerTick) return;
        _inTimerTick = true;
        try { Timer_TickCore(); }
        finally { _inTimerTick = false; }
    }

    private void Timer_TickCore()
    {
        // Renew tracked item descriptions
        if (Base.Renew && (DateTime.Now - Base.RenewDate).TotalMilliseconds >= Base.RenewTime)
        {
            Base.RenewDate = DateTime.Now;
            foreach (var item in _trackedItems.ToList())
            {
                try
                {
                    string? desc = Base.GetDescription(item.Address, item.Genus, item.IsFloat);
                    if (desc == null)
                    {
                        _trackedItems.Remove(item);
                    }
                    else
                    {
                        item.Description = Watermark.StripColorTags(desc);
                    }
                }
                catch
                {
                    _trackedItems.Remove(item);
                }
            }
            TrackedList.Items.Refresh();
            SelectionText.Text = $"{_trackedItems.Count} tracked";
        }

        // Freeze values
        if ((DateTime.Now - Base.FreezeDate).TotalMilliseconds >= Base.FreezeTime)
        {
            Base.FreezeDate = DateTime.Now;
            foreach (var item in _trackedItems.ToList())
            {
                if (!item.IsFrozen) continue;
                try
                {
                    if (item.Genus == Base.Genus.Misc && Base.MiscFreeze.ContainsKey(item.Address))
                    {
                        byte[] bytes = BitConverter.GetBytes(Base.MiscFreeze[item.Address]);
                        Base.Instance.WriteMemory(item.Address, bytes);
                    }
                    else if (item.Genus == Base.Genus.Location && Base.LocationFreeze.ContainsKey(item.Address))
                    {
                        byte[] bytes = BitConverter.GetBytes(Base.LocationFreeze[item.Address]);
                        Base.Instance.WriteMemory(item.Address + 8, bytes);
                    }
                }
                catch
                {
                    _trackedItems.Remove(item);
                }
            }
        }

        // Simulate G press
        if (Base.SimulateG && (DateTime.Now - Base.SimulateDate).TotalMilliseconds >= Base.SimulateTime)
        {
            Base.SimulateDate = DateTime.Now;
            SimulateGPress();
        }

        // Auto Kill runs on its own background loop (see StartAutoKillLoop) so
        // long scans or other UI-thread blocking can't delay it.
    }

    // ── Simulate G key press (ported from original) ─────────────────

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, int message, int wParam, long lParam);

    // Backoff + one-shot warning for the "game isn't running" case below.
    private DateTime _simGRetryAfter = DateTime.MinValue;
    private bool _simGWarned;

    private void SimulateGPress()
    {
        // NEVER the notifying Base.OpenProcess() here. This runs off the 50 ms
        // UI timer, and the notifying overload raises a modal when the game is
        // missing — a modal pumps the message loop, so the timer ticks again
        // behind it and raises another, one per second, until the user can
        // neither close the app nor switch Simulate G back off. Attach
        // quietly, say so in the status bar once, and leave the toggle alone
        // so it simply starts working when the game launches (same behaviour
        // as Auto-Kill's "game not running" idle).
        if (DateTime.Now < _simGRetryAfter) return;

        if (!Base.OpenProcess(notify: false))
        {
            // Don't re-enumerate every process once a second while the game
            // is closed — the failing path is the expensive one.
            _simGRetryAfter = DateTime.Now.AddSeconds(2);
            if (!_simGWarned)
            {
                _simGWarned = true;
                StatusText.Text = "Simulate G: waiting — not attached to DunDefGame.exe.";
            }
            return;
        }

        _simGWarned = false;
        if (Base.MainWindow == IntPtr.Zero) return;

        PostMessage(Base.MainWindow, 256, 71, 2228225L);
        PostMessage(Base.MainWindow, 257, 71, 3223453697L);
    }

    // ── Auto Kill ───────────────────────────────────────────────────
    //
    // DD1 uses UE3's pawn linked list to track all actors.
    // From the SDK:
    //   ADunDefDamageableTarget: Health +0x021C, MaxHealth +0x0220
    //   APawn: NextPawn +0x0230
    //   AActor (DD): WorldInfo +0x0048
    //   AWorldInfo: PawnList +0x041C
    //
    // We find WorldInfo by reading it from any pawn we can locate.
    // Then we walk PawnList and set Health=0 for non-player enemies.

    private bool _autoKillEnabled;
    // Game-speed multiplier. 1.0f means "don't write" — the game's own
    // value stays untouched. Any other value is written to
    // WorldInfo.TimeDilation (+0x0374) every tick so the game can't
    // quietly restore it on map changes or Kismet-driven slo-mo.
    private volatile float _speedMultiplier = 1.0f;
    // Unlimited-mana toggle. When on, the AK loop tops the local player
    // controller's ManaPower up to its MaxManaPower every tick (see the
    // OFF_PC_* constants). Independent of Auto-Kill.
    private volatile bool _unlimitedMana;
    // Max-tower-units toggle. When on, the AK loop pins the GRI's
    // MaxTowerUnits (the map's DU budget cap) to a large value every tick
    // (see OFF_GRI_MAXTOWERUNITS). Independent of Auto-Kill.
    private volatile bool _maxTowerUnits;
    private int _cachedWorldInfo;
    private int _cachedPlayerPawn;                    // address of the player pawn to skip
    private DateTime _akLastValidated = DateTime.MinValue;
    private const int AK_TICK_MS = 100;               // how often the background loop runs
    private const int AK_VALIDATE_MS = 2000;          // how often we re-verify the cache
    // Hard cap on the pawn chain walk (runaway-list guard, not a target
    // budget). Was 300 — high-wave/modded content can field more live
    // pawns than that, and everything past the cap was silently never
    // killed. Walk cost is ~2 small reads per pawn, so 1000 is still
    // trivially cheap per 100 ms tick.
    private const int AK_CHAIN_MAX = 1000;
    // Sanity ceiling for "is this int plausibly an HP value, not pointer
    // garbage". Historically it kept getting outgrown by real content:
    // 5M/10M (4.0.0.4 era) → 500M (2026-05: Ogre 23.5M) → and 500M in
    // turn silently broke high-HP late-game/modded enemies at three
    // gates (scan pre-filter, WI-validate head-pawn eviction, tail hero
    // gate). Default is now int.MaxValue — effectively off; the scan's
    // real noise rejection is the vtable/backref/loop-closure chain.
    // Sourced from Tunables so it can still be hand-tuned down via the
    // override file without a rebuild.
    // Property, NOT a static-readonly snapshot: Settings → GAME ADDRESSES →
    // RELOAD promises "apply it now", and MaxPlausibleHp is one of the two
    // values the panel tells users to hand-edit. A type-init snapshot
    // silently defeated that. The getter is a loaded-check + field read —
    // free at tick/scan scale.
    private static int AK_MAX_PLAUSIBLE_HP => Tunables.MaxPlausibleHp;
    private System.Threading.CancellationTokenSource? _akCts;
    private System.Threading.Tasks.Task? _akLoopTask;

    // ── Background loop lifecycle ──────────────────────────────────
    //
    // Auto Kill runs on its own Task.Run loop rather than the UI timer so
    // long UI-thread operations (big scans, dialogs) can't stall kills.
    // StopAutoKillLoop signals cancellation and does NOT await the task —
    // the loop will exit on its next Delay; a stray extra tick is harmless.

    private void StartAutoKillLoop()
    {
        StopAutoKillLoop();
        _akCts = new System.Threading.CancellationTokenSource();
        var ct = _akCts.Token;
        _akLoopTask = System.Threading.Tasks.Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { AutoKillTick(); } catch { /* each tick is best-effort */ }
                try { await System.Threading.Tasks.Task.Delay(AK_TICK_MS, ct); }
                catch { break; }
            }
        });
    }

    private void StopAutoKillLoop()
    {
        try { _akCts?.Cancel(); } catch { }
        _akCts = null;
        _akLoopTask = null;
        ResetAutoKillHandle();
    }

    private void SetAkStatus(string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_autoKillEnabled) StatusText.Text = text;
        }));
    }

    // ── Cache validation ───────────────────────────────────────────
    //
    // On level change, UE3 re-allocates WorldInfo but the old address often
    // remains readable (pooled heap). So "AKRead worked" is not enough —
    // we also check that the PawnList head and first pawn look sane.

    private bool ValidateCachedWorldInfo()
    {
        byte[]? plData = AKRead(_cachedWorldInfo + 0x041C, 4);
        if (plData == null) return false;
        int head = BitConverter.ToInt32(plData, 0);
        if (!IsHeapPtr(head)) return false;

        byte[]? first = AKRead(head, 0x32C);
        if (first == null || first.Length < 0x32C) return false;

        // Vtable pointer lives in the game's code section (~0x00400000-0x01FFFFFF)
        uint vtable = BitConverter.ToUInt32(first, 0);
        if (vtable < 0x00400000 || vtable > 0x02000000) return false;

        int hp = BitConverter.ToInt32(first, 0x0324);
        int hpMax = BitConverter.ToInt32(first, 0x0328);
        // The head pawn is the NEWEST spawn — it can legitimately be a
        // zero-max prop/decoy pawn, so hpMax==0 must not evict the cache
        // (a spurious eviction here costs a full re-scan EVERY tick while
        // that pawn stays at the head). The vtable check above plus the
        // hp ≤ hpMax shape is enough staleness signal.
        if (hpMax < 0 || hpMax > AK_MAX_PLAUSIBLE_HP || hp < -1 || hp > hpMax) return false;
        return true;
    }

    // On-demand resolve of the local player pawn (the pawn-chain tail),
    // reusing the same verified WorldInfo/pawn scan Auto-Kill uses. Lets the
    // Forge Viewer anchor the HeroManager pointer chain
    // (pawn +0x22C → controller → Player → ViewportClient → TheHeroManager →
    // ItemBoxEquipments) without its own scanner. Returns 0 when not in a
    // resolvable game state (menu/loading). Same scan AK runs each tick, just
    // invoked once on a button click.
    public int ResolvePlayerPawnAddress()
    {
        // Same gate the AK tick and ForceStructuralReseed use: this path
        // mutates _cachedWorldInfo / _pawnVtable and can run a multi-second
        // structural sweep, and it is called from the Forge/Hero scans and
        // the CALIBRATE forge probe on non-AK threads while the AK task is
        // mutating the same fields. Ticks arriving meanwhile TryEnter-skip
        // (100 ms later they run again); a concurrent reseed serializes.
        lock (_scanStateGate)
        {
            if (GetAKHandle() == IntPtr.Zero) return 0;
            if (_cachedWorldInfo == 0 || !ValidateCachedWorldInfo())
                _cachedWorldInfo = FindWorldInfoViaPawnScan();
            if (_cachedWorldInfo == 0) return 0;

            byte[]? plData = AKRead(_cachedWorldInfo + 0x041C, 4);
            if (plData == null) return 0;
            int cur = BitConverter.ToInt32(plData, 0);

            var pawns = new List<(int addr, uint pri)>(16);
            var visited = new HashSet<int>();
            while (IsHeapPtr(cur) && visited.Add(cur) && visited.Count <= AK_CHAIN_MAX)
            {
                byte[]? pd = AKRead(cur, 0x32C);
                if (pd == null || pd.Length < 0x32C) break;
                uint pri = 0;
                byte[]? priB = AKRead(cur + OFF_PAWN_PRI, 4);
                if (priB != null && priB.Length >= 4)
                    pri = BitConverter.ToUInt32(priB, 0);
                pawns.Add((cur, pri));
                int np = BitConverter.ToInt32(pd, 0x0230);
                if (np == 0) break;
                cur = np;
            }

            // Any successful resolve has located the local player pawn —
            // selected by verified PRI + ULocalPlayer, not by list position
            // (SelectLocalPlayerPawn; the tail is an NPC in the tavern). Save
            // its vtable as the durable seed so a good value found by a plain
            // Forge/Hero scan persists. Gated on a verified PRI (player only,
            // never an enemy); PinPawnVtable no-ops when unchanged, so this is
            // free on repeat scans. Honors the "never persist the structural
            // first match" rule — we pin the verified player, not the scan's
            // first hit.
            int player = SelectLocalPlayerPawn(pawns, out var pick);
            _playerPick = pick;
            Base.Log($"Resolve: wi=0x{_cachedWorldInfo:X8} pawns={pawns.Count} " +
                     $"picked=0x{player:X8} confidence={pick}");
            TryPinPlayerSeed(player);
            return player;
        }
    }

    // Pin the player pawn's vtable as the durable seed, gated on a verified
    // PRI. Returns the pinned vtable (0 if not pinned). Single home for the
    // pin rule used by ResolvePlayerPawnAddress / AutoKillTick / calibration.
    private uint TryPinPlayerSeed(int playerPawn)
    {
        if (playerPawn == 0) return 0;
        byte[]? priB = AKRead(playerPawn + OFF_PAWN_PRI, 4);
        if (priB == null || priB.Length < 4) return 0;
        if (!IsVerifiedPri(BitConverter.ToUInt32(priB, 0))) return 0;
        byte[]? vtb = AKRead(playerPawn, 4);
        if (vtb == null || vtb.Length < 4) return 0;
        uint pv = BitConverter.ToUInt32(vtb, 0);
        if (pv < 0x00400000u || pv >= 0x02000000u) return 0;
        _pawnVtable = pv;
        Tunables.PinPawnVtable(pv, _liveGameStamp); // no-ops when unchanged
        return pv;
    }

    // Select the LOCAL player's pawn from a walked PawnList chain.
    // Position is NOT a reliable signal: UE3 inserts newly spawned pawns
    // at the list HEAD, so the player is the tail in a solo mission
    // (spawned before the enemies) but sits at/near the HEAD in the
    // tavern, where the earlier-spawned shop NPCs / practice dummies
    // occupy the tail — the old "tail = local player" rule made the
    // Calibrate pin and the pawn→HeroManager resolve fail there
    // (debug-proven 2026-06-11: the player's NextPawn points at a
    // MaxHealth==0 shop pawn, so the player cannot be the tail).
    // Selection: the pawn with a verified PRI whose controller carries a
    // ULocalPlayer (+0x3B8) — only the locally-controlled player has one;
    // remote multiplayer players' controllers don't. Fallbacks: the last
    // PRI-verified pawn (controller/Player link still settling), then the
    // tail (PRI not replicated yet, first tick after a map load — the
    // historical rule, still correct in solo missions).
    // How confident the selection is. Ordered — higher is stronger. Anything
    // below LocalPlayer is a GUESS (no ULocalPlayer was found on any pawn),
    // which is why writes that go through the controller are gated on it.
    internal enum PlayerPawnPick
    {
        None = 0,
        Tail = 1,            // no PRI anywhere — first tick after a map load
        PriOnly = 2,         // a PRI-verified pawn, but NO ULocalPlayer found
        LocalPlayer = 3,     // controller carries a ULocalPlayer
        ViewportVerified = 4 // ...and that ULocalPlayer carries a ViewportClient
    }

    // Confidence of the most recent selection, for the diagnostic report and
    // the controller-write gate.
    private volatile PlayerPawnPick _playerPick = PlayerPawnPick.None;
    internal PlayerPawnPick LastPlayerPick => _playerPick;

    // Why the last Unlimited-Mana tick did NOT write (null = it wrote, or the
    // toggle is off). Release builds have no log, so without this a gated
    // mana write is indistinguishable from a broken one — surfaced in the
    // diagnostic report.
    private volatile string? _manaGateReason;
    internal string? ManaGateReason => _manaGateReason;

    private int SelectLocalPlayerPawn(List<(int addr, uint pri)> pawns)
        => SelectLocalPlayerPawn(pawns, out _);

    private int SelectLocalPlayerPawn(List<(int addr, uint pri)> pawns, out PlayerPawnPick how)
    {
        how = PlayerPawnPick.None;
        if (pawns.Count == 0) return 0;

        int lastVerified = 0, localPlayer = 0;
        foreach (var p in pawns)
        {
            if (!IsVerifiedPri(p.pri)) continue;
            lastVerified = p.addr;
            int ctl = ReadU32(p.addr + OFF_PAWN_CONTROLLER);
            if (!IsHeapPtr(ctl)) continue;
            int lp = ReadU32(ctl + GameChain.OFF_CONTROLLER_PLAYER);
            if (!IsHeapPtr(lp)) continue;

            // Strongest signal, and exactly what the Forge/Hero chain needs
            // one hop later: the ULocalPlayer carries a ViewportClient. In a
            // multiplayer game several pawns can clear the PRI bar and a
            // remote player's controller can still be pointer-shaped, so
            // prefer the one that actually reaches the viewport rather than
            // returning the first ULocalPlayer-ish match and hoping.
            int vp = ReadU32(lp + GameChain.OFF_PLAYER_VIEWPORT);
            if (IsHeapPtr(vp)) { how = PlayerPawnPick.ViewportVerified; return p.addr; }

            if (localPlayer == 0) localPlayer = p.addr; // keep as the runner-up
        }

        // Ladder (unchanged in substance — only the viewport tier is new):
        // ULocalPlayer-bearing → last PRI-verified → tail (PRI replication
        // lag on the first post-load tick). The bottom two tiers are GUESSES
        // and are reported as such.
        if (localPlayer != 0) { how = PlayerPawnPick.LocalPlayer; return localPlayer; }
        if (lastVerified != 0) { how = PlayerPawnPick.PriOnly; return lastVerified; }
        how = PlayerPawnPick.Tail;
        return pawns[pawns.Count - 1].addr;
    }

    // Mutual exclusion between the AK tick body and ForceStructuralReseed
    // (the Calibrate / viewer auto-recalibrate sweep). A tick that can't
    // take the gate skips — the next one is 100 ms away; the reseed BLOCKS
    // until an in-flight tick drains, then owns the shared cache fields
    // (_pawnVtable/_cachedWorldInfo/_cachedPlayerPawn) and the AK handle
    // for its whole sweep. This replaces an earlier one-directional
    // volatile flag, which neither stopped a tick already in flight nor
    // survived two concurrent reseeds (the first finisher cleared it).
    private readonly object _scanStateGate = new();

    private void AutoKillTick()
    {
        if (!System.Threading.Monitor.TryEnter(_scanStateGate)) return; // a reseed owns the caches — skip this tick
        try { AutoKillTickCore(); }
        finally { System.Threading.Monitor.Exit(_scanStateGate); }
    }

    private void AutoKillTickCore()
    {
        // Ensure we have a live handle to DunDefGame
        if (GetAKHandle() == IntPtr.Zero)
        {
            _cachedWorldInfo = 0;
            _cachedPlayerPawn = 0;
            SetAkStatus("Auto Kill: game not running");
            return;
        }

        // PID re-check enumerates every process (~10-50 ms) → stays on the
        // AK_VALIDATE_MS (2 s) cadence; a game restart is otherwise caught
        // by the AKRead fail-count.
        if ((DateTime.Now - _akLastValidated).TotalMilliseconds >= AK_VALIDATE_MS)
        {
            _akLastValidated = DateTime.Now;
            CheckTargetPid();
        }

        // WorldInfo liveness is two tiny RPM reads (PawnList head + first
        // pawn vtable/HP sanity) — cheap enough to run EVERY tick. A map
        // change reallocates WorldInfo while the old address usually stays
        // pooled-readable, so a 2 s cadence let Unlimited Mana / Max Tower
        // Units write to a dead controller/GRI for seconds (or until the
        // pool was reused). Per-tick validation catches it in ~one tick
        // (~100 ms) and the re-find below re-resolves immediately.
        if (_cachedWorldInfo != 0 && !ValidateCachedWorldInfo())
        {
            _cachedWorldInfo = 0;
            _cachedPlayerPawn = 0;
        }

        // (Re)find WorldInfo if we don't have a valid cache
        if (_cachedWorldInfo == 0)
        {
            SetAkStatus("Auto Kill: searching...");
            int wi = FindWorldInfoViaPawnScan();
            _cachedWorldInfo = wi;
            _akLastValidated = DateTime.Now;
            if (wi == 0) { SetAkStatus("Auto Kill: no enemies found"); return; }
            SetAkStatus($"Auto Kill: found WorldInfo 0x{wi:X8}");
        }

        // Read PawnList head
        byte[]? plData = AKRead(_cachedWorldInfo + 0x041C, 4);
        if (plData == null) { _cachedWorldInfo = 0; _cachedPlayerPawn = 0; return; }
        int head = BitConverter.ToInt32(plData, 0);
        if (!IsHeapPtr(head)) return;

        // Single walk: collect each pawn's address, HP, UObject Class*
        // pointer. A second, larger read fills in PlayerReplicationInfo
        // (+0x038C) *when it's available* — some pawns may live at the end
        // of a committed region and the longer read would fail. PRI is
        // purely additive; failing to read it is fine.
        //
        // Offsets (DD_ModMenu SDK):
        //   +0x0034  UObject::Class*                (DD_Core_classes.hpp)
        //   +0x0230  APawn::NextPawn*               (DD_Engine_classes.hpp)
        //   +0x0324  DDTopDownPawn::Health
        //   +0x0328  DDTopDownPawn::MaxHealth
        //   +0x038C  APawn::PlayerReplicationInfo*  (DD_Engine_classes.hpp)
        var chain = new List<(int addr, int hp, int hpMax, uint klass, uint pri)>(64);
        int cur = head;
        var visited = new HashSet<int>();
        while (IsHeapPtr(cur) && visited.Add(cur) && visited.Count <= AK_CHAIN_MAX)
        {
            // Core read: everything we need to kill. Matches the pre-PRI
            // size so we never regress on pawns where only 0x32C bytes
            // are legible.
            byte[]? pd = AKRead(cur, 0x32C);
            if (pd == null || pd.Length < 0x32C) break;
            uint klass = BitConverter.ToUInt32(pd, OFF_OBJECT_CLASS);
            int hp = BitConverter.ToInt32(pd, 0x0324);
            int hpMax = BitConverter.ToInt32(pd, 0x0328);
            int np = BitConverter.ToInt32(pd, 0x0230);

            // Best-effort PRI read. 4 bytes at a known offset; if the page
            // isn't readable we silently treat PRI as unknown (0).
            uint pri = 0;
            byte[]? priData = AKRead(cur + OFF_PAWN_PRI, 4);
            if (priData != null && priData.Length >= 4)
                pri = BitConverter.ToUInt32(priData, 0);

            chain.Add((cur, hp, hpMax, klass, pri));
            if (np == 0) break;
            cur = np;
        }

        if (chain.Count == 0) return;
        var pawnPris = new List<(int addr, uint pri)>(chain.Count);
        foreach (var p in chain) pawnPris.Add((p.addr, p.pri));
        int playerPawn = SelectLocalPlayerPawn(pawnPris, out var pick);
        _playerPick = pick;
        _cachedPlayerPawn = playerPawn;

        // Durable fast-scan seed: persist ONLY the local player's own
        // pawn-class vtable (PRI-verified, position-independent — see
        // SelectLocalPlayerPawn), via the shared TryPinPlayerSeed.
        // Deliberately BEFORE the gameplay-level gate: it's a read + an
        // overrides.json write (no game write), so a tavern/build-phase
        // self-heal pins immediately instead of waiting for the first
        // combat tick. PinPawnVtable no-ops when unchanged (no per-tick
        // disk I/O). The recorded build stamp rides along so the next
        // patch is detected on attach (EnsureGameBuildStampChecked).
        TryPinPlayerSeed(playerPawn);

        // Level-loaded gate. Historically auto-kill glitched the level
        // when left on across a map transition — our writes were hitting
        // actors that only exist during loading. Require GRI to report
        // "gameplay level" before we do any writes this tick.
        //
        // In the tavern (lobby) practice dummies survive our LifeSpan
        // writes by respawning, so rather than trying to detect them we
        // just auto-disable the toggle entirely when the user returns to
        // the tavern — they can flip it back on when they enter a real
        // mission. Dispatched because SetAutoKillEnabled touches the UI
        // toggle on the UI thread.
        bool inGameplayLevel = IsInGameplayLevel();
        if (!inGameplayLevel)
        {
            if (_autoKillEnabled)
                Dispatcher.BeginInvoke(new Action(() => SetAutoKillEnabled(false)));
            SetAkStatus("Auto Kill: disabled (lobby/loading)");
            return;
        }

        // Learning-only use of PRI: a verified PRI means the pawn is
        // possessed by an APlayerController, so its Class* is a hero class.
        // Sparing a pawn is driven by _heroClasses alone — we never skip a
        // kill based on PRI directly. Misclassification here is the main
        // failure mode for auto-kill (one false positive permanently
        // whitelists an enemy class until toggle-off), so verification
        // dereferences the pointer rather than trusting its range alone.
        foreach (var p in chain)
        {
            if (p.klass != 0 && IsVerifiedPri(p.pri) && _heroClasses.Add(p.klass))
                Base.Log($"HeroClass: added 0x{p.klass:X8} via PRI (pawn 0x{p.addr:X8}, pri 0x{p.pri:X8})");
        }
        // Tail fallback: during the first tick after a map load PRI can
        // still be replicating. Gate it behind the same vtable check so a
        // freshly-spawned enemy sitting at tail can't poison the set on its
        // own — both signals must agree.
        var tail = chain[chain.Count - 1];
        if (LooksLikeHero(tail.klass, tail.hp, tail.hpMax) && IsVerifiedPri(tail.pri)
            && _heroClasses.Add(tail.klass))
            Base.Log($"HeroClass: added 0x{tail.klass:X8} via tail (pawn 0x{tail.addr:X8})");

        _akTicksSinceEnable++;
        bool learnOnly = _akTicksSinceEnable <= AK_LEARN_GRACE_TICKS;

        int killed = 0;
        int towersKilled = 0;
        int protectedHeroes = 0;
        if (_autoKillEnabled && !learnOnly)
        {
            // The positional tail-skip is the HISTORICAL rule (solo mission:
            // player spawned first ⇒ player is the tail). It is debug-proven
            // wrong in general (DECISIONS.md "tail rule retired 2026-06-11")
            // but deliberately KEPT pending in-game re-validation — do NOT
            // remove or "unify" it. The PRI-selected playerPawn skip below is
            // purely ADDITIVE protection for states where the player is not
            // the tail; an extra skip can only spare a pawn, never kill one.
            for (int i = 0; i < chain.Count - 1; i++) // tail skip (historical — see above)
            {
                var p = chain[i];
                if (p.addr == playerPawn) { protectedHeroes++; continue; } // PRI-selected local player
                if (p.klass != 0 && _heroClasses.Contains(p.klass)) { protectedHeroes++; continue; }
                bool killable = p.hp > 0 && p.hpMax > 0;
                if (!killable)
                {
                    // hp ≤ 0 with hpMax == 1 is OUR half-finished kill from
                    // an earlier tick: Health/MaxHealth landed but the pawn
                    // is still in the list, so the LifeSpan write missed
                    // (transient WPM failure) or was reset. Re-assert it
                    // instead of abandoning the pawn as a permanent zombie —
                    // this was a real "some enemies randomly never die"
                    // mode: one dropped write made the pawn unkillable for
                    // its whole life (hp reads 0 ⇒ skipped every later tick).
                    if (p.hp <= 0 && p.hpMax == 1)
                    {
                        if (!LifeSpanTicking(p.addr))
                            AKWrite(p.addr + OFF_ACTOR_LIFESPAN, unchecked((int)0x3D4CCCCD));
                        killed++;
                        continue;
                    }
                    // Log once per unique (class, hp/hpMax shape) combo so
                    // we can see what's escaping the kill. Deduped by pawn
                    // address + class so a given spared enemy logs exactly
                    // once per session.
                    string key = $"{p.addr:X8}:{p.klass:X8}";
                    if (_loggedUnkilledPawns.Add(key))
                        Base.Log($"Unkilled: pawn=0x{p.addr:X8} class=0x{p.klass:X8} hp={p.hp} hpMax={p.hpMax}");
                    continue;
                }
                // Health=0 + MaxHealth=1 routes through the pawn tick's
                // "am I dead yet?" check and fires the normal Died()
                // cascade — but that path is gated by the pawn's SpawnIn
                // state for ~5s after spawn. LifeSpan triggers engine-
                // level destruction on a separate code path, which is NOT
                // gated by SpawnIn, so enemies die within ~50ms regardless
                // of spawn state.
                AKWrite(p.addr + 0x0324, 0);
                AKWrite(p.addr + 0x0328, 1);
                AKWrite(p.addr + OFF_ACTOR_LIFESPAN, unchecked((int)0x3D4CCCCD));
                killed++;
            }

            // Same LifeSpan lever also handles TargetableActors (enemy
            // towers, crystals, bosses that aren't in the pawn list). DD1's
            // tower/crystal Destroyed() overrides do the proper list
            // cleanup (auras, chaining refs, TargetableActors itself), so
            // unlike the older Health=0/bDeleteMe attempts this doesn't
            // leak dangling ptrs.
            towersKilled = KillEnemyTargetableActors();
            // Only log when we actually killed something — idle ticks with
            // killed=0 used to dominate the log file.
            if (towersKilled > 0)
            {
                string towerLine = $"KillTowers: killed={towersKilled} {_towerStatus}";
                if (towerLine != _lastTowerLog)
                {
                    Base.Log(towerLine);
                    _lastTowerLog = towerLine;
                }
            }
        }

        // Speed multiplier — write every tick so the game can't quietly
        // restore it on map changes or slo-mo Kismet events. 1.0f means
        // "do nothing" so the user's natural gameplay isn't overwritten.
        // Runs outside the grace gate since it's not damage-related.
        float speed = _speedMultiplier;
        if (speed != 1.0f && _cachedWorldInfo != 0)
            AKWrite(_cachedWorldInfo + OFF_WI_TIMEDILATION, FloatBits(speed));

        // Unlimited mana — refill the local player controller's ManaPower to
        // its current MaxManaPower every tick. Reading Max keeps this correct
        // across heroes / maps / mana-upgrade pickups (no baked-in cap) and
        // leaves the HUD looking normal. _cachedPlayerPawn is the local
        // player (PRI-verified, SelectLocalPlayerPawn); reached only past
        // the gameplay-level gate, so this never fires in the
        // tavern/menu/loading. Independent of Auto-Kill.
        if (_unlimitedMana && _cachedPlayerPawn != 0)
        {
            // Only write through a pawn we actually VERIFIED as the local
            // player. Below LocalPlayer the selection is a guess (no
            // ULocalPlayer was found on any pawn — e.g. an online game where
            // the pick landed on a remote player, or a PRI-carrying pet/NPC),
            // and its "+0x22C controller" is then some other object entirely.
            // Same fail-closed principle as the mana/tower layout gates: a
            // write we can't justify becomes a no-op with a stated reason,
            // never a blind poke at an unknown field 10x/second.
            int ctrl = _playerPick >= PlayerPawnPick.LocalPlayer
                ? ReadU32(_cachedPlayerPawn + OFF_PAWN_CONTROLLER)
                : 0;

            if (_playerPick < PlayerPawnPick.LocalPlayer)
                _manaGateReason = $"skipped — couldn't identify your character (pick={_playerPick}). " +
                                  "In an online game this happens when the tool locks onto another player.";
            else if (!IsHeapPtr(ctrl))
                _manaGateReason = "skipped — your character's Controller (+0x22C) didn't resolve";
            else
            {
                int maxMana = ReadU32(ctrl + OFF_PC_MAXMANAPOWER);
                // Sanity: real in-mission mana caps are small (≈2k), so 1M is
                // 500× headroom while sitting BELOW the heap-pointer range
                // (0x01000000 = 16.7M) — if a DD1 patch ever shifts the
                // ADunDefPlayerController layout, this offset reads some other
                // field (pointer/float/garbage) and the gate makes the write
                // no-op instead of stomping an unknown field every tick.
                if (maxMana > 0 && maxMana <= 1_000_000)
                {
                    AKWrite(ctrl + OFF_PC_MANAPOWER, maxMana);
                    _manaGateReason = null; // writing normally
                }
                else
                {
                    _manaGateReason = $"skipped — MaxManaPower at controller+0x{OFF_PC_MAXMANAPOWER:X} read " +
                                      $"{maxMana}, outside the plausible range (controller layout may have shifted)";
                }
            }
        }

        // Max tower units — raise the map's DU budget cap so far more towers
        // can be placed. MaxTowerUnits is an 8-byte engine slot at
        // GRI+0x039C; write BOTH dwords every tick (value, then a zeroed
        // upper half) or the tower allocator crashes. GRI is valid here —
        // we're past the gameplay-level gate, which already deref'd it.
        if (_maxTowerUnits && _cachedWorldInfo != 0)
        {
            int gri = ReadU32(_cachedWorldInfo + OFF_WI_GRI);
            if (IsHeapPtr(gri))
            {
                // Sanity pre-read: only write when the slot currently holds a
                // plausible DU budget — real maps cap ~260, our own pinned
                // value and the Tunables ceiling are ≤1M, and 0 (a no-DU map)
                // still passes so behavior there is unchanged. If a DD1 patch
                // shifts the GRI layout this offset reads a float/pointer/
                // garbage (> 1M or negative) and the 8-byte write is skipped
                // instead of corrupting two unknown dwords every tick.
                int curUnits = ReadU32(gri + OFF_GRI_MAXTOWERUNITS);
                if (curUnits >= 0 && curUnits <= 1_000_000)
                {
                    AKWrite(gri + OFF_GRI_MAXTOWERUNITS, MAX_TOWER_UNITS_VALUE);
                    AKWrite(gri + OFF_GRI_MAXTOWERUNITS + 4, 0);
                }
            }
        }

        // Compact status line. Each segment only appears when it has
        // something to say.
        if (learnOnly)
        {
            SetAkStatus($"Auto Kill: learning ({_heroClasses.Count} hero cls)");
            return;
        }
        string bits = $"Kill: {killed}e";
        if (towersKilled > 0) bits += $" + {towersKilled}t";
        if (protectedHeroes > 0) bits += $" · {protectedHeroes} hero safe";
        if (speed != 1.0f) bits += $" · {speed:0.##}x";
        SetAkStatus(bits);
    }

    // Walks AMain::TargetableActors (enemy towers, crystals, bosses) and
    // writes LifeSpan=0.05f on every entry whose TargetingTeam is PLAYERS
    // (=2, meaning "this target goes after players" — i.e. it's an enemy).
    // Hypothesis: the engine-level destruction path that LifeSpan triggers
    // runs each actor's Destroyed() cleanup, which in DD1's tower/crystal
    // classes should remove them from auras/chaining lists / TargetableActors
    // itself. Previous attempts using Health=0 + bDeleteMe crashed because
    // they skipped that cleanup.
    //
    // Diagnostic message is stored in _towerStatus so the status bar can
    // surface exactly which stage failed (GameInfo read, TArray header,
    // team filter distribution) when writes produce 0 kills.
    private string _towerStatus = "";
    private string _lastTowerLog = "";
    // Counts ticks since Auto Kill was turned on. For the first few ticks
    // we walk the pawn list to *learn* hero classes but do NOT apply kills —
    // closes a brief friendly-fire window while the PRI sweep settles.
    private int _akTicksSinceEnable = 0;
    private const int AK_LEARN_GRACE_TICKS = 2;
    // Deduplicates "Unkilled" log lines so one persistently unkillable
    // pawn doesn't flood the log. Cleared when auto-kill toggles on.
    private readonly HashSet<string> _loggedUnkilledPawns = new();
    // DD1 is LARGEADDRESSAWARE on WOW64, so heap allocations can sit anywhere
    // in [0x01000000, 0xFFFE0000). The older 0x7F000000 upper bound rejected
    // valid high-half pointers (observed: Game=0xB6E2EA00).
    private static bool IsHeapPtr(uint p) => p >= 0x01000000u && p < 0xFFFE0000u;
    private static bool IsHeapPtr(int p) => IsHeapPtr(unchecked((uint)p));

    private int KillEnemyTargetableActors()
    {
        if (_cachedWorldInfo == 0) { _towerStatus = "no WI"; return 0; }
        int gameInfo = ReadU32(_cachedWorldInfo + OFF_WI_GAME);
        if (!IsHeapPtr(gameInfo))
        {
            _towerStatus = $"bad Game=0x{gameInfo:X8}";
            return 0;
        }

        // TArray<AActor*> layout: Data (4 bytes), Count (4), Max (4).
        byte[]? hdr = AKRead(gameInfo + OFF_GAME_TARGETABLE, 12);
        if (hdr == null || hdr.Length < 12) { _towerStatus = "no TArray"; return 0; }
        int dataPtr = BitConverter.ToInt32(hdr, 0);
        int count   = BitConverter.ToInt32(hdr, 4);
        if (!IsHeapPtr(dataPtr) || count <= 0 || count > 4096)
        {
            _towerStatus = $"empty Game=0x{gameInfo:X8} data=0x{dataPtr:X8} n={count}";
            return 0;
        }

        byte[]? arr = AKRead(dataPtr, count * 4);
        if (arr == null || arr.Length < count * 4)
        {
            _towerStatus = $"array read failed n={count}";
            return 0;
        }

        int team0 = 0, team1 = 0, team2 = 0, teamOther = 0;
        int killed = 0;
        for (int i = 0; i < count; i++)
        {
            int actor = BitConverter.ToInt32(arr, i * 4);
            if (!IsHeapPtr(actor)) continue;

            int team = ReadU32(actor + OFF_DDT_TARGETING_TEAM);
            if (team == 0) team0++;
            else if (team == 1) team1++;
            else if (team == 2) team2++;
            else teamOther++;

            if (team != ENEMY_TARGETING_TEAM) continue;
            // Guarded write: the old unconditional per-tick rewrite reset
            // the countdown every 100 ms, and LifeSpan decrements in GAME
            // time — below ~0.5× game speed it could never reach zero, so
            // enemy towers became unkillable whenever slo-mo was active.
            if (!LifeSpanTicking(actor))
                AKWrite(actor + OFF_ACTOR_LIFESPAN, unchecked((int)0x3D4CCCCD));
            killed++;
        }

        _towerStatus = $"Game=0x{gameInfo:X8} n={count} t0={team0} t1={team1} t2={team2} tX={teamOther}";
        return killed;
    }

    private int ReadU32(int addr)
    {
        byte[]? b = AKRead(addr, 4);
        return (b != null && b.Length >= 4) ? BitConverter.ToInt32(b, 0) : 0;
    }

    // True when the actor already carries an in-flight LifeSpan countdown
    // from one of our earlier writes (0 < LifeSpan ≤ 0.05). LifeSpan
    // decrements in GAME time, so rewriting it every real-time tick resets
    // the countdown — at game speeds below ~0.5× (0.05 s game-time needed
    // vs 0.1 s real-time ticks) it would never reach zero and the actor
    // would never die. Never reset a ticking countdown.
    private bool LifeSpanTicking(int addr)
    {
        float ls = BitConverter.Int32BitsToSingle(ReadU32(addr + OFF_ACTOR_LIFESPAN));
        return ls > 0f && ls <= 0.05f;
    }

    // True when GRI reports a gameplay level is loaded (not lobby, not a
    // loading/transition state). Used to gate every write in AutoKillTick
    // so the loop can be left on across map changes without glitching.
    private bool IsInGameplayLevel()
    {
        if (_cachedWorldInfo == 0) return false;
        int gri = ReadU32(_cachedWorldInfo + OFF_WI_GRI);
        if (!IsHeapPtr(gri)) return false;
        uint flags = unchecked((uint)ReadU32(gri + OFF_GRI_FLAGS_02C4));
        if ((flags & BIT_IS_LOBBY_LEVEL) != 0) return false;
        return (flags & BIT_IS_GAMEPLAY) != 0;
    }

    // A real APlayerReplicationInfo is a UObject: its first 4 bytes are a
    // vtable pointer that lives in DunDefGame.exe's code section
    // (~0x00400000..0x02000000 — same range ValidateCachedWorldInfo uses
    // for pawn vtables). Require both the PRI pointer itself AND its vtable
    // to pass the check. Without the vtable dereference the "does it look
    // like a heap pointer?" range covers essentially all user memory, so
    // enemy subclasses that stash anything non-null at +0x038C (inventory
    // array, AI state, owner link, etc.) end up whitelisted.
    private bool IsVerifiedPri(uint pri)
    {
        // Upper bound matches IsHeapPtr (LARGEADDRESSAWARE — DD1 heap
        // reaches into the upper 2 GB). An earlier 0x7F000000 cap
        // silently rejected high-half PRI pointers, failing to learn
        // those hero classes.
        if (pri < 0x00100000u || pri >= 0xFFFE0000u) return false;
        byte[]? vt = AKRead((int)pri, 4);
        if (vt == null || vt.Length < 4) return false;
        uint vtable = BitConverter.ToUInt32(vt, 0);
        return vtable >= 0x00400000u && vtable < 0x02000000u;
    }

    // UObject base offset — Class pointer at +0x34 per the DD_ModMenu SDK
    // (DD_Core_classes.hpp). Load-bearing for multiplayer hero protection.
    private const int OFF_OBJECT_CLASS = 0x0034;

    // APawn::PlayerReplicationInfo at +0x038C (DD_Engine_classes.hpp).
    // Non-null iff the pawn is possessed by an APlayerController — the
    // universal "this is a hero" signal across every DLC hero class.
    private const int OFF_PAWN_PRI = 0x038C;

    // AActor.LifeSpan (float, +0x0114). Engine destroys the actor when
    // LifeSpan counts down to zero — a separate code path from damage/Died
    // that is NOT gated by the pawn's SpawnIn state, so writing 0.05f here
    // kills enemies within ~50ms regardless of where they are in their
    // spawn-in animation. AActor is the base class, so the offset is safe
    // on every pawn in the chain. See CLAUDE.md "Spawn-in bypass" for why
    // this ended up being the only workable lever.
    private const int OFF_ACTOR_LIFESPAN = 0x0114;

    // Unlimited-mana feature. Verified live 2026-05-14 (memdump
    // mana_via_pawn) end-to-end through this exact path:
    //   APawn.Controller                    +0x022C → ADunDefPlayerController
    //   ADunDefPlayerController.ManaPower    +0x06B4 (int — the value the
    //                                                tower build/upgrade
    //                                                system decrements)
    //   ADunDefPlayerController.MaxManaPower +0x06B8 (int — read only; never
    //                                                written, keeps HUD sane
    //                                                and avoids a baked-in
    //                                                cap that would go stale)
    // Triple-confirmed: regenerated SDK, DD_ModMenu bUnlimitedManaTowers,
    // SashaFloats table.
    private const int OFF_PAWN_CONTROLLER  = 0x022C;
    private const int OFF_PC_MANAPOWER     = 0x06B4;
    private const int OFF_PC_MAXMANAPOWER  = 0x06B8;

    // Max tower units (per-map DU budget cap).
    // ADunDefGameReplicationInfo.MaxTowerUnits at GRI+0x039C. The SDK
    // declares a 4-byte int there but the engine uses an 8-byte slot
    // (DD_UDKGame_classes.hpp:4797-4798 tags the upper 4 bytes "FIX WRONG
    // TYPE SIZE OF PREVIOUS PROPERTY"). A 4-byte-only write leaves the upper
    // dword garbage and the tower allocator CRASHES — so we write BOTH
    // dwords (value, then 0). GRI reached via _cachedWorldInfo + OFF_WI_GRI
    // (same path IsInGameplayLevel uses). Per DD_ModMenu: write this
    // directly, NOT GlobalTowerUnitLimitMultiplier (baked in at level load,
    // ignored mid-game). 100,000 is effectively unlimited (real maps cap
    // ~260 DU) while keeping the HUD's "used / max" less absurd; well
    // within DD_ModMenu's 1,000,000 ceiling.
    private const int OFF_GRI_MAXTOWERUNITS = 0x039C;
    // Value (not the offset) sourced from Tunables — see AK_MAX_PLAUSIBLE_HP.
    // Property for the same reason as AK_MAX_PLAUSIBLE_HP: Settings RELOAD
    // must actually apply a hand-edited MaxTowerUnits without a restart.
    private static int MAX_TOWER_UNITS_VALUE => Tunables.MaxTowerUnits;

    // TargetableActors sweep (enemy towers / crystals / bosses). Offsets
    // from DD_ModMenu SDK:
    //   AWorldInfo.Game (AGameInfo*)               at +0x03FC
    //   AMain.TargetableActors (TArray<AActor*>)   at +0x0394
    //   ADunDefDamageableTarget.TargetingTeam      at +0x02CC
    // DD_ModMenu's TARGET_TEAM enum: NONE=0, ENEMYS=1, PLAYERS=2. Enemy
    // towers/crystals have TargetingTeam==PLAYERS — their *target* is the
    // player side, so the field describes "who this actor attacks".
    private const int OFF_WI_GAME              = 0x03FC;
    private const int OFF_GAME_TARGETABLE      = 0x0394;
    private const int OFF_DDT_TARGETING_TEAM   = 0x02CC;
    private const int ENEMY_TARGETING_TEAM     = 2;

    // WorldInfo.TimeDilation (float at +0x0374). Engine multiplies all
    // per-actor tick deltas by this value, so <1.0 is slo-mo and >1.0 is
    // fast-forward. Must be written every tick because the game resets it
    // on map changes and during scripted slo-mo sequences.
    private const int OFF_WI_TIMEDILATION = 0x0374;

    // AWorldInfo.GRI (AGameReplicationInfo*) at +0x03D0. Used to reach
    // ADunDefGameReplicationInfo state flags — specifically the "we're
    // actually in a gameplay level, not main menu / lobby / loading"
    // check so writes don't fire during map transitions (historically
    // glitched level loads when auto-kill was left on).
    private const int OFF_WI_GRI = 0x03D0;
    // GRI flag word at +0x02C4 (second dword of bitfields). Bit 12 =
    // IsLobbyLevel, bit 13 = IsGameplayLevel.
    private const int  OFF_GRI_FLAGS_02C4   = 0x02C4;
    private const uint BIT_IS_LOBBY_LEVEL   = 0x00001000; // bit 12
    private const uint BIT_IS_GAMEPLAY      = 0x00002000; // bit 13

    // Accumulated across ticks within one session. Cleared whenever Auto-Kill
    // is toggled off → on (so a stale class from a crashed session doesn't
    // persist). Never shrinks otherwise — DD1 UClass pointers are stable
    // for the game's lifetime.
    private readonly HashSet<uint> _heroClasses = new();

    // Basic sanity for "this tail looks like an actual hero" before we trust
    // its class. Blocks pets/projectiles that briefly appear as tail during
    // a hero switch (usually have zero health or absurd/garbage health).
    private static bool LooksLikeHero(uint klass, int hp, int hpMax)
    {
        if (klass == 0) return false;
        if (hp <= 0) return false;                  // dead/transient
        if (hpMax <= 0 || hpMax > AK_MAX_PLAUSIBLE_HP) return false;
        if (hp > hpMax) return false;
        return true;
    }

    // ── Auto Kill — direct P/Invoke for reliability ────────────────

    // APawn vtable address inside DunDefGame.exe's .rdata. This is the
    // ONE value in the auto-kill code that depends on the executable's
    // code-section layout — every other constant is a struct-field
    // offset. Whenever DD1 ships a patch, code/data sizes shift and
    // this address rebases, which makes the fast scan find zero pawns.
    //
    // Seed history (for context if this ever needs re-deriving):
    //   0x00FCD9A8 — Grandeu-97-4-0-4-1669256200 (original)
    //   0x00FCD998 — DD1 patch April 2026 (16-byte rebase)
    //   0x00FCD7D8 — DD1 build 2026-05 (Steam Win32; live DLL dump, base 0x00400000 / +0xBCD7D8)
    //   0x00FCC830 — DD1 v10.0 anti-cheat update beta 2026-05-18 (injector
    //                player-pawn dump; base 0x00400000 / +0xBCC830). ~4 KB
    //                code shift; struct offsets unchanged (SDK-verified).
    //
    // The seed only matters for the very first scan in a session. If
    // it misses, FindWorldInfoViaPawnScan's structural fallback rewrites
    // this field with the live vtable, and subsequent calls hit the
    // fast path again. Bumping the seed when a new patch ships is just
    // an optimization to skip the one-time rediscovery.
    //
    // Seeded from Tunables: a moved seed can be re-pointed via the
    // optional overrides.json (offline, no rebuild). The runtime
    // structural self-heal below is unchanged and still authoritative —
    // a wrong override is corrected on the first scan, never worse than
    // the compiled default. Additionally, EnsureGameBuildStampChecked
    // zeroes this on attach when the exe's PE TimeDateStamp differs from
    // the stamp recorded with the pin — patch day then skips the doomed
    // fast sweep and goes straight to the structural re-derive.
    private uint _pawnVtable = Tunables.PawnVtableSeed;

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "OpenProcess")]
    private static extern IntPtr OpenProcess2(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadProcessMemory")]
    private static extern bool RPM(IntPtr hProc, IntPtr addr, byte[] buf, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "WriteProcessMemory")]
    private static extern bool WPM(IntPtr hProc, IntPtr addr, byte[] buf, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CloseHandle")]
    private static extern bool CloseHandle2(IntPtr h);

    private IntPtr _akHandle;
    private int _akTargetPid;      // PID the current handle was opened against
    private int _akFailCount;      // consecutive AKRead failures

    private void ResetAutoKillHandle()
    {
        if (_akHandle != IntPtr.Zero)
        {
            try { CloseHandle2(_akHandle); } catch { }
            _akHandle = IntPtr.Zero;
        }
        _akTargetPid = 0;
        _akFailCount = 0;
    }

    // Hot path: return the cached handle without touching Process.GetProcessesByName,
    // which enumerates every process on the box and is ~10-50ms per call. We rely on
    // (1) AKRead fail-count to detect a stale handle, and (2) CheckTargetPid() in the
    // validation phase to detect a game restart.
    private IntPtr GetAKHandle()
    {
        if (_akHandle != IntPtr.Zero) return _akHandle;
        int pid = ResolveGamePid();
        if (pid == 0) return IntPtr.Zero;
        _akHandle = OpenProcess2(0x1F0FFF, false, pid); // PROCESS_ALL_ACCESS
        _akTargetPid = pid;
        return _akHandle;
    }

    // Called from the periodic validation tick — cheap enough at 2s cadence.
    private void CheckTargetPid()
    {
        if (_akHandle == IntPtr.Zero) return;
        int pid = ResolveGamePid();
        if (pid != _akTargetPid) ResetAutoKillHandle();
    }

    // Which DunDefGame the AK/pawn-scan handle should target. Prefer the
    // instance the Scanner is attached to (the one the user picked in the
    // choose-process dialog when several are running) so the pawn chain
    // resolved through THIS handle is read back through Base.Instance in
    // the SAME process; fall back to "first process found" when nothing is
    // attached yet (Auto-Kill toggled on before any scan). Both GetAKHandle
    // and CheckTargetPid go through here so they can never disagree and
    // flap the handle every 2 s.
    private static int ResolveGamePid()
    {
        int pid = Base.AttachedPid;
        if (pid != 0) return pid;
        var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
        pid = procs.Length > 0 ? procs[0].Id : 0;
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        return pid;
    }

    // ── Game-build (patch) detection ─────────────────────────────────
    // The exe's PE COFF TimeDateStamp changes on every Steam patch and is
    // readable straight out of the loaded module's header — no filesystem
    // path needed, works through the existing AK handle. Comparing it to
    // the stamp recorded beside the pinned seed turns patch day from
    // "discover by a full wasted fast sweep" into "detect on attach and
    // go straight to the structural scan".
    private int  _stampCheckedPid;  // PID the stamp was read for (re-check on re-attach)
    private uint _liveGameStamp;    // current exe's TimeDateStamp (0 = unknown)

    private uint ReadGameTimeDateStamp()
    {
        // Module base is fixed at 0x00400000 (DD1 ships non-ASLR; every
        // recorded seed in DD1_INTERNALS.md §6 assumes it).
        byte[]? dos = AKRead(0x00400000, 0x40);
        if (dos == null || dos.Length < 0x40) return 0;
        if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return 0;
        int e_lfanew = BitConverter.ToInt32(dos, 0x3C);
        if (e_lfanew <= 0 || e_lfanew > 0x10000) return 0;
        byte[]? pe = AKRead(0x00400000 + e_lfanew, 12);
        if (pe == null || pe.Length < 12) return 0;
        if (BitConverter.ToUInt32(pe, 0) != 0x00004550u) return 0; // "PE\0\0"
        return BitConverter.ToUInt32(pe, 8); // IMAGE_FILE_HEADER.TimeDateStamp
    }

    // Once per attach: read the live build stamp and, if it differs from
    // the one recorded with the pinned seed, presume the seed stale and
    // drop it so FindWorldInfoViaPawnScan skips the doomed fast sweep.
    // Fail-safe: an unreadable header (returns 0) changes nothing and is
    // retried on the next call; with no recorded stamp (fresh install /
    // pre-stamp overrides.json) behavior is identical to before.
    private void EnsureGameBuildStampChecked()
    {
        if (GetAKHandle() == IntPtr.Zero) return;
        if (_stampCheckedPid == _akTargetPid) return;
        uint live = ReadGameTimeDateStamp();
        if (live == 0) return;
        _stampCheckedPid = _akTargetPid;
        _liveGameStamp = live;
        uint recorded = Tunables.GameTimeDateStamp;
        if (recorded != 0 && recorded != live && _pawnVtable != 0)
        {
            Base.Log($"GameBuildStamp: exe TimeDateStamp 0x{recorded:X8} -> 0x{live:X8} (game updated); " +
                     $"dropping seed 0x{_pawnVtable:X8}, structural scan will re-derive");
            _pawnVtable = 0;
        }
    }

    private byte[]? AKRead(int addr, int size)
    {
        IntPtr h = GetAKHandle();
        if (h == IntPtr.Zero) return null;
        byte[] buf = new byte[size];
        if (RPM(h, (IntPtr)addr, buf, size, out int read) && read > 0)
        {
            _akFailCount = 0;
            return buf;
        }
        // Stale handle? Force a reopen on next call.
        if (++_akFailCount >= 5)
            ResetAutoKillHandle();
        return null;
    }

    // Probe read for the structural sweep: identical to AKRead but does
    // NOT touch the stale-handle fail counter. The sweep probes
    // garbage-pointer candidates BY DESIGN (vtable probes, noise
    // "PawnList heads"), so failed reads are expected in streaks — five
    // in a row used to trip AKRead's detector, which CLOSED the shared
    // handle mid-sweep. Later AKReads silently recovered (GetAKHandle
    // reopens), but the sweep's VirtualQueryEx loop still held the closed
    // handle, so the region walk died early (the truncated
    // regions=200/309 ScanDiag sweeps) — calibration then failed
    // intermittently depending on heap noise. Failure here is data, not
    // a handle-health signal.
    private byte[]? AKReadProbe(int addr, int size)
    {
        IntPtr h = GetAKHandle();
        if (h == IntPtr.Zero) return null;
        byte[] buf = new byte[size];
        return (RPM(h, (IntPtr)addr, buf, size, out int read) && read > 0) ? buf : null;
    }

    private void AKWrite(int addr, int value)
    {
        IntPtr h = GetAKHandle();
        if (h == IntPtr.Zero) return;
        byte[] data = BitConverter.GetBytes(value);
        WPM(h, (IntPtr)addr, data, 4, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, int size);

    private int FindWorldInfoViaPawnScan()
    {
        // Patch-day shortcut: if the exe's build stamp no longer matches
        // the one recorded with the pinned seed, this zeroes _pawnVtable
        // so we skip a full fast sweep that cannot hit.
        EnsureGameBuildStampChecked();

        // Fast path: try the cached vtable. Hits on every non-patched run.
        if (_pawnVtable != 0)
        {
            int wi = ScanForWorldInfo(_pawnVtable);
            if (wi != 0) return wi;
        }

        // Fast path missed — most likely DD1 was patched and its code
        // section rebased, so _pawnVtable no longer matches any pawn.
        // Re-run the same scan with structural matching only (Health /
        // HealthMax / WorldInfo backref) and let the first confirmed
        // pawn teach us the new vtable. ScanForWorldInfo writes the
        // discovered value back to _pawnVtable so subsequent calls in
        // this session take the fast path again.
        int wi2 = ScanForWorldInfo(0);
        if (wi2 != 0) DumpWorldInfoDiagnostic(wi2);
        return wi2;
    }

    // One-shot diagnostic that fires when the structural scan rediscovers
    // a vtable. Dumps the candidate WorldInfo, the inferred PawnList
    // head, and the first few chain entries' field values so we can tell
    // — post-patch — whether the candidate is a real WorldInfo with
    // shifted pawn offsets, or a structural false-positive entirely.
    // DEBUG-only via Base.Log.
    private bool _diagDumped;
    private int _diagSeedPawnAddr;     // address of the pawn that triggered the structural match
    private bool _diagWeakMatch;       // true if the match passed the loose check but failed the strict one

    private void DumpWorldInfoDiagnostic(int wi)
    {
        if (_diagDumped) return;
        _diagDumped = true;
        try
        {
            Base.Log($"PawnScanDiag: vtable=0x{_pawnVtable:X8} wi=0x{wi:X8} seedPawn=0x{_diagSeedPawnAddr:X8} weakMatch={_diagWeakMatch}");

            // Walk the seed pawn's OWN NextPawn chain. Independent of WI,
            // so this confirms the seed is a real pawn even if PawnList
            // moved off +0x041C.
            int cur = _diagSeedPawnAddr;
            var seen = new HashSet<int>();
            for (int i = 0; i < 5 && IsHeapPtr(cur) && seen.Add(cur); i++)
            {
                byte[]? pd = AKRead(cur, 0x400);
                if (pd == null || pd.Length < 0x400)
                {
                    Base.Log($"PawnScanDiag:  seed[{i}] 0x{cur:X8} read failed (len={(pd?.Length ?? -1)})");
                    break;
                }
                uint vt = BitConverter.ToUInt32(pd, 0x0000);
                uint kl = BitConverter.ToUInt32(pd, 0x0034);
                int  back = BitConverter.ToInt32(pd, 0x0110);
                int  hp   = BitConverter.ToInt32(pd, 0x0324);
                int  hpM  = BitConverter.ToInt32(pd, 0x0328);
                int  np   = BitConverter.ToInt32(pd, 0x0230);
                uint pri  = BitConverter.ToUInt32(pd, 0x038C);
                Base.Log($"PawnScanDiag:  seed[{i}] 0x{cur:X8} vt=0x{vt:X8} cls=0x{kl:X8} back(+0x0110)=0x{back:X8} hp={hp}/{hpM} next(+0x0230)=0x{np:X8} pri=0x{pri:X8} backMatchesWI={(back == wi)}");
                cur = np;
            }

            // Dump every heap-pointer-shaped dword in WI's first 0x600 bytes.
            // If PawnList moved off +0x041C, one of these offsets is the
            // new location — specifically the one whose value equals the
            // seed pawn address, or any other pawn we walked above.
            byte[]? wiBlock = AKRead(wi, 0x600);
            if (wiBlock != null && wiBlock.Length >= 0x600)
            {
                int found = 0;
                for (int off = 0; off + 4 <= 0x600; off += 4)
                {
                    uint v = BitConverter.ToUInt32(wiBlock, off);
                    if (v < 0x02000000u || v >= 0xFFFE0000u) continue;
                    // Verify it's actually committed memory (catches the
                    // 0xFF7FFFFF/-FLT_MAX style sentinels we saw in the
                    // patched build).
                    byte[]? probe = AKRead((int)v, 4);
                    if (probe == null || probe.Length < 4) continue;
                    uint targetVt = BitConverter.ToUInt32(probe, 0);
                    bool isPawnAddr = ((int)v == _diagSeedPawnAddr);
                    Base.Log($"PawnScanDiag:  WI[+0x{off:X3}]=0x{v:X8} targetVt=0x{targetVt:X8}{(isPawnAddr ? " <-- SEED PAWN" : "")}");
                    if (++found >= 24) { Base.Log("PawnScanDiag:  ...truncated"); break; }
                }
                if (found == 0) Base.Log("PawnScanDiag: no committed heap pointers in WI[0..0x600]");
            }
            else
            {
                Base.Log($"PawnScanDiag: WI block read failed (len={(wiBlock?.Length ?? -1)})");
            }
        }
        catch { }
    }

    // matchVtable != 0 → fast scan: every aligned dword is compared
    // against the literal vtable bytes before any further checks.
    // matchVtable == 0 → structural scan: accept any aligned dword
    // whose value looks like a code-section pointer, then rely on the
    // Health / HealthMax / WorldInfo backref checks to filter out
    // non-pawns. The structural pass also writes the discovered vtable
    // back to _pawnVtable on success.
    private int ScanForWorldInfo(uint matchVtable)
    {
        IntPtr hProc = GetAKHandle();
        if (hProc == IntPtr.Zero) return 0;

        // Liveness check before committing to the sweep: the sweep uses
        // probe reads (no stale-handle detection by design — see
        // AKReadProbe), so a genuinely dead handle must be caught HERE.
        // The exe's MZ header at the fixed non-ASLR base is always
        // readable through a live handle.
        if (AKReadProbe(0x00400000, 4) == null)
        {
            ResetAutoKillHandle();
            hProc = GetAKHandle();
            if (hProc == IntPtr.Zero || AKReadProbe(0x00400000, 4) == null) return 0;
        }

        bool fast = matchVtable != 0;
        byte b0 = (byte)(matchVtable & 0xFF);
        byte b1 = (byte)((matchVtable >> 8) & 0xFF);
        byte b2 = (byte)((matchVtable >> 16) & 0xFF);
        byte b3 = (byte)((matchVtable >> 24) & 0xFF);

        // Scan diagnostics (debug builds only — Base.Log is
        // [Conditional("DEBUG")]). Keep: the ScanDiag OK/FAIL counters are
        // how the 2026-06-11 tavern-NPC rejection bug was found, and a
        // failed structural scan is otherwise undiagnosable. The PawnList
        // walk is cached per WI so candidates sharing the real WorldInfo
        // cost one walk, not one each.
        int _dbgRegions = 0, _dbgPrefilter = 0, _dbgWiOk = 0;
        var wiPawnCache = new Dictionary<int, HashSet<int>?>();

        long address = 0;
        // DD1 is LARGEADDRESSAWARE on WOW64, so committed allocations can
        // sit up to ~0xFFFE0000. The older 0x7FFFFFFF ceiling silently cut
        // the scan in half for maps whose WorldInfo spilled into the
        // upper 2 GB.
        //
        // x86 HIGH-HALF SIGN-EXTENSION (fixed 2026-06-11): this binary is
        // compiled x86, so IntPtr is 32-bit and `mbi.BaseAddress.ToInt64()`
        // / `mbi.RegionSize.ToInt64()` SIGN-EXTEND any value with the top
        // bit set — a region at 0x80000000 comes back as the negative long
        // 0xFFFFFFFF80000000. That made `baseAddr >= 0x02000000` false for
        // EVERY region in the upper 2 GB, so the scan silently skipped the
        // whole high half. When the player pawn's heap landed above 2 GB
        // (LARGEADDRESSAWARE — common) it sat in a skipped region → not
        // found → "no character" (intermittent, heap-address dependent; a
        // <2 GB heap worked). Mask both values to their low 32 bits so the
        // address space is treated as the flat unsigned [0, 0xFFFFFFFF] it
        // actually is. (The earlier "(IntPtr)long throws" theory was wrong
        // — .NET 8 truncates — but building the IntPtr from the int is kept
        // for clarity.)
        while (address < 0xFFFE0000L)
        {
            MEMORY_BASIC_INFORMATION mbi;
            int result = VirtualQueryEx(hProc, (IntPtr)unchecked((int)address), out mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (result == 0) break;

            long baseAddr = mbi.BaseAddress.ToInt64() & 0xFFFFFFFFL;
            long regionSize = mbi.RegionSize.ToInt64() & 0xFFFFFFFFL;
            if (regionSize <= 0) { address += 0x1000; continue; }

            // Only scan committed, readable memory
            bool readable = mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Protect & 0x100) == 0;

            if (readable && regionSize > 0x32C && regionSize < 50_000_000 && baseAddr >= 0x02000000)
            {
                byte[]? chunk = AKReadProbe((int)baseAddr, (int)regionSize);
                if (chunk != null && chunk.Length >= 0x32C)
                {
                    _dbgRegions++;
                    for (int i = 0; i + 0x32C < chunk.Length; i += 4)
                    {
                        if (fast)
                        {
                            if (chunk[i] != b0 || chunk[i+1] != b1 || chunk[i+2] != b2 || chunk[i+3] != b3)
                                continue;
                        }
                        else
                        {
                            // Structural pre-filter: vtable pointer
                            // candidate must be in the game's code
                            // section AND dword-aligned. Real C++
                            // vtables are always 4-byte aligned;
                            // random float bit patterns frequently are
                            // not. Filtering on alignment kills the
                            // bulk of float-shaped noise that would
                            // otherwise survive the range check.
                            uint v0 = BitConverter.ToUInt32(chunk, i);
                            if ((v0 & 3u) != 0u) continue;
                            if (v0 < 0x00400000u || v0 >= 0x02000000u) continue;
                        }

                        int h = BitConverter.ToInt32(chunk, i + 0x0324);
                        int hm = BitConverter.ToInt32(chunk, i + 0x0328);
                        if (hm <= 0 || hm > AK_MAX_PLAUSIBLE_HP || h < 0 || h > hm) continue;

                        int wi = BitConverter.ToInt32(chunk, i + 0x0110);
                        // Same alignment trick — 0x3F666666 (=0.9f) is
                        // a heap-range value but not dword-aligned, so
                        // this rejects it without needing to know what
                        // +0x0110 actually means on the new build.
                        if ((unchecked((uint)wi) & 3u) != 0u) continue;
                        if (!IsHeapPtr(wi) || unchecked((uint)wi) < 0x02000000u) continue;

                        // In structural mode the bare "vtable in code
                        // section" pre-filter is too lax — it accepts
                        // any int that numerically falls in that range,
                        // including UTF-16 string fragments and integer
                        // ids. Run two confirmations that don't depend
                        // on knowing the post-patch PawnList offset:
                        //
                        // 1. The candidate's vtable must be committed
                        //    memory. UTF-16 fragments like 0x00740069
                        //    point into the unmapped first MB and fail.
                        // 2. UObject::Class (+0x0034) must be a real
                        //    heap pointer. Every real UObject has a
                        //    non-null Class; pure noise will be 0 or
                        //    non-pointer-shaped.
                        // 3. If NextPawn (+0x0230) is non-null, walk it
                        //    one step and require the entry to also
                        //    look pawn-shaped with the same WI backref.
                        //    If NextPawn IS null (single-pawn chain:
                        //    mission build phase with no enemies yet,
                        //    or an empty tavern), require a verified
                        //    PRI instead — only a possessed player
                        //    pawn has one, which is a stronger signal
                        //    than a second pawn.
                        //
                        // Fast path skips this — the literal vtable
                        // match already proves the candidate is a real
                        // instance of the cached APawn class.
                        if (!fast) _dbgPrefilter++;
                        if (!fast)
                        {
                            // Probe candidate vtable: must be committed
                            // memory. UTF-16 string fragments like
                            // 0x00740069 fall in the code-section
                            // numeric range but point into the
                            // unmapped first MB of address space and
                            // fail the read.
                            uint vt = BitConverter.ToUInt32(chunk, i);
                            byte[]? vtProbe = AKReadProbe((int)vt, 4);
                            if (vtProbe == null || vtProbe.Length < 4) continue;

                            // UObject::Class pointer must be a real,
                            // dword-aligned heap pointer — every UObject
                            // has a non-null Class.
                            uint kls = BitConverter.ToUInt32(chunk, i + 0x0034);
                            if ((kls & 3u) != 0u) continue;
                            if (kls < 0x02000000u || kls >= 0xFFFE0000u) continue;

                            // ── Real-pawn confirmation: validate the WORLD,
                            //    not the neighbour (rewritten 2026-06-11).
                            //
                            // The previous split rejected the player pawn in
                            // a populated tavern. It branched on NextPawn:
                            // a lone pawn (NextPawn==0) was accepted via PRI
                            // + "PawnList head == me", a pawn with a
                            // neighbour (NextPawn!=0) by walking one step and
                            // requiring a HEALTHY enemy there. In the tavern
                            // the player's NextPawn points at a shop/NPC pawn
                            // with MaxHealth==0, so the neighbour walk failed
                            // and the player was rejected — debug-log proven
                            // (14,605 such candidates, every one rejected,
                            // "no character found"). It only ever worked when
                            // the player happened to be the sole/last pawn.
                            //
                            // Robust replacement, independent of neighbours
                            // and NPC health: (a) the backref WI must look
                            // like a real WorldInfo (plausible TimeDilation,
                            // heap Game pointer), and (b) this candidate must
                            // be REACHABLE by walking that WI's PawnList —
                            // true whether the player is head, mid-list, or
                            // tail. The list walk is cached per WI, so the
                            // many candidates sharing the real WorldInfo cost
                            // one walk, not one each.
                            int candAddr = (int)(baseAddr + i);
                            if (!wiPawnCache.TryGetValue(wi, out HashSet<int>? pawnSet))
                            {
                                // First candidate for this WI: validate it once
                                // (plausible TimeDilation + heap Game pointer),
                                // walk its PawnList once, cache the verdict
                                // (null = validated bad / unwalkable). The old
                                // shape re-read+revalidated the 0x430 block for
                                // EVERY candidate — ~14k redundant 1KB reads
                                // per sweep, since all real candidates share
                                // one WorldInfo.
                                //
                                // TimeDilation bounds cover the tool's OWN
                                // speed feature (clamped 0.05–15): the old
                                // (0.05, 10.0) gate rejected the REAL WorldInfo
                                // whenever the user ran 10–15x game speed,
                                // breaking every structural scan and CALIBRATE
                                // until speed was reset.
                                HashSet<int>? walked = null;
                                byte[]? wiB = AKReadProbe(wi, 0x430);
                                if (wiB != null && wiB.Length >= 0x430)
                                {
                                    float td = BitConverter.ToSingle(wiB, 0x374);
                                    bool gameOk = IsHeapPtr(BitConverter.ToInt32(wiB, 0x3FC));
                                    if (td > 0.04f && td < 15.5f && gameOk) // Game ptr
                                    {
                                        _dbgWiOk++;
                                        walked = WalkPawnList(wiB);
                                    }
                                    // Debug-only per-WI verdict: which gate
                                    // rejected it, or how many pawns its list
                                    // walk reached. This is the line that
                                    // diagnosed the 2026-06-11 truncated-sweep
                                    // bug ("wiOk>0 but every candidate
                                    // rejected"). Capped per sweep — a noisy
                                    // heap can yield thousands of distinct WI
                                    // candidates (~1 MB of log per sweep
                                    // uncapped).
                                    if (wiPawnCache.Count <= 200)
                                        Base.Log($"ScanDiag: WI 0x{wi:X8} td={td:0.####} gameOk={gameOk} " +
                                                 $"head=0x{BitConverter.ToInt32(wiB, 0x41C):X8} pawnsWalked={(walked?.Count.ToString() ?? "-")}");
                                }
                                pawnSet = walked;
                                wiPawnCache[wi] = pawnSet;
                            }
                            if (pawnSet == null || !pawnSet.Contains(candAddr)) continue;
                        }

                        // Teach the session seed from the first structural
                        // match so this scan's WorldInfo resolution and the
                        // immediate next ticks fast-path. Do NOT persist it
                        // here: the first structural match scanning memory
                        // low->high is frequently a transient ENEMY pawn.
                        // Persisting that bricks the next launch's fast path
                        // (enemy unloads -> no match -> WorldInfo goes stale
                        // -> IsInGameplayLevel() auto-disables Auto-Kill).
                        // The durable pin is taken in AutoKillTick from the
                        // verified local player pawn only.
                        if (!fast)
                        {
                            _pawnVtable = BitConverter.ToUInt32(chunk, i);
                            Base.Log($"ScanDiag: OK wi=0x{wi:X8} pawn=0x{(uint)(baseAddr + i):X8} " +
                                     $"vtable=0x{_pawnVtable:X8} regions={_dbgRegions} prefilter={_dbgPrefilter} wiOk={_dbgWiOk}");
                        }
                        _diagSeedPawnAddr = (int)(baseAddr + i);
                        _diagWeakMatch = false;
                        return wi;
                    }
                }
            }

            address = baseAddr + regionSize;
            if (address <= baseAddr) address += 0x1000;
        }
        if (!fast)
            Base.Log($"ScanDiag: FAIL regions={_dbgRegions} prefilter={_dbgPrefilter} " +
                     $"wiOk={_dbgWiOk} maxHp={AK_MAX_PLAUSIBLE_HP}");
        return 0;
    }

    // Walks an AWorldInfo's PawnList into a set of pawn addresses (as the
    // int bit-pattern, so high-half LAA pointers compare correctly). Used
    // by the structural scan to confirm a candidate is a real, listed pawn
    // — robust to where in the list it sits and to zero-health NPCs.
    // PawnList head @ WorldInfo+0x41C; APawn.NextPawn @ +0x230.
    private HashSet<int>? WalkPawnList(byte[] wiBlock)
    {
        int head = BitConverter.ToInt32(wiBlock, 0x41C);
        if (!IsHeapPtr(head)) return null;
        var set = new HashSet<int>();
        int cur = head;
        while (IsHeapPtr(cur) && set.Add(cur) && set.Count <= AK_CHAIN_MAX)
        {
            // Probe read: this is only called from the structural sweep,
            // where "heads" are frequently noise — failures are expected
            // and must not trip the stale-handle detector (see AKReadProbe).
            byte[]? pd = AKReadProbe(cur, 0x234);
            if (pd == null || pd.Length < 0x234) break;
            int np = BitConverter.ToInt32(pd, 0x0230);
            if (np == 0) break;
            cur = np;
        }
        return set;
    }
}

// ── Data classes ────────────────────────────────────────────────

public class ResultItem
{
    public int Address { get; set; }
    public string Display { get; set; } = "";
    public string Name { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Extra { get; set; } = "";
    public System.Windows.Media.Brush QualityColor { get; set; } = System.Windows.Media.Brushes.Gray;
    public bool IsFloat { get; set; }
    public override string ToString() => Display;
}

public class TrackedItem
{
    public int Address { get; set; }
    internal Base.Genus Genus { get; set; }
    public bool IsFloat { get; set; }
    public bool IsFrozen { get; set; }
    public string AddressText { get; set; } = "";
    public string TypeText { get; set; } = "";
    public string Description { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Level { get; set; } = "";
    public string ForgerName { get; set; } = "";
}
