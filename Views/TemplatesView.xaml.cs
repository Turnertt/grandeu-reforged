using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Modinator.Views;

// The template library as a tab: saved items drawn as Forge-style cards.
// Browsing, editing and deleting work offline (no attach, no game reads).
// ADD TEMPLATE opens the Item Dupe source picker without its Templates tab
// (Forge / Players & Floor) and saves the picked item; templates can also be added from a Forge
// Viewer card, the item editor, or the source / target cards in Item Dupe.
// USE IN DUPE hands the selected one to the dupe tab.
public partial class TemplatesView : UserControl
{
    private readonly Button _use, _edit, _delete, _more, _add;
    private bool _adding;

    public TemplatesView()
    {
        InitializeComponent();
        Browser.Configure("TEMPLATES", "\uE8F1", "Search templates by name, item, or description...",
            "Click to select  ·  double-click to edit",
            "No templates yet. Press ADD TEMPLATE to save one of your items.");
        Browser.AddNewestSort(select: false);

        _use = AddAction("USE IN DUPE", null, "Open Item Dupe with this template as the source.", Use);
        _use.Style = (Style)FindResource("AccentButton");
        _edit = AddAction("EDIT", "\uE70F", "Change the template's name, stats, colors or text.", () => Edit(duplicate: false));
        _delete = AddAction("DELETE", "\uE74D", "Remove this template from the library.", Delete);
        _delete.Style = (Style)FindResource("DangerButton");
        _add = AddAction("ADD TEMPLATE", "\uE710", "Pick one of your items, another player's gear or a floor item and save it as a template.", Add);
        _more = AddAction("MORE", null, "More template actions.", () => { });
        _more.Content = DropMenu.ButtonContent("MORE");
        var menu = new DropMenu(_more);
        var duplicate = menu.Add("\uE8C8", "Duplicate template", () => Edit(duplicate: true));
        menu.Add("\uE838", "Show file location", ShowFileLocation);
        menu.Opening += () => DropMenu.SetEnabled(duplicate, SelectedTemplate != null);

        Browser.SelectionChanged += UpdateButtons;
        Browser.Activated += _ => Edit(duplicate: false);
        // The view instance is cached by MainWindow; reload on every visit so
        // a template just added from the Forge Viewer is already here.
        Loaded += (_, _) => Reload();
        UpdateButtons();
    }

    // glyph null: text only.
    private Button AddAction(string text, string? glyph, string toolTip, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 7, 14, 7),
            Height = 32,
            Margin = new Thickness(Browser.Actions.Children.Count == 0 ? 0 : 8, 0, 0, 0),
            ToolTip = toolTip
        };
        if (glyph != null)
        {
            button.Tag = glyph;
            button.ContentTemplate = (DataTemplate)FindResource("IconButtonContent");
        }
        button.Click += (_, _) => onClick();
        Browser.Actions.Children.Add(button);
        return button;
    }

    private ItemTemplate? SelectedTemplate => Browser.Selected?.Payload as ItemTemplate;

    private void UpdateButtons()
        => _use.IsEnabled = _edit.IsEnabled = _delete.IsEnabled = SelectedTemplate != null;

    private void Reload(Guid? select = null)
    {
        try
        {
            var entries = ItemTemplateLibrary.Load(out int unreadable);
            var cards = new List<ItemCardData>(entries.Count);
            foreach (var entry in entries)
            {
                try { cards.Add(ItemCard.FromTemplate(entry)); }
                catch { unreadable++; }
            }
            Browser.SetItems(cards, unreadable > 0 ? unreadable + " unreadable files skipped" : "", keepView: true);
            if (select is Guid id) Browser.Select(id.ToString("N"));
        }
        catch (Exception ex)
        {
            Browser.SetItems(Array.Empty<ItemCardData>());
            Browser.SetMessage("Library unavailable: " + ex.Message);
        }
    }

    private void Use()
    {
        if (SelectedTemplate is ItemTemplate entry && Window.GetWindow(this) is MainWindow main)
            main.UseTemplateInDupe(entry);
    }

    // Opens the template editor on a draft; a save reloads and selects it.
    private void OpenEditor(ItemTemplate draft, bool isNew)
    {
        var editor = new ItemTemplateEditDialog(draft, isNew) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true && editor.Saved is ItemTemplate saved)
        {
            Reload(saved.Id);
            Browser.SetMessage("Saved “" + saved.Name + "”.");
        }
    }

    private static ItemTemplate CopyOf(ItemTemplate entry)
    {
        var draft = entry.Copy(newIdentity: true);
        draft.Name = (entry.Name.Length > 113 ? entry.Name[..113] : entry.Name) + " (copy)";
        return draft;
    }

    private void Edit(bool duplicate)
    {
        if (SelectedTemplate is not ItemTemplate entry) return;
        try { OpenEditor(duplicate ? CopyOf(entry) : entry.Copy(), isNew: duplicate); }
        catch (Exception ex) { Browser.SetMessage("Template could not be opened: " + ex.Message); }
    }

    // ADD TEMPLATE: the dupe source picker (Forge / Players & Floor, no
    // Templates tab) with a different verb. The picked item is captured
    // under the scan gate exactly like the other "add to templates" paths.
    //
    // Reading the item takes a few seconds (its references are resolved by
    // name), so the button is disabled and a moving bar shows until the
    // editor opens; any failure is reported in a message box, never just
    // in the status line, so a pick can't appear to "do nothing".
    private async void Add()
    {
        if (_adding || Window.GetWindow(this) is not MainWindow main) return;
        if (!Base.OpenProcess())
        {
            Browser.SetMessage("Start the game first — adding a template reads an item from it.");
            return;
        }
        var picker = new ItemPickerDialog(main, ItemPickerDialog.Mode.Template, excludeAddress: 0);
        if (picker.ShowDialog() != true) return;
        _adding = true;
        _add.IsEnabled = false;
        try
        {
            var remote = picker.PickedRemote;
            int address = remote?.Address ?? picker.PickedAddress ?? 0;
            if (address == 0) throw new InvalidOperationException("No item was picked.");
            Browser.SetWorking("Reading the item for the template library…");
            ItemIdentity identity = remote?.Identity ?? ItemIdentity.Of(DupeMemory.ReadItem(address));
            DupeSession session = remote?.Session ?? DupeSession.Current;
            var draft = await Task.Run(() => main.ReadForDupe(world =>
                ItemTemplateCapture.Capture(address, identity, session, CancellationToken.None,
                    remote != null ? () => MultiplayerEquipment.Verify(remote, world) : null),
                discoverWorld: remote != null));
            Browser.SetProgress(null);
            Reload(); // restores the count in the status line if the editor is cancelled
            OpenEditor(draft, isNew: true);
        }
        catch (Exception ex)
        {
            Browser.SetProgress(null);
            Reload();
            Browser.SetMessage("Template not saved: " + ex.Message);
            Base.LogEvent("Add template failed: " + ex.Message);
            Base.RaiseMessage("That item could not be saved as a template.\n\n" + ex.Message, "Add template");
        }
        finally
        {
            Browser.SetProgress(null);
            _add.IsEnabled = true;
            _adding = false;
        }
    }

    private void Delete()
    {
        if (SelectedTemplate is not ItemTemplate entry) return;
        if (MessageBox.Show(Application.Current.MainWindow, $"Delete template “{entry.Name}”?", "Delete template",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { ItemTemplateLibrary.Delete(entry); Reload(); Toast.Show("“" + entry.Name + "” was removed from your templates.", ToastKind.Info, "Template deleted"); }
        catch (Exception ex) { Browser.SetMessage("Template was not deleted: " + ex.Message); }
    }

    // Explorer with the selected template's file highlighted, or just the
    // library folder when nothing is selected.
    private void ShowFileLocation()
    {
        try
        {
            Directory.CreateDirectory(ItemTemplateLibrary.Folder);
            string? file = SelectedTemplate is ItemTemplate entry
                ? Path.Combine(ItemTemplateLibrary.Folder, entry.Id.ToString("N") + ".json") : null;
            string arguments = file != null && File.Exists(file)
                ? "/select,\"" + file + "\""
                : "\"" + ItemTemplateLibrary.Folder + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) { Browser.SetMessage("Could not open the folder: " + ex.Message); }
    }
}
