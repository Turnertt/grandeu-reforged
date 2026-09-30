using System.Windows;
using System.Windows.Controls;

namespace Modinator.Views;

public partial class ItemDupeView
{
    private sealed record SourceSnapshot(ItemNative Native, string Name, string Description, string Forger, string DisplayName);
    private sealed record PreparedCopy(ItemDupeValues Values, string Name, string Description, string Forger,
        int[] References, int World, DupeReferenceResolver? Resolver);

    private SourceSnapshot ReadLiveSource(int world)
    {
        _sourceSession.Check();
        if (_sourceAddr is not int address || _sourceId is not ItemIdentity id)
            throw new InvalidOperationException("Pick a live source item first.");
        if (_remoteSource != null) MultiplayerEquipment.Verify(_remoteSource, world);
        var native = DupeMemory.ReadItem(address);
        if (!id.Matches(native)) throw new InvalidOperationException(ItemIdentity.ChangedMessage);
        var snapshot = new SourceSnapshot(native,
            Base.ReadUni<ItemNative>(address, "EquipmentName"),
            Base.ReadUni<ItemNative>(address, "Description"),
            Base.ReadUni<ItemNative>(address, "ForgerName"), DupeMemory.ItemName(address));
        _sourceSession.Check();
        if (!id.Matches(DupeMemory.ReadItem(address))) throw new InvalidOperationException(ItemIdentity.ChangedMessage);
        return snapshot;
    }

    // Also called by MainWindow when the Templates tab's USE IN DUPE is pressed.
    internal void UseTemplate(ItemTemplate entry)
    {
        if (_busy) return;
        _templateSource = entry.Copy();
        _sourceAddr = null;
        _sourceId = null;
        _remoteSource = null;
        TxtSrcName.Text = entry.DisplayName;
        TxtSrcName.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        TxtSrcMeta.Text = "Template: " + entry.Name;
        TxtSrcAddr.Text = "Saved " + entry.CreatedUtc.ToLocalTime().ToString("g");
        BuildStats(SrcStats, Base.ItemToUser(entry.Values.Preview()));
        SrcEmpty.Visibility = Visibility.Collapsed;
        SrcBody.Visibility = Visibility.Visible;
        RefreshDupeEnabled();
    }

    // Another player's equipped item or a floor drop, picked in the source
    // picker's Players & Floor tab. Re-verified against the live game before
    // every read (MultiplayerEquipment.Verify).
    private void UseRemote(MultiplayerItem item)
    {
        _remoteSource = item;
        _sourceAddr = item.Address;
        _sourceId = item.Identity;
        _sourceSession = item.Session;
        _templateSource = null;
        ShowItem(false, item.Address);
        TxtSrcAddr.Text = item.SourceDescription + " · live source";
        RefreshDupeEnabled();
    }

    private void BtnSaveTemplate_Click(object sender, RoutedEventArgs e) => AddToTemplates(target: false);
    private void BtnSaveTargetTemplate_Click(object sender, RoutedEventArgs e) => AddToTemplates(target: true);

    // "Add to templates" on the source or the target card. The picked item
    // stays picked; the saved template goes to the library (Templates tab).
    private async void AddToTemplates(bool target)
    {
        int? picked = target ? _sacrificialAddr : _sourceAddr;
        ItemIdentity? pickedId = target ? _sacrificialId : _sourceId;
        if (_busy || picked is not int address || pickedId is not ItemIdentity identity ||
            !Base.OpenProcess() || Window.GetWindow(this) is not MainWindow main) return;
        var session = target ? _targetSession : _sourceSession;
        var remote = target ? null : _remoteSource;
        BeginOperation(target ? "Reading target and saving its item references…" : "Reading source and saving its item references…");
        try
        {
            var token = _operation!.Token;
            var entry = await Task.Run(() => main.ReadForDupe(world =>
            {
                return ItemTemplateCapture.Capture(address, identity, session, token,
                    remote != null ? () => MultiplayerEquipment.Verify(remote, world) : null);
            }, discoverWorld: remote != null));
            token.ThrowIfCancellationRequested();
            EndOperation();
            var dialog = new ItemTemplateEditDialog(entry, isNew: true) { Owner = main };
            TxtStatus.Text = dialog.ShowDialog() == true && dialog.Saved is ItemTemplate saved
                ? "Template saved: " + saved.Name
                : "Template not saved.";
        }
        catch (OperationCanceledException) { EndOperation(); TxtStatus.Text = "Template capture cancelled."; }
        catch (Exception ex) { EndOperation(); TxtStatus.Text = "Template not saved: " + ex.Message; }
    }

    private static int[] References(ItemNative n) =>
        [n.EquipmentTemplate, n.DamageReductions[0].DamageType, n.DamageReductions[1].DamageType,
         n.DamageReductions[2].DamageType, n.DamageReductions[3].DamageType, n.WeaponAdditionalDamage.DamageType];

    private static void SetReferences(ref ItemNative merged, int[] references)
    {
        merged.EquipmentTemplate = references[0];
        for (int i = 0; i < 4; i++) merged.DamageReductions[i].DamageType = references[i + 1];
        merged.WeaponAdditionalDamage.DamageType = references[5];
    }

    private ItemNative ReadOwnedTarget(MainWindow main, int address)
    {
        _targetSession.Check();
        var target = DupeMemory.ReadItem(address);
        if (_sacrificialId is not ItemIdentity id || !id.Matches(target))
            throw new InvalidOperationException(ItemIdentity.ChangedMessage);
        int pawn = main.ResolvePlayerPawnAddress();
        if (main.LastPlayerPick < MainWindow.PlayerPawnPick.LocalPlayer)
            throw new InvalidOperationException("Your character could not be verified, so the target can't be confirmed as yours. Load into the Tavern or a level and try again.");
        int hm = GameChain.ResolveHeroManager(pawn);
        if (!GameChain.IsVerifiedHeroManager(hm)) throw new InvalidOperationException("Your local inventory is unavailable.");
        int equipment = unchecked(address - 0x38);
        bool owned = GameChain.ReadItemBox(hm).Contains(equipment);
        if (!owned)
            foreach (int hero in GameChain.ReadLocalHeroes(hm))
            {
                if (DupeMemory.ReadArray(hero + 0x5B0, 128).Contains(equipment) || GameChain.RdInt(hero + 0x5C8) == equipment)
                { owned = true; break; }
            }
        if (!owned) throw new InvalidOperationException("The target is no longer in your item box or on one of your heroes. Pick it again.");
        _targetSession.Check();
        var latest = DupeMemory.ReadItem(address);
        if (!id.Matches(latest)) throw new InvalidOperationException(ItemIdentity.ChangedMessage);
        return latest;
    }

    private async void BtnDupe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _sacrificialAddr is not int targetAddress || (_sourceAddr == null && _templateSource == null) ||
            targetAddress == _sourceAddr || !Base.OpenProcess() || Window.GetWindow(this) is not MainWindow main) return;
        // A different-class target is allowed: it adds one line to the
        // confirmation, and the copy path only refuses a mismatch that was
        // not shown here.
        string? unstable = CurrentSourceClassPath() is string sourcePath ? ClassMismatch(targetAddress, sourcePath) : null;
        bool acceptedMismatch = unstable != null;
        if (MessageBox.Show(main, $"Overwrite “{TxtSacName.Text}” using “{TxtSrcName.Text}”?\n\n" +
            (unstable != null ? unstable + "\n\n" : "") +
            "Copy stats, item type, supported appearance and text. The target keeps its own identity.\n\n" +
            "This replaces the target item's values. The copy keeps the sacrificial item's old skin/look until you drop it. " +
            "Drop it and pick it back up to refresh the skin/texture.",
            "Confirm copy", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        BeginOperation("Preparing copy…");
        bool writeStarted = false;
        var notes = new List<string>();
        try
        {
            var token = _operation!.Token;
            var progress = new Progress<string>(text => { if (_busy) TxtStatus.Text = text; });
            var copy = await Task.Run(() => main.ReadForDupe(world =>
            {
                token.ThrowIfCancellationRequested();
                var initialTarget = ReadOwnedTarget(main, targetAddress);
                ItemDupeValues values;
                string name, description, forger;
                int[] references;
                DupeReferenceResolver? resolver = null;
                if (_templateSource is ItemTemplate entry)
                {
                    entry.Validate();
                    values = entry.Values;
                    name = entry.EquipmentName; description = entry.Description; forger = entry.ForgerName;
                    resolver = new DupeReferenceResolver(token);
                    references = resolver.Resolve(entry.References, progress, References(initialTarget));
                }
                else
                {
                    var source = ReadLiveSource(world);
                    values = ItemDupeValues.Capture(source.Native);
                    name = source.Name; description = source.Description; forger = source.Forger;
                    references = References(source.Native);
                }
                // Nothing above writes. Resolve all references and validate both
                // ends before even the first string write can occur.
                token.ThrowIfCancellationRequested();
                ReadOwnedTarget(main, targetAddress);
                // Class check (see ClassMismatch): a different-class target
                // goes ahead only if the confirmation already said so.
                string sourceClass = _templateSource is ItemTemplate te ? DupeMemory.TemplateClassPath(te.References[0])
                                                                        : DupeMemory.ClassPath(references[0]);
                if (!acceptedMismatch && ClassMismatch(targetAddress, sourceClass) is string why)
                    throw new InvalidOperationException(why + " Press Overwrite again to continue anyway.");
                if (_remoteSource != null) MultiplayerEquipment.Verify(_remoteSource, world);
                if (_sourceAddr != null) ReadLiveSource(world);
                if (resolver != null)
                    for (int i = 0; i < references!.Length; i++)
                        if (resolver.Key(references[i]) != _templateSource!.References[i])
                            throw new InvalidOperationException("A template reference unloaded. Retry the copy.");
                return new PreparedCopy(values, name, description, forger, references, world, resolver);
            }, discoverWorld: true));
            // Like the existing editors, the short commit stays on the UI
            // thread. Attach/reset handlers and the freeze timer cannot swap
            // Scanner handles midway through this sequence. Never Invoke from
            // inside the scan gate: the worker has already released it here.
            token.ThrowIfCancellationRequested();
            SaveBackup.OnGameWrite(); // finish potentially slow I/O before final identity checks
            _targetSession.Check();
            if (_sourceAddr != null)
            {
                var source = ReadLiveSource(copy.World);
                copy = new(ItemDupeValues.Capture(source.Native), source.Name, source.Description, source.Forger,
                    References(source.Native), copy.World, null);
            }
            if (copy.Resolver != null)
                for (int i = 0; i < copy.References!.Length; i++)
                    if (copy.Resolver.Key(copy.References[i]) != _templateSource!.References[i])
                        throw new InvalidOperationException("A template reference unloaded. Retry the copy.");
            var target = ReadOwnedTarget(main, targetAddress);
            if (_sacrificialId is not ItemIdentity targetId || !targetId.Matches(target))
                throw new InvalidOperationException(ItemIdentity.ChangedMessage);
            if (!acceptedMismatch && ClassMismatch(targetAddress, DupeMemory.ClassPath(copy.References[0])) is string mismatch)
                throw new InvalidOperationException(mismatch + " Press Overwrite again to continue anyway.");
            if (acceptedMismatch) notes.Add("target was a different kind of item than the source");
            var merged = copy.Values.Apply(target, DupeMemory.InstanceStateMask(unchecked(targetAddress - 0x38)));
            SetReferences(ref merged, copy.References);
            token.ThrowIfCancellationRequested();
            _targetSession.Check();
            // Once the short commit starts, finish it even if the user cancels.
            writeStarted = true;
            // Name and forger only into the target's own buffers (WriteFit);
            // a custom name that doesn't fit is blanked so the game names the
            // item from the copied name index + archetype tables.
            //
            // The description is marked like every other edit: in the
            // target's own buffer when it fits (full mark, else the short
            // plain one), otherwise in a newly allocated buffer, as the
            // item editor and Bulk Edit do. It is the only string the copy
            // allocates for.
            string customName = string.IsNullOrWhiteSpace(copy.Name) ? " " : copy.Name.Trim();
            merged.EquipmentName = WriteFit(target.EquipmentName, customName, "Custom name", notes, allowTruncate: false);
            string description = copy.Description ?? "";
            string marked = Watermark.Apply(description);
            string compact = Watermark.ApplyCompact(description);
            int room = GameChain.IsGamePtr(target.Description.Address) ? target.Description.MaximumLength : 0;
            if (marked.Length + 1 <= room || !Watermark.Enabled)
                // It fits — or the mark is switched off, where the old
                // no-allocation rule applies (shorten to fit).
                merged.Description = WriteFit(target.Description, marked, "Description", notes, allowTruncate: true);
            else if (compact != description && compact.Length + 1 <= room)
                merged.Description = WriteFit(target.Description, compact, "Description", notes, allowTruncate: false);
            else
                // Includes a source that is ALREADY marked: shortening it to
                // fit would cut the mark off the end.
                merged.Description = Base.WriteUni(targetAddress, "Description", marked);
            merged.ForgerName = WriteFit(target.ForgerName, copy.Forger, "Forger name", notes, allowTruncate: false);
            Base.Instance.WriteMemory(targetAddress, Base.Push(merged));
            _targetSession.Check();
            var actual = DupeMemory.ReadItem(targetAddress);
            // Identity after the copy: the IDs must be the target's own; for an
            // ID-less target the archetype is now the SOURCE's by design.
            bool stillTarget = targetId.HasIds ? targetId.Matches(actual) : actual.EquipmentTemplate == merged.EquipmentTemplate;
            if (!stillTarget)
                throw new InvalidOperationException("The target changed during the copy. Rescan before doing anything else.");
            EndOperation();
            ShowItem(true, targetAddress);
            TxtStatus.Text = "Copied. Drop the target and pick it back up to refresh its skin/texture, then rescan the Forge Viewer." +
                             (notes.Count > 0 ? " Note: " + string.Join("; ", notes) + "." : "");
            Base.LogEvent($"Item Dupe: copied onto 0x{targetAddress:X8}" + (notes.Count > 0 ? " — " + string.Join("; ", notes) : ""));
            Toast.Show("Drop the target and pick it back up to refresh its look, then rescan the Forge Viewer." +
                       (notes.Count > 0 ? "\n" + string.Join("; ", notes) + "." : ""), ToastKind.Success, "Item copied");
        }
        catch (OperationCanceledException) { EndOperation(); TxtStatus.Text = "Copy cancelled before writing."; }
        catch (Exception ex)
        {
            EndOperation();
            TxtStatus.Text = (writeStarted ? "Copy interrupted; some values may have changed. " : "Not copied. ") + ex.Message;
            Base.LogEvent("Item Dupe: " + TxtStatus.Text);
            Toast.Show(TxtStatus.Text, ToastKind.Error, "Item Dupe");
        }
    }

    private void BeginOperation(string status)
    {
        _operation = new CancellationTokenSource();
        _busy = true;
        RefreshDupeEnabled();
        TxtStatus.Text = status;
    }

    private void EndOperation()
    {
        _operation?.Dispose(); _operation = null;
        _busy = false;
        RefreshDupeEnabled();
    }

    private void BtnCancelOperation_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
}
