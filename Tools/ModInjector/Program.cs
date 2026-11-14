using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModInjector;

/// <summary>
/// Build-time IL injector for Stardew Valley 1.6.15.0 (Android).
///
///   ModInjector patch  &lt;in.dll&gt; &lt;out.dll&gt; [--hook-method &lt;name&gt;] [--loader-api loadfrom|load]
///                      [--refs-dir &lt;dir&gt;] [--mods-dir &lt;dir&gt;] [--runtime-dll &lt;name&gt;]
///                      [--loader-type &lt;ns.type&gt;] [--mode append] [--no-catch]
///   ModInjector verify &lt;in.dll&gt; [--hook-method &lt;name&gt;]
///
/// Note: this file's comments were corrupted by a PowerShell round-trip in Phase 0
/// (mojibake plus swallowed code lines). Do not rewrite it with Get-Content/Set-Content.
/// </summary>
public static class Program
{
    private const string TargetType = "StardewValley.MainActivity";
    private const string TargetMethod = "OnCreatePartTwo";

    private const string DefaultModsDir =
        "/sdcard/Android/data/com.chucklefish.stardewvalley/files/Mods";

    private const string DefaultRuntimeDll = "ModFramework.Runtime.dll";
    private const string DefaultLoaderType = "ModFramework.Runtime.Loader";
    private const string LoaderMethod = "Start";

    private const string BootNamespace = "ModFramework";
    private const string BootType = "Bootstrap";

    // ------------------------------------------------------------------ Phase 2.0 event bridge
    private const string EventDispatchType = "ModFramework.Runtime.EventDispatcher";
    private const string EventLaunchedMethod = "DispatchGameLaunched";
    private const string EventTickMethod = "DispatchTick";
    private const string BridgeGameLaunched = "GameLaunched";
    private const string BridgeTick = "Tick";
    private const string BridgeDiagnosticMethod = "WriteEventsDisabled";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        var command = args[0];
        var options = ParseOptions(args.Skip(1).ToArray());

        try
        {
            switch (command)
            {
                case "patch":
                    return Patch(options);
                case "verify":
                    return Verify(options);
                case "help":
                case "-h":
                case "--help":
                    return Usage();
                default:
                    Console.Error.WriteLine("[error] unknown command: " + command);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[error] " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("ModInjector - Stardew Valley Android build-time IL injection");
        Console.WriteLine();
        Console.WriteLine("  patch  <in.dll> <out.dll> [options]");
        Console.WriteLine("         Inserts Bootstrap.Start() at the head of MainActivity.OnCreatePartTwo");
        Console.WriteLine("         and merges a self-contained ModFramework.Bootstrap type into the module.");
        Console.WriteLine();
        Console.WriteLine("  verify <in.dll> [--hook-method <name>]");
        Console.WriteLine("         Checks bootstrap type, Start(), the hook call, the assembly-load API");
        Console.WriteLine("         baked into Start(), the Mods directory baked into Start(), and the MVID.");
        Console.WriteLine();
        Console.WriteLine("  verify options:");
        Console.WriteLine("    --hook-method <name>   method expected to carry the hook (default: OnCreatePartTwo)");
        Console.WriteLine("    --mods-dir <dir>       Mods directory Start() must have baked in");
        Console.WriteLine("                           (default: the external Mods root; a baked");
        Console.WriteLine("                           /data/data/... path fails unless --allow-private-mods-dir)");
        Console.WriteLine();
        Console.WriteLine("  patch options:");
        Console.WriteLine("    --hook-method <name>   method to hook (default: OnCreatePartTwo)");
        Console.WriteLine("    --mods-dir <dir>       Mods directory baked into Start()");
        Console.WriteLine("    --runtime-dll <name>   runtime assembly name (default: ModFramework.Runtime.dll)");
        Console.WriteLine("    --loader-type <ns.type>  loader type (default: ModFramework.Runtime.Loader)");
        Console.WriteLine("    --loader-api <api>     loadfrom (default) | load");
        Console.WriteLine("                           loadfrom = Assembly.LoadFrom(string)  <- the only API");
        Console.WriteLine("                           that exists in this trimmed runtime");
        Console.WriteLine("                           load     = Assembly.Load(byte[]), kept only to");
        Console.WriteLine("                           reproduce the Phase 0 failure (MissingMethodException)");
        Console.WriteLine("    --refs-dir <dir[;dir]> extra assembly search directories for Cecil");
        Console.WriteLine("                           (needed when <in.dll> is not next to the other 64 assemblies)");
        Console.WriteLine("    --mode append          diagnostic build: append marker + throw, no loader call");
        Console.WriteLine("    --no-catch             keep try/catch IL valid but rethrow (diagnosis builds)");
        Console.WriteLine("    --no-event-hooks       loader-only build: no GameRunner hooks and no event bridge");
        Console.WriteLine("                           (this is the Phase 2.0 B build / rollback artifact)");
        Console.WriteLine();
        Console.WriteLine("  verify options (in addition to the patch ones):");
        Console.WriteLine("    --no-event-hooks       require the assembly to have NO event hooks at all");
        Console.WriteLine();
        Console.WriteLine("  defaults: --mods-dir " + DefaultModsDir);
        Console.WriteLine("            --runtime-dll " + DefaultRuntimeDll);
        Console.WriteLine("            --loader-type " + DefaultLoaderType);
        Console.WriteLine("            --loader-api loadfrom");
        return 0;
    }

    // ------------------------------------------------------------------ patch

    private static int Patch(Dictionary<string, string> opts)
    {
        var (input, output) = Paths(opts, required: 2);
        var modsDir = opts.TryGetValue("mods-dir", out var m) ? m : DefaultModsDir;
        var runtimeDll = opts.TryGetValue("runtime-dll", out var r) ? r : DefaultRuntimeDll;
        var loaderType = opts.TryGetValue("loader-type", out var l) ? l : DefaultLoaderType;
        var minimal = opts.TryGetValue("mode", out var modeOpt) && modeOpt == "append";
        var noCatch = opts.ContainsKey("no-catch");
        // Assembly.Load(byte[]) does not exist in this ILLink-trimmed runtime: calling it makes
        // Bootstrap.Start() fail at JIT time, and its own catch handler swallows that failure, so
        // mods would silently never load. LoadFrom(string) is the only working entry point, so it
        // is the default; the byte[] form stays reachable for reproducing the Phase 0 failure.
        var loaderApi = opts.TryGetValue("loader-api", out var la) ? la.Trim().ToLowerInvariant() : "loadfrom";
        if (loaderApi != "loadfrom" && loaderApi != "load" && loaderApi != "bytes")
        {
            throw new ArgumentException("--loader-api must be loadfrom or load, got: " + la);
        }
        var useLoadFrom = loaderApi == "loadfrom";
        var eventHooks = opts.ContainsKey("event-hooks")
            ? ParseSwitch("event-hooks", opts["event-hooks"])
            : !opts.ContainsKey("no-event-hooks");
        if (minimal && eventHooks)
        {
            throw new ArgumentException(
                "--mode append is a diagnosis build of Bootstrap.Start() only; it cannot carry event hooks");
        }

        Console.WriteLine("[1/4] read    " + input);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
        if (opts.TryGetValue("refs-dir", out var refsDirs))
        {
            foreach (var dir in refsDirs.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Directory.Exists(dir.Trim()))
                {
                    resolver.AddSearchDirectory(dir.Trim());
                }
            }
        }
        Console.WriteLine("      loader-api = " + (useLoadFrom ? "loadfrom (Assembly.LoadFrom)" : "load (Assembly.Load(byte[]))"));
        Console.WriteLine("      event-hooks = " + (eventHooks ? "enabled (GameLaunched + Tick)" : "disabled (loader-only build)"));

        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadingMode = ReadingMode.Immediate,
        });

        var originalMvid = module.Mvid;
        var beforeRefs = module.AssemblyReferences.Select(x => x.FullName).ToHashSet();
        Console.WriteLine("      assembly=" + module.Assembly.Name.Name + " " + module.Assembly.Name.Version + " mvid=" + originalMvid);

        Console.WriteLine("[2/4] locate  " + TargetType + "::" + TargetMethod);
        var type = module.GetType(TargetType);
        if (type is null)
        {
            throw new InvalidOperationException("type not found: " + TargetType);
        }

        var hookMethodName = opts.TryGetValue("hook-method", out var hm) ? hm : TargetMethod;
        var target = type.Methods.FirstOrDefault(x => x.Name == hookMethodName && !x.IsStatic
                        && (x.Parameters.Count == 0
                            || (x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == "Android.OS.Bundle")))
                     ?? type.Methods.FirstOrDefault(x => x.Name == hookMethodName && !x.IsStatic);
        if (target is null)
        {
            throw new InvalidOperationException("method not found: " + hookMethodName);
        }

        Console.WriteLine("      hook target = " + target.FullName + " (" + target.Body.Instructions.Count + " instr)");

        var existing = module.GetType(BootNamespace, BootType);
        if (existing is not null)
        {
            Console.WriteLine("      bootstrap type already present (idempotent mode)");
            ApplyToExistingBootstrap(module, existing, target, hookMethodName, eventHooks, minimal);
        }
        else
        {
            var bootstrap = EmitBootstrap(module, modsDir, runtimeDll, loaderType, minimal, noCatch, useLoadFrom, eventHooks);
            InsertCall(target, bootstrap, LoaderMethod);
            if (eventHooks)
            {
                InsertEventHooks(module);
            }
        }

        // a modified assembly must carry a new MVID, otherwise the stale AOT image is reused
        module.Mvid = Guid.NewGuid();
        if (module.Mvid == originalMvid)
        {
            // Defensive: this exact silent failure happened once (an edit commented the
            // assignment out), and it made injected IL look "not executed" on device.
            throw new InvalidOperationException(
                "MVID did not change; the patched assembly would reuse the stale AOT image");
        }

        Console.WriteLine("[3/4] write   " + output);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        module.Write(output);

        // Re-read what was actually written and validate it: this is the gate that would have
        // caught the three silent IL-generation bugs this tool already produced.
        using (var written = ModuleDefinition.ReadModule(output))
        {
            var boot = written.GetType(BootNamespace, BootType)
                       ?? throw new InvalidOperationException("bootstrap type missing after write");
            var startMethod = boot.Methods.FirstOrDefault(x => x.Name == LoaderMethod)
                              ?? throw new InvalidOperationException("bootstrap Start() missing after write");
            var hooked = written.GetType(TargetType)?.Methods.FirstOrDefault(x => x.Name == hookMethodName && !x.IsStatic)
                         ?? throw new InvalidOperationException("hook method missing after write");
            IlChecks.Validate(written, startMethod, hooked, originalMvid, eventHooks);
        }

        var addedRefs = module.AssemblyReferences
            .Where(x => !beforeRefs.Contains(x.FullName))
            .Select(x => x.FullName)
            .ToList();

        Console.WriteLine("[4/4] self-check");
        Console.WriteLine("      old mvid = " + originalMvid);
        Console.WriteLine("      new mvid = " + module.Mvid);
        Console.WriteLine("      added AssemblyRef = " + (addedRefs.Count == 0 ? "(none)" : string.Join(", ", addedRefs)));
        Console.WriteLine("      output bytes = " + new FileInfo(output).Length.ToString("N0"));

        var finalCheck = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["_0"] = output,
            ["hook-method"] = hookMethodName,
            ["mods-dir"] = modsDir,
            ["runtime-dll"] = runtimeDll,
        };
        if (!eventHooks)
        {
            finalCheck["no-event-hooks"] = "true";
        }
        if (!useLoadFrom)
        {
            finalCheck["allow-bytes-api"] = "true";
        }
        return Verify(finalCheck);
    }

    private static bool ParseSwitch(string name, string value)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "enabled":
            case "on":
            case "true":
            case "1":
                return true;
            case "disabled":
            case "off":
            case "false":
            case "0":
                return false;
            default:
                throw new ArgumentException("--" + name + " must be enabled or disabled, got: " + value);
        }
    }

    /// <summary>
    /// Inserts the two event hooks into the real game methods.
    ///
    /// Both targets are resolved by exact signature (type, name, return type and parameter list),
    /// never by "the first method with that name", and both insertions go through
    /// <see cref="GameMethodInjector"/> so branch, switch, leave and handler boundaries are
    /// re-targeted instead of being skipped.
    /// </summary>
    private static void InsertEventHooks(ModuleDefinition module)
    {
        var boot = module.GetType(BootNamespace, BootType)
                   ?? throw new InvalidOperationException("bootstrap type missing before event injection");
        var launched = boot.Methods.FirstOrDefault(m => m.Name == BridgeGameLaunched && m.IsStatic && m.Parameters.Count == 0)
                       ?? throw new InvalidOperationException("bridge method missing: " + BridgeGameLaunched);
        var tick = boot.Methods.FirstOrDefault(m => m.Name == BridgeTick && m.IsStatic && m.Parameters.Count == 0)
                   ?? throw new InvalidOperationException("bridge method missing: " + BridgeTick);

        var initialize = GameMethodInjector.RequireMethod(
            module, GameMethodInjector.GameRunnerType, GameMethodInjector.InitializeMethod, "System.Void");
        var update = GameMethodInjector.RequireMethod(
            module, GameMethodInjector.GameRunnerType, GameMethodInjector.UpdateMethod,
            "System.Void", GameMethodInjector.GameTimeType);

        Console.WriteLine("      event target (completion) = " + initialize.FullName);
        var launches = GameMethodInjector.InjectCompletionHook(initialize, launched);
        Console.WriteLine("      GameLaunched call sites    = " + launches + " (every normal ret)");

        Console.WriteLine("      event target (entry)      = " + update.FullName);
        var ticks = GameMethodInjector.InjectEntryHook(update, tick);
        Console.WriteLine("      Tick call sites           = " + ticks + " (method entry)");
    }

    /// <summary>
    /// Idempotency / migration handling for an input that already contains
    /// <c>ModFramework.Bootstrap</c>.
    ///
    /// The rule is "same shape or refuse": a loader-only bootstrap cannot silently gain event
    /// hooks, an event-capable bootstrap cannot silently lose them, a duplicate or misplaced hook
    /// is a build failure, and a partially hooked method is refused instead of being completed.
    /// That is what keeps a re-patched assembly from becoming a mixed state nobody can reason
    /// about.
    /// </summary>
    private static void ApplyToExistingBootstrap(
        ModuleDefinition module,
        TypeDefinition existing,
        MethodDefinition target,
        string hookMethodName,
        bool eventHooks,
        bool minimal)
    {
        if (minimal)
        {
            throw new InvalidOperationException(
                "--mode append cannot be applied to an assembly that already contains ModFramework.Bootstrap");
        }

        var start = existing.Methods.FirstOrDefault(m => m.Name == LoaderMethod)
                    ?? throw new InvalidOperationException("existing ModFramework.Bootstrap has no Start()");

        var hasLaunched = existing.Methods.Any(m => m.Name == BridgeGameLaunched);
        var hasTick = existing.Methods.Any(m => m.Name == BridgeTick);
        if (eventHooks && (!hasLaunched || !hasTick))
        {
            throw new InvalidOperationException(
                "existing ModFramework.Bootstrap is a loader-only build (no event bridge); refusing to " +
                "produce a mixed state. Rebuild from the pristine StardewValley.dll instead of patching " +
                "an already-patched assembly.");
        }

        if (!eventHooks && (hasLaunched || hasTick))
        {
            throw new InvalidOperationException(
                "existing ModFramework.Bootstrap carries the event bridge but --no-event-hooks was " +
                "requested; refusing to produce a mixed state. Rebuild from the pristine " +
                "StardewValley.dll instead of patching an already-patched assembly.");
        }

        var startCalls = GameMethodInjector.CountCalls(target, BootNamespace + "." + BootType, LoaderMethod);
        if (startCalls > 1)
        {
            throw new InvalidOperationException(
                "duplicate Bootstrap.Start() hook calls in " + target.FullName + " (count=" + startCalls + ")");
        }

        if (startCalls == 0)
        {
            InsertCall(target, start, LoaderMethod);
        }
        else
        {
            Console.WriteLine("      Start hook call already present (" + hookMethodName + ")");
        }

        if (!eventHooks)
        {
            return;
        }

        var launched = existing.Methods.First(m => m.Name == BridgeGameLaunched);
        var tick = existing.Methods.First(m => m.Name == BridgeTick);
        var initialize = GameMethodInjector.RequireMethod(
            module, GameMethodInjector.GameRunnerType, GameMethodInjector.InitializeMethod, "System.Void");
        var update = GameMethodInjector.RequireMethod(
            module, GameMethodInjector.GameRunnerType, GameMethodInjector.UpdateMethod,
            "System.Void", GameMethodInjector.GameTimeType);

        EnsureSingleHook(initialize, launched, BridgeGameLaunched, completion: true);
        EnsureSingleHook(update, tick, BridgeTick, completion: false);
    }

    private static void EnsureSingleHook(
        MethodDefinition method,
        MethodDefinition bridge,
        string name,
        bool completion)
    {
        var typeName = BootNamespace + "." + BootType;
        var count = GameMethodInjector.CountCalls(method, typeName, name);
        if (count > 1)
        {
            throw new InvalidOperationException(
                "duplicate " + name + " hook calls in " + method.FullName + " (count=" + count + ")");
        }

        if (count == 1)
        {
            var complete = completion
                ? GameMethodInjector.HasCompleteCompletionHook(method, typeName, name)
                : GameMethodInjector.HasEntryHook(method, typeName, name);
            if (!complete)
            {
                throw new InvalidOperationException(
                    "partial or misplaced " + name + " hook in " + method.FullName +
                    "; refusing to complete a partially patched method");
            }

            Console.WriteLine("      " + name + " hook already present and complete");
            return;
        }

        if (completion)
        {
            GameMethodInjector.InjectCompletionHook(method, bridge);
        }
        else
        {
            GameMethodInjector.InjectEntryHook(method, bridge);
        }

        Console.WriteLine("      inserted missing " + name + " hook");
    }

    /// <summary>
    /// Imports a BCL method but retargets its declaring type's scope to this module's own
    /// corelib reference (on net8.0 that is usually the <c>System.Runtime</c> facade).
    ///
    /// Why: <c>Cecil.ImportReference</c> builds an AssemblyRef from the *desktop* runtime's
    /// definition assembly (<c>System.Private.CoreLib</c> with PublicKeyToken=7cec85d7bea7798e).
    /// Android's facade does not carry that identity, so the call fails at runtime with
    /// <c>MissingMethodException</c>. Reusing the module's existing corelib reference fixes it.
    /// </summary>
    private static MethodReference ImportCoreMethod(ModuleDefinition module, System.Reflection.MethodInfo mi, IMetadataScope scope)
    {
        var reference = module.ImportReference(mi);
        RetargetToModuleCore(reference.DeclaringType, scope);
        foreach (var p in reference.Parameters)
        {
            RetargetToModuleCore(p.ParameterType, scope);
        }
        RetargetToModuleCore(reference.ReturnType, scope);
        return reference;
    }

    private static MethodReference ImportCoreCtor(ModuleDefinition module, System.Reflection.ConstructorInfo ci, IMetadataScope scope)
    {
        var reference = module.ImportReference(ci);
        RetargetToModuleCore(reference.DeclaringType, scope);
        return reference;
    }

    private static TypeReference ImportCoreType(ModuleDefinition module, Type type, IMetadataScope scope)
    {
        var reference = module.ImportReference(type);
        RetargetToModuleCore(reference, scope);
        return reference;
    }

    private static IMetadataScope PickCoreScope(ModuleDefinition module)
    {
        // Do NOT point the scope at System.Private.CoreLib as imported by Cecil from the
        // desktop runtime: pick an existing facade reference from the module itself.
        foreach (var name in new[] { "System.Runtime", "netstandard", "mscorlib" })
        {
            var reference = module.AssemblyReferences.FirstOrDefault(a => a.Name == name);
            if (reference is not null)
            {
                Console.WriteLine($"      core scope = {reference.FullName}");
                return reference;
            }
        }

        Console.WriteLine($"      core scope = {module.TypeSystem.CoreLibrary} (fallback)");
        return module.TypeSystem.CoreLibrary;
    }

    private static void RetargetToModuleCore(TypeReference? type, IMetadataScope scope)
    {
        if (type is null)
        {
            return;
        }

        if (type is TypeSpecification spec)
        {
            RetargetToModuleCore(spec.ElementType, scope);
            return;
        }

        if (type.Scope is AssemblyNameReference asm
            && asm.Name is "System.Private.CoreLib" or "mscorlib" or "System.Runtime" or "netstandard")
        {
            type.Scope = scope;
        }

        RetargetToModuleCore(type.DeclaringType, scope);
    }

    /// <summary>
    /// Emits a self-contained <c>ModFramework.Bootstrap.Start()</c> into the game module.
    /// Returns void; every failure is either thrown (with a stage tag) or swallowed by the
    /// method's own catch handler, so a broken mod setup can never stop the game from starting.
    /// </summary>
    private static MethodDefinition EmitBootstrap(ModuleDefinition module, string modsDir, string runtimeDll, string loaderType, bool minimal, bool noCatch, bool useLoadFrom, bool eventHooks)
    {
        var attrs = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
                    | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
        var bootstrap = new TypeDefinition(BootNamespace, BootType, attrs, module.TypeSystem.Object);
        module.Types.Add(bootstrap);

        var start = new MethodDefinition(LoaderMethod,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
        bootstrap.Methods.Add(start);
        start.Body.InitLocals = true;

        // minimal mode: one AppendAllText call, used to test cross-assembly BCL calls
        if (minimal)
        {
            var minimalScope = PickCoreScope(module);
            var append = ImportCoreMethod(module,
                typeof(File).GetMethod(nameof(File.AppendAllText), new[] { typeof(string), typeof(string) })!,
                minimalScope);
            var marker = "/data/data/com.chucklefish.stardewvalley/files/bootstrap-marker.txt";
            var mil = start.Body.GetILProcessor();
            var afterTry = mil.Create(OpCodes.Ldstr, "bootstrap-ran");
            mil.Emit(OpCodes.Ldstr, marker);
            mil.Emit(OpCodes.Ldstr, "bootstrap-marker" + Environment.NewLine);
            mil.Emit(OpCodes.Call, append);
            mil.Emit(OpCodes.Leave, afterTry);
            var mhandler = mil.Create(OpCodes.Pop);
            mil.Append(mhandler);
            mil.Emit(OpCodes.Leave, afterTry);
            // 无论写没写成，都抛一个可识别的异常：用于证明 Start() 确实被调用过。
            mil.Append(afterTry);
            mil.Emit(OpCodes.Newobj, ImportCoreCtor(module,
                typeof(Exception).GetConstructor(new[] { typeof(string) })!, minimalScope));
            mil.Emit(OpCodes.Throw);
            if (!noCatch)
        start.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = start.Body.Instructions[0],
                TryEnd = mhandler,
                HandlerStart = mhandler,
                HandlerEnd = afterTry,
                CatchType = ImportCoreType(module, typeof(Exception), minimalScope),
            });
            return start;
        }

        var scope = PickCoreScope(module);
        var fileExists = ImportCoreMethod(module, typeof(File).GetMethod(nameof(File.Exists), new[] { typeof(string) })!, scope);
        // Only imported for the legacy byte[] path; an unused import would still add a MemberRef.
        var readAllBytes = useLoadFrom
            ? null
            : ImportCoreMethod(module, typeof(File).GetMethod(nameof(File.ReadAllBytes), new[] { typeof(string) })!, scope);
        // Default: Assembly.LoadFrom(string). Assembly.Load(byte[]) is absent from this
        // trimmed runtime (MissingMethodException at JIT time) and is kept only behind
        // --loader-api load so the Phase 0 failure stays reproducible.
        var asmLoad = useLoadFrom
            ? ImportCoreMethod(module, typeof(System.Reflection.Assembly)
                .GetMethod("LoadFrom", new[] { typeof(string) })!, scope)
            : ImportCoreMethod(module, typeof(System.Reflection.Assembly)
                .GetMethod(nameof(System.Reflection.Assembly.Load), new[] { typeof(byte[]) })!, scope);
        // Assembly.GetType(string) is the instance method we need here; Type.GetType(string)
        // is static and would leave the Assembly on the stack (caught by IlChecks).
        var getType = ImportCoreMethod(module, typeof(System.Reflection.Assembly)
            .GetMethod(nameof(System.Reflection.Assembly.GetType), new[] { typeof(string) })!, scope);
        var getMethod = ImportCoreMethod(module, typeof(Type).GetMethod(nameof(Type.GetMethod), new[] { typeof(string) })!, scope);
        var invoke = ImportCoreMethod(module, typeof(System.Reflection.MethodBase)
            .GetMethod(nameof(System.Reflection.MethodBase.Invoke), new[] { typeof(object), typeof(object[]) })!, scope);

        var path = Path.Combine(modsDir, runtimeDll).Replace('\\', '/');
        var il = start.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        var asmLocal = new VariableDefinition(ImportCoreType(module, typeof(System.Reflection.Assembly), scope));
        var typeLocal = new VariableDefinition(ImportCoreType(module, typeof(Type), scope));
        var methodLocal = new VariableDefinition(ImportCoreType(module, typeof(System.Reflection.MethodInfo), scope));
        start.Body.Variables.Add(asmLocal);
        start.Body.Variables.Add(typeLocal);
        start.Body.Variables.Add(methodLocal);

        // Each stage throws a distinct message so one deploy tells us exactly where it stops.
        var exCtor = ImportCoreCtor(module, typeof(Exception).GetConstructor(new[] { typeof(string) })!, scope);

        // stage-1: Mods/ModFramework.Runtime.dll must exist
        il.Emit(OpCodes.Ldstr, path);
        il.Emit(OpCodes.Call, fileExists);
        EmitThrowUnlessTrue(il, exCtor, "stage-1:file-missing");

        // stage-2: assembly load (LoadFrom or Load(byte[]))
        il.Emit(OpCodes.Ldstr, path);
        if (!useLoadFrom)
        {
            il.Emit(OpCodes.Call, readAllBytes!);
        }
        il.Emit(OpCodes.Call, asmLoad);
        il.Emit(OpCodes.Stloc, asmLocal);
        il.Emit(OpCodes.Ldloc, asmLocal);
        EmitThrowUnlessTrue(il, exCtor, "stage-2:assembly-null");

        // stage-3: asm.GetType("ModFramework.Runtime.Loader")
        il.Emit(OpCodes.Ldloc, asmLocal);
        il.Emit(OpCodes.Ldstr, loaderType);
        il.Emit(OpCodes.Callvirt, getType);
        il.Emit(OpCodes.Stloc, typeLocal);
        il.Emit(OpCodes.Ldloc, typeLocal);
        EmitThrowUnlessTrue(il, exCtor, "stage-3:type-null");

        // stage-4: t.GetMethod("Start")
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, LoaderMethod);
        il.Emit(OpCodes.Callvirt, getMethod);
        il.Emit(OpCodes.Stloc, methodLocal);
        il.Emit(OpCodes.Ldloc, methodLocal);
        EmitThrowUnlessTrue(il, exCtor, "stage-4:method-null");

        // stage-5: m.Invoke(null, null)
        // Loader.Start() returns null on success, or a diagnostic string on failure; a non-null
        // result is turned into a thrown Exception so the message survives TargetInvocationException.
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Castclass, module.TypeSystem.String);
        var afterInvoke = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, afterInvoke);
        il.Emit(OpCodes.Newobj, exCtor);
        il.Emit(OpCodes.Throw);
        il.Append(afterInvoke);
        il.Emit(OpCodes.Pop);

        // stage-6 (Phase 2.0): bind the event dispatcher delegates once. This runs after the
        // loader call, inside the same try block, so a missing dispatcher (older Runtime) or a
        // failed binding leaves the game startable and writes one bounded diagnostic line.
        if (eventHooks)
        {
            EmitEventBridge(module, bootstrap, il, scope, asmLocal, typeLocal, methodLocal, ret, modsDir);
        }

        il.Emit(OpCodes.Leave, ret);

        // catch (Exception) { pop; leave ret; }  -- normal mode: swallow
        // catch (Exception) { rethrow; }         -- noCatch mode: keep valid IL, but surface the error
        var handlerStart = il.Create(noCatch ? OpCodes.Rethrow : OpCodes.Pop);
        il.Append(handlerStart);
        if (!noCatch)
        {
            il.Emit(OpCodes.Leave, ret);
        }
        il.Append(ret);

        start.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = start.Body.Instructions[0],
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = ret,
            CatchType = ImportCoreType(module, typeof(Exception), scope),
        });

        return start;
    }

    /// <summary>
    /// Emits the event side of <c>ModFramework.Bootstrap</c>:
    ///
    ///   1. two private static <c>System.Action</c> fields that hold the dispatcher delegates;
    ///   2. <c>GameLaunched()</c> and <c>Tick()</c> - a null check plus one cached virtual call,
    ///      each wrapped in its own catch so the game loop can never be broken by the bridge;
    ///   3. <c>WriteEventsDisabled(reason)</c> - a one-line, best-effort diagnostic file writer;
    ///   4. the binding IL appended to <c>Start()</c> that resolves
    ///      <c>ModFramework.Runtime.EventDispatcher</c> by name, turns its two static methods into
    ///      delegates via <c>Delegate.CreateDelegate</c>, and caches them.
    ///
    /// There is deliberately <b>no</b> static AssemblyRef to <c>ModFramework.Runtime</c>: every
    /// external type used here (<c>Action</c>, <c>Delegate</c>, <c>Type</c>, <c>Assembly</c>,
    /// <c>File</c>, <c>String</c>) comes from the module's existing corelib reference, so the game
    /// assembly still resolves when the runtime assembly is missing.
    /// </summary>
    private static void EmitEventBridge(
        ModuleDefinition module,
        TypeDefinition bootstrap,
        ILProcessor il,
        IMetadataScope scope,
        VariableDefinition asmLocal,
        VariableDefinition typeLocal,
        VariableDefinition methodLocal,
        Instruction ret,
        string modsDir)
    {
        var actionType = ImportCoreType(module, typeof(Action), scope);
        var launchedField = new FieldDefinition("s_onGameLaunched",
            FieldAttributes.Private | FieldAttributes.Static, actionType);
        var tickField = new FieldDefinition("s_onTick",
            FieldAttributes.Private | FieldAttributes.Static, actionType);
        bootstrap.Fields.Add(launchedField);
        bootstrap.Fields.Add(tickField);

        var diagnostic = EmitEventsDisabledWriter(module, bootstrap, scope, modsDir);
        EmitBridgeMethod(module, bootstrap, BridgeGameLaunched, launchedField, scope);
        EmitBridgeMethod(module, bootstrap, BridgeTick, tickField, scope);

        var delegateCreate = ImportCoreMethod(module, typeof(Delegate)
            .GetMethod("CreateDelegate", new[] { typeof(Type), typeof(System.Reflection.MethodInfo) })!, scope);
        var typeFromHandle = ImportCoreMethod(module, typeof(Type)
            .GetMethod(nameof(Type.GetTypeFromHandle), new[] { typeof(RuntimeTypeHandle) })!, scope);
        var getType = ImportCoreMethod(module, typeof(System.Reflection.Assembly)
            .GetMethod(nameof(System.Reflection.Assembly.GetType), new[] { typeof(string) })!, scope);
        var getMethod = ImportCoreMethod(module, typeof(Type)
            .GetMethod(nameof(Type.GetMethod), new[] { typeof(string) })!, scope);

        var afterDispatcher = il.Create(OpCodes.Nop);
        var afterLaunched = il.Create(OpCodes.Nop);
        var afterTick = il.Create(OpCodes.Nop);

        // Dispatcher type: asm.GetType("ModFramework.Runtime.EventDispatcher")
        il.Emit(OpCodes.Ldloc, asmLocal);
        il.Emit(OpCodes.Ldstr, EventDispatchType);
        il.Emit(OpCodes.Callvirt, getType);
        il.Emit(OpCodes.Stloc, typeLocal);
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brtrue, afterDispatcher);
        EmitDisabledExit(il, diagnostic, ret, "events-disabled:dispatcher-type-missing");
        il.Append(afterDispatcher);

        // GameLaunched entry point
        EmitBindField(module, il, typeLocal, methodLocal, actionType, delegateCreate,
            typeFromHandle, getMethod, launchedField, EventLaunchedMethod,
            "events-disabled:game-launched-entry-missing", diagnostic, ret, afterLaunched);

        // Tick entry point
        EmitBindField(module, il, typeLocal, methodLocal, actionType, delegateCreate,
            typeFromHandle, getMethod, tickField, EventTickMethod,
            "events-disabled:tick-entry-missing", diagnostic, ret, afterTick);
    }

    private static void EmitBindField(
        ModuleDefinition module,
        ILProcessor il,
        VariableDefinition typeLocal,
        VariableDefinition methodLocal,
        TypeReference actionType,
        MethodReference delegateCreate,
        MethodReference typeFromHandle,
        MethodReference getMethod,
        FieldDefinition field,
        string entryName,
        string diagnosticReason,
        MethodDefinition diagnostic,
        Instruction ret,
        Instruction okTarget)
    {
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, entryName);
        il.Emit(OpCodes.Callvirt, getMethod);
        il.Emit(OpCodes.Stloc, methodLocal);
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Brtrue, okTarget);
        EmitDisabledExit(il, diagnostic, ret, diagnosticReason);
        il.Append(okTarget);

        il.Emit(OpCodes.Ldtoken, actionType);
        il.Emit(OpCodes.Call, typeFromHandle);
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Call, delegateCreate);
        il.Emit(OpCodes.Castclass, actionType);
        il.Emit(OpCodes.Stsfld, field);
    }

    private static void EmitDisabledExit(
        ILProcessor il,
        MethodDefinition diagnostic,
        Instruction ret,
        string reason)
    {
        il.Emit(OpCodes.Ldstr, reason);
        il.Emit(OpCodes.Call, diagnostic);
        il.Emit(OpCodes.Leave, ret);
    }

    /// <summary>Emits <c>private static void WriteEventsDisabled(string reason)</c>.</summary>
    private static MethodDefinition EmitEventsDisabledWriter(
        ModuleDefinition module,
        TypeDefinition bootstrap,
        IMetadataScope scope,
        string modsDir)
    {
        var method = new MethodDefinition(BridgeDiagnosticMethod,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        method.Parameters.Add(new ParameterDefinition("reason", ParameterAttributes.None,
            module.TypeSystem.String));
        bootstrap.Methods.Add(method);

        var append = ImportCoreMethod(module, typeof(File)
            .GetMethod(nameof(File.AppendAllText), new[] { typeof(string), typeof(string) })!, scope);
        var concat = ImportCoreMethod(module, typeof(string)
            .GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) })!, scope);

        var path = Path.Combine(modsDir, "bootstrap-events.txt").Replace('\\', '/');
        var il = method.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        var handler = il.Create(OpCodes.Pop);

        il.Emit(OpCodes.Ldstr, path);
        il.Emit(OpCodes.Ldstr, "events-disabled: ");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Ldstr, " (see loader-report.txt)\n");
        il.Emit(OpCodes.Call, concat);
        il.Emit(OpCodes.Call, append);
        il.Emit(OpCodes.Leave, ret);
        il.Append(handler);
        il.Emit(OpCodes.Leave, ret);
        il.Append(ret);

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = method.Body.Instructions[0],
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = ret,
            CatchType = ImportCoreType(module, typeof(Exception), scope),
        });
        return method;
    }

    /// <summary>
    /// Emits one bridge entry point:
    /// <code>
    /// if (field == null) return;
    /// try { field(); } catch (Exception) { }
    /// </code>
    /// Cheap enough for a per-tick call (one static field read and one cached delegate call) and
    /// still safe when the dispatcher itself throws.
    /// </summary>
    private static MethodDefinition EmitBridgeMethod(
        ModuleDefinition module,
        TypeDefinition bootstrap,
        string name,
        FieldDefinition field,
        IMetadataScope scope)
    {
        var method = new MethodDefinition(name,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            module.TypeSystem.Void);
        bootstrap.Methods.Add(method);

        var invoke = ImportCoreMethod(module, typeof(Action).GetMethod(nameof(Action.Invoke))!, scope);
        var il = method.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        var call = il.Create(OpCodes.Ldsfld, field);
        var handler = il.Create(OpCodes.Pop);

        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Brtrue, call);
        il.Emit(OpCodes.Ret);
        il.Append(call);
        il.Emit(OpCodes.Callvirt, invoke);
        il.Emit(OpCodes.Leave, ret);
        il.Append(handler);
        il.Emit(OpCodes.Leave, ret);
        il.Append(ret);

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = call,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = ret,
            CatchType = ImportCoreType(module, typeof(Exception), scope),
        });
        return method;
    }

    /// <summary>
    /// 消费栈顶的 bool：为 true 继续执行，否则抛 <paramref name="message"/>。
    /// 用于把"静默 return"改成"带阶段号的异常"，让一次部署就能定位卡点。
    /// </summary>
    private static void EmitThrowUnlessTrue(ILProcessor il, MethodReference exceptionCtor, string message)
    {
        var ok = il.Create(OpCodes.Nop);
        il.Emit(OpCodes.Brtrue, ok);
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, exceptionCtor);
        il.Emit(OpCodes.Throw);
        il.Append(ok);
    }

    private static void InsertCall(MethodDefinition target, MethodDefinition start, string name)
    {
        var il = target.Body.GetILProcessor();
        var first = target.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Call, start));
        // Inserting five bytes moves every instruction after the insertion point; short branches
        // are widened so no displacement can silently overflow the signed byte they encode.
        GameMethodInjector.WidenShortBranches(target);
        Console.WriteLine("      inserted call at head -> " + start.DeclaringType.FullName + "::" + name + "()");
    }

    // ----------------------------------------------------------------- verify

    private static int Verify(Dictionary<string, string> opts)
    {
        var (input, _) = Paths(opts, required: 1);
        using var module = ModuleDefinition.ReadModule(input);
        var ok = true;

        var bootstrap = module.GetType(BootNamespace, BootType);
        Console.WriteLine("bootstrap-type : " + (bootstrap is null ? "MISSING" : "present"));
        ok &= bootstrap is not null;

        var start = bootstrap?.Methods.FirstOrDefault(x => x.Name == LoaderMethod);
        Console.WriteLine("bootstrap-start: " + (start is null
            ? "MISSING"
            : "present (" + start.Body.Instructions.Count + " instr, " + start.Body.CodeSize + " bytes)"));
        ok &= start is not null;

        var hookName = opts.TryGetValue("hook-method", out var hn) ? hn : TargetMethod;
        var target = module.GetType(TargetType)?.Methods.FirstOrDefault(x => x.Name == hookName && !x.IsStatic);
        Console.WriteLine("hook-target    : " + (target is null ? "MISSING " + hookName : TargetType + "::" + target.Name));
        ok &= target is not null;

        var call = target?.Body.Instructions.FirstOrDefault(x => x.OpCode == OpCodes.Call
            && x.Operand is MethodReference mr
            && mr.DeclaringType.FullName == BootNamespace + "." + BootType);
        Console.WriteLine("hook-call      : " + (call is null ? "MISSING" : "present at IL_" + call.Offset.ToString("x4")));
        ok &= call is not null;

        // Which assembly-load API did Start() bake in? Default builds must use LoadFrom(string);
        // Load(byte[]) is absent from this runtime and would fail at JIT time.
        var loadApi = "(none)";
        if (start is not null)
        {
            foreach (var ins in start.Body.Instructions)
            {
                if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt)
                    && ins.Operand is MethodReference m
                    && m.DeclaringType.FullName == "System.Reflection.Assembly")
                {
                    if (m.Name == "LoadFrom")
                    {
                        loadApi = "loadfrom (Assembly.LoadFrom(string))";
                    }
                    else if (m.Name == "Load")
                    {
                        var p0 = m.Parameters.Count > 0 ? m.Parameters[0].ParameterType.FullName : "?";
                        loadApi = (p0 == "System.Byte[]" ? "load (Assembly.Load(byte[]))" : "load (" + p0 + ")");
                    }
                }
            }
        }
        Console.WriteLine("loader-api     : " + loadApi);
        var allowBytes = opts.ContainsKey("allow-bytes-api");
        ok &= allowBytes || loadApi.StartsWith("loadfrom", StringComparison.Ordinal);
        if (allowBytes && !loadApi.StartsWith("loadfrom", StringComparison.Ordinal))
        {
            Console.WriteLine("                 (byte[] API accepted only because --allow-bytes-api was given;");
            Console.WriteLine("                  such a build cannot load mods on this runtime)");
        }

        var firstReal = target?.Body.Instructions.FirstOrDefault(x => x.OpCode != OpCodes.Nop);
        var isFirst = firstReal is not null && ReferenceEquals(firstReal, call);
        Console.WriteLine("hook-is-first  : " + (isFirst ? "yes" : "no"));
        ok &= isFirst;

        // Which mods directory did Start() bake in? Bootstrap and Loader have to agree on one
        // root, and the shipped build has to use the external Mods directory: the app-private
        // files/Mods is not where the injected path points, so a fallback there would make the
        // loader read a directory Bootstrap never populated.
        var runtimeDll = opts.TryGetValue("runtime-dll", out var rd) ? rd : DefaultRuntimeDll;
        var expectedModsDir = opts.TryGetValue("mods-dir", out var emd) ? emd : DefaultModsDir;
        var bakedModsDir = "(none)";
        if (start is not null)
        {
            var suffix = "/" + runtimeDll;
            foreach (var ins in start.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldstr
                    && ins.Operand is string s
                    && s.EndsWith(suffix, StringComparison.Ordinal))
                {
                    bakedModsDir = s.Substring(0, s.Length - suffix.Length);
                }
            }
        }
        Console.WriteLine("mods-dir       : " + bakedModsDir);
        Console.WriteLine("mods-dir-expect: " + expectedModsDir);
        var modsDirOk = string.Equals(bakedModsDir, expectedModsDir, StringComparison.Ordinal);
        Console.WriteLine("mods-dir-ok    : " + (modsDirOk ? "yes" : "no"));
        ok &= modsDirOk;

        // The private directory is only ever accepted when a caller explicitly asks for it,
        // so a default build cannot silently bake the wrong root.
        if (!opts.ContainsKey("allow-private-mods-dir")
            && bakedModsDir.StartsWith("/data/data/", StringComparison.Ordinal))
        {
            Console.WriteLine("                 (the app-private files/Mods is rejected by default;");
            Console.WriteLine("                  pass --allow-private-mods-dir only for a diagnosis build)");
            ok = false;
        }

        Console.WriteLine("mvid           : " + module.Mvid);

        // ---------------------------------------------------------------- Phase 2.0 event hooks
        //
        // "Should be there" and "should not be there" are both verified: a loader-only build must
        // not carry a single event hook, and an event build must carry exactly one GameLaunched
        // call per normal return of GameRunner.Initialize plus exactly one Tick call at the head of
        // GameRunner.Update(GameTime).
        var eventHooks = opts.ContainsKey("event-hooks")
            ? ParseSwitch("event-hooks", opts["event-hooks"])
            : !opts.ContainsKey("no-event-hooks");

        var bridgeLaunched = bootstrap?.Methods.FirstOrDefault(x => x.Name == BridgeGameLaunched);
        var bridgeTick = bootstrap?.Methods.FirstOrDefault(x => x.Name == BridgeTick);

        MethodDefinition? initialize = null;
        MethodDefinition? update = null;
        try
        {
            initialize = GameMethodInjector.RequireMethod(module, GameMethodInjector.GameRunnerType,
                GameMethodInjector.InitializeMethod, "System.Void");
            update = GameMethodInjector.RequireMethod(module, GameMethodInjector.GameRunnerType,
                GameMethodInjector.UpdateMethod, "System.Void", GameMethodInjector.GameTimeType);
        }
        catch (InvalidOperationException)
        {
            // Reported below as missing hooks; a pristine game assembly has both methods.
        }

        var launchedCalls = initialize is null ? -1 : GameMethodInjector.CountCalls(initialize,
            BootNamespace + "." + BootType, BridgeGameLaunched);
        var tickCalls = update is null ? -1 : GameMethodInjector.CountCalls(update,
            BootNamespace + "." + BootType, BridgeTick);
        var updateHead = update?.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop);

        Console.WriteLine("event-hooks    : " + (eventHooks ? "enabled" : "disabled"));
        Console.WriteLine("bridge-game-launched: " + (bridgeLaunched is null ? "absent" : "present"));
        Console.WriteLine("bridge-tick    : " + (bridgeTick is null ? "absent" : "present"));
        Console.WriteLine("game-launched-hook: " + (launchedCalls == 1
            ? "present (1 normal-completion call)"
            : launchedCalls == 0 ? "absent (0 calls)"
            : launchedCalls < 0 ? "MISSING (GameRunner.Initialize not found)"
            : "WRONG count=" + launchedCalls));
        Console.WriteLine("tick-hook      : " + (tickCalls == 1
            ? "present (entry call)"
            : tickCalls == 0 ? "absent (0 calls)"
            : tickCalls < 0 ? "MISSING (GameRunner.Update(GameTime) not found)"
            : "WRONG count=" + tickCalls));
        if (updateHead is not null)
        {
            Console.WriteLine("tick-is-first  : " + (updateHead.OpCode == OpCodes.Call &&
                updateHead.Operand is MethodReference tr &&
                tr.DeclaringType.FullName == BootNamespace + "." + BootType &&
                tr.Name == BridgeTick ? "yes" : "no"));
        }

        if (eventHooks)
        {
            ok &= bridgeLaunched is not null && bridgeTick is not null;
            ok &= launchedCalls == 1 && tickCalls == 1;
        }
        else
        {
            ok &= bridgeLaunched is null && bridgeTick is null;
            ok &= launchedCalls == 0 && tickCalls == 0;
        }

        Console.WriteLine(ok ? "RESULT: OK" : "RESULT: FAILED");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------------ utils

    private static (string input, string output) Paths(Dictionary<string, string> opts, int required)
    {
        var positional = opts
            .Where(kv => kv.Key.StartsWith('_'))
            .OrderBy(kv => int.Parse(kv.Key[1..]))
            .Select(kv => kv.Value)
            .ToList();

        if (positional.Count < required)
        {
            throw new ArgumentException("expected " + required + " positional arguments, got " + positional.Count);
        }

        return (Path.GetFullPath(positional[0]),
            required > 1 ? Path.GetFullPath(positional[1]) : string.Empty);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result[key] = args[++i];
                }
                else
                {
                    result[key] = "true";
                }
            }
            else
            {
                result["_" + positional] = a;
                positional++;
            }
        }

        return result;
    }
}
