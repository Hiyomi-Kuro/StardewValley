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
    /// Test seam: makes framework-version detection behave as if reading the version failed, so
    /// the "version could not be determined" branch is reachable on a desktop where
    /// <c>ModFramework.Runtime</c> always has a readable version. <c>false</c> in production.
    /// </summary>
    internal static bool FrameworkVersionUnavailableForTests;

    /// <summary>
    /// Test seam: invoked immediately before <c>Assembly.LoadFrom</c> is issued for one claim.
    ///
    /// Phase 1.1.2 uses it to place a replacement inside the "the preflight has finished reading,
    /// the load has not been issued yet" window, which is the only way to exercise the TOCTOU
    /// binding deterministically. <c>null</c> in production.
    /// </summary>
    internal static Action<AssemblyClaim>? BeforeLoadForTests;

    /// <summary>
    /// Test seam: replaces the fixed-point round bound so the "did not converge" branch is
    /// reachable with a two-mod graph. <c>0</c> (the production value) selects the computed bound.
    /// </summary>
    internal static int RoundLimitOverrideForTests;

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
            var diagnostic = "loader-report: loader-crashed: " + DescribeException(ex);
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
        sb.AppendLine("locate=" + PathScrubber.Leaf(DefaultModsRoot()));
        sb.AppendLine("mods-root=" + PathScrubber.Leaf(root));
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
        var framework = DetectFrameworkVersion();
        var report = new LoadReport(modsRoot, DefaultModsRoot(), gameVersion, versionState, versionSource);
        report.AddLine("framework-version=" + framework.Text);
        report.AddLine("framework-version-state=" + FrameworkStateText(framework.State));

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
            report.AddWarning("could not create the log directory \"" + LogsDirectoryName +
                              "\"; per-mod logs fall back to the mod package directory");
        }

        // ---- gate 1: version compatibility, still without touching any mod assembly --------
        //
        // Phase 1.1.1: every reason a mod cannot run is collected into one classification map
        // BEFORE the assembly preflight. Phase 1.1 fed the compatibility-gate failures straight
        // into the preflight while leaving "my required dependency failed" for the sequential
        // loop, so a mod that was already doomed could still contribute a conflicting assembly
        // claim and take an unrelated healthy mod down with it.
        var intrinsicFailures = new Dictionary<string, ModOutcome>(StringComparer.OrdinalIgnoreCase);

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
                // The resolver already proved this mod cannot run: missing dependency, version
                // mismatch or cycle.
                intrinsicFailures[manifest.Id] = new ModOutcome(dependencyProblem.Code, dependencyProblem.Message);
            }
        }

        for (var i = 0; i < plan.Order.Count; i++)
        {
            var manifest = plan.Order[i];
            if (!byId.ContainsKey(manifest.Id) || intrinsicFailures.ContainsKey(manifest.Id))
            {
                continue;
            }
            var compatibility = CheckCompatibility(manifest, framework, report);
            if (compatibility != null)
            {
                intrinsicFailures[manifest.Id] = new ModOutcome(compatibility.Code, compatibility.Message);
            }
        }

        // ---- gate 2: global assembly identity preflight (metadata only, no LoadFrom) ------
        //
        // Fixed point over TWO mutually dependent sets:
        //   * `dependencySet`  - mods blocked because a required dependency cannot run;
        //   * `conflictSet`    - mods rejected by the current preflight pass.
        //
        // They interact: a mod rejected as a conflict co-owner may also be a dependent of a
        // failed mod, and excluding it for that reason can leave its former conflict partner
        // healthy again. Phase 1.1 locked the first pass in permanently and therefore reported
        // such a partner as failed although nothing conflicted with it any more.
        //
        // Each round: recompute the dependency closure over (intrinsic + conflictSet), then run
        // the preflight over everything that is not intrinsically failed and not dependency
        // blocked. Conflict-caused rejections are NOT accumulated - they are replaced by the new
        // set, which is what allows a wrongly rejected partner to come back. The loop stops when
        // the conflict set stops changing; a round bound keeps it deterministic even for a
        // pathological dependency graph (the last computed state is used and recorded).
        //
        // Phase 1.1.2: "stopped changing" is now an explicit state. A repeated (or unbounded)
        // iteration means the loader could not classify the candidates, and an unclassified
        // candidate must not be loaded - see `MF0208` below. The previous code simply broke out of
        // the loop and classified whatever state happened to be current.
        var staging = AssemblyStagingArea.TryCreate(modsRoot, out var stagingError);
        if (staging == null)
        {
            // Without a snapshot area the approved bytes cannot be the loaded bytes, so nothing is
            // loaded at all rather than falling back to path loading.
            var stagingMessage = ErrorCodes.AssemblyStagingFailed + ": " +
                                 ErrorCodes.Describe(ErrorCodes.AssemblyStagingFailed) +
                                 " (" + stagingError + ")";
            report.Result = "failed";
            report.AddLine("result=failed");
            report.AddLine("assembly-snapshot=unavailable");
            report.AddLine("error=" + stagingMessage);
            WriteReport(report);
            return stagingMessage;
        }
        report.AddLine("assembly-snapshot=private");
        report.AddLine("assembly-snapshot-policy=staged-then-verified");

        var conflictSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedByDependency = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var events = new Dictionary<string, PreflightEvent>(StringComparer.Ordinal);
        var declaredFiles = 0;
        var contentPolicy = "";
        var rounds = 0;
        var roundLimit = RoundLimitOverrideForTests > 0 ? RoundLimitOverrideForTests : plan.Order.Count * 2 + 4;
        var converged = false;
        var exhaustedReason = "";
        var seenStates = new HashSet<string>(StringComparer.Ordinal);
        AssemblyPreflightResult preflight;

        while (true)
        {
            rounds++;

            // 1. Dependency closure over the causes that are already settled. A mod that is
            //    currently conflict-rejected is allowed to move to "dependency blocked": the
            //    dependency reason is the more fundamental one, and (crucially) removing it from
            //    the conflict set lets its former partner be re-evaluated.
            dependencySet.Clear();
            blockedByDependency.Clear();
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < plan.Order.Count; i++)
                {
                    var manifest = plan.Order[i];
                    if (!byId.ContainsKey(manifest.Id) ||
                        intrinsicFailures.ContainsKey(manifest.Id) ||
                        dependencySet.Contains(manifest.Id))
                    {
                        continue;
                    }
                    var blockedBy = FirstExcludedRequiredDependency(manifest, intrinsicFailures, conflictSet, dependencySet);
                    if (blockedBy == null)
                    {
                        continue;
                    }
                    dependencySet.Add(manifest.Id);
                    blockedByDependency[manifest.Id] = blockedBy;
                    conflictSet.Remove(manifest.Id);
                    changed = true;
                }
            }

            // 2. Preflight every mod that could still run.
            var pool = new List<ModManifest>();
            for (var i = 0; i < plan.Order.Count; i++)
            {
                var manifest = plan.Order[i];
                if (!byId.ContainsKey(manifest.Id) ||
                    intrinsicFailures.ContainsKey(manifest.Id) ||
                    dependencySet.Contains(manifest.Id))
                {
                    continue;
                }
                pool.Add(manifest);
            }
            preflight = AssemblyPreflight.Analyze(pool, staging);
            CollectPreflightEvents(preflight, events);
            if (preflight.DeclaredFiles > declaredFiles)
            {
                declaredFiles = preflight.DeclaredFiles;
            }
            if (contentPolicy.Length == 0)
            {
                contentPolicy = preflight.ContentPolicy;
            }

            // Cycle detection: the three sets below are the complete input of the next round (they
            // decide the dependency closure and the candidate pool), so a repetition proves the
            // iteration can never settle, whatever the round bound is.
            if (!seenStates.Add(StateSignature(intrinsicFailures, dependencySet, conflictSet)))
            {
                exhaustedReason = "the conflict and dependency sets repeated, so the iteration cycles instead of converging";
                break;
            }

            // 3. A mod whose own declared file is missing/damaged, or that collides with a host
            //    assembly, cannot be repaired by removing another mod: that is intrinsic.
            var addedIntrinsic = 0;
            foreach (var rejection in preflight.Rejections)
            {
                if (!IsIntrinsicPreflightCode(rejection.Value.Code) ||
                    intrinsicFailures.ContainsKey(rejection.Key))
                {
                    continue;
                }
                intrinsicFailures[rejection.Key] = new ModOutcome(rejection.Value.Code, rejection.Value.Message);
                // An intrinsic failure is settled, so it must not keep a stale conflict membership.
                conflictSet.Remove(rejection.Key);
                addedIntrinsic++;
            }
            if (addedIntrinsic > 0)
            {
                if (rounds > roundLimit)
                {
                    exhaustedReason = "the round bound (" + roundLimit + ") was reached while intrinsic rejections were still being added";
                    break;
                }
                continue;
            }

            // 4. What is left can only be symmetric conflicts. Recompute them from scratch.
            var newConflictSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rejection in preflight.Rejections)
            {
                newConflictSet.Add(rejection.Key);
            }
            if (newConflictSet.SetEquals(conflictSet))
            {
                // A stable fixed point: the pool, the dependency closure and the conflict set all
                // reproduce themselves. This is the only state in which mods may be loaded.
                converged = true;
                break;
            }
            conflictSet = newConflictSet;
            if (rounds > roundLimit)
            {
                exhaustedReason = "the round bound (" + roundLimit + ") was reached before the conflict set stabilized";
                break;
            }
        }

        // 5. Final classification, rebuilt from the three settled sets. Dependency failure
        //    outranks a conflict for the same mod: the mod could not run because its dependency
        //    did not, whatever else is true of it.
        var outcomes = new Dictionary<string, ModOutcome>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in intrinsicFailures)
        {
            outcomes[pair.Key] = pair.Value;
        }

        if (!converged)
        {
            // Fail closed. The sets above are the state of an iteration that did not settle, so
            // they cannot classify anything: a mod that is absent from the last pool was never
            // evaluated, and a mod that is in it was evaluated against a pool that may still
            // change. Every candidate that is not already intrinsically doomed is rejected with
            // its own code, so "the loader could not decide" is never reported as "this mod is
            // fine" (Phase 1.1.2, MF0208).
            for (var i = 0; i < plan.Order.Count; i++)
            {
                var manifest = plan.Order[i];
                if (!byId.ContainsKey(manifest.Id) || outcomes.ContainsKey(manifest.Id))
                {
                    continue;
                }
                outcomes[manifest.Id] = new ModOutcome(ErrorCodes.PreflightNotConverged,
                    "the assembly preflight did not converge (rounds=" + rounds +
                    ", limit=" + roundLimit + ": " + exhaustedReason +
                    "), so this mod was not classified and was not loaded");
                report.AddLine("preflight-not-converged=" + manifest.Id + " code=" + ErrorCodes.PreflightNotConverged);
            }
            report.AddWarning("the assembly preflight did not converge (" + exhaustedReason +
                              "); no mod assembly was loaded - see " + ErrorCodes.PreflightNotConverged);
        }
        else
        {
            foreach (var id in dependencySet)
            {
                string? blockedBy;
                if (!blockedByDependency.TryGetValue(id, out blockedBy) || blockedBy == null)
                {
                    blockedBy = "unknown";
                }
                outcomes[id] = new ModOutcome(ErrorCodes.DependencyFailed,
                    "required dependency \"" + blockedBy + "\" did not initialize");
                report.AddLine("dependency-blocked=" + id + " blocked-by=" + blockedBy +
                               " code=" + ErrorCodes.DependencyFailed);
            }
            foreach (var id in conflictSet)
            {
                if (outcomes.ContainsKey(id))
                {
                    continue;
                }
                GateProblem? problem;
                if (preflight.Rejections.TryGetValue(id, out problem) && problem != null)
                {
                    outcomes[id] = new ModOutcome(problem.Code, problem.Message);
                }
            }
        }

        RenderPreflight(report, events, outcomes, declaredFiles, contentPolicy);
        report.AddLine("preflight-stage=complete");
        report.AddLine("preflight-rounds=" + rounds);
        report.AddLine("preflight-round-limit=" + roundLimit);
        report.AddLine("preflight-convergence=" + (converged ? "converged" : "exhausted"));
        if (!converged)
        {
            report.AddLine("preflight-exhausted-reason=" + exhaustedReason);
        }
        report.AddLine("assembly-snapshot-files=" + staging.FileCount +
                       " bytes=" + staging.TotalBytes);

        var initialized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < plan.Order.Count; i++)
        {
            var manifest = plan.Order[i];
            ModRecord? record;
            if (!byId.TryGetValue(manifest.Id, out record) || record == null)
            {
                continue;
            }

            ModOutcome? outcome;
            if (outcomes.TryGetValue(manifest.Id, out outcome) && outcome != null)
            {
                record.Fail(outcome.Code, outcome.Message);
                initialized[manifest.Id] = false;
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

            // Defence in depth only: the fixed point above already blocked every mod whose
            // required dependency failed before the preflight. This still catches a dependency
            // that failed while mods were initializing.
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
    private static GateProblem? CheckCompatibility(ModManifest manifest, FrameworkVersionInfo framework, LoadReport report)
    {
        if (manifest.FrameworkVersion.Text != "*")
        {
            if (framework.State == FrameworkVersionState.Unknown)
            {
                // Fail closed: "cannot be determined" is not "0.0.0", and a declared range must
                // never be checked against a placeholder.
                return new GateProblem(ErrorCodes.FrameworkVersionUnknown,
                    "requires framework \"" + manifest.FrameworkVersion.Describe() +
                    "\" but the running framework version could not be determined, so the range cannot be enforced " +
                    "(declare \"frameworkVersion\": \"*\" if the mod does not depend on the framework version)");
            }
            if (framework.State == FrameworkVersionState.Invalid || framework.Value == null)
            {
                return new GateProblem(ErrorCodes.FrameworkVersionInvalid,
                    "requires framework \"" + manifest.FrameworkVersion.Describe() +
                    "\" but the running framework version " + Describe(framework.Text) +
                    " is not a valid semantic version, so the range cannot be enforced");
            }
            if (!manifest.FrameworkVersion.IsSatisfiedBy(framework.Value!))
            {
                return new GateProblem(ErrorCodes.FrameworkVersionUnsupported,
                    "requires framework \"" + manifest.FrameworkVersion.Describe() +
                    "\" but this framework is " + framework.Text);
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

    /// <summary>One mod's terminal classification, decided before any assembly is loaded.</summary>
    private sealed class ModOutcome
    {
        internal ModOutcome(string code, string message)
        {
            Code = code;
            Message = message;
        }

        internal string Code { get; }

        internal string Message { get; }
    }

    /// <summary>
    /// First required dependency of <paramref name="manifest"/> that is already excluded by any
    /// of the three sets, or <c>null</c>.
    ///
    /// Optional dependencies never block: that is the manifest contract, and it is the one case
    /// where a failure must not spread. Everything is driven by the deterministic plan order and
    /// by each manifest's own dependency order, so the outcome cannot depend on directory
    /// enumeration or file-system ordering.
    /// </summary>
    private static string? FirstExcludedRequiredDependency(
        ModManifest manifest,
        Dictionary<string, ModOutcome> intrinsicFailures,
        HashSet<string> conflictSet,
        HashSet<string> dependencySet)
    {
        for (var i = 0; i < manifest.Dependencies.Count; i++)
        {
            var dep = manifest.Dependencies[i];
            if (dep.Optional)
            {
                continue;
            }
            if (intrinsicFailures.ContainsKey(dep.Id) || conflictSet.Contains(dep.Id) ||
                dependencySet.Contains(dep.Id))
            {
                return dep.Id;
            }
        }
        return null;
    }

    /// <summary>
    /// Rejections that describe this mod's own declarations and cannot be cured by removing
    /// another mod: a missing file, a damaged file, or a collision with the host.
    /// </summary>
    private static bool IsIntrinsicPreflightCode(string code)
    {
        return code == ErrorCodes.EntryAssemblyMissing ||
               code == ErrorCodes.AssemblyIdentityUnreadable ||
               code == ErrorCodes.HostAssemblyConflict ||
               code == ErrorCodes.AssemblyStagingFailed;
    }

    /// <summary>Keeps one copy of every finding across all preflight rounds.</summary>
    private static void CollectPreflightEvents(
        AssemblyPreflightResult preflight,
        Dictionary<string, PreflightEvent> events)
    {
        for (var i = 0; i < preflight.Events.Count; i++)
        {
            var e = preflight.Events[i];
            if (!events.ContainsKey(e.Key))
            {
                events[e.Key] = e;
            }
        }
    }

    /// <summary>
    /// Renders the preflight section against the <b>final</b> classification.
    ///
    /// A finding only survives if it still explains a rejection, or if it still describes the
    /// mods that are going to load. That is what stops a transient early-round conflict - formed
    /// together with a mod that was later excluded for an unrelated reason - from appearing as
    /// the reason a healthy mod failed.
    /// </summary>
    private static void RenderPreflight(
        LoadReport report,
        Dictionary<string, PreflightEvent> events,
        Dictionary<string, ModOutcome> outcomes,
        int declaredFiles,
        string contentPolicy)
    {
        var ordered = new List<PreflightEvent>(events.Values);
        ordered.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

        var conflicts = 0;
        var shared = 0;
        var hostShared = 0;
        var hostUnverified = 0;

        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            switch (e.Kind)
            {
                case PreflightEventKind.ModConflict:
                {
                    var kept = new List<string>();
                    for (var o = 0; o < e.Owners.Count; o++)
                    {
                        if (RejectedWith(e.Owners[o], outcomes, ErrorCodes.AssemblyIdentityConflict))
                        {
                            kept.Add(e.OwnerLabels[o]);
                        }
                    }
                    if (kept.Count < 2)
                    {
                        break;
                    }
                    conflicts++;
                    report.AddLine("preflight-conflict=" + e.SimpleName + " kind=mod owners=" +
                                   JoinValues(kept) + " identities=" + e.Identities);
                    break;
                }

                case PreflightEventKind.HostConflict:
                    if (!RejectedWith(e.Owners[0], outcomes, ErrorCodes.HostAssemblyConflict))
                    {
                        break;
                    }
                    report.AddLine("preflight-conflict=" + e.SimpleName + " kind=host owner=" +
                                   e.OwnerLabels[0] + " identity=" + e.Identities);
                    break;

                case PreflightEventKind.HostUnverifiable:
                    if (!RejectedWith(e.Owners[0], outcomes, ErrorCodes.HostAssemblyConflict))
                    {
                        break;
                    }
                    hostUnverified++;
                    report.AddLine("preflight-host-unverifiable-rejected=" + e.SimpleName + " owner=" +
                                   e.OwnerLabels[0] + " " + e.Detail);
                    break;

                case PreflightEventKind.HostAmbiguous:
                    if (!RejectedWith(e.Owners[0], outcomes, ErrorCodes.HostAssemblyConflict))
                    {
                        break;
                    }
                    report.AddLine("preflight-host-ambiguous=" + e.SimpleName + " owner=" +
                                   e.OwnerLabels[0] + " " + e.Detail);
                    break;

                case PreflightEventKind.Shared:
                {
                    var anyRejected = false;
                    for (var o = 0; o < e.Owners.Count; o++)
                    {
                        if (outcomes.ContainsKey(e.Owners[o]))
                        {
                            anyRejected = true;
                            break;
                        }
                    }
                    if (anyRejected)
                    {
                        break;
                    }
                    shared += e.Owners.Count - 1;
                    var parts = e.Detail.Split('|');
                    report.AddLine("preflight-shared=" + e.SimpleName + " canonical=" + parts[0] +
                                   " representative=" + parts[1] + " owners=" + parts[2]);
                    break;
                }

                case PreflightEventKind.HostShare:
                    if (outcomes.ContainsKey(e.Owners[0]))
                    {
                        break;
                    }
                    hostShared++;
                    report.AddLine("preflight-host-share=" + e.SimpleName + " owner=" + e.Owners[0] +
                                   " " + e.Detail);
                    break;
            }
        }

        var rejectedByPreflight = 0;
        foreach (var pair in outcomes)
        {
            if (IsPreflightCode(pair.Value.Code))
            {
                rejectedByPreflight++;
            }
        }

        report.AddLine("preflight=assemblies=" + declaredFiles +
                       " conflicts=" + conflicts +
                       " shared=" + shared +
                       " host-shared=" + hostShared +
                       " host-unverified=" + hostUnverified +
                       " rejected=" + rejectedByPreflight);
        report.AddLine("preflight-content-identity=" + contentPolicy);
    }

    private static bool IsPreflightCode(string code)
    {
        return code == ErrorCodes.EntryAssemblyMissing ||
               code == ErrorCodes.AssemblyIdentityUnreadable ||
               code == ErrorCodes.AssemblyIdentityConflict ||
               code == ErrorCodes.HostAssemblyConflict ||
               code == ErrorCodes.AssemblyStagingFailed;
    }

    private static bool RejectedWith(string modId, Dictionary<string, ModOutcome> outcomes, string code)
    {
        ModOutcome? outcome;
        return outcomes.TryGetValue(modId, out outcome) && outcome != null &&
               string.Equals(outcome.Code, code, StringComparison.Ordinal);
    }

    private static string JoinValues(List<string> values)
    {
        var text = "";
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                text += ",";
            }
            text += values[i];
        }
        return text;
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
                    string privateCode;
                    string privateError;
                    if (!TryResolveClaim(claim, report, out privateAssembly, out privateCode, out privateError))
                    {
                        record.Fail(privateCode, privateError);
                        initialized[manifest.Id] = false;
                        return;
                    }
                    NoteShared(record, claim);
                }
            }

            Assembly assembly;
            string entryCode;
            string entryError;
            if (!TryResolveClaim(entryClaim, report, out assembly, out entryCode, out entryError))
            {
                record.Fail(entryCode, entryError);
                initialized[manifest.Id] = false;
                return;
            }
            NoteShared(record, entryClaim);
            // Path-free display value: the report names the declared file, or the host when the
            // mod reuses an already loaded instance. An absolute path is never persisted.
            record.SetAssemblyPath(entryClaim.Resolution == AssemblyResolution.ShareFromHost
                ? "host:" + entryClaim.Identity.Name
                : manifest.EntryAssembly);

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

    /// <summary>
    /// Identity of one fixed-point state: the three sets below are the complete input of the next
    /// round, so two equal signatures prove the iteration is repeating.
    /// </summary>
    private static string StateSignature(
        Dictionary<string, ModOutcome> intrinsicFailures,
        HashSet<string> dependencySet,
        HashSet<string> conflictSet)
    {
        var sb = new StringBuilder();
        var intrinsic = new List<string>();
        foreach (var pair in intrinsicFailures)
        {
            intrinsic.Add(pair.Key + ":" + pair.Value.Code);
        }
        AppendSorted(sb, intrinsic);
        sb.Append('|');
        AppendSorted(sb, dependencySet);
        sb.Append('|');
        AppendSorted(sb, conflictSet);
        return sb.ToString();
    }

    private static void AppendSorted(StringBuilder sb, IEnumerable<string> values)
    {
        var list = new List<string>();
        foreach (var value in values)
        {
            list.Add(value);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(list[i]);
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
    ///
    /// <para>Phase 1.1.3: every path that hands back an <see cref="Assembly"/> - host reuse, the
    /// per-path cache and a fresh <c>LoadFrom</c> - first checks that the instance carries the
    /// identity the claim was preflighted with. A mismatch is never cached as a success for that
    /// claim: the mod fails with <see cref="ErrorCodes.AssemblyLoadIdentityMismatch"/>. This is
    /// defence in depth only - the authoritative gate is that only preflighted bytes reach this
    /// method at all - because a mismatched load has already entered the process.</para>
    /// </summary>
    private static bool TryResolveClaim(
        AssemblyClaim claim,
        LoadReport report,
        out Assembly assembly,
        out string code,
        out string error)
    {
        assembly = null!;
        code = "";
        error = "";

        if (claim.Resolution == AssemblyResolution.ShareFromHost)
        {
            if (claim.HostAssembly == null)
            {
                code = ErrorCodes.HostAssemblyConflict;
                error = "the already loaded host assembly \"" + claim.Identity.Name + "\" is no longer present";
                return false;
            }
            if (!VerifyLoadedIdentity(claim, claim.HostAssembly, out error))
            {
                code = ErrorCodes.AssemblyLoadIdentityMismatch;
                error = claim.DeclaredName + ": " + error;
                return false;
            }
            assembly = claim.HostAssembly;
            return true;
        }

        // Phase 1.1.2: the load source is the snapshot that was written from the bytes the
        // preflight parsed. The mod-declared file is never opened here, so replacing it after the
        // preflight cannot change what is loaded.
        if (claim.StagedPath.Length == 0 || claim.SnapshotFingerprint.Length == 0)
        {
            // Deliberately MF0202, not MF0209: Phase 1.1.2's delivered contract is that "the
            // approved bytes cannot be loaded at load time" is a load failure, while MF0209 stays
            // the preflight-time staging failure. Phase 1.1.3 does not renumber either.
            code = ErrorCodes.AssemblyLoadFailed;
            error = claim.DeclaredName + ": " + ErrorCodes.Describe(ErrorCodes.AssemblyStagingFailed) +
                    " (the claim has no snapshot)";
            return false;
        }

        var path = claim.StagedPath;
        Assembly? cached;
        if (s_loadedByPath.TryGetValue(path, out cached) && cached != null)
        {
            // The cache is keyed by canonical artifact, so a hit still has to answer the same
            // identity question as a fresh load - otherwise a poisoned entry would answer for
            // every later claim that shares the path.
            if (!VerifyLoadedIdentity(claim, cached, out error))
            {
                code = ErrorCodes.AssemblyLoadIdentityMismatch;
                error = claim.DeclaredName + ": " + error;
                return false;
            }
            assembly = cached;
            return true;
        }

        // The seam runs as late as possible: whatever a test (or an attacker) changes after this
        // point still has to survive the snapshot verification below.
        var beforeLoad = BeforeLoadForTests;
        if (beforeLoad != null)
        {
            beforeLoad(claim);
        }

        // Consistency check of the snapshot itself, immediately before it is used. This narrows -
        // it does not close - the window between "checked" and "loaded"; see AssemblyStagingArea.
        string snapshotError;
        if (!AssemblyStagingArea.VerifySnapshot(path, claim.SnapshotFingerprint, claim.Length, out snapshotError))
        {
            code = ErrorCodes.AssemblyLoadFailed;
            error = claim.DeclaredName + ": " + ErrorCodes.Describe(ErrorCodes.AssemblyStagingFailed) +
                    " (" + snapshotError + ")";
            return false;
        }

        try
        {
            assembly = Assembly.LoadFrom(path);
        }
        catch (Exception ex)
        {
            code = ErrorCodes.AssemblyLoadFailed;
            error = claim.DeclaredName + ": " + DescribeException(ex);
            return false;
        }

        // Defence in depth: the instance that was actually loaded must be the assembly the claim
        // was preflighted as. A failure here is *not* recorded in the path cache, so it cannot
        // become another claim's "successful" load either.
        if (!VerifyLoadedIdentity(claim, assembly, out error))
        {
            code = ErrorCodes.AssemblyLoadIdentityMismatch;
            error = claim.DeclaredName + ": " + error;
            return false;
        }

        s_loadedByPath[path] = assembly;
        report.AddLine("assembly-load=" + claim.ModId + " file=" + claim.DeclaredName +
                       " canonical=" + claim.CanonicalModId +
                       " snapshot=" + (claim.SnapshotFingerprint.Length > 0 ? claim.SnapshotFingerprint : "none"));
        return true;
    }

    /// <summary>
    /// Compares the identity of a loaded instance with the identity the claim was preflighted
    /// with (name, version, culture, public-key token). Never used to <i>authorize</i> a load; it
    /// only refuses to treat a mismatch as this claim's successful load.
    /// </summary>
    private static bool VerifyLoadedIdentity(AssemblyClaim claim, Assembly assembly, out string error)
    {
        error = "";
        var identity = HostAssemblyIdentity.From(assembly);
        if (identity == null)
        {
            error = "the loaded assembly has no readable identity";
            return false;
        }

        string difference;
        if (!AssemblyIdentityComparison.SameLoadedIdentity(claim.Identity, identity, out difference))
        {
            error = "the loaded assembly is \"" + identity.Describe() + "\" but this claim was preflighted as \"" +
                    claim.IdentityText + "\" (" + difference + ")";
            return false;
        }
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
        // Exception type plus a path-free reason. The raw message of an IO exception quotes the
        // absolute file name, and the report is persisted, so it must not be copied verbatim.
        return PathScrubber.Describe(ex);
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

    /// <summary>How confident the loader is about the running ModFramework.Runtime version.</summary>
    internal enum FrameworkVersionState
    {
        /// <summary>A version was read and parses as SemVer.</summary>
        Known = 0,

        /// <summary>The version could not be read at all: it is <b>not</b> "0.0.0".</summary>
        Unknown = 1,

        /// <summary>A version was read but it is not SemVer.</summary>
        Invalid = 2,
    }

    internal sealed class FrameworkVersionInfo
    {
        internal string Text = "unknown";

        internal FrameworkVersionState State = FrameworkVersionState.Unknown;

        internal SemVersion? Value;
    }

    /// <summary>
    /// Version of the running <c>ModFramework.Runtime</c>, mapped to the three-part SemVer form
    /// the manifest ranges use.
    ///
    /// Phase 1.1 returned the placeholder <c>"0.0.0"</c> when the version could not be read.
    /// That is a <i>valid</i> semantic version, so an explicit manifest range was then evaluated
    /// against a version the loader had invented. Phase 1.1.1 keeps the three states apart and
    /// lets the compatibility gate fail closed on the two non-<see cref="FrameworkVersionState.Known"/>
    /// ones.
    /// </summary>
    internal static FrameworkVersionInfo DetectFrameworkVersion()
    {
        var info = new FrameworkVersionInfo();

        var overrideText = FrameworkVersionOverride;
        if (overrideText != null)
        {
            info.Text = overrideText;
            ClassifyFrameworkVersion(info);
            return info;
        }

        if (!FrameworkVersionUnavailableForTests)
        {
            try
            {
                var name = typeof(Loader).Assembly.GetName();
                var version = name.Version;
                if (version != null && version.Major >= 0 && version.Minor >= 0 && version.Build >= 0)
                {
                    info.Text = version.Major + "." + version.Minor + "." + version.Build;
                    ClassifyFrameworkVersion(info);
                    return info;
                }
            }
            catch (Exception)
            {
                // Unknown is a valid answer; it is never replaced by a placeholder version.
            }
        }

        info.State = FrameworkVersionState.Unknown;
        info.Text = "unknown";
        return info;
    }

    private static void ClassifyFrameworkVersion(FrameworkVersionInfo info)
    {
        SemVersion parsed;
        string error;
        if (SemVersion.TryParse(info.Text, out parsed, out error))
        {
            info.State = FrameworkVersionState.Known;
            info.Value = parsed;
            return;
        }
        info.State = FrameworkVersionState.Invalid;
        info.Value = null;
    }

    internal static string FrameworkVersion()
    {
        return DetectFrameworkVersion().Text;
    }

    private static string FrameworkStateText(FrameworkVersionState state)
    {
        switch (state)
        {
            case FrameworkVersionState.Known: return "known";
            case FrameworkVersionState.Invalid: return "invalid";
            default: return "unknown";
        }
    }

    /// <summary>
    /// The mods root the shipped APK actually uses: the app's external files directory.
    ///
    /// It is the same string <c>Tools/ModInjector</c> bakes into
    /// <c>ModFramework.Bootstrap.Start()</c>, and Phase 1.1.6 observed the game migrating its
    /// <c>files/Mods</c> here on first launch. A fallback to the app-private
    /// <c>/data/data/&lt;pkg&gt;/files/Mods</c> would therefore point the loader at a directory
    /// Bootstrap never loads from, so it is not used on the normal Android path.
    /// </summary>
    internal const string ExternalModsRoot =
        "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods";

    internal static string DefaultModsRoot()
    {
        string? location;
        try
        {
            location = typeof(Loader).Assembly.Location;
        }
        catch (Exception)
        {
            // A host that cannot report a location can only mean "load from the external root".
            return ExternalModsRoot;
        }

        return ResolveModsRoot(location);
    }

    /// <summary>
    /// Pure resolver behind <see cref="DefaultModsRoot"/>: the directory that contains the
    /// framework assembly, or <see cref="ExternalModsRoot"/> when the host reports no usable
    /// location (the assembly was loaded from a <c>byte[]</c>, from a single-file bundle, or
    /// <see cref="System.Reflection.Assembly.Location"/> threw).
    ///
    /// Separated from the production entry point so the desktop host tests can exercise every
    /// branch without a device, and so the fallback stays one constant shared with the injector.
    /// The returned path is normalized (<c>/</c> separators) exactly like
    /// <see cref="PathRules.Normalize"/>, so an Android path and a desktop path compare the same.
    /// </summary>
    internal static string ResolveModsRoot(string? location)
    {
        if (!string.IsNullOrEmpty(location))
        {
            try
            {
                // Normalize *before* asking the host for the parent directory. Windows accepts
                // both '\' and '/' as separators, but Unix/Android only accepts '/', so
                // "C:\mods\test\ModFramework.Runtime.dll" would otherwise yield no parent at all
                // on a Linux CI host and silently fall back to ExternalModsRoot. PathRules.Normalize
                // only rewrites separators (it never touches roots, UNC prefixes or URIs), so this
                // keeps the answer independent of the OS the tests happen to run on.
                var normalizedLocation = PathRules.Normalize(location!);
                var directory = Path.GetDirectoryName(normalizedLocation);
                if (!string.IsNullOrEmpty(directory))
                {
                    return PathRules.Normalize(directory!);
                }
            }
            catch (Exception)
            {
                // An unusable location is the same answer as a missing one.
            }
        }

        return ExternalModsRoot;
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
            report.AddLine("stack=" + PathScrubber.Scrub(stack!).Replace("\n", " | "));
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
            // Path policy (Phase 1.1.1): the report is persisted next to the mods and may be
            // shared when someone asks for help, so it carries no host location. The runtime
            // directory and the mods root are reduced to their last segment, and each mod is
            // named by its own package directory relative to the root.
            sb.AppendLine("locate=" + PathScrubber.Leaf(RuntimeDirectory));
            sb.AppendLine("mods-root=" + DescribeRoot(ModsRoot));
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
                        sb.Append(" code=" + record.ErrorCode);
                    }
                    if (record.FailureKind.Length > 0)
                    {
                        sb.Append(" kind=" + record.FailureKind);
                    }
                    if (record.ErrorCode.Length > 0)
                    {
                        sb.Append(" message=" + record.ErrorMessage);
                    }
                    sb.AppendLine();
                    sb.AppendLine("    dir=" + relativeDirectory(record));
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

        /// <summary>The mod's package directory, relative to the mods root: never a host path.</summary>
        private string relativeDirectory(ModRecord record)
        {
            var component = record.DirectoryName;
            if (string.IsNullOrEmpty(component))
            {
                component = PathScrubber.Leaf(record.Directory);
            }
            return component.Length == 0 ? "." : component;
        }

        /// <summary>The mods root reduced to its last segment (or "." when it has none).</summary>
        private static string DescribeRoot(string root)
        {
            var leaf = PathScrubber.Leaf(root);
            return leaf.Length == 0 ? "." : leaf;
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
