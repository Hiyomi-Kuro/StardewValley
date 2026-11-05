using System.Text;

namespace ModFramework.Runtime;

/// <summary>One declared dependency of a mod.</summary>
internal sealed class ModDependency
{
    internal ModDependency(string id, string rangeText, VersionRange range, bool optional)
    {
        Id = id;
        RangeText = rangeText;
        Range = range;
        Optional = optional;
    }

    internal string Id { get; }

    internal string RangeText { get; }

    internal VersionRange Range { get; }

    internal bool Optional { get; }
}

/// <summary>
/// A validated mod manifest. Instances only exist for manifests that passed every check;
/// everything rejected earlier is reported through <see cref="ManifestLoadResult"/>.
/// </summary>
internal sealed class ModManifest
{
    internal const int SupportedSchemaVersion = 1;
    internal const int MaxManifestBytes = 256 * 1024;
    internal const int MaxDependencies = 64;
    internal const int MaxIdLength = 64;
    internal const int MaxNameLength = 128;
    internal const int MaxTypeNameLength = 256;
    internal const int MaxAssemblyNameLength = 128;
    internal const int MaxPrivateAssemblies = 32;

    internal string Directory { get; set; } = "";

    internal string ManifestPath { get; set; } = "";

    internal int SchemaVersion { get; set; }

    internal string Id { get; set; } = "";

    internal string Name { get; set; } = "";

    internal string VersionText { get; set; } = "";

    internal SemVersion Version { get; set; } = SemVersion.Of(0, 0, 0);

    internal string Author { get; set; } = "";

    internal string Description { get; set; } = "";

    internal string Website { get; set; } = "";

    internal string EntryAssembly { get; set; } = "";

    internal string EntryType { get; set; } = "";

    /// <summary>
    /// Private assemblies shipped next to the entry assembly, loaded eagerly before it.
    /// Declared explicitly because Assembly.LoadFrom does not probe the load directory on
    /// .NET Core (verified by the host test) and because Directory.GetFiles is trimmed.
    /// </summary>
    internal List<string> PrivateAssemblies { get; } = new();

    internal string GameVersionText { get; set; } = "";

    internal VersionRange GameVersion { get; set; } = VersionRange.Any;

    internal string FrameworkVersionText { get; set; } = "";

    internal VersionRange FrameworkVersion { get; set; } = VersionRange.Any;

    internal List<ModDependency> Dependencies { get; } = new();
}

internal sealed class ManifestLoadResult
{
    private ManifestLoadResult(bool ok, string code, string message, ModManifest? manifest)
    {
        Ok = ok;
        Code = code;
        Message = message;
        Manifest = manifest;
    }

    internal bool Ok { get; }

    internal string Code { get; }

    internal string Message { get; }

    internal ModManifest? Manifest { get; }

    internal static ManifestLoadResult Success(ModManifest manifest) => new(true, "", "", manifest);

    internal static ManifestLoadResult Failure(string code, string message) => new(false, code, message, null);
}

/// <summary>
/// Reads and validates <c>manifest.json</c>. Every rejection carries a stable
/// <see cref="ErrorCodes"/> value and a message that names the offending field.
/// </summary>
internal static class ManifestReader
{
    internal const string FileName = "manifest.json";

    private static readonly string[] KnownFields =
    {
        "schemaVersion", "id", "name", "version", "author", "description", "website",
        "entryAssembly", "entryType", "gameVersion", "frameworkVersion", "dependencies",
        "privateAssemblies",
    };

    internal static ManifestLoadResult Load(string packageDirectory)
    {
        var path = Path.Combine(packageDirectory, FileName);
        if (!File.Exists(path))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestMissing, "manifest.json not found");
        }

        string text;
        string readCode;
        string readError;
        if (!TryReadText(path, out text, out readCode, out readError))
        {
            return ManifestLoadResult.Failure(readCode, readError);
        }
        if (text.IndexOf('\uFFFD') >= 0)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidUtf8, "manifest.json contains invalid UTF-8");
        }

        JsonValue root;
        try
        {
            root = JsonParser.Parse(text);
        }
        catch (JsonException ex)
        {
            var code = ex.Error == JsonError.DuplicateKey ? ErrorCodes.ManifestDuplicateKey
                : ex.Error == JsonError.TooDeep ? ErrorCodes.ManifestNestingTooDeep
                : ex.Error == JsonError.TooLarge ? ErrorCodes.ManifestTooLarge
                : ErrorCodes.ManifestInvalidJson;
            return ManifestLoadResult.Failure(code, ex.Message + " (at character " + ex.Offset + ")");
        }

        if (root.Kind != JsonKind.Object)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, "the manifest root must be a JSON object");
        }

        return Validate(root, packageDirectory, path);
    }

    private static bool TryReadText(string path, out string text, out string code, out string error)
    {
        text = "";
        code = ErrorCodes.ManifestUnreadable;
        error = "";
        try
        {
            using (var stream = File.OpenRead(path))
            {
                var buffer = new byte[ManifestSizeLimit + 1];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer, total, buffer.Length - total);
                    if (read <= 0)
                    {
                        break;
                    }
                    total += read;
                }
                if (total > ManifestSizeLimit)
                {
                    code = ErrorCodes.ManifestTooLarge;
                    error = "manifest.json is larger than " + ManifestSizeLimit + " bytes";
                    return false;
                }
                text = Encoding.UTF8.GetString(buffer, 0, total);
                return true;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private const int ManifestSizeLimit = ModManifest.MaxManifestBytes;

    private static ManifestLoadResult Validate(JsonValue root, string packageDirectory, string manifestPath)
    {
        for (var i = 0; i < root.Members.Count; i++)
        {
            var key = root.Members[i].Key;
            if (!IsKnownField(key))
            {
                return ManifestLoadResult.Failure(ErrorCodes.ManifestUnknownField,
                    "unknown field \"" + key + "\" (schemaVersion 1 rejects unknown fields so typos cannot be ignored)");
            }
        }

        // ---- schemaVersion ----
        var schemaValue = root.Get("schemaVersion");
        if (schemaValue == null)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestMissingField, "schemaVersion is required");
        }
        if (schemaValue.Kind != JsonKind.Number)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, "schemaVersion must be a number");
        }
        var schema = schemaValue.TryGetInt(-1);
        if (schema != ModManifest.SupportedSchemaVersion)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestUnknownSchema,
                "unsupported schemaVersion " + schemaValue.RawNumber + " (this loader supports 1)");
        }

        var manifest = new ModManifest
        {
            Directory = packageDirectory,
            ManifestPath = manifestPath,
            SchemaVersion = schema,
        };

        // ---- id ----
        string id;
        var failure = RequiredString(root, "id", out id);
        if (failure != null) { return failure; }
        if (!IsValidId(id))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidId,
                "id \"" + id + "\" must be 1-" + ModManifest.MaxIdLength +
                " characters of A-Z a-z 0-9 . _ - , start with a letter or digit, and contain no \"..\"");
        }
        manifest.Id = id;

        // ---- name ----
        string name;
        failure = RequiredString(root, "name", out name);
        if (failure != null) { return failure; }
        if (name.Length > ModManifest.MaxNameLength)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidId,
                "name is longer than " + ModManifest.MaxNameLength + " characters");
        }
        if (ContainsControlCharacter(name))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidId, "name contains a control character");
        }
        manifest.Name = name;

        // ---- version ----
        string versionText;
        failure = RequiredString(root, "version", out versionText);
        if (failure != null) { return failure; }
        SemVersion version;
        string versionError;
        if (!SemVersion.TryParse(versionText, out version, out versionError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidVersion,
                "version \"" + versionText + "\": " + versionError);
        }
        manifest.VersionText = versionText;
        manifest.Version = version;

        // ---- entryAssembly ----
        string entryAssembly;
        failure = RequiredString(root, "entryAssembly", out entryAssembly);
        if (failure != null) { return failure; }
        string entryError;
        if (!IsValidEntryAssemblyName(entryAssembly, out entryError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidEntryAssembly, entryError);
        }
        manifest.EntryAssembly = entryAssembly;

        // ---- entryType ----
        string entryType;
        failure = RequiredString(root, "entryType", out entryType);
        if (failure != null) { return failure; }
        string typeError;
        if (!IsValidTypeName(entryType, out typeError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, typeError);
        }
        manifest.EntryType = entryType;

        // ---- privateAssemblies (optional) ----
        var privateValue = root.Get("privateAssemblies");
        if (privateValue != null)
        {
            if (privateValue.Kind != JsonKind.Array)
            {
                return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, "privateAssemblies must be an array");
            }
            if (privateValue.Items.Count > ModManifest.MaxPrivateAssemblies)
            {
                return ManifestLoadResult.Failure(ErrorCodes.ManifestTooManyPrivateAssemblies,
                    "more than " + ModManifest.MaxPrivateAssemblies + " private assemblies");
            }
            for (var i = 0; i < privateValue.Items.Count; i++)
            {
                var item = privateValue.Items[i];
                var label = "privateAssemblies[" + i + "]";
                if (item.Kind != JsonKind.String)
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, label + " must be a string");
                }
                var privateName = item.StringValue.Trim();
                string privateError;
                if (!IsValidEntryAssemblyName(privateName, out privateError))
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidEntryAssembly, label + ": " + privateError);
                }
                if (string.Equals(privateName, manifest.EntryAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestDuplicatePrivateAssembly,
                        label + ": the entry assembly must not also be listed as a private assembly");
                }
                for (var s = 0; s < manifest.PrivateAssemblies.Count; s++)
                {
                    if (string.Equals(manifest.PrivateAssemblies[s], privateName, StringComparison.OrdinalIgnoreCase))
                    {
                        return ManifestLoadResult.Failure(ErrorCodes.ManifestDuplicatePrivateAssembly,
                            label + ": \"" + privateName + "\" is declared more than once");
                    }
                }
                manifest.PrivateAssemblies.Add(privateName);
            }
        }

        // ---- optional metadata ----
        string optionalError;
        if (!TryOptionalString(root, "author", 256, out var author, out optionalError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, optionalError);
        }
        manifest.Author = author;

        if (!TryOptionalString(root, "description", 2048, out var description, out optionalError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, optionalError);
        }
        manifest.Description = description;

        if (!TryOptionalString(root, "website", 512, out var website, out optionalError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, optionalError);
        }
        manifest.Website = website;

        // ---- compatibility ranges ----
        var gameRangeText = "";
        if (!TryOptionalRange(root, "gameVersion", out gameRangeText, out var gameRange, out optionalError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidRange, optionalError);
        }
        manifest.GameVersionText = gameRangeText;
        manifest.GameVersion = gameRange;

        var frameworkRangeText = "";
        if (!TryOptionalRange(root, "frameworkVersion", out frameworkRangeText, out var frameworkRange, out optionalError))
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidRange, optionalError);
        }
        manifest.FrameworkVersionText = frameworkRangeText;
        manifest.FrameworkVersion = frameworkRange;

        // ---- dependencies ----
        var depsValue = root.Get("dependencies");
        if (depsValue != null)
        {
            if (depsValue.Kind != JsonKind.Array)
            {
                return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, "dependencies must be an array");
            }
            if (depsValue.Items.Count > ModManifest.MaxDependencies)
            {
                return ManifestLoadResult.Failure(ErrorCodes.ManifestTooManyDependencies,
                    "more than " + ModManifest.MaxDependencies + " dependencies");
            }
            var seen = new List<string>();
            for (var i = 0; i < depsValue.Items.Count; i++)
            {
                var item = depsValue.Items[i];
                var label = "dependencies[" + i + "]";
                if (item.Kind != JsonKind.Object)
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, label + " must be an object");
                }
                for (var k = 0; k < item.Members.Count; k++)
                {
                    var key = item.Members[k].Key;
                    if (key != "id" && key != "version" && key != "optional")
                    {
                        return ManifestLoadResult.Failure(ErrorCodes.ManifestUnknownField,
                            label + " has unknown field \"" + key + "\"");
                    }
                }
                var depIdValue = item.Get("id");
                if (depIdValue == null)
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestMissingField, label + ".id is required");
                }
                if (depIdValue.Kind != JsonKind.String)
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, label + ".id must be a string");
                }
                var depId = depIdValue.StringValue.Trim();
                if (!IsValidId(depId))
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidId,
                        label + ".id \"" + depId + "\" is not a valid mod id");
                }
                if (string.Equals(depId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return ManifestLoadResult.Failure(ErrorCodes.ManifestSelfDependency,
                        "the mod depends on itself (" + depId + ")");
                }
                for (var s = 0; s < seen.Count; s++)
                {
                    if (string.Equals(seen[s], depId, StringComparison.OrdinalIgnoreCase))
                    {
                        return ManifestLoadResult.Failure(ErrorCodes.ManifestDuplicateDependency,
                            "dependency \"" + depId + "\" is declared more than once");
                    }
                }
                seen.Add(depId);

                var rangeText = "*";
                var range = VersionRange.Any;
                var optional = false;
                var rangeValue = item.Get("version");
                if (rangeValue != null)
                {
                    if (rangeValue.Kind != JsonKind.String)
                    {
                        return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, label + ".version must be a string");
                    }
                    rangeText = rangeValue.StringValue.Trim();
                    string rangeError;
                    if (!VersionRange.TryParse(rangeText, out range, out rangeError))
                    {
                        return ManifestLoadResult.Failure(ErrorCodes.ManifestInvalidRange,
                            label + ".version \"" + rangeText + "\": " + rangeError);
                    }
                }
                var optionalValue = item.Get("optional");
                if (optionalValue != null)
                {
                    if (optionalValue.Kind != JsonKind.Bool)
                    {
                        return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, label + ".optional must be a boolean");
                    }
                    optional = optionalValue.BoolValue;
                }
                manifest.Dependencies.Add(new ModDependency(depId, rangeText, range, optional));
            }
        }

        return ManifestLoadResult.Success(manifest);
    }

    private static bool IsKnownField(string key)
    {
        for (var i = 0; i < KnownFields.Length; i++)
        {
            if (string.Equals(KnownFields[i], key, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static ManifestLoadResult? RequiredString(JsonValue root, string field, out string value)
    {
        value = "";
        var node = root.Get(field);
        if (node == null)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestMissingField, field + " is required");
        }
        if (node.Kind != JsonKind.String)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestWrongType, field + " must be a string");
        }
        var trimmed = node.StringValue.Trim();
        if (trimmed.Length == 0)
        {
            return ManifestLoadResult.Failure(ErrorCodes.ManifestEmptyField, field + " must not be empty");
        }
        value = trimmed;
        return null;
    }

    private static bool TryOptionalString(JsonValue root, string field, int maxLength, out string value, out string error)
    {
        value = "";
        error = "";
        var node = root.Get(field);
        if (node == null)
        {
            return true;
        }
        if (node.Kind != JsonKind.String)
        {
            error = field + " must be a string";
            return false;
        }
        var trimmed = node.StringValue.Trim();
        if (trimmed.Length > maxLength)
        {
            error = field + " is longer than " + maxLength + " characters";
            return false;
        }
        if (ContainsControlCharacter(trimmed))
        {
            error = field + " contains a control character";
            return false;
        }
        value = trimmed;
        return true;
    }

    private static bool TryOptionalRange(JsonValue root, string field, out string text, out VersionRange range, out string error)
    {
        text = "";
        range = VersionRange.Any;
        error = "";
        var node = root.Get(field);
        if (node == null)
        {
            return true;
        }
        if (node.Kind != JsonKind.String)
        {
            error = field + " must be a string";
            return false;
        }
        text = node.StringValue.Trim();
        if (text.Length == 0)
        {
            error = field + " must not be empty (use \"*\" for any version)";
            return false;
        }
        string rangeError;
        if (!VersionRange.TryParse(text, out range, out rangeError))
        {
            error = field + " \"" + text + "\": " + rangeError;
            return false;
        }
        return true;
    }

    internal static bool IsValidId(string id)
    {
        if (id.Length == 0 || id.Length > ModManifest.MaxIdLength)
        {
            return false;
        }
        var first = id[0];
        if (!IsAsciiLetterOrDigit(first))
        {
            return false;
        }
        for (var i = 1; i < id.Length; i++)
        {
            var c = id[i];
            if (!IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '-')
            {
                return false;
            }
        }
        return id.IndexOf("..", StringComparison.Ordinal) < 0;
    }

    /// <summary>
    /// entryAssembly must be a plain file name directly inside the mod package. Rejecting
    /// every separator removes path traversal, absolute paths and separator confusion at once.
    /// </summary>
    internal static bool IsValidEntryAssemblyName(string name, out string error)
    {
        error = "";
        if (name.Length == 0 || name.Length > ModManifest.MaxAssemblyNameLength)
        {
            error = "entryAssembly must be 1-" + ModManifest.MaxAssemblyNameLength + " characters";
            return false;
        }
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
        {
            error = "entryAssembly must be a file name without directory separators";
            return false;
        }
        if (name == "." || name == ".." || name.IndexOf("..", StringComparison.Ordinal) >= 0)
        {
            error = "entryAssembly must not contain \"..\"";
            return false;
        }
        if (name.IndexOf(':') >= 0)
        {
            error = "entryAssembly must not contain ':'";
            return false;
        }
        if (ContainsControlCharacter(name))
        {
            error = "entryAssembly contains a control character";
            return false;
        }
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = "entryAssembly must end with .dll";
            return false;
        }
        return true;
    }

    internal static bool IsValidTypeName(string name, out string error)
    {
        error = "";
        if (name.Length == 0 || name.Length > ModManifest.MaxTypeNameLength)
        {
            error = "entryType must be 1-" + ModManifest.MaxTypeNameLength + " characters";
            return false;
        }
        if (ContainsControlCharacter(name))
        {
            error = "entryType contains a control character";
            return false;
        }
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!IsAsciiLetterOrDigit(c) && c != '.' && c != '_' && c != '+' && c != '`')
            {
                error = "entryType contains an invalid character '" + c + "'";
                return false;
            }
        }
        return true;
    }

    private static bool ContainsControlCharacter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] < 0x20 || text[i] == 0x7F)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAsciiLetterOrDigit(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
    }
}
