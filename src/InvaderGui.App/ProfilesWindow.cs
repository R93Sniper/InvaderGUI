using System.Windows;
using System.Windows.Controls;
using System.Text.Json;
using InvaderGui.Core;

namespace InvaderGui.App;

public sealed class ProfilesWindow : UtilityWindow
{
    private readonly ShellViewModel state;
    private readonly ListBox profiles = new() { MinWidth = 200, Margin = new Thickness(0, 0, 12, 0), DisplayMemberPath = "Name" };
    private readonly StackPanel editor = DialogUi.Column();
    private ProjectProfile? draft;
    private string? savedEditorState;
    private Guid? loadedId;
    private TextBox? name, working, data, maps, resources;
    private ListBox? roots;
    private bool refreshing;

    public ProfilesWindow(ShellViewModel state)
    {
        this.state = state;
        Title = "Editing Kit Profiles"; Width = 1040; Height = 790; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel(); grid.Children.Add(left);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; DockPanel.SetDock(buttons, Dock.Top); left.Children.Add(buttons);
        buttons.Children.Add(DialogUi.Button("New", New)); buttons.Children.Add(DialogUi.Button("Duplicate", Duplicate)); buttons.Children.Add(DialogUi.Button("Delete", Delete));
        left.Children.Add(profiles);
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); grid.Children.Add(scroll); Content = grid;
        profiles.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            if (profiles.SelectedItem is ProjectProfile selected)
            {
                if (!CanAbandon()) { Select(loadedId); return; }
                Select(selected.Id);
                Load(selected.Copy());
            }
        };
        Closing += (_, e) => { if (!CanAbandon()) e.Cancel = true; };
        Refresh(state.SelectedProfile?.Id);
    }

    private void Capture()
    {
        if (draft is null || name is null) return;
        draft.Name = name.Text.Trim(); draft.WorkingDirectory = working!.Text.Trim();
        draft.Paths.DataRoot = data!.Text.Trim(); draft.Paths.MapsRoot = maps!.Text.Trim();
        draft.Paths.ResourceMapsRoot = string.IsNullOrWhiteSpace(resources!.Text) ? null : resources.Text.Trim();
    }
    private bool CanAbandon()
    {
        Capture();
        if (draft is null) return true;
        if (savedEditorState == EditorState(draft)) return true;
        var choice = MessageBox.Show(this, "Save profile edits before continuing? Choose No to discard these editor changes.", "Profile edits", MessageBoxButton.YesNoCancel);
        return choice == MessageBoxResult.No || (choice == MessageBoxResult.Yes && Save());
    }
    private static string EditorState(ProjectProfile profile) =>
        JsonSerializer.Serialize(new { profile.Name, profile.WorkingDirectory, profile.Paths }, JsonFiles.Options);
    private void Select(Guid? id)
    {
        refreshing = true;
        profiles.SelectedItem = state.Profiles.Value.Profiles.FirstOrDefault(p => p.Id == id);
        refreshing = false;
    }
    private void Refresh(Guid? id)
    {
        RefreshList(id);
        Load((profiles.SelectedItem as ProjectProfile)?.Copy());
    }
    private void RefreshList(Guid? id)
    {
        refreshing = true;
        profiles.ItemsSource = null; profiles.ItemsSource = state.Profiles.Value.Profiles;
        profiles.SelectedItem = state.Profiles.Value.Profiles.FirstOrDefault(p => p.Id == id) ?? state.Profiles.Value.Profiles.FirstOrDefault();
        refreshing = false;
    }
    private void Load(ProjectProfile? profile)
    {
        draft = profile; loadedId = profile?.Id; editor.Children.Clear();
        savedEditorState = profile is not null && state.Profiles.Value.Profiles.Any(p => p.Id == profile.Id) ? EditorState(profile) : null;
        if (profile is null) { editor.Children.Add(DialogUi.Text("Create a profile to configure your editing kit folders.")); return; }
        editor.Children.Add(DialogUi.Text("Editing kit paths apply to future tasks. Running tasks keep the profile and paths captured at launch. Relative data, maps, and tag folders resolve beneath the Editing Kit Folder and need not exist yet."));
        name = DialogUi.Field(editor, "Profile name", profile.Name);
        working = DialogUi.Folder(editor, "Editing Kit Folder (existing absolute folder)", profile.WorkingDirectory, this);
        data = DialogUi.Folder(editor, "Data root", profile.Paths.DataRoot, this);
        maps = DialogUi.Folder(editor, "Maps root", profile.Paths.MapsRoot, this);
        resources = DialogUi.Folder(editor, "Resource maps root (Only used when building gearbox maps)", profile.Paths.ResourceMapsRoot ?? "", this);
        editor.Children.Add(DialogUi.Text("Tag roots — first has highest precedence. Single-root operations will select an explicit root."));
        roots = new ListBox { Height = 140 }; editor.Children.Add(roots); RefreshRoots();
        var rootButtons = DialogUi.Row(); editor.Children.Add(rootButtons);
        rootButtons.Children.Add(DialogUi.Button("Add…", () => EditRoot(null)));
        rootButtons.Children.Add(DialogUi.Button("Edit…", () => { if (roots.SelectedItem is TagRoot root) EditRoot(root); }));
        rootButtons.Children.Add(DialogUi.Button("Remove", () =>
        {
            if (roots.SelectedItem is not TagRoot root) return;
            if (!draft!.RemoveTagRoot(root.Id))
            { MessageBox.Show(this, "Keep at least one tag root in the profile. Add a replacement folder before removing this one."); return; }
            RefreshRoots();
        }));
        rootButtons.Children.Add(DialogUi.Button("Up", () => MoveRoot(-1)));
        rootButtons.Children.Add(DialogUi.Button("Down", () => MoveRoot(1)));
        var actions = DialogUi.Row(); editor.Children.Add(actions);
        actions.Children.Add(DialogUi.Button("Save profile", () => Save()));
        actions.Children.Add(DialogUi.Button("Use this profile", () =>
        {
            if (Save()) { state.SelectedProfile = state.ProfileItems.First(p => p.Id == draft!.Id); DialogUi.SaveWithRecovery(this, state); }
        }));
        actions.Children.Add(DialogUi.Button("Close", Close));
    }
    private void RefreshRoots(TagRoot? select = null)
    {
        roots!.ItemsSource = null; roots.ItemsSource = draft!.Paths.TagRoots; roots.SelectedItem = select;
    }
    private void MoveRoot(int direction)
    {
        if (roots!.SelectedItem is not TagRoot root) return;
        int index = draft!.Paths.TagRoots.IndexOf(root), target = index + direction;
        if (target < 0 || target >= draft.Paths.TagRoots.Count) return;
        draft.Paths.TagRoots.RemoveAt(index); draft.Paths.TagRoots.Insert(target, root); RefreshRoots(root);
    }
    private void EditRoot(TagRoot? root)
    {
        var window = new UtilityWindow { Owner = this, Title = root is null ? "Add tag root" : "Edit tag root", Width = 600, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = DialogUi.Column(); window.Content = panel;
        var rootName = DialogUi.Field(panel, "Name", root?.Name ?? "Tags");
        var rootPath = DialogUi.Folder(panel, "Tag folder", root?.Path ?? "tags", window);
        panel.Children.Add(DialogUi.Button("Apply", () =>
        {
            if (string.IsNullOrWhiteSpace(rootName.Text) || string.IsNullOrWhiteSpace(rootPath.Text)) { MessageBox.Show(window, "Enter a root name and path."); return; }
            var item = root ?? new TagRoot(); item.Name = rootName.Text.Trim(); item.Path = rootPath.Text.Trim();
            if (root is null) draft!.Paths.TagRoots.Add(item);
            RefreshRoots(item); window.Close();
        })); window.ShowDialog();
    }
    private bool Save()
    {
        Capture(); if (draft is null) return true;
        var errors = draft.Validate().ToList();
        if (state.Profiles.Value.Profiles.Any(p => p.Id != draft.Id && p.Name.Equals(draft.Name, StringComparison.OrdinalIgnoreCase))) errors.Add("Choose a unique profile name.");
        if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), "Profile validation"); return false; }
        var index = state.Profiles.Value.Profiles.FindIndex(p => p.Id == draft.Id);
        if (index < 0) state.Profiles.Value.Profiles.Add(draft.Copy()); else state.Profiles.Value.Profiles[index] = draft.Copy();
        state.Profiles.MarkDirty(); state.RefreshProfiles(state.SelectedProfile?.Id ?? draft.Id);
        DialogUi.SaveWithRecovery(this, state);
        savedEditorState = EditorState(draft);
        // List<T> and the profile model do not notify WPF when a row is added/replaced.
        // Rebind the sidebar after saving, retaining the editor and its selected identity.
        RefreshList(draft.Id);
        return true;
    }
    private void New()
    {
        if (!CanAbandon()) return;
        Select(null);
        Load(new ProjectProfile { Name = UniqueName("New profile"), Paths = new ProfilePaths { TagRoots = new() { new TagRoot() } } });
    }
    private void Duplicate()
    {
        if (!CanAbandon() || profiles.SelectedItem is not ProjectProfile selected) return;
        var copy = selected.Copy(); copy.Id = Guid.NewGuid(); copy.Name = UniqueName(selected.Name + " copy");
        Select(null); Load(copy);
    }
    private string UniqueName(string basis)
    {
        var candidate = basis; int suffix = 2;
        while (state.Profiles.Value.Profiles.Any(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))) candidate = $"{basis} {suffix++}";
        return candidate;
    }
    private void Delete()
    {
        if (profiles.SelectedItem is not ProjectProfile selected) return;
        if (MessageBox.Show(this, $"Delete profile '{selected.Name}' and its retained operation settings? Running tasks keep their captured profile. No editing kit files will be deleted.", "Delete profile", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        state.Profiles.Value.Profiles.Remove(selected); state.Profiles.MarkDirty();
        state.RefreshProfiles(state.SelectedProfile?.Id); draft = null;
        DialogUi.SaveWithRecovery(this, state); Refresh(state.SelectedProfile?.Id);
    }
}

