using System.Reflection;
using System.Text;

namespace ModFramework.Runtime;

/// <summary>
/// Entry point called by the injected <c>ModFramework.Bootstrap.Start()</c>.
///
/// Return contract (Bootstrap depends on it):
///   * <c>null</c> - the scan completed; individual mods may still have failed, and that is
///     recorded in the report and in the per-mod logs;
///   * a diagnostic string - the loader itself could not run (missing root, unexpected
///     exception). Bootstrap turns it into an exception which its catch handler swallows, so
///     the game still starts.
///
/// The full report is always written to <c>&lt;mods root&gt;/loader-report.txt</c>, because in a
/// default build the exception is swallowed and the file is the only surviving evidence.
/// </summary>
public static class Loader
{
    private const string ReportFileName = "loader-report.txt";
    private const string LogFileName = "loader.log";
    private const string LogsDirectoryName = "logs";
    private const string LegacyProbeAssembly = "MonoRuntimeProbe.dll";
    private const string GameAssemblyName = "StardewValley";

    private static bool s_running;
    private static string? s_completedRoot;

    /// <summary>
    /// Test seam: when set, overrides the detected game version and marks it as authoritative.
    /// Never set in production code; it exists so the host tests can exercise the
    /// game-version compatibility gate on a desktop where no game assembly is loaded.
    /// </summary>
    internal static string? GameVersionOverride;

    /// <summary>
    /// Runs the loader against the directory that contains <c>ModFramework.Runtime.dll</c>.
    /// </summary>
    /// <returns><c>null</c> on a completed scan, otherwise a diagnostic string.</returns>
    public static string? Start()
    {
        return Run(DefaultModsRoot());
    }

    /// <summary>
    /// Runs the loader against an explicit mods root. Used by the host tests and by custom
    /// hosts; <see cref="Start"/> is what Bootstrap calls.
    ///
    /// One mods root is initialized at most once per process: a second call for the same root
    /// returns <c>null</c> without running any mod entry again.
    /// </summary>
    /// <param name="modsRoot">Absolute path of the directory that holds mod packages.</param>
    /// <returns><c>null</c> on a completed scan, otherwise a diagnostic string.</returns>
    public static string? Run(string modsRoot)
    {
        if (s_running)
        {
            // Re-entrancy guard. The loader is single-threaded by contract (UI thread), so a
            // plain flag is enough; it exists to make a double Bootstrap call harmless.
            return ErrorCodes.LoaderReentered + ": " + ErrorCodes.Describe(ErrorCodes.LoaderReentered);
        }

        var root = string.IsNullOrEmpty(modsRoot) ? DefaultModsRoot() : modsRoot;
        if (s_completedRoot != null && string.Equals(s_completedRoot, Normalize(root), StringComparison.OrdinalIgnoreCase))
        {
            WriteAlreadyInitialized(root);
            return null;
        }

        s_running = true;
        try
        {
            return RunCore(root);
        }
        catch (Exception ex)
        {
            var text = BuildFatalReport(root, ex);
            TryWrite(Path.Combine(root, ReportFileName), text);
            TryWrite(Path.Combine(root, LogFileName), text);
            return "loader-report: loader-crashed: " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            s_running = false;
        }
    }

    private static void WriteAlreadyInitialized(string root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("loader-report " + DateTimeOffset.Now.ToString("O"));
        sb.AppendLine("locate=" + DefaultModsRoot());
        sb.AppendLine("mods-root=" + root);
        sb.AppendLine("result=already-initialized");
        var text = sb.ToString();
        TryWrite(Path.Combine(root, ReportFileName), text);
        TryWrite(Path.Combine(root, LogFileName), text);
    }

    private static string? RunCore(string modsRoot)
    {
        bool gameVersionKnown;
        var gameVersion = DetectGameVersion(out gameVersionKnown);
        var report = new LoadReport(modsRoot, DefaultModsRoot(), gameVersion, gameVersionKnown);
        report.AddLine("framework-version=" + FrameworkVersion());

        var discovery = ModDiscovery.Discover(modsRoot);
        report.Attach(discovery.Records);
        for (var i = 0; i < discovery.Warnings.Count; i++)
        {
            report.AddWarning(discovery.Warnings[i]);
        }

        if (!discovery.RootExists)
        {
            report.Result = "failed";
            report.AddLine("result=failed");
            report.AddLine("error=" + ErrorCodes.ModsRootMissing + ": " + ErrorCodes.Describe(ErrorCodes.ModsRootMissing));
            WriteReport(report);
            return ErrorCodes.ModsRootMissing + ": " + ErrorCodes.Describe(ErrorCodes.ModsRootMissing);
        }

        var validated = new List<ModManifest>();
        for (var i = 0; i < discovery.Records.Count; i++)
        {
            var record = discovery.Records[i];
            if (record.Manifest != null && !record.IsTerminalFailure)
            {
                validated.Add(record.Manifest);
            }
        }

        var plan = DependencyResolver.Resolve(validated);
        for (var i = 0; i < plan.Cycles.Count; i++)
        {
            report.AddWarning("dependency cycle: " + plan.Cycles[i]);
        }

        var logDirectory = Path.Combine(modsRoot, LogsDirectoryName);
        var logsReady = TryCreateDirectory(logDirectory);
        if (!logsReady)
        {
            report.AddWarning("could not create the log directory: " + logDirectory);
        }

        var byId = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < discovery.Records.Count; i++)
        {
            var record = discovery.Records[i];
            if (record.Manifest != null && !record.IsTerminalFailure)
            {
                byId[record.Id] = record;
            }
        }

        var initialized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < plan.Order.Count; i++)
        {
            var manifest = plan.Order[i];
            ModRecord? record;
            if (!byId.TryGetValue(manifest.Id, out record) || record == null)
            {
                continue;
            }

            DependencyProblem? problem;
            if (plan.Problems.TryGetValue(manifest.Id, out problem) && problem != null)
            {
                record.Fail(problem.Code, problem.Message);
                continue;
            }

            record.Advance(ModState.DependenciesResolved);

            List<string>? optionalAbsent;
            if (plan.OptionalAbsent.TryGetValue(manifest.Id, out optionalAbsent) && optionalAbsent != null)
            {
                for (var o = 0; o < optionalAbsent.Count; o++)
                {
                    report.AddWarning(manifest.Id + ": optional dependency \"" + optionalAbsent[o] + "\" is not installed");
                }
            }

            var compatibility = CheckCompatibility(manifest, report);
            if (compatibility != null)
            {
                record.Fail(compatibility.Value.Code, compatibility.Value.Message);
                initialized[manifest.Id] = false;
                continue;
            }

            var blockedBy = FirstFailedDependency(manifest, initialized);
            if (blockedBy != null)
            {
                record.Fail(ErrorCodes.DependencyFailed,
                    "dependency \"" + blockedBy + "\" did not initialize");
                initialized[manifest.Id] = false;
                continue;
            }

            LoadAndInitialize(record, manifest, report, logDirectory, logsReady, initialized);
        }

        report.Result = "ok";
        report.AddLine("result=ok");
        report.AddLine("probe=" + RunLegacyProbe(modsRoot));
        WriteReport(report);
        s_completedRoot = Normalize(modsRoot);
        return null;
    }

    /// <summary>
    /// Game/framework version gate. The game version check is only enforced when the running
    /// game assembly was actually found; guessing would reject every mod on a host that has
    /// no game loaded (the desktop test host).
    /// </summary>
    private static EntryProblem? CheckCompatibility(ModManifest manifest, LoadReport report)
    {
        if (manifest.FrameworkVersion.Text != "*")
        {
            SemVersion framework;
            string error;
            if (SemVersion.TryParse(FrameworkVersion(), out framework, out error) &&
                !manifest.FrameworkVersion.IsSatisfiedBy(framework))
            {
                return new EntryProblem
                {
                    Code = ErrorCodes.FrameworkVersionUnsupported,
                    Message = "requires framework \"" + manifest.FrameworkVersion.Describe() +
                              "\" but this framework is " + FrameworkVersion(),
                };
            }
        }

        if (manifest.GameVersion.Text == "*")
        {
            return null;
        }
        if (!report.GameVersionKnown)
        {
            report.AddWarning(manifest.Id + ": game version could not be determined, so \"" +
                              manifest.GameVersion.Describe() + "\" was not enforced");
            return null;
        }

        SemVersion game;
        string gameError;
        if (!SemVersion.TryParse(report.GameVersion, out game, out gameError))
        {
            report.AddWarning(manifest.Id + ": game version \"" + report.GameVersion + "\" is not SemVer, check skipped");
            return null;
        }
        if (!manifest.GameVersion.IsSatisfiedBy(game))
        {
            return new EntryProblem
            {
                Code = ErrorCodes.GameVersionUnsupported,
                Message = "requires game \"" + manifest.GameVersion.Describe() +
                          "\" but this game is " + report.GameVersion,
            };
        }
        return null;
    }

    private static string? FirstFailedDependency(ModManifest manifest, Dictionary<string, bool> initialized)
    {
        for (var i = 0; i < manifest.Dependencies.Count; i++)
        {
            var dep = manifest.Dependencies[i];
            bool ok;
            if (!initialized.TryGetValue(dep.Id, out ok))
            {
                // Not attempted yet, or absent. Absent required dependencies were already
                // reported by the resolver; an absent optional one must not block.
                if (dep.Optional)
                {
                    continue;
                }
                return dep.Id;
            }
            if (!ok && !dep.Optional)
            {
                return dep.Id;
            }
        }
        return null;
    }


    /// <summary>
    /// Loads the manifest's declared private assemblies before the entry assembly.
    ///
    /// Why this is explicit instead of automatic: <c>Assembly.LoadFrom</c> resolves a
    /// dependency through the host's normal probing, not through the directory of the file
    /// that was loaded (the host test proves this on .NET Core), and
    /// <c>Directory.GetFiles</c> does not exist in the shipped runtime, so the set cannot be
    /// enumerated. Two passes over the declared list make the result independent of the
    /// declared order, which keeps the mod package format forgiving.
    /// </summary>
    private static EntryProblem? LoadPrivateAssemblies(ModManifest manifest)
    {
        if (manifest.PrivateAssemblies.Count == 0)
        {
            return null;
        }

        var pending = new List<string>();
        for (var i = 0; i < manifest.PrivateAssemblies.Count; i++)
        {
            var name = manifest.PrivateAssemblies[i];
            var path = Path.Combine(manifest.Directory, name);
            if (!IsInside(manifest.Directory, path))
            {
                return new EntryProblem
                {
                    Code = ErrorCodes.ManifestEntryOutsidePackage,
                    Message = "private assembly \"" + name + "\" resolves outside the mod package",
                };
            }
            if (!File.Exists(path))
            {
                return new EntryProblem
                {
                    Code = ErrorCodes.EntryAssemblyMissing,
                    Message = "missing private assembly \"" + name + "\"",
                };
            }
            pending.Add(path);
        }

        var lastError = "";
        while (pending.Count > 0)
        {
            var progressed = false;
            for (var i = 0; i < pending.Count; i++)
            {
                try
                {
                    Assembly.LoadFrom(pending[i]);
                    pending.RemoveAt(i);
                    i--;
                    progressed = true;
                }
                catch (Exception ex)
                {
                    lastError = Path.GetFileName(pending[i]) + ": " + DescribeException(ex);
                }
            }
            if (!progressed)
            {
                break;
            }
        }

        if (pending.Count > 0)
        {
            return new EntryProblem
            {
                Code = ErrorCodes.AssemblyLoadFailed,
                Message = "private assembly could not be loaded (" + pending.Count + " pending): " + lastError,
            };
        }
        return null;
    }

    private static void LoadAndInitialize(
        ModRecord record,
        ModManifest manifest,
        LoadReport report,
        string logDirectory,
        bool logsReady,
        Dictionary<string, bool> initialized)
    {
        var logger = new ModLogger(manifest.Id, logsReady ? logDirectory : record.Directory);
        var context = new ModContext(manifest, report.GameVersion, FrameworkVersion(), logger);

        try
        {
            var assemblyPath = Path.Combine(manifest.Directory, manifest.EntryAssembly);
            if (!IsInside(manifest.Directory, assemblyPath))
            {
                record.Fail(ErrorCodes.ManifestEntryOutsidePackage, "entryAssembly resolves outside the mod package");
                initialized[manifest.Id] = false;
                return;
            }
            if (!File.Exists(assemblyPath))
            {
                record.Fail(ErrorCodes.EntryAssemblyMissing, "missing " + manifest.EntryAssembly);
                initialized[manifest.Id] = false;
                return;
            }

            record.SetAssemblyPath(assemblyPath);

            var privateFailure = LoadPrivateAssemblies(manifest);
            if (privateFailure != null)
            {
                record.Fail(privateFailure.Value.Code, privateFailure.Value.Message);
                initialized[manifest.Id] = false;
                return;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(assemblyPath);
            }
            catch (Exception ex)
            {
                record.Fail(ErrorCodes.AssemblyLoadFailed, DescribeException(ex));
                initialized[manifest.Id] = false;
                return;
            }

            var expectedName = Path.GetFileNameWithoutExtension(manifest.EntryAssembly);
            var actualName = assembly.GetName().Name;
            if (!string.Equals(expectedName, actualName, StringComparison.OrdinalIgnoreCase))
            {
                record.Fail(ErrorCodes.AssemblyNameMismatch,
                    "file \"" + manifest.EntryAssembly + "\" contains assembly \"" + actualName + "\"");
                initialized[manifest.Id] = false;
                return;
            }

            record.Advance(ModState.AssemblyLoaded);

            Type? entryType;
            try
            {
                entryType = assembly.GetType(manifest.EntryType, false, false);
            }
            catch (Exception ex)
            {
                record.Fail(ErrorCodes.EntryTypeMissing, DescribeException(ex));
                initialized[manifest.Id] = false;
                return;
            }

            if (entryType == null)
            {
                record.Fail(ErrorCodes.EntryTypeMissing, "type \"" + manifest.EntryType + "\" not found in " + manifest.EntryAssembly);
                initialized[manifest.Id] = false;
                return;
            }

            var validationError = ValidateEntryType(entryType);
            if (validationError != null)
            {
                record.Fail(validationError.Value.Code, validationError.Value.Message);
                initialized[manifest.Id] = false;
                return;
            }

            record.Advance(ModState.EntryValidated);

            object instance;
            try
            {
                instance = Activator.CreateInstance(entryType)!;
            }
            catch (Exception ex)
            {
                record.Fail(ErrorCodes.EntryConstructorFailed, DescribeException(Unwrap(ex)));
                initialized[manifest.Id] = false;
                return;
            }

            record.Advance(ModState.Instantiated);

            var entry = instance as IModEntry;
            if (entry == null)
            {
                // ValidateEntryType already checked assignability; this is the type-identity
                // safety net for a mod that shipped its own copy of ModFramework.Runtime.dll.
                record.Fail(ErrorCodes.ContractTypeMismatch,
                    "the entry type does not implement this framework's IModEntry");
                initialized[manifest.Id] = false;
                return;
            }

            try
            {
                entry.Initialize(context);
            }
            catch (Exception ex)
            {
                record.Fail(ErrorCodes.EntryInitializeFailed, DescribeException(Unwrap(ex)));
                logger.Error("Initialize threw; the mod is marked as failed", Unwrap(ex));
                initialized[manifest.Id] = false;
                return;
            }

            record.Advance(ModState.Initialized);
            initialized[manifest.Id] = true;
            report.AddLine("initialized=" + manifest.Id);
        }
        catch (Exception ex)
        {
            record.Fail(ErrorCodes.EntryInitializeFailed, DescribeException(ex));
            initialized[manifest.Id] = false;
        }
    }

    private struct EntryProblem
    {
        internal string Code;
        internal string Message;
    }

    private static EntryProblem? ValidateEntryType(Type type)
    {
        if (type.IsInterface || type.IsValueType || !type.IsClass)
        {
            return new EntryProblem { Code = ErrorCodes.EntryTypeNotClass, Message = "entryType is not a class" };
        }
        if (!type.IsPublic && !type.IsNestedPublic)
        {
            return new EntryProblem { Code = ErrorCodes.EntryTypeNotPublic, Message = "entryType is not public" };
        }
        if (type.IsAbstract)
        {
            return new EntryProblem { Code = ErrorCodes.EntryTypeAbstract, Message = "entryType is abstract" };
        }
        if (type.IsGenericType)
        {
            return new EntryProblem { Code = ErrorCodes.EntryTypeGeneric, Message = "entryType is a generic type" };
        }

        if (!typeof(IModEntry).IsAssignableFrom(type))
        {
            // Distinguish "wrong contract" from "same-named contract from another copy of the
            // framework assembly", which is the classic type-identity trap.
            var interfaces = type.GetInterfaces();
            for (var i = 0; i < interfaces.Length; i++)
            {
                var candidate = interfaces[i];
                if (string.Equals(candidate.FullName, "ModFramework.Runtime.IModEntry", StringComparison.Ordinal) &&
                    candidate.Assembly != typeof(IModEntry).Assembly)
                {
                    return new EntryProblem
                    {
                        Code = ErrorCodes.ContractTypeMismatch,
                        Message = "entryType implements IModEntry from \"" + candidate.Assembly.GetName().Name +
                                  "\" instead of this framework's ModFramework.Runtime",
                    };
                }
            }
            return new EntryProblem
            {
                Code = ErrorCodes.EntryTypeNotModEntry,
                Message = "entryType does not implement ModFramework.Runtime.IModEntry",
            };
        }

        var constructors = type.GetConstructors();
        for (var i = 0; i < constructors.Length; i++)
        {
            if (constructors[i].GetParameters().Length == 0)
            {
                return null;
            }
        }
        return new EntryProblem
        {
            Code = ErrorCodes.EntryNoDefaultConstructor,
            Message = "entryType has no public parameterless constructor",
        };
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);
        if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
        {
            normalizedRoot += "/";
        }
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }

    private static Exception Unwrap(Exception ex)
    {
        var target = ex as TargetInvocationException;
        if (target != null && target.InnerException != null)
        {
            return target.InnerException;
        }
        return ex;
    }

    private static string DescribeException(Exception ex)
    {
        return ex.GetType().Name + ": " + ex.Message;
    }

    /// <summary>
    /// Version of the running game, mapped to SemVer (the assembly revision is dropped).
    /// <paramref name="confident"/> is true only when an assembly actually named
    /// <c>StardewValley</c> was found; otherwise the version is <c>unknown</c> and the
    /// compatibility gate stays open instead of guessing.
    /// </summary>
    private static string DetectGameVersion(out bool confident)
    {
        confident = false;

        if (GameVersionOverride != null)
        {
            confident = true;
            return GameVersionOverride;
        }

        try
        {
            var entry = Assembly.GetEntryAssembly();
            if (entry != null)
            {
                var name = entry.GetName();
                if (name != null && string.Equals(name.Name, GameAssemblyName, StringComparison.Ordinal))
                {
                    var mapped = MapVersion(name);
                    if (mapped != null)
                    {
                        confident = true;
                        return mapped;
                    }
                }
            }
        }
        catch (Exception)
        {
            // fall through to the AppDomain scan
        }

        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < loaded.Length; i++)
            {
                var name = loaded[i].GetName();
                if (name != null && string.Equals(name.Name, GameAssemblyName, StringComparison.Ordinal))
                {
                    var mapped = MapVersion(name);
                    if (mapped != null)
                    {
                        confident = true;
                        return mapped;
                    }
                }
            }
        }
        catch (Exception)
        {
            // unknown is a valid answer
        }
        return "unknown";
    }

    private static string? MapVersion(AssemblyName name)
    {
        var version = name.Version;
        if (version == null)
        {
            return null;
        }
        var text = version.Major + "." + version.Minor + "." + version.Build;
        SemVersion parsed;
        string error;
        return SemVersion.TryParse(text, out parsed, out error) ? text : null;
    }

    internal static string FrameworkVersion()
    {
        try
        {
            var name = typeof(Loader).Assembly.GetName();
            var version = name.Version;
            if (version != null)
            {
                return version.Major + "." + version.Minor + "." + version.Build;
            }
        }
        catch (Exception)
        {
            // fall through
        }
        return "0.0.0";
    }

    internal static string DefaultModsRoot()
    {
        try
        {
            var location = typeof(Loader).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var directory = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(directory))
                {
                    return directory!;
                }
            }
        }
        catch (Exception)
        {
            // fall through
        }
        return "/data/data/com.chucklefish.stardewvalley/files/Mods";
    }

    private static string RunLegacyProbe(string modsRoot)
    {
        try
        {
            var probePath = Path.Combine(modsRoot, LegacyProbeAssembly);
            if (!File.Exists(probePath))
            {
                return "absent";
            }
            var assembly = Assembly.LoadFrom(probePath);
            var type = assembly.GetType("MonoRuntimeProbe.Probe", false, false);
            var run = type == null ? null : type.GetMethod("Run");
            if (run == null)
            {
                return "no-method";
            }
            var result = run.Invoke(null, new object?[] { Path.Combine(modsRoot, "probe-output"), false });
            return Convert.ToString(result) ?? "null";
        }
        catch (Exception ex)
        {
            return "failed: " + DescribeException(Unwrap(ex));
        }
    }

    private static void WriteReport(LoadReport report)
    {
        var text = report.Render();
        TryWrite(Path.Combine(report.ModsRoot, ReportFileName), text);
        TryWrite(Path.Combine(report.ModsRoot, LogFileName), text);
    }

    private static bool TryWrite(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildFatalReport(string modsRoot, Exception ex)
    {
        var report = new LoadReport(modsRoot, DefaultModsRoot(), "unknown", false);
        report.Result = "failed";
        report.AddLine("result=failed");
        report.AddLine("error=" + DescribeException(ex));
        var stack = ex.StackTrace;
        if (!string.IsNullOrEmpty(stack))
        {
            report.AddLine("stack=" + stack!.Replace("\n", " | "));
        }
        return report.Render();
    }

    /// <summary>Accumulates the human-readable report; the only output that survives a swallowed exception.</summary>
    private sealed class LoadReport
    {
        private readonly StringBuilder _body = new();
        private readonly List<string> _warnings = new();
        private readonly List<ModRecord> _records = new();

        internal LoadReport(string modsRoot, string runtimeDirectory, string gameVersion, bool gameVersionKnown)
        {
            ModsRoot = modsRoot;
            RuntimeDirectory = runtimeDirectory;
            GameVersion = gameVersion;
            GameVersionKnown = gameVersionKnown;
        }

        internal string ModsRoot { get; }

        internal string RuntimeDirectory { get; }

        internal string GameVersion { get; }

        internal bool GameVersionKnown { get; }

        internal string Result { get; set; } = "ok";

        internal void AddLine(string line) => _body.AppendLine(line);

        internal void AddWarning(string text) => _warnings.Add(text);

        internal void Attach(List<ModRecord> records)
        {
            for (var i = 0; i < records.Count; i++)
            {
                _records.Add(records[i]);
            }
        }

        internal string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("loader-report " + DateTimeOffset.Now.ToString("O"));
            sb.AppendLine("locate=" + RuntimeDirectory);
            sb.AppendLine("mods-root=" + ModsRoot);
            sb.AppendLine("game-version=" + GameVersion);
            sb.AppendLine("mods-discovered=" + _records.Count);

            var initialized = 0;
            var failed = 0;
            var skipped = 0;
            for (var i = 0; i < _records.Count; i++)
            {
                var state = _records[i].State;
                if (state == ModState.Initialized) { initialized++; }
                else if (state == ModState.Failed) { failed++; }
                else if (state == ModState.Skipped) { skipped++; }
            }
            sb.AppendLine("mods-initialized=" + initialized);
            sb.AppendLine("mods-failed=" + failed);
            sb.AppendLine("mods-skipped=" + skipped);

            var body = _body.ToString();
            sb.Append(body);
            if (!body.EndsWith("\n", StringComparison.Ordinal) && body.Length > 0)
            {
                sb.AppendLine();
            }

            if (_warnings.Count > 0)
            {
                sb.AppendLine("warnings:");
                for (var i = 0; i < _warnings.Count; i++)
                {
                    sb.AppendLine("  - " + _warnings[i]);
                }
            }

            if (_records.Count > 0)
            {
                sb.AppendLine("mods:");
                for (var i = 0; i < _records.Count; i++)
                {
                    var record = _records[i];
                    sb.Append("  - id=" + record.Id + " state=" + record.State);
                    if (record.ErrorCode.Length > 0)
                    {
                        sb.Append(" code=" + record.ErrorCode + " message=" + record.ErrorMessage);
                    }
                    sb.AppendLine();
                    sb.AppendLine("    dir=" + record.Directory);
                    if (record.AssemblyPath.Length > 0)
                    {
                        sb.AppendLine("    assembly=" + record.AssemblyPath);
                    }
                    sb.AppendLine("    history=" + record.DescribeHistory());
                }
            }

            return sb.ToString();
        }
    }
}
