using System.Diagnostics;

namespace InvaderGui.Core;

/// <summary>External GUI launcher; the editor owns its lifetime and is not an asset-processing slot.</summary>
public static class EditorLauncher
{
    public static ProcessStartInfo CreateStartInfo(string installationDirectory, ProjectProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(installationDirectory) || !Path.IsPathFullyQualified(installationDirectory))
            throw new ArgumentException("Choose the Invader directory using the Invader button first.");
        var executable = Path.Combine(installationDirectory, "invader-edit-qt.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("invader-edit-qt.exe was not found in the selected Invader directory.", executable);
        if (profile is null) throw new ArgumentException("Select an editing kit profile before opening the tag editor.");
        var errors = profile.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = profile.WorkingDirectory, UseShellExecute = false };
        foreach (var root in profile.Paths.TagRoots)
        {
            var path = Path.GetFullPath(root.Path, profile.WorkingDirectory);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Tag root '{root.Name}' does not exist: {path}");
            start.ArgumentList.Add("--tags"); start.ArgumentList.Add(path);
        }
        return start;
    }
}
