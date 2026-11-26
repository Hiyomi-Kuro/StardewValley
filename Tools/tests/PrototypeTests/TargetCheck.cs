using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Phase21.WorldReady;

namespace Phase21PrototypeTests;

/// <summary>
/// Layer 3: applies the prototype to a <b>temporary copy</b> of the real assembly and verifies the
/// written bytes structurally. It never loads or executes the game assembly, never builds an APK
/// and never touches a device.
///
/// <para>The input is refused unless it is the expected pristine <c>StardewValley</c>
/// 1.6.15.24354 and does not already contain either the default Bootstrap or this prototype's
/// bridge. A default-injected build and this prototype must never be mixed.</para>
/// </summary>
internal static class TargetCheck
{
    private const string ExpectedAssembly = "StardewValley";
    private static readonly Version ExpectedVersion = new Version(1, 6, 15, 24354);
    private const string BridgeNamespace = "Phase21.Prototype";
    private const string BridgeType = "WorldReadyBridge";

    private static readonly string[] BridgeMethods =
    {
        PrototypeInjector.ResetEntryHookName,
        PrototypeInjector.ResetExitHookName,
        PrototypeInjector.ResetEpochGetterName,
        PrototypeInjector.StartHookName,
        PrototypeInjector.CompletionHookName,
        PrototypeInjector.AdvanceHookName,
        PrototypeInjector.LoaderCreationHookName,
    };

    private static readonly string[] ExpectedChangedMethods =
    {
        "System.Void StardewValley.Game1::loadForNewGame(System.Boolean)",
        "System.Void StardewValley.Game1::OnDayStarted()",
        "System.Void StardewValley.Game1::ResetGameStateOnTitleScreen()",
        "System.Void StardewValley.Game1::UpdateTitleScreenDuringLoadingMode()",
        "System.Void StardewValley.SaveGame::LoadAfterPermissionCheck()",
    };

    public static int Apply(string inputPath, string outputPath, string? refsDirectory)
    {
        var failures = new List<string>();
        var inputBytes = File.ReadAllBytes(inputPath);
        var inputHash = Hash(inputBytes);

        using var module = ReadModule(inputPath, refsDirectory);

        Console.WriteLine("EVIDENCE input        = " + Path.GetFileName(inputPath));
        Console.WriteLine("EVIDENCE input_bytes  = " + inputBytes.Length.ToString("N0"));
        Console.WriteLine("EVIDENCE input_sha256 = " + inputHash);
        Console.WriteLine("EVIDENCE assembly     = " + module.Assembly.Name.Name + " " +
                          module.Assembly.Name.Version);
        Console.WriteLine("EVIDENCE mvid_before  = " + module.Mvid);

        if (!string.Equals(module.Assembly.Name.Name, ExpectedAssembly, StringComparison.Ordinal) ||
            module.Assembly.Name.Version != ExpectedVersion)
        {
            Console.WriteLine("FAIL identity is not " + ExpectedAssembly + " " + ExpectedVersion);
            return 1;
        }

        if (module.GetType("ModFramework", "Bootstrap") != null)
        {
            Console.WriteLine("FAIL input already carries ModFramework.Bootstrap (default-injected build); " +
                              "refusing to mix it with the prototype");
            return 1;
        }

        if (module.GetType(BridgeNamespace, BridgeType) != null)
        {
            Console.WriteLine("FAIL input already carries the prototype bridge");
            return 1;
        }

        var before = PrototypeInjector.Fingerprint(module);
        var maxStackBefore = PrototypeInjector.MaxStackMap(module);
        var refsBefore = string.Join("|", module.AssemblyReferences.Select(a => a.FullName));
        var originalMvid = module.Mvid;

        var targets = PrototypeInjector.Resolve(module);
        var hooks = TargetBridgeEmitter.Emit(module);
        var injection = PrototypeInjector.Inject(targets, hooks);
        module.Mvid = Guid.NewGuid();

        Console.WriteLine("EVIDENCE reset_entry_hook_sites   = " + injection.ResetEntryCalls);
        Console.WriteLine("EVIDENCE reset_exit_hook_sites    = " + injection.ResetCompletedCalls);
        Console.WriteLine("EVIDENCE reset_handler_returns_skipped = " +
                          injection.ResetSkippedHandlerReturns);
        Console.WriteLine("EVIDENCE load_hook_sites          = " + injection.WorldLoadingCalls);
        Console.WriteLine("EVIDENCE completion_hook_sites    = " + injection.DayCompletedCalls);
        Console.WriteLine("EVIDENCE handler_returns_skipped  = " + injection.SkippedHandlerReturns);
        Console.WriteLine("EVIDENCE maxstack reset           = " + injection.ResetMaxStackBefore +
                          " -> " + injection.ResetMaxStackAfter);
        Console.WriteLine("EVIDENCE maxstack load            = " + injection.LoadMaxStackBefore + " -> " +
                          injection.LoadMaxStackAfter);
        Console.WriteLine("EVIDENCE maxstack completion      = " + injection.CompletionMaxStackBefore +
                          " -> " + injection.CompletionMaxStackAfter);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        module.Write(outputPath);

        var outputBytes = File.ReadAllBytes(outputPath);
        Console.WriteLine("EVIDENCE output_bytes = " + outputBytes.Length.ToString("N0"));
        Console.WriteLine("EVIDENCE output_sha256 = " + Hash(outputBytes));
        Console.WriteLine("EVIDENCE mvid_after   = " + module.Mvid);

        if (module.Mvid == originalMvid)
        {
            failures.Add("MVID did not change");
        }

        if (Hash(File.ReadAllBytes(inputPath)) != inputHash)
        {
            failures.Add("input file bytes changed (must stay untouched)");
        }

        using var written = ReadModule(outputPath, refsDirectory);

        var writtenTargets = PrototypeInjector.Resolve(written);
        var bridge = written.GetType(BridgeNamespace, BridgeType);
        if (bridge is null)
        {
            failures.Add("bridge type missing after write");
            Report(failures);
            return 1;
        }

        var hookRefs = ResolveBridgeHooks(bridge);
        if (hookRefs is null)
        {
            failures.Add("bridge hook methods missing after write");
            Report(failures);
            return 1;
        }

        var verification = PrototypeInjector.Verify(writtenTargets, hookRefs);
        foreach (var problem in verification.Problems)
        {
            failures.Add("verify: " + problem);
        }

        Console.WriteLine("EVIDENCE verify_reset_entry_calls      = " + verification.ResetEntryCalls);
        Console.WriteLine("EVIDENCE verify_reset_completed_calls  = " + verification.ResetCompletedCalls);
        Console.WriteLine("EVIDENCE verify_reset_normal_returns   = " + verification.ResetNormalReturns);
        Console.WriteLine("EVIDENCE verify_reset_handler_returns  = " + verification.ResetHandlerReturns);
        Console.WriteLine("EVIDENCE verify_start_calls            = " + verification.WorldLoadingCalls);
        Console.WriteLine("EVIDENCE verify_epoch_read_first       = " + verification.EpochReadFirst);
        Console.WriteLine("EVIDENCE verify_completion_calls       = " + verification.DayCompletedCalls);
        Console.WriteLine("EVIDENCE normal_returns                = " + verification.NormalReturns);
        Console.WriteLine("EVIDENCE handler_returns               = " + verification.HandlerReturns);
        Console.WriteLine("EVIDENCE edges_to_normal_returns       = " + verification.EdgesToNormalReturns);
        Console.WriteLine("EVIDENCE verify_advance_calls          = " + verification.AdvanceCalls);
        Console.WriteLine("EVIDENCE verify_advance_identity_local = " +
                          verification.AdvanceIdentityFromLocal);
        Console.WriteLine("EVIDENCE verify_advance_guards_intact  = " +
                          verification.AdvanceGuardsIntact);
        Console.WriteLine("EVIDENCE verify_advance_site_unprotected = " +
                          verification.AdvanceSiteUnprotected);
        Console.WriteLine("EVIDENCE verify_creation_calls          = " +
                          verification.LoaderCreationCalls);
        Console.WriteLine("EVIDENCE verify_creation_from_factory   = " +
                          verification.LoaderCreationIdentityFromFactory);
        Console.WriteLine("EVIDENCE verify_creation_write_intact   = " + verification.LoaderWriteIntact);
        Console.WriteLine("EVIDENCE verify_creation_epoch_first    = " +
                          verification.LoaderCreationEpochFirst);
        Console.WriteLine("EVIDENCE verify_creation_unprotected    = " +
                          verification.LoaderCreationSiteUnprotected);
        Console.WriteLine("EVIDENCE mvid_changed                  = " + (written.Mvid != originalMvid));

        if (string.Join("|", written.AssemblyReferences.Select(a => a.FullName)) != refsBefore)
        {
            failures.Add("AssemblyRef set changed");
        }

        var after = PrototypeInjector.Fingerprint(written);
        var changed = before.Keys.Where(k => !after.TryGetValue(k, out var value) || value != before[k])
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var added = after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Console.WriteLine("EVIDENCE changed_methods = " + changed.Count);
        foreach (var name in changed)
        {
            Console.WriteLine("EVIDENCE   changed: " + name);
        }

        Console.WriteLine("EVIDENCE added_methods   = " + added.Count);
        foreach (var name in added)
        {
            Console.WriteLine("EVIDENCE   added: " + name);
        }

        Console.WriteLine("EVIDENCE removed_methods = " + removed.Count);

        foreach (var expected in ExpectedChangedMethods)
        {
            if (!changed.Contains(expected, StringComparer.Ordinal))
            {
                failures.Add("expected method not marked changed: " + expected);
            }
        }

        foreach (var name in changed)
        {
            if (!ExpectedChangedMethods.Contains(name, StringComparer.Ordinal))
            {
                failures.Add("unexpected method changed: " + name);
            }
        }

        if (removed.Count != 0)
        {
            failures.Add("methods disappeared");
        }

        if (added.Count != BridgeMethods.Length || added.Any(a =>
                !a.Contains(" " + BridgeNamespace + "." + BridgeType + "::", StringComparison.Ordinal)))
        {
            failures.Add("added methods are not exactly the " + BridgeMethods.Length +
                         " bridge methods");
        }

        CheckBridgeFields(bridge, failures);
        ReportMaxStackNormalisation(maxStackBefore, written, failures);

        Report(failures);
        return failures.Count == 0 ? 0 : 1;
    }

    public static int VerifyOnly(string path, string? refsDirectory)
    {
        var failures = new List<string>();
        var bytes = File.ReadAllBytes(path);
        using var module = ReadModule(path, refsDirectory);

        Console.WriteLine("EVIDENCE verify_only   = " + Path.GetFileName(path));
        Console.WriteLine("EVIDENCE sha256        = " + Hash(bytes));
        Console.WriteLine("EVIDENCE mvid          = " + module.Mvid);

        var bridge = module.GetType(BridgeNamespace, BridgeType);
        if (bridge is null)
        {
            failures.Add("bridge type missing");
        }
        else
        {
            var hookRefs = ResolveBridgeHooks(bridge);
            if (hookRefs is null)
            {
                failures.Add("bridge hook methods missing");
            }
            else
            {
                var targets = PrototypeInjector.Resolve(module);
                var verification = PrototypeInjector.Verify(targets, hookRefs);
                failures.AddRange(verification.Problems.Select(p => "verify: " + p));
                Console.WriteLine("EVIDENCE verify_reset_entry_calls     = " +
                                  verification.ResetEntryCalls);
                Console.WriteLine("EVIDENCE verify_reset_completed_calls = " +
                                  verification.ResetCompletedCalls);
                Console.WriteLine("EVIDENCE verify_start_calls           = " +
                                  verification.WorldLoadingCalls);
                Console.WriteLine("EVIDENCE verify_epoch_read_first      = " +
                                  verification.EpochReadFirst);
                Console.WriteLine("EVIDENCE verify_completion_calls      = " +
                                  verification.DayCompletedCalls);
                Console.WriteLine("EVIDENCE normal_returns               = " +
                                  verification.NormalReturns);
            }

            CheckBridgeFields(bridge, failures);
        }

        Report(failures);
        return failures.Count == 0 ? 0 : 1;
    }

    private static PrototypeHooks? ResolveBridgeHooks(TypeDefinition bridge)
    {
        var resetBegan = bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.ResetEntryHookName);
        var resetCompleted = bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.ResetExitHookName);
        var getEpoch = bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.ResetEpochGetterName);
        var worldLoading = bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.StartHookName);
        var dayCompleted = bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.CompletionHookName);
        var loadAdvanced = bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.AdvanceHookName);
        var loadEnumeratorCreated =
            bridge.Methods.FirstOrDefault(m => m.Name == PrototypeInjector.LoaderCreationHookName);
        if (resetBegan is null || resetCompleted is null || getEpoch is null || worldLoading is null ||
            dayCompleted is null || loadAdvanced is null || loadEnumeratorCreated is null)
        {
            return null;
        }

        return new PrototypeHooks(resetBegan, resetCompleted, getEpoch, worldLoading, dayCompleted,
            loadAdvanced, loadEnumeratorCreated);
    }

    private static void CheckBridgeFields(TypeDefinition bridge, List<string> failures)
    {
        foreach (var name in new[]
                 {
                     "ResetBeganCalls", "ResetCompletedCalls", "GetResetEpochCalls", "EpochCounter",
                     "LastResetEpochAtStart", "LastResetEpochOnExit", "WorldLoadingCalls",
                     "DayCompletedCalls", "LastLoadedGame", "LastHasLoadedGame", "LastGameMode",
                     "LastLoaderWasNull", "LoadEnumeratorCreatedCalls", "LastCreationResetEpoch",
                     "LastCreationGameMode", "LastCreationLoaderWasNull",
                 })
        {
            var field = bridge.Fields.FirstOrDefault(f => f.Name == name);
            if (field is null || !field.IsStatic)
            {
                failures.Add("bridge field missing or not static: " + name);
            }
        }
    }

    /// <summary>
    /// Cecil recomputes maxstack for every written method, so methods unrelated to the three hook
    /// sites can change their maxstack value. That is a metadata normalisation, not a semantic
    /// edit; it is listed separately and never counted as a changed method.
    /// </summary>
    private static void ReportMaxStackNormalisation(Dictionary<string, int> before,
        ModuleDefinition written, List<string> failures)
    {
        var after = PrototypeInjector.MaxStackMap(written);
        var normalised = new List<string>();
        foreach (var pair in before)
        {
            if (after.TryGetValue(pair.Key, out var value) && value != pair.Value)
            {
                normalised.Add(pair.Key + " " + pair.Value + "->" + value);
            }
        }

        normalised.Sort(StringComparer.Ordinal);
        Console.WriteLine("EVIDENCE maxstack_normalised_methods = " + normalised.Count);
        foreach (var item in normalised)
        {
            Console.WriteLine("EVIDENCE   maxstack: " + item);
        }

        foreach (var hooked in ExpectedChangedMethods)
        {
            if (before.TryGetValue(hooked, out var hookedBefore) &&
                after.TryGetValue(hooked, out var hookedAfter))
            {
                Console.WriteLine("EVIDENCE maxstack_hooked: " + hooked + " " + hookedBefore + "->" +
                                  hookedAfter);
            }
        }

        _ = failures;
    }

    private static void Report(List<string> failures)
    {
        if (failures.Count == 0)
        {
            Console.WriteLine("RESULT: PASS (static structure only; no execution, no APK, no device)");
            return;
        }

        foreach (var failure in failures)
        {
            Console.WriteLine("FAIL " + failure);
        }

        Console.WriteLine("RESULT: FAILED (" + failures.Count + " problems)");
    }

    private static ModuleDefinition ReadModule(string path, string? refsDirectory)
    {
        var resolver = new DefaultAssemblyResolver();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            resolver.AddSearchDirectory(directory);
        }

        if (!string.IsNullOrEmpty(refsDirectory) && Directory.Exists(refsDirectory))
        {
            resolver.AddSearchDirectory(refsDirectory);
        }

        return ModuleDefinition.ReadModule(path, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadingMode = ReadingMode.Immediate,
        });
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
