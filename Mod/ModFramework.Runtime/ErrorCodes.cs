namespace ModFramework.Runtime;

/// <summary>
/// Stable error codes. They are part of the contract: tests assert on them, and a future
/// in-game UI can map them to localized text. Never renumber an existing code; add new ones.
/// </summary>
internal static class ErrorCodes
{
    // ---- discovery / manifest -------------------------------------------------
    internal const string ManifestMissing = "MF0101";
    internal const string ManifestUnreadable = "MF0102";
    internal const string ManifestTooLarge = "MF0103";
    internal const string ManifestInvalidJson = "MF0104";
    internal const string ManifestInvalidUtf8 = "MF0105";
    internal const string ManifestDuplicateKey = "MF0106";
    internal const string ManifestUnknownSchema = "MF0107";
    internal const string ManifestMissingField = "MF0108";
    internal const string ManifestEmptyField = "MF0109";
    internal const string ManifestWrongType = "MF0110";
    internal const string ManifestUnknownField = "MF0111";
    internal const string ManifestTooManyDependencies = "MF0112";
    internal const string ManifestInvalidId = "MF0113";
    internal const string ManifestDuplicateDependency = "MF0114";
    internal const string ManifestSelfDependency = "MF0115";
    internal const string ManifestInvalidVersion = "MF0116";
    internal const string ManifestInvalidRange = "MF0117";
    internal const string ManifestInvalidEntryAssembly = "MF0118";
    internal const string ManifestEntryOutsidePackage = "MF0119";
    internal const string DuplicateModId = "MF0120";
    internal const string ManifestNestingTooDeep = "MF0121";
    internal const string ManifestDuplicatePrivateAssembly = "MF0122";
    internal const string ManifestTooManyPrivateAssemblies = "MF0123";

    // ---- assembly loading -----------------------------------------------------
    internal const string EntryAssemblyMissing = "MF0201";
    internal const string AssemblyLoadFailed = "MF0202";
    internal const string AssemblyNameMismatch = "MF0203";
    internal const string ContractTypeMismatch = "MF0204";
    internal const string AssemblyIdentityUnreadable = "MF0205";
    internal const string AssemblyIdentityConflict = "MF0206";
    internal const string HostAssemblyConflict = "MF0207";

    // ---- entry validation -----------------------------------------------------
    internal const string EntryTypeMissing = "MF0301";
    internal const string EntryTypeNotPublic = "MF0302";
    internal const string EntryTypeAbstract = "MF0303";
    internal const string EntryTypeNotClass = "MF0304";
    internal const string EntryTypeNotModEntry = "MF0305";
    internal const string EntryTypeGeneric = "MF0306";
    internal const string EntryNoDefaultConstructor = "MF0307";
    internal const string EntryConstructorFailed = "MF0308";
    internal const string EntryInitializeFailed = "MF0309";

    // ---- dependencies ---------------------------------------------------------
    internal const string DependencyMissing = "MF0401";
    internal const string DependencyVersionMismatch = "MF0402";
    internal const string DependencyCycle = "MF0403";
    internal const string DependencyFailed = "MF0404";

    // ---- compatibility --------------------------------------------------------
    // MF0501/MF0502 predate Phase 1.1 and keep their original meaning (a *known* version
    // that does not satisfy the declared range). MF0503-MF0505 are the fail-closed codes
    // for "the loader cannot prove the constraint is satisfied".
    internal const string GameVersionUnsupported = "MF0501";
    internal const string FrameworkVersionUnsupported = "MF0502";
    internal const string GameVersionUnknown = "MF0503";
    internal const string GameVersionInvalid = "MF0504";
    internal const string FrameworkVersionInvalid = "MF0505";

    /// <summary>
    /// Phase 1.1.1: the running framework version could not be determined at all, so a declared
    /// range cannot be enforced. Deliberately distinct from <see cref="FrameworkVersionInvalid"/>
    /// (a version was read but does not parse) and from <see cref="FrameworkVersionUnsupported"/>
    /// (a known version that does not satisfy the range).
    /// </summary>
    internal const string FrameworkVersionUnknown = "MF0506";

    // ---- loader level ---------------------------------------------------------
    internal const string LoaderReentered = "MF0901";
    internal const string ModsRootMissing = "MF0902";
    internal const string ModsRootUnreadable = "MF0903";
    internal const string LogWriteFailed = "MF0904";
    internal const string ReportWriteFailed = "MF0905";

    internal static string Describe(string code)
    {
        switch (code)
        {
            case ManifestMissing: return "manifest.json not found in the mod package";
            case ManifestUnreadable: return "manifest.json could not be read";
            case ManifestTooLarge: return "manifest.json exceeds the size limit";
            case ManifestInvalidJson: return "manifest.json is not valid JSON";
            case ManifestInvalidUtf8: return "manifest.json is not valid UTF-8";
            case ManifestDuplicateKey: return "manifest.json contains a duplicate object key";
            case ManifestUnknownSchema: return "unsupported manifest schemaVersion";
            case ManifestMissingField: return "required manifest field is missing";
            case ManifestEmptyField: return "required manifest field is empty";
            case ManifestWrongType: return "manifest field has the wrong JSON type";
            case ManifestUnknownField: return "manifest contains an unknown field";
            case ManifestTooManyDependencies: return "manifest declares too many dependencies";
            case ManifestInvalidId: return "mod id is not a valid identifier";
            case ManifestDuplicateDependency: return "the same dependency is declared twice";
            case ManifestSelfDependency: return "a mod cannot depend on itself";
            case ManifestInvalidVersion: return "version is not a valid semantic version";
            case ManifestInvalidRange: return "version range is not valid";
            case ManifestInvalidEntryAssembly: return "entryAssembly is not a plain relative .dll file name";
            case ManifestEntryOutsidePackage: return "entryAssembly resolves outside the mod package";
            case DuplicateModId: return "another mod package already uses this mod id";
            case ManifestNestingTooDeep: return "manifest JSON is nested too deeply";
            case ManifestDuplicatePrivateAssembly: return "the same private assembly is declared twice";
            case ManifestTooManyPrivateAssemblies: return "manifest declares too many private assemblies";
            case EntryAssemblyMissing: return "entryAssembly file does not exist";
            case AssemblyLoadFailed: return "the entry assembly could not be loaded";
            case AssemblyNameMismatch: return "the loaded assembly name does not match the file name";
            case ContractTypeMismatch: return "the mod was compiled against a different ModFramework.Runtime";
            case AssemblyIdentityUnreadable: return "a declared assembly file is missing, damaged or not a managed assembly";
            case AssemblyIdentityConflict: return "two mods declare the same assembly with a different identity or content";
            case HostAssemblyConflict: return "a mod assembly collides with an assembly already loaded by the host";
            case EntryTypeMissing: return "entryType does not exist in the entry assembly";
            case EntryTypeNotPublic: return "entryType is not public";
            case EntryTypeAbstract: return "entryType is abstract";
            case EntryTypeNotClass: return "entryType is not a class";
            case EntryTypeNotModEntry: return "entryType does not implement IModEntry";
            case EntryTypeGeneric: return "entryType is a generic type";
            case EntryNoDefaultConstructor: return "entryType has no public parameterless constructor";
            case EntryConstructorFailed: return "constructing the mod entry threw";
            case EntryInitializeFailed: return "IModEntry.Initialize threw";
            case DependencyMissing: return "a required dependency is not present";
            case DependencyVersionMismatch: return "a dependency does not satisfy the declared version range";
            case DependencyCycle: return "dependency cycle";
            case DependencyFailed: return "a dependency failed to load or initialize";
            case GameVersionUnsupported: return "the running game version is not supported by this mod";
            case FrameworkVersionUnsupported: return "this mod requires a different framework version";
            case GameVersionUnknown: return "the game version could not be determined, so the declared range cannot be enforced";
            case GameVersionInvalid: return "the detected game version is not a valid semantic version";
            case FrameworkVersionInvalid: return "the runtime framework version is not a valid semantic version";
            case FrameworkVersionUnknown: return "the runtime framework version could not be determined, so the declared range cannot be enforced";
            case LoaderReentered: return "the loader was called re-entrantly; the second call was ignored";
            case ModsRootMissing: return "the mods root directory does not exist";
            case ModsRootUnreadable: return "the mods root directory could not be enumerated";
            case LogWriteFailed: return "a log file could not be written";
            case ReportWriteFailed: return "neither loader-report.txt nor loader.log could be written";
            default: return "unknown error";
        }
    }
}
