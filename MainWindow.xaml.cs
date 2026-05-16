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
    private DispatcherTimer _timer;
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
                            ri.Quality = user.Quality2.ToString();
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
    {
        return q switch
        {
            Quality2.Ultimate => System.Windows.Media.Color.FromRgb(200, 140, 20),
            Quality2.Supreme => System.Windows.Media.Color.FromRgb(130, 70, 190),
            Quality2.Transcendent => System.Windows.Media.Color.FromRgb(40, 140, 200),
            Quality2.Mythical => System.Windows.Media.Color.FromRgb(200, 50, 80),
            Quality2.Godly => System.Windows.Media.Color.FromRgb(200, 160, 30),
            Quality2.Legendary => System.Windows.Media.Color.FromRgb(200, 110, 30),
            Quality2.Epic => System.Windows.Media.Color.FromRgb(140, 60, 200),
            Quality2.Amazing => System.Windows.Media.Color.FromRgb(50, 160, 70),
            Quality2.Powerful => System.Windows.Media.Color.FromRgb(80, 150, 90),
            _ => System.Windows.Media.Color.FromRgb(120, 125, 135),
        };
    }

    // ── Sidebar navigation ──────────────────────────────────────────

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string view = btn.Tag?.ToString() ?? "";
        NavigateToView(view, btn);
    }

    private void BtnHome_Click(object sender, RoutedEventArgs e) => ShowHome();

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
            "ItemDupe" => BtnItemDupe,
            "Settings" => BtnSettings,
            _ => null,
        };
        SetActiveNavButton(target);

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
                tracked.Quality = user.Quality2.ToString();
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

    private void Timer_Tick(object? sender, EventArgs e)
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
                        item.Description = desc;
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

    private void SimulateGPress()
    {
        if (!Base.OpenProcess()) return;
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
    private const int AK_CHAIN_MAX = 300;             // hard cap on pawn chain walk
    // Sanity ceiling for "is this int plausibly an HP value, not pointer
    // garbage". The original 4.0.0.4-era heuristics (5M structural / 10M
    // validate) are far below modern DD1 HP — live dump 2026-05 showed
    // Orc 10.27M, Ogre 23.5M, Spider 7.2M. Too-low caps made the pawn
    // scan reject every beefy enemy and ValidateCachedWorldInfo evict the
    // cache on the first Ogre → permanent "no enemies found".
    private const int AK_MAX_PLAUSIBLE_HP = 500_000_000;
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
        if (hpMax < 1 || hpMax > AK_MAX_PLAUSIBLE_HP || hp < -1 || hp > hpMax) return false;
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
        if (GetAKHandle() == IntPtr.Zero) return 0;
        if (_cachedWorldInfo == 0 || !ValidateCachedWorldInfo())
            _cachedWorldInfo = FindWorldInfoViaPawnScan();
        if (_cachedWorldInfo == 0) return 0;

        byte[]? plData = AKRead(_cachedWorldInfo + 0x041C, 4);
        if (plData == null) return 0;
        int cur = BitConverter.ToInt32(plData, 0);

        int tail = 0;
        var visited = new HashSet<int>();
        while (IsHeapPtr(cur) && visited.Add(cur) && visited.Count <= AK_CHAIN_MAX)
        {
            byte[]? pd = AKRead(cur, 0x32C);
            if (pd == null || pd.Length < 0x32C) break;
            tail = cur;
            int np = BitConverter.ToInt32(pd, 0x0230);
            if (np == 0) break;
            cur = np;
        }
        return tail;
    }

    private void AutoKillTick()
    {
        // Ensure we have a live handle to DunDefGame
        if (GetAKHandle() == IntPtr.Zero)
        {
            _cachedWorldInfo = 0;
            _cachedPlayerPawn = 0;
            SetAkStatus("Auto Kill: game not running");
            return;
        }

        // Periodically re-verify state: PID unchanged, WorldInfo still alive.
        // Runs every AK_VALIDATE_MS — cheap enough for a process enumeration.
        if ((DateTime.Now - _akLastValidated).TotalMilliseconds >= AK_VALIDATE_MS)
        {
            _akLastValidated = DateTime.Now;
            CheckTargetPid();
            if (_cachedWorldInfo != 0 && !ValidateCachedWorldInfo())
            {
                _cachedWorldInfo = 0;
                _cachedPlayerPawn = 0;
            }
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
        int playerPawn = chain[chain.Count - 1].addr;
        _cachedPlayerPawn = playerPawn;

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
            for (int i = 0; i < chain.Count - 1; i++) // tail = local player, skip
            {
                var p = chain[i];
                if (p.klass != 0 && _heroClasses.Contains(p.klass)) { protectedHeroes++; continue; }
                bool killable = p.hp > 0 && p.hpMax > 0;
                if (!killable)
                {
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
        // leaves the HUD looking normal. _cachedPlayerPawn is the chain tail
        // (local player); reached only past the gameplay-level gate, so this
        // never fires in the tavern/menu/loading. Independent of Auto-Kill.
        if (_unlimitedMana && _cachedPlayerPawn != 0)
        {
            int ctrl = ReadU32(_cachedPlayerPawn + OFF_PAWN_CONTROLLER);
            if (IsHeapPtr(ctrl))
            {
                int maxMana = ReadU32(ctrl + OFF_PC_MAXMANAPOWER);
                // Sanity: reject a garbage read (bad ptr) — real mana caps are
                // small (≈2k); anything huge means the controller read missed.
                if (maxMana > 0 && maxMana < 100_000_000)
                    AKWrite(ctrl + OFF_PC_MANAPOWER, maxMana);
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
                AKWrite(gri + OFF_GRI_MAXTOWERUNITS, MAX_TOWER_UNITS_VALUE);
                AKWrite(gri + OFF_GRI_MAXTOWERUNITS + 4, 0);
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
    private const int MAX_TOWER_UNITS_VALUE = 100_000;

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
    //
    // The seed only matters for the very first scan in a session. If
    // it misses, FindWorldInfoViaPawnScan's structural fallback rewrites
    // this field with the live vtable, and subsequent calls hit the
    // fast path again. Bumping the seed when a new patch ships is just
    // an optimization to skip the one-time rediscovery.
    private uint _pawnVtable = 0x00FCD7D8;

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
        var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
        if (procs.Length == 0) return IntPtr.Zero;
        int pid = procs[0].Id;
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        _akHandle = OpenProcess2(0x1F0FFF, false, pid); // PROCESS_ALL_ACCESS
        _akTargetPid = pid;
        return _akHandle;
    }

    // Called from the periodic validation tick — cheap enough at 2s cadence.
    private void CheckTargetPid()
    {
        if (_akHandle == IntPtr.Zero) return;
        var procs = System.Diagnostics.Process.GetProcessesByName("DunDefGame");
        int pid = procs.Length > 0 ? procs[0].Id : 0;
        foreach (var p in procs) { try { p.Dispose(); } catch { } }
        if (pid != _akTargetPid) ResetAutoKillHandle();
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

        bool fast = matchVtable != 0;
        byte b0 = (byte)(matchVtable & 0xFF);
        byte b1 = (byte)((matchVtable >> 8) & 0xFF);
        byte b2 = (byte)((matchVtable >> 16) & 0xFF);
        byte b3 = (byte)((matchVtable >> 24) & 0xFF);

        long address = 0;
        // DD1 is LARGEADDRESSAWARE on WOW64, so committed allocations can
        // sit up to ~0xFFFE0000. The older 0x7FFFFFFF ceiling silently cut
        // the scan in half for maps whose WorldInfo spilled into the
        // upper 2 GB.
        while (address < 0xFFFE0000L)
        {
            MEMORY_BASIC_INFORMATION mbi;
            int result = VirtualQueryEx(hProc, (IntPtr)address, out mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>());
            if (result == 0) break;

            long baseAddr = mbi.BaseAddress.ToInt64();
            long regionSize = mbi.RegionSize.ToInt64();
            if (regionSize <= 0) { address += 0x1000; continue; }

            // Only scan committed, readable memory
            bool readable = mbi.State == 0x1000 && (mbi.Protect & 0xEE) != 0 && (mbi.Protect & 0x100) == 0;

            if (readable && regionSize > 0x32C && regionSize < 50_000_000 && baseAddr >= 0x02000000)
            {
                byte[]? chunk = AKRead((int)baseAddr, (int)regionSize);
                if (chunk != null && chunk.Length >= 0x32C)
                {
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
                        //    A real pawn list has at least the player
                        //    pawn + monsters; chain length 1 (next=0)
                        //    is plausible only for empty maps and we
                        //    fall back to the first two checks alone.
                        //
                        // Fast path skips this — the literal vtable
                        // match already proves the candidate is a real
                        // instance of the cached APawn class.
                        if (!fast)
                        {
                            // Probe candidate vtable: must be committed
                            // memory. UTF-16 string fragments like
                            // 0x00740069 fall in the code-section
                            // numeric range but point into the
                            // unmapped first MB of address space and
                            // fail the read.
                            uint vt = BitConverter.ToUInt32(chunk, i);
                            byte[]? vtProbe = AKRead((int)vt, 4);
                            if (vtProbe == null || vtProbe.Length < 4) continue;

                            // UObject::Class pointer must be a real,
                            // dword-aligned heap pointer — every UObject
                            // has a non-null Class.
                            uint kls = BitConverter.ToUInt32(chunk, i + 0x0034);
                            if ((kls & 3u) != 0u) continue;
                            if (kls < 0x02000000u || kls >= 0xFFFE0000u) continue;

                            // NextPawn must be non-null and walk one
                            // step to a pawn-shaped object with the
                            // same WI backref. Populated maps always
                            // have ≥2 pawns (player + monsters), so
                            // dropping the next=0 free pass is safe and
                            // it kills false positives whose entire
                            // structure is incidental noise.
                            int next = BitConverter.ToInt32(chunk, i + 0x0230);
                            if ((unchecked((uint)next) & 3u) != 0u) continue;
                            if (!IsHeapPtr(next)) continue;
                            byte[]? nd = AKRead(next, 0x32C);
                            if (nd == null || nd.Length < 0x32C) continue;
                            uint nvt = BitConverter.ToUInt32(nd, 0);
                            if ((nvt & 3u) != 0u) continue;
                            if (nvt < 0x00400000u || nvt >= 0x02000000u) continue;
                            int nh = BitConverter.ToInt32(nd, 0x0324);
                            int nhm = BitConverter.ToInt32(nd, 0x0328);
                            if (nhm <= 0 || nhm > AK_MAX_PLAUSIBLE_HP || nh < 0 || nh > nhm) continue;
                            int nwi = BitConverter.ToInt32(nd, 0x0110);
                            if (nwi != wi) continue;
                        }

                        if (!fast)
                            _pawnVtable = BitConverter.ToUInt32(chunk, i);
                        _diagSeedPawnAddr = (int)(baseAddr + i);
                        _diagWeakMatch = false;
                        return wi;
                    }
                }
            }

            address = baseAddr + regionSize;
            if (address <= baseAddr) address += 0x1000;
        }
        return 0;
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
