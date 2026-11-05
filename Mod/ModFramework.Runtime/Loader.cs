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
///     exception, or no report file could be written at all). Bootstrap turns it into an
///     exception which its catch handler swallows, so the game still starts.
///
/// The full report is always written to <c>&lt;mods root&gt;/loader-report.txt</c>, because in a
/// default build the exception is swallowed and the file is the only surviving evidence.
///
/// Phase 1.1 guarantees, in this order:
///   1. discovery and manifest validation never load a mod assembly;
///   2. dependency resolution and the version gates run next;
///   3. the global assembly identity preflight reads every declared assembly's metadata
///      <b>without loading it</b> and rejects conflicting mods;
///   4. only then is the first <c>Assembly.LoadFrom</c> issued (see the
///      <c>assembly-load=</c> lines, which always follow the <c>preflight-stage=complete</c>
///      line in the report).
/// </summary>
public static class Loader
{
    private const string ReportFileName = "loader-report.txt";
    private const string LogFileName = "loader.log";
    private const string LogsDirectoryName = "logs";
    private const string GameAssemblyName = "StardewValley";

    /// <summary>
    /// Value of the <c>probe=</c> report line.
    ///
    /// Phase 1.1 removed the implicit <c>MonoRuntimeProbe.dll</c> execution: a bare DLL in the
    /// mods root is never loaded, discovered, or invoked by the normal loader. The line is kept
    /// (with this explicit value) so existing report readers see a definite status instead of a
    /// silently missing field. Running the probe is now an explicit, separate diagnostic step -
    /// see <c>Mod/MonoRuntimeProbe/README.md</c>.
    /// </summary>
    private const string ProbeStatus = "not-run-by-loader";

    private static bool s_running;
    private static string? s_completedRoot;

    /// <summary>
    /// Test seams. Never set in production code; both exist so the host tests can exercise the
    /// version gates on a desktop where neither a game assembly nor a second framework build is
    /// available. Every test restores the previous value in a <c>finally</c> block.
    /// </summary>
    internal static string? GameVersionOverride;

    /// <inheritdoc cref="GameVersionOverride"/>
    internal static string? FrameworkVersionOverride;

    /// <summary>
    /// Canonical path -&gt; loaded assembly, for the whole process. Guarantees that one file is
    /// handed to <c>Assembly.LoadFrom</c> exactly once even when several mods share it, and
    /// prevents <c>FileLoadException</c> when the loader is invoked again for a second root.
    /// Never cleared: the AppDomain keeps the assembly for the life of the process anyway.
    /// </summary>
    private static readonly Dictionary<string, Assembly> s_loadedByPath = new(StringComparer.Ordinal);

    internal static void ResetOverridesForTests()
    {
        GameVersionOverride = null;
        FrameworkVersionOverride = null;
    }

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
        if (s_completedRoot != null && string.Equals(s_completedRoot, PathRules.Normalize(root), StringComparison.OrdinalIgnoreCase))
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
            var reportWritten = TryWrite(Path.Combine(root, ReportFileName), text);
            var logWritten = TryWrite(Path.Combine(root, LogFileName), text);
            var diagnostic = "loader-report: loader-crashed: " + ex.GetType().Name + ": " + ex.Message;
            if (!reportWritten && !logWritten)
            {
                diagnostic += " (and neither loader-report.txt nor loader.log could be written)";
            }
            return diagnostic;
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
        GameVersionState versionState;
        string versionSource;
        var gameVersion = DetectGameVersion(out versionState, out versionSource);
        var report = new LoadReport(modsRoot, DefaultModsRoot(), gameVersion, versionState, versionSource);
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
        var byId = new Dictionary<string, ModRecord>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < discovery.Records.Count; i++)
        {
            var record = discovery.Records[i];
            if (record.Manifest != null && !record.IsTerminalFailure)
            {
                validated.Add(record.Manifest);
                byId[record.Id] = record;
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

        // ---- gate 1: version compatibility, still without touching any mod assembly --------
        var compatibilityFailures = new Dictionary<string, GateProblem>(StringComparer.OrdinalIgnoreCase);
        var preflightInput = new List<ModManifest>();
        for (var i = 0; i < plan.Order.Count; i++)
        {
            var manifest = plan.Order[i];
            if (!byId.ContainsKey(manifest.Id))
            {
                continue;
            }
            DependencyProblem? dependencyProblem;
            if (plan.Problems.TryGetValue(manifest.Id, out dependencyProblem) && dependencyProblem != null)
            {
                // Reported by the dependency pass below; do not let it participate in the
                // assembly conflict map either.
                continue;
            }
            var compatibility = CheckCompatibility(manifest, report);
            if (compatibility != null)
            {
                compatibilityFailures[manifest.Id] = compatibility;
                continue;
            }
            preflightInput.Add(manifest);
        }

        // ---- gate 2: global assembly identity preflight (metadata only, no LoadFrom) ------
        var preflight = AssemblyPreflight.Analyze(preflightInput);
        for (var i = 0; i < preflight.Lines.Count; i++)
        {
            report.AddLine(preflight.Lines[i]);
        }
        report.AddLine("preflight-stage=complete");

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

            GateProblem? gateFailure;
            if (compatibilityFailures.TryGetValue(manifest.Id, out gateFailure) && gateFailure != null)
            {
                record.Fail(gateFailure.Code, gateFailure.Message);
                initialized[manifest.Id] = false;
                continue;
            }

            if (preflight.Rejections.TryGetValue(manifest.Id, out gateFailure) && gateFailure != null)
            {
                record.Fail(gateFailure.Code, gateFailure.Message);
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

            LoadAndInitialize(record, manifest, report, logDirectory, logsReady, initialized, preflight);
        }

        report.Result = "ok";
        report.AddLine("result=ok");
        report.AddLine("probe=" + ProbeStatus);
        // Marked before the write: the mods have already initialized, so a second call must not
        // run them again even when the report could not be persisted.
        s_completedRoot = PathRules.Normalize(modsRoot);
        if (!WriteReport(report))
        {
            // Nothing was persisted: do not pretend the scan produced evidence.
            return ErrorCodes.ReportWriteFailed + ": " + ErrorCodes.Describe(ErrorCodes.ReportWriteFailed);
        }
        return null;
    }

    /// <summary>
    /// Game/framework version gate - <b>fail closed</b>.
    ///
    /// Only an explicit <c>"*"</c> means "no constraint". A mod that names a range must be
    /// provably satisfied: a known and parseable version that fits passes, everything else is
    /// rejected with its own stable code. Phase 1 used to warn and load; that turned a missing
    /// detection into a silent bypass of the mod's own compatibility claim.
    /// </summary>
    private static GateProblem? CheckCompatibility(ModManifest manifest, LoadReport report)
    {
        if (manifest.FrameworkVersion.Text != "*")
        {
            var frameworkText = FrameworkVersion();
            SemVersion framework;
            string frameworkError;
            if (!SemVersion.TryParse(frameworkText, out framework, out frameworkError))
            {
                return new GateProblem(ErrorCodes.FrameworkVersionInvalid,
                    "requires framework \"" + manifest.FrameworkVersion.Describe() +
                    "\" but the running framework version " + Describe(frameworkText) +
                    " is not a valid semantic version, so the range cannot be enforced");
            }
            if (!manifest.FrameworkVersion.IsSatisfiedBy(framework))
            {
                return new GateProblem(ErrorCodes.FrameworkVersionUnsupported,
                    "requires framework \"" + manifest.FrameworkVersion.Describe() +
                    "\" but this framework is " + frameworkText);
            }
        }

        if (manifest.GameVersion.Text == "*")
        {
            return null;
        }

        if (report.GameVersionState == GameVersionState.Unknown)
        {
            return new GateProblem(ErrorCodes.GameVersionUnknown,
                "requires game \"" + manifest.GameVersion.Describe() +
                "\" but the running game version could not be determined, so the range cannot be enforced " +
                "(declare \"gameVersion\": \"*\" if the mod does not depend on the game version)");
        }

        if (report.GameVersionState == GameVersionState.Invalid)
        {
            return new GateProblem(ErrorCodes.GameVersionInvalid,
                "requires game \"" + manifest.GameVersion.Describe() + "\" but the detected game version " +
                Describe(report.GameVersion) + " is not a valid semantic version");
        }

        SemVersion game;
        string gameError;
        if (!SemVersion.TryParse(report.GameVersion, out game, out gameError))
        {
            // Defensive: the state machine above already classifies this, but a report object
            // built by a future caller must not fall through to "load".
            return new GateProblem(ErrorCodes.GameVersionInvalid,
                "requires game \"" + manifest.GameVersion.Describe() + "\" but the detected game version " +
                Describe(report.GameVersion) + " is not a valid semantic version");
        }
        if (!manifest.GameVersion.IsSatisfiedBy(game))
        {
            return new GateProblem(ErrorCodes.GameVersionUnsupported,
                "requires game \"" + manifest.GameVersion.Describe() +
                "\" but this game is " + report.GameVersion);
        }
        return null;
    }

    private static string Describe(string version)
    {
        return version.Length == 0 ? "\"\"" : "\"" + version + "\"";
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

    private static void LoadAndInitialize(
        ModRecord record,
        ModManifest manifest,
        LoadReport report,
        string logDirectory,
        bool logsReady,
        Dictionary<string, bool> initialized,
        AssemblyPreflightResult preflight)
    {
        var logger = new ModLogger(manifest.Id, logsReady ? logDirectory : record.Directory);
        var context = new ModContext(manifest, report.GameVersion, FrameworkVersion(), logger);

        try
        {
            var entryClaim = preflight.FindClaim(manifest.Id, manifest.EntryAssembly);
            if (entryClaim == null)
            {
                record.Fail(ErrorCodes.AssemblyIdentityUnreadable,
                    "the entry assembly \"" + manifest.EntryAssembly + "\" was not accepted by the preflight");
                initialized[manifest.Id] = false;
                return;
            }

            // Private assemblies first, in the order the manifest declares them (explicit and
            // therefore deterministic). Each claim already passed the preflight, so a failure
            // here is a genuine load failure, not a conflict.
            var claims = preflight.GetClaims(manifest.Id);
            if (claims != null)
            {
                for (var i = 0; i < claims.Count; i++)
                {
                    var claim = claims[i];
                    if (ReferenceEquals(claim, entryClaim))
                    {
                        continue;
                    }
                    Assembly privateAssembly;
                    string privateError;
                    if (!TryResolveClaim(claim, report, out privateAssembly, out privateError))
                    {
                        record.Fail(ErrorCodes.AssemblyLoadFailed, privateError);
                        initialized[manifest.Id] = false;
                        return;
                    }
                    NoteShared(record, claim);
                }
            }

            Assembly assembly;
            string entryError;
            if (!TryResolveClaim(entryClaim, report, out assembly, out entryError))
            {
                record.Fail(ErrorCodes.AssemblyLoadFailed, entryError);
                initialized[manifest.Id] = false;
                return;
            }
            NoteShared(record, entryClaim);
            record.SetAssemblyPath(entryClaim.CanonicalPath);

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
        finally
        {
            // Logging degradation is recorded on every exit path - success, failure and
            // exception - and never changes the mod's own state.
            record.SetLogStatus(logger.IsBroken, logger.FailureType);
            if (logger.IsBroken)
            {
                report.AddWarning(manifest.Id + ": logging degraded (" + logger.FailureType +
                                  "), the mod log file could not be written; the mod state is unchanged");
            }
        }
    }

    private static void NoteShared(ModRecord record, AssemblyClaim claim)
    {
        if (claim.Resolution == AssemblyResolution.ShareFromClaim)
        {
            record.AddSharedAssembly(claim.DeclaredName + " -> " + claim.CanonicalModId);
        }
        else if (claim.Resolution == AssemblyResolution.ShareFromHost)
        {
            record.AddSharedAssembly(claim.DeclaredName + " -> host");
        }
    }

    /// <summary>
    /// Materializes one preflight-approved claim exactly once per canonical path.
    /// <c>Assembly.LoadFrom</c> is only ever reached here, i.e. after the preflight completed.
    /// </summary>
    private static bool TryResolveClaim(AssemblyClaim claim, LoadReport report, out Assembly assembly, out string error)
    {
        assembly = null!;
        error = "";

        if (claim.Resolution == AssemblyResolution.ShareFromHost)
        {
            if (claim.HostAssembly == null)
            {
                error = "the already loaded host assembly \"" + claim.Identity.Name + "\" is no longer present";
                return false;
            }
            assembly = claim.HostAssembly;
            return true;
        }

        var path = claim.CanonicalPath;
        Assembly? cached;
        if (s_loadedByPath.TryGetValue(path, out cached) && cached != null)
        {
            assembly = cached;
            return true;
        }

        try
        {
            assembly = Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            error = claim.DeclaredName + ": " + DescribeException(ex);
            return false;
        }

        s_loadedByPath[path] = assembly;
        report.AddLine("assembly-load=" + claim.ModId + " file=" + claim.DeclaredName +
                       " canonical=" + claim.CanonicalModId);
        return true;
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

    /// <summary>How confident the loader is about the running game version.</summary>
    internal enum GameVersionState
    {
        /// <summary>No assembly named <c>StardewValley</c> was found: the version is unknown.</summary>
        Unknown = 0,

        /// <summary>A game assembly was found and its version parses as SemVer.</summary>
        Known = 1,

        /// <summary>A game assembly was found but its version does not parse as SemVer.</summary>
        Invalid = 2,
    }

    /// <summary>
    /// Version of the running game, mapped to SemVer (the assembly revision is dropped).
    ///
    /// Three distinct answers, because Phase 1.1 needs to fail closed on all but the first:
    /// <see cref="GameVersionState.Known"/> (an assembly actually named <c>StardewValley</c>
    /// provided a parseable version), <see cref="GameVersionState.Invalid"/> (such an assembly
    /// was found but its version is not SemVer) and <see cref="GameVersionState.Unknown"/>
    /// (no such assembly at all). The loader never guesses a version.
    /// </summary>
    private static string DetectGameVersion(out GameVersionState state, out string source)
    {
        state = GameVersionState.Unknown;
        source = "none";

        if (GameVersionOverride != null)
        {
            source = "override";
            return Classify(GameVersionOverride, out state);
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
                        source = "entry-assembly";
                        return Classify(mapped, out state);
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
                        source = "appdomain";
                        return Classify(mapped, out state);
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

    private static string Classify(string text, out GameVersionState state)
    {
        SemVersion parsed;
        string error;
        state = SemVersion.TryParse(text, out parsed, out error) ? GameVersionState.Known : GameVersionState.Invalid;
        return text;
    }

    private static string? MapVersion(AssemblyName name)
    {
        var version = name.Version;
        if (version == null)
        {
            return null;
        }
        // Deliberately not validated here: an unparseable mapping must stay visible as
        // "detected but invalid" instead of collapsing into "unknown".
        return version.Major + "." + version.Minor + "." + version.Build;
    }

    internal static string FrameworkVersion()
    {
        var text = FrameworkVersionOverride;
        if (text != null)
        {
            return text;
        }
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

    /// <summary>Writes both report files; returns false when neither could be written.</summary>
    private static bool WriteReport(LoadReport report)
    {
        var text = report.Render();
        var written = TryWrite(Path.Combine(report.ModsRoot, ReportFileName), text);
        var logWritten = TryWrite(Path.Combine(report.ModsRoot, LogFileName), text);
        return written || logWritten;
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
        var report = new LoadReport(modsRoot, DefaultModsRoot(), "unknown",
            GameVersionState.Unknown, "none");
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

        internal LoadReport(
            string modsRoot,
            string runtimeDirectory,
            string gameVersion,
            GameVersionState gameVersionState,
            string gameVersionSource)
        {
            ModsRoot = modsRoot;
            RuntimeDirectory = runtimeDirectory;
            GameVersion = gameVersion;
            GameVersionState = gameVersionState;
            GameVersionSource = gameVersionSource;
        }

        internal string ModsRoot { get; }

        internal string RuntimeDirectory { get; }

        internal string GameVersion { get; }

        internal GameVersionState GameVersionState { get; }

        /// <summary>Where the version came from: <c>override</c>, <c>entry-assembly</c>, <c>appdomain</c> or <c>none</c>.</summary>
        internal string GameVersionSource { get; }

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
            sb.AppendLine("game-version-state=" + StateText(GameVersionState));
            sb.AppendLine("game-version-source=" + GameVersionSource);
            sb.AppendLine("mods-discovered=" + _records.Count);

            var initialized = 0;
            var failed = 0;
            var skipped = 0;
            var degraded = 0;
            for (var i = 0; i < _records.Count; i++)
            {
                var state = _records[i].State;
                if (state == ModState.Initialized) { initialized++; }
                else if (state == ModState.Failed) { failed++; }
                else if (state == ModState.Skipped) { skipped++; }
                if (_records[i].LogStatus == "degraded") { degraded++; }
            }
            sb.AppendLine("mods-initialized=" + initialized);
            sb.AppendLine("mods-failed=" + failed);
            sb.AppendLine("mods-skipped=" + skipped);
            sb.AppendLine("logging=" + (degraded == 0 ? "healthy" : "degraded (" + degraded + " of " + _records.Count + " mods)"));

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
                    sb.AppendLine("    log-status=" + record.LogStatus +
                                  (record.LogStatus == "degraded" ? " log-error=" + record.LogFailureType : ""));
                    if (record.SharedAssemblies.Count > 0)
                    {
                        sb.AppendLine("    shared=" + DescribeList(record.SharedAssemblies));
                    }
                    sb.AppendLine("    history=" + record.DescribeHistory());
                }
            }

            return sb.ToString();
        }

        private static string DescribeList(List<string> values)
        {
            var text = "";
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    text += ", ";
                }
                text += values[i];
            }
            return text;
        }

        private static string StateText(GameVersionState state)
        {
            switch (state)
            {
                case GameVersionState.Known: return "known";
                case GameVersionState.Invalid: return "invalid";
                default: return "unknown";
            }
        }
    }
}
