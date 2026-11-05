namespace ModFramework.Runtime;

/// <summary>
/// Finds mod packages under the mods root.
///
/// Rules (deliberately narrow):
/// * only the immediate subdirectories of the root are considered - no recursion;
/// * a directory is a candidate only if it contains <c>manifest.json</c>;
/// * names starting with <c>.</c> or <c>_</c> are ignored (hidden/scratch/framework folders);
/// * candidates are processed in ordinal name order, so the file-system enumeration order
///   can never change the result;
/// * the same mod id may only appear once (case-insensitive).
///
/// <c>Directory.GetFiles</c> and <c>DirectoryInfo</c> do not exist in this trimmed runtime,
/// so discovery uses <c>Directory.GetDirectories</c> plus the manifest-declared file names.
/// </summary>
internal static class ModDiscovery
{
    internal sealed class Result
    {
        internal List<ModRecord> Records { get; } = new();

        internal List<string> Warnings { get; } = new();

        internal bool RootExists { get; set; }
    }

    internal static Result Discover(string modsRoot)
    {
        var result = new Result();
        if (!Directory.Exists(modsRoot))
        {
            result.RootExists = false;
            // Report the root by its last segment only: a warning is persisted in the report.
            result.Warnings.Add("mods root \"" + PathScrubber.Leaf(modsRoot) + "\" does not exist");
            return result;
        }
        result.RootExists = true;

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(modsRoot);
        }
        catch (Exception ex)
        {
            result.Warnings.Add("mods root could not be enumerated: " + PathScrubber.Describe(ex));
            result.RootExists = false;
            return result;
        }

        var names = new List<string>();
        for (var i = 0; i < directories.Length; i++)
        {
            names.Add(directories[i]);
        }
        names.Sort(StringComparer.Ordinal);

        var byId = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < names.Count; i++)
        {
            var directory = names[i];
            var name = Path.GetFileName(directory);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            if (name[0] == '.' || name[0] == '_')
            {
                result.Warnings.Add("ignored (hidden or reserved name): " + name);
                continue;
            }

            var manifestPath = Path.Combine(directory, ManifestReader.FileName);
            if (!File.Exists(manifestPath))
            {
                result.Warnings.Add("ignored (no manifest.json): " + name);
                continue;
            }

            var record = new ModRecord(name, directory);
            var loaded = ManifestReader.Load(directory);
            if (!loaded.Ok)
            {
                record.Fail(loaded.Code, loaded.Message);
                result.Records.Add(record);
                continue;
            }

            record.Manifest = loaded.Manifest;
            record.Advance(ModState.ManifestValidated);

            ModRecord? existing;
            if (byId.TryGetValue(record.Id, out existing))
            {
                // Both sides are rejected: picking one at random would make the result
                // depend on directory order.
                existing.Fail(ErrorCodes.DuplicateModId,
                    "mod id \"" + existing.Id + "\" is also declared by \"" + name + "\"");
                record.Fail(ErrorCodes.DuplicateModId,
                    "mod id \"" + record.Id + "\" is already declared by \"" + existing.DirectoryName + "\"");
                result.Records.Add(record);
                continue;
            }
            byId[record.Id] = record;
            result.Records.Add(record);
        }

        return result;
    }
}
