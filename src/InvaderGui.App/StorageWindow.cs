using System.Windows;
using System.Windows.Controls;
using InvaderGui.Core;
using Microsoft.Win32;

namespace InvaderGui.App;

public sealed class StorageWindow : UtilityWindow
{
    private readonly ShellViewModel state;
    private readonly StackPanel panel = DialogUi.Column();
    public StorageWindow(ShellViewModel state)
    {
        this.state = state;
        Title = "Configuration recovery"; Width = 760; Height = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }
    private void Refresh()
    {
        panel.Children.Clear();
        panel.Children.Add(DialogUi.Text("Configuration stays beside the application. In-memory edits remain available when saving fails. A failed read blocks saving that document until its original is repaired or explicitly preserved as a backup."));
        Add(state.Settings); Add(state.Profiles); Add(state.Presets);
        panel.Children.Add(DialogUi.Button("Close recovery", Close));
    }
    private void Add<T>(JsonDocumentStore<T> store) where T : class, IVersionedDocument, new()
    {
        var group = new GroupBox { Header = System.IO.Path.GetFileName(store.FilePath), Margin = new Thickness(0, 8, 0, 8) };
        var box = DialogUi.Column(); group.Content = box; panel.Children.Add(group);
        box.Children.Add(DialogUi.Text(store.Error ?? (store.IsDirty ? "Unsaved changes" : "Saved / no changes")));
        var row = DialogUi.Row(); box.Children.Add(row);
        row.Children.Add(DialogUi.Button("Retry save", () => { store.Save(); Refresh(); }));
        row.Children.Add(DialogUi.Button("Export copy…", () =>
        {
            var picker = new SaveFileDialog { FileName = System.IO.Path.GetFileName(store.FilePath), Filter = "JSON files|*.json" };
            if (picker.ShowDialog(this) == true)
            {
                try { store.Export(picker.FileName); MessageBox.Show(this, "Exported. The application continues using its original configuration location."); }
                catch (Exception ex) { DialogUi.Error(this, ex); }
            }
        }));
        if (store.ReadBlocked)
        {
            row.Children.Add(DialogUi.Button("Reload repaired original", () =>
            {
                if (MessageBox.Show(this, "Replace this document's in-memory edits with the repaired original?", "Reload configuration", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                if (store.Load() && store == (object)state.Profiles) state.RefreshProfiles(state.Settings.Value.SelectedProfileId);
                Refresh();
            }));
            box.Children.Add(DialogUi.Button("Preserve original as backup and enable saving", () =>
            {
                if (MessageBox.Show(this, "Move the original to a uniquely named .bak file beside it and save the current in-memory document?", "Preserve and recover", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try { store.PreserveOriginalAndUnlock(); store.Save(); }
                catch (Exception ex) { DialogUi.Error(this, ex); }
                Refresh();
            }));
        }
    }
}
