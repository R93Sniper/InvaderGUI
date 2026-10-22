using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using InvaderGui.Core;

namespace InvaderGui.App;

public partial class RefactorPreview
{
    private TagHierarchySnapshot? suggestionSnapshot;
    private long suggestionRevision = -1;
    private Guid? suggestionEditId;
    private bool updatingPathSuggestions;
    private Dictionary<string, TagHierarchyNode[]> suggestionFolders = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> suggestionDirectories = new(StringComparer.OrdinalIgnoreCase);

    private static string ParentFolder(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator < 0 ? "" : path[..separator];
    }

    private void AttachPathSuggestions(ComboBox source, ComboBox destination)
    {
        bool queued = false;
        void Refresh() => PopulatePathSuggestions(source, destination);
        source.DropDownOpened += (_, _) => Refresh();
        destination.DropDownOpened += (_, _) => Refresh();
        // Let ComboBox finish committing typed/selected text before updating its items.
        void TextChanged(object sender, TextChangedEventArgs e)
        {
            if (updatingPathSuggestions || queued || !source.IsDropDownOpen && !destination.IsDropDownOpen) return;
            queued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                queued = false;
                if (source.IsLoaded && (ReferenceEquals(source, SourceField) || extraPairs.Any(pair => ReferenceEquals(pair.Source, source)))) Refresh();
            }));
        }
        source.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(TextChanged));
        destination.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(TextChanged));
        source.MaxDropDownHeight = destination.MaxDropDownHeight = 260;
    }

    private void RefreshAllPathSuggestions()
    {
        if (!stagingReady || updatingPathSuggestions || MappingSelector.SelectedIndex is not (0 or 1)) return;
        PopulatePathSuggestions(SourceField, DestinationField);
        foreach (var pair in extraPairs) PopulatePathSuggestions(pair.Source, pair.Destination);
    }

    private void EnsureSuggestionFolders()
    {
        var snapshot = Snapshot is not null && PlanIsActive() ? Snapshot : null;
        if (ReferenceEquals(snapshot, suggestionSnapshot) && suggestionRevision == planRevision && suggestionEditId == editingCommandId) return;
        suggestionSnapshot = snapshot; suggestionRevision = planRevision; suggestionEditId = editingCommandId;
        suggestionFolders.Clear(); suggestionDirectories.Clear();
        if (snapshot is null) return;
        var prefixCount = editingCommandId is { } id ? pendingCommands.FindIndex(command => command.Id == id) : pendingCommands.Count;
        var context = prefixCount <= 0 ? snapshot : RefactorPlanner.Replay(snapshot, pendingCommands.Take(prefixCount)).Planned;
        var nodes = RefactorPlanner.Flatten(context.Nodes).ToArray();
        suggestionDirectories = nodes.Where(node => node.IsFolder).Select(node => node.LogicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        suggestionFolders = nodes.GroupBy(node => ParentFolder(node.LogicalPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(node => node.IsFolder).ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private void PopulatePathSuggestions(ComboBox source, ComboBox destination)
    {
        if (updatingPathSuggestions || MappingSelector.SelectedIndex is not (0 or 1) || !source.IsEditable) return;
        updatingPathSuggestions = true;
        try
        {
            EnsureSuggestionFolders();
            var foldersOnly = MappingSelector.SelectedIndex == 1;
            string Folder(string text, string fallback)
            {
                text = text.Replace('/', '\\');
                if (text.Length == 0) return fallback;
                if (text.EndsWith('\\')) return text.TrimEnd('\\');
                if (!foldersOnly && suggestionDirectories.Contains(text)) return text;
                return ParentFolder(text);
            }
            var sourceFolder = Folder(source.Text, "");
            var destinationFolder = Folder(destination.Text, sourceFolder);
            var group = Path.GetExtension(source.Text.Replace('/', '\\'));
            string[] Nearby(string folder, bool target) => suggestionFolders.GetValueOrDefault(folder, [])
                .Where(node => node.IsFolder || !foldersOnly && (!target || SelectedMode == "no-move" || group.Length == 0 || Path.GetExtension(node.LogicalPath).Equals(group, StringComparison.OrdinalIgnoreCase)))
                .Select(node => node.LogicalPath + (node.IsFolder ? "\\" : "")).ToArray();
            SetSuggestions(source, Nearby(sourceFolder, false));
            SetSuggestions(destination, Nearby(destinationFolder, true));
            source.ToolTip = foldersOnly ? "Choose a nearby folder or type a folder path. Folder suggestions end with a backslash." :
                "Choose a tag in this folder or type a tag path. Choose a folder ending in a backslash to browse inside it.";
            destination.ToolTip = foldersOnly ? "Choose a nearby folder or type a new destination folder path." :
                "Choose a nearby folder or tag, or type a new destination path. Existing tags are valid targets for References only; Move/Copy still checks collisions.";
        }
        finally { updatingPathSuggestions = false; }
    }

    private static void SetSuggestions(ComboBox field, string[] suggestions)
    {
        if (field.ItemsSource is string[] existing && existing.SequenceEqual(suggestions, StringComparer.OrdinalIgnoreCase)) return;
        var text = field.Text;
        var editor = field.Template?.FindName("PART_EditableTextBox", field) as TextBox;
        var start = editor?.SelectionStart ?? 0; var length = editor?.SelectionLength ?? 0;
        field.ItemsSource = suggestions;
        field.Text = text;
        editor?.Select(Math.Min(start, text.Length), Math.Min(length, Math.Max(0, text.Length - start)));
    }
}
