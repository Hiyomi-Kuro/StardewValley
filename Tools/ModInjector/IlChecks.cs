using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModInjector;

/// <summary>
/// 注入后的结构校验。
///
/// 存在的理由：ModInjector 已经因为"生成的 IL 没有被校验"出过三次事故
/// （MVID 赋值被注释、if (minimal) 被注释、leave 用在无保护区）。
/// 这些都应该在构建阶段报错，而不是等设备上一句 InvalidProgramException。
///
/// Phase 2.0 adds a second reason: the event hooks must be proven to be <b>where</b> they claim to
/// be. "A call to Bootstrap exists somewhere" is not evidence that every normal return of
/// <c>GameRunner.Initialize</c> runs it, so the checks below are positional and per-instruction:
/// every normal <c>ret</c> must be immediately preceded by the hook call, no edge may jump
/// straight at a <c>ret</c>, and no exception-handler boundary may name one.
/// </summary>
internal static class IlChecks
{
    private const string BootNamespace = "ModFramework";
    private const string BootType = "Bootstrap";
    private const string BridgeLaunched = "GameLaunched";
    private const string BridgeTick = "Tick";
    private const string DiagnosticMethod = "WriteEventsDisabled";

    public static void Validate(ModuleDefinition module, MethodDefinition start, MethodDefinition hook,
        Guid originalMvid, bool eventHooks)
    {
        var problems = new List<string>();

        var bootstrap = module.GetType(BootNamespace, BootType);
        if (bootstrap is null)
        {
            problems.Add("ModFramework.Bootstrap type missing");
        }

        if (module.Mvid == originalMvid)
        {
            problems.Add("MVID unchanged (stale AOT image would be reused)");
        }

        var first = hook.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop);
        if (first is null || first.OpCode != OpCodes.Call
            || first.Operand is not MethodReference call
            || call.DeclaringType.FullName != "ModFramework.Bootstrap"
            || call.Name != "Start")
        {
            problems.Add("hook method does not start with call ModFramework.Bootstrap::Start");
        }

        problems.AddRange(CheckBranches(start.Body));
        problems.AddRange(CheckLeaveUsage(start.Body));
        problems.AddRange(CheckStack(start.Body));
        problems.AddRange(CheckHandlerShape(start.Body, "Bootstrap.Start"));

        if (bootstrap is not null)
        {
            if (eventHooks)
            {
                problems.AddRange(CheckEventBridge(module, bootstrap, start));
                problems.AddRange(CheckGameHooks(module, bootstrap));
            }
            else
            {
                problems.AddRange(CheckNoEventBridge(module, bootstrap));
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "generated IL failed validation:" + Environment.NewLine + "  - " +
                string.Join(Environment.NewLine + "  - ", problems));
        }
    }

    // ------------------------------------------------------------------ event bridge

    private static IEnumerable<string> CheckEventBridge(
        ModuleDefinition module,
        TypeDefinition bootstrap,
        MethodDefinition start)
    {
        var launchedField = FindField(bootstrap, "s_onGameLaunched");
        var tickField = FindField(bootstrap, "s_onTick");
        if (launchedField is null || launchedField.FieldType.FullName != "System.Action" || !launchedField.IsStatic)
        {
            yield return "bootstrap field s_onGameLaunched is missing or not a static System.Action";
        }

        if (tickField is null || tickField.FieldType.FullName != "System.Action" || !tickField.IsStatic)
        {
            yield return "bootstrap field s_onTick is missing or not a static System.Action";
        }

        var launched = FindMethod(bootstrap, BridgeLaunched);
        var tick = FindMethod(bootstrap, BridgeTick);
        if (launched is null)
        {
            yield return "bootstrap bridge method GameLaunched() missing";
        }

        if (tick is null)
        {
            yield return "bootstrap bridge method Tick() missing";
        }

        if (launched is not null && launchedField is not null)
        {
            foreach (var problem in CheckBridgeBody(launched, launchedField, "GameLaunched"))
            {
                yield return problem;
            }
        }

        if (tick is not null && tickField is not null)
        {
            foreach (var problem in CheckBridgeBody(tick, tickField, "Tick"))
            {
                yield return problem;
            }
        }

        var diagnostic = FindMethod(bootstrap, DiagnosticMethod);
        if (diagnostic is null || !diagnostic.IsStatic || diagnostic.Parameters.Count != 1 ||
            diagnostic.Parameters[0].ParameterType.FullName != "System.String")
        {
            yield return "bootstrap " + DiagnosticMethod + "(string) missing";
        }
        else
        {
            foreach (var problem in CheckBridgeBody(diagnostic, null, DiagnosticMethod))
            {
                yield return problem;
            }
        }

        // Start() must actually bind the delegates (not merely declare the fields), and it must do
        // so without a static reference to the runtime assembly.
        var createsDelegate = false;
        var namesDispatcher = false;
        foreach (var ins in start.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr &&
                mr.DeclaringType.FullName == "System.Delegate" && mr.Name == "CreateDelegate")
            {
                createsDelegate = true;
            }

            if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s &&
                s == "ModFramework.Runtime.EventDispatcher")
            {
                namesDispatcher = true;
            }
        }

        if (!createsDelegate)
        {
            yield return "Bootstrap.Start does not call Delegate.CreateDelegate (event delegates not bound)";
        }

        if (!namesDispatcher)
        {
            yield return "Bootstrap.Start does not resolve ModFramework.Runtime.EventDispatcher by name";
        }

        if (start.Body.ExceptionHandlers.Count == 0)
        {
            yield return "Bootstrap.Start has no exception handler";
        }

        for (var i = 0; i < module.AssemblyReferences.Count; i++)
        {
            if (module.AssemblyReferences[i].Name == "ModFramework.Runtime")
            {
                yield return "the patched game assembly gained a static AssemblyRef to ModFramework.Runtime";
            }
        }
    }

    private static IEnumerable<string> CheckBridgeBody(
        MethodDefinition method,
        FieldDefinition? field,
        string label)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
        {
            yield return label + ": empty body";
            yield break;
        }

        if (method.Body.ExceptionHandlers.Count != 1 ||
            method.Body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Catch)
        {
            yield return label + ": expected exactly one catch handler";
        }

        var callsInvoke = false;
        var readsField = false;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference mr &&
                mr.DeclaringType.FullName == "System.Action" && mr.Name == "Invoke")
            {
                callsInvoke = true;
            }

            if (field is not null && ins.OpCode == OpCodes.Ldsfld &&
                ReferenceEquals(ins.Operand, field))
            {
                readsField = true;
            }
        }

        if (!callsInvoke && field is not null)
        {
            yield return label + ": does not call the cached System.Action delegate";
        }

        if (field is not null && !readsField)
        {
            yield return label + ": does not read the static delegate field";
        }

        foreach (var problem in CheckBranches(method.Body))
        {
            yield return label + ": " + problem;
        }

        foreach (var problem in CheckLeaveUsage(method.Body))
        {
            yield return label + ": " + problem;
        }

        foreach (var problem in CheckStack(method.Body))
        {
            yield return label + ": " + problem;
        }

        foreach (var problem in CheckHandlerShape(method.Body, label))
        {
            yield return problem;
        }
    }

    private static IEnumerable<string> CheckNoEventBridge(ModuleDefinition module, TypeDefinition bootstrap)
    {
        if (FindMethod(bootstrap, BridgeLaunched) is not null ||
            FindMethod(bootstrap, BridgeTick) is not null ||
            FindField(bootstrap, "s_onGameLaunched") is not null ||
            FindField(bootstrap, "s_onTick") is not null)
        {
            yield return "loader-only build still carries the event bridge (mixed state)";
        }

        foreach (var target in GameTargets(module))
        {
            if (target.Value is null)
            {
                continue;
            }

            var calls = GameMethodInjector.CountCalls(target.Value, BootNamespace + "." + BootType, BridgeLaunched) +
                        GameMethodInjector.CountCalls(target.Value, BootNamespace + "." + BootType, BridgeTick);
            if (calls != 0)
            {
                yield return "loader-only build still calls a Bootstrap event hook in " + target.Value.FullName;
            }
        }
    }

    private static IEnumerable<string> CheckGameHooks(ModuleDefinition module, TypeDefinition bootstrap)
    {
        MethodDefinition? initialize = null;
        MethodDefinition? update = null;
        var missing = "";
        try
        {
            initialize = GameMethodInjector.RequireMethod(module, GameMethodInjector.GameRunnerType,
                GameMethodInjector.InitializeMethod, "System.Void");
            update = GameMethodInjector.RequireMethod(module, GameMethodInjector.GameRunnerType,
                GameMethodInjector.UpdateMethod, "System.Void", GameMethodInjector.GameTimeType);
        }
        catch (InvalidOperationException ex)
        {
            missing = ex.Message;
        }

        if (initialize is null || update is null)
        {
            yield return missing;
            yield break;
        }

        var hookName = BootNamespace + "." + BootType;

        // ---- GameRunner.Initialize: every normal return must run GameLaunched -------------
        var normalReturns = 0;
        foreach (var ins in initialize.Body.Instructions)
        {
            if (ins.OpCode.Code != Code.Ret)
            {
                continue;
            }

            if (IsInsideHandler(ins, initialize.Body))
            {
                yield return "GameRunner.Initialize has a ret inside an exception handler (error path would report a launch)";
                continue;
            }

            if (IsInsideTry(ins, initialize.Body))
            {
                yield return "GameRunner.Initialize has a ret inside a try region";
                continue;
            }

            normalReturns++;
            var previous = PreviousNonNop(ins);
            if (previous is null || previous.OpCode != OpCodes.Call ||
                previous.Operand is not MethodReference mr ||
                mr.DeclaringType.FullName != hookName || mr.Name != BridgeLaunched)
            {
                yield return "GameRunner.Initialize ret at IL_" + ins.Offset.ToString("x4") +
                             " is not preceded by call Bootstrap.GameLaunched";
            }
        }

        if (normalReturns == 0)
        {
            yield return "GameRunner.Initialize has no normal return path";
        }

        var launchedCalls = GameMethodInjector.CountCalls(initialize, hookName, BridgeLaunched);
        if (launchedCalls != normalReturns)
        {
            yield return "GameRunner.Initialize has " + launchedCalls +
                         " GameLaunched call(s) for " + normalReturns + " normal return(s)";
        }

        foreach (var problem in CheckNoEdgeTargetsRet("GameRunner.Initialize", initialize))
        {
            yield return problem;
        }

        // ---- GameRunner.Update: entry instruction must be Tick ----------------------------
        var updateFirst = update.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop);
        if (updateFirst is null || updateFirst.OpCode != OpCodes.Call ||
            updateFirst.Operand is not MethodReference tickRef ||
            tickRef.DeclaringType.FullName != hookName || tickRef.Name != BridgeTick)
        {
            yield return "GameRunner.Update does not start with call Bootstrap.Tick";
        }

        var tickCalls = GameMethodInjector.CountCalls(update, hookName, BridgeTick);
        if (tickCalls != 1)
        {
            yield return "GameRunner.Update has " + tickCalls + " Tick call(s), expected exactly 1";
        }

        foreach (var problem in CheckNoEdgeTargetsRet("GameRunner.Update", update))
        {
            yield return problem;
        }

        foreach (var problem in CheckHandlerBoundaries("GameRunner.Initialize", initialize))
        {
            yield return problem;
        }

        foreach (var problem in CheckHandlerBoundaries("GameRunner.Update", update))
        {
            yield return problem;
        }
    }

    private static IEnumerable<KeyValuePair<string, MethodDefinition?>> GameTargets(ModuleDefinition module)
    {
        MethodDefinition? initialize = null;
        MethodDefinition? update = null;
        try
        {
            initialize = GameMethodInjector.RequireMethod(module, GameMethodInjector.GameRunnerType,
                GameMethodInjector.InitializeMethod, "System.Void");
        }
        catch (InvalidOperationException)
        {
            // Absent target: nothing to check in a loader-only build.
        }

        try
        {
            update = GameMethodInjector.RequireMethod(module, GameMethodInjector.GameRunnerType,
                GameMethodInjector.UpdateMethod, "System.Void", GameMethodInjector.GameTimeType);
        }
        catch (InvalidOperationException)
        {
            // Same.
        }

        yield return new KeyValuePair<string, MethodDefinition?>("initialize", initialize);
        yield return new KeyValuePair<string, MethodDefinition?>("update", update);
    }

    private static IEnumerable<string> CheckNoEdgeTargetsRet(string label, MethodDefinition method)
    {
        var rets = new HashSet<Instruction>();
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ret)
            {
                rets.Add(ins);
            }
        }

        foreach (var ins in method.Body.Instructions)
        {
            if (ins.Operand is Instruction target && rets.Contains(target))
            {
                yield return label + ": " + Describe(ins) + " still jumps straight at a ret";
            }

            if (ins.Operand is Instruction[] targets)
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    if (rets.Contains(targets[i]))
                    {
                        yield return label + ": " + Describe(ins) + " switch target is a ret";
                        break;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> CheckHandlerBoundaries(string label, MethodDefinition method)
    {
        var rets = new HashSet<Instruction>();
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ret)
            {
                rets.Add(ins);
            }
        }

        foreach (var eh in method.Body.ExceptionHandlers)
        {
            if (eh.TryStart is not null && rets.Contains(eh.TryStart))
            {
                yield return label + ": a try region starts at a ret";
            }

            if (eh.HandlerStart is not null && rets.Contains(eh.HandlerStart))
            {
                yield return label + ": a handler region starts at a ret";
            }
        }
    }

    // ------------------------------------------------------------------ generic checks

    private static IEnumerable<string> CheckBranches(MethodBody body)
    {
        var owned = body.Instructions.ToHashSet();
        foreach (var ins in body.Instructions)
        {
            if (ins.Operand is Instruction target && !owned.Contains(target))
            {
                yield return $"{Describe(ins)} branches outside the method";
            }

            if (ins.Operand is Instruction[] targets && targets.Any(t => !owned.Contains(t)))
            {
                yield return $"{Describe(ins)} has a switch target outside the method";
            }
        }
    }

    private static IEnumerable<string> CheckLeaveUsage(MethodBody body)
    {
        var leaves = body.Instructions.Where(i => i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S).ToList();
        if (body.ExceptionHandlers.Count == 0 && leaves.Count > 0)
        {
            yield return "leave emitted without an exception handler (invalid outside a protected region)";
        }

        if (body.ExceptionHandlers.Count > 0 && leaves.Count == 0)
        {
            yield return "exception handler without any leave instruction";
        }
    }

    private static IEnumerable<string> CheckHandlerShape(MethodBody body, string label)
    {
        var order = new Dictionary<Instruction, int>();
        for (var i = 0; i < body.Instructions.Count; i++)
        {
            order[body.Instructions[i]] = i;
        }

        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart is null || eh.TryEnd is null)
            {
                yield return label + ": exception handler with a null try boundary";
                continue;
            }

            if (!order.ContainsKey(eh.TryStart) || !order.ContainsKey(eh.TryEnd))
            {
                yield return label + ": exception handler try boundary outside the method";
            }

            if (order[eh.TryEnd] <= order[eh.TryStart])
            {
                yield return label + ": empty or inverted try region";
            }

            if (eh.HandlerStart is null || !order.ContainsKey(eh.HandlerStart))
            {
                yield return label + ": exception handler start outside the method";
                continue;
            }

            if (order[eh.HandlerStart] < order[eh.TryEnd])
            {
                yield return label + ": handler starts before the try region ends";
            }

            if (eh.HandlerEnd is not null)
            {
                if (!order.ContainsKey(eh.HandlerEnd))
                {
                    yield return label + ": exception handler end outside the method";
                }
                else if (order[eh.HandlerEnd] < order[eh.HandlerStart])
                {
                    yield return label + ": handler ends before it starts";
                }
            }
        }
    }

    /// <summary>工作列表式栈深检查：合并点深度必须一致，ret 时深度必须为 0。</summary>
    private static IEnumerable<string> CheckStack(MethodBody body)
    {
        var problems = new List<string>();
        var depth = new Dictionary<Instruction, int>();
        var work = new Queue<Instruction>();

        var entry = body.Instructions[0];
        depth[entry] = 0;
        work.Enqueue(entry);

        while (work.Count > 0)
        {
            var ins = work.Dequeue();
            var d = depth[ins];
            var (pop, push) = Effect(ins);
            if (d < pop)
            {
                problems.Add($"{Describe(ins)}: stack underflow (depth {d}, needs {pop})");
                continue;
            }

            var after = d - pop + push;
            if (ins.OpCode == OpCodes.Ret && after != 0)
            {
                problems.Add($"{Describe(ins)}: ret with non-empty stack ({after})");
            }

            foreach (var next in Successors(ins, body))
            {
                if (depth.TryGetValue(next, out var existing))
                {
                    if (existing != after)
                    {
                        problems.Add($"{Describe(next)}: inconsistent stack depth from {Describe(ins)} ({existing} vs {after})");
                    }
                    continue;
                }
                depth[next] = after;
                work.Enqueue(next);
            }
        }

        return problems;
    }

    private static IEnumerable<Instruction> Successors(Instruction ins, MethodBody body)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Ret:
            case Code.Throw:
            case Code.Rethrow:
                yield break;
            case Code.Br:
            case Code.Br_S:
            case Code.Leave:
            case Code.Leave_S:
                yield return (Instruction)ins.Operand!;
                yield break;
            case Code.Brfalse:
            case Code.Brfalse_S:
            case Code.Brtrue:
            case Code.Brtrue_S:
                yield return (Instruction)ins.Operand!;
                break;
        }

        if (ins.Next is not null)
        {
            yield return ins.Next;
        }
    }

    private static (int pop, int push) Effect(Instruction ins)
    {
        switch (ins.OpCode.Code)
        {
            case Code.Nop:
                return (0, 0);
            case Code.Ldstr:
            case Code.Ldnull:
            case Code.Ldc_I4:
            case Code.Ldc_I4_S:
            case Code.Ldc_I4_0:
            case Code.Ldc_I4_1:
            case Code.Ldc_I4_M1:
            case Code.Ldc_I8:
            case Code.Ldloc:
            case Code.Ldloc_S:
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldloca:
            case Code.Ldloca_S:
            case Code.Ldarg:
            case Code.Ldarg_S:
            case Code.Ldarg_0:
            case Code.Ldarg_1:
            case Code.Ldarg_2:
            case Code.Ldarg_3:
            case Code.Ldarga:
            case Code.Ldarga_S:
            case Code.Ldsfld:
            case Code.Ldtoken:
                return (0, 1);
            case Code.Stloc:
            case Code.Stloc_S:
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Stsfld:
            case Code.Pop:
                return (1, 0);
            case Code.Starg:
            case Code.Starg_S:
                return (1, 0);
            case Code.Ldfld:
            case Code.Isinst:
            case Code.Castclass:
                // castclass pops one object reference and pushes one reference.
                return (1, 1);
            case Code.Stfld:
                return (2, 0);
            case Code.Brfalse:
            case Code.Brfalse_S:
            case Code.Brtrue:
            case Code.Brtrue_S:
                return (1, 0);
            case Code.Br:
            case Code.Br_S:
            case Code.Leave:
            case Code.Leave_S:
                return (0, 0);
            case Code.Ret:
                return (0, 0);
            case Code.Throw:
            case Code.Rethrow:
                return (1, 0);
            case Code.Call:
            case Code.Callvirt:
            {
                var m = (MethodReference)ins.Operand!;
                var pop = m.Parameters.Count + (m.HasThis ? 1 : 0);
                var push = m.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                return (pop, push);
            }
            case Code.Dup:
                return (1, 2);
            case Code.Newobj:
            {
                var m = (MethodReference)ins.Operand!;
                return (m.Parameters.Count, 1);
            }
            default:
                // Unknown opcode: fail loudly rather than guessing.
                throw new InvalidOperationException(
                    $"IL validation does not model opcode {ins.OpCode} at {Describe(ins)}");
        }
    }

    private static FieldDefinition? FindField(TypeDefinition type, string name)
    {
        for (var i = 0; i < type.Fields.Count; i++)
        {
            if (type.Fields[i].Name == name)
            {
                return type.Fields[i];
            }
        }

        return null;
    }

    private static MethodDefinition? FindMethod(TypeDefinition type, string name)
    {
        for (var i = 0; i < type.Methods.Count; i++)
        {
            if (type.Methods[i].Name == name)
            {
                return type.Methods[i];
            }
        }

        return null;
    }

    private static Instruction? PreviousNonNop(Instruction ins)
    {
        var previous = ins.Previous;
        while (previous is not null && previous.OpCode == OpCodes.Nop)
        {
            previous = previous.Previous;
        }

        return previous;
    }

    private static bool IsInsideTry(Instruction ins, MethodBody body)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (InRange(ins, eh.TryStart, eh.TryEnd))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideHandler(Instruction ins, MethodBody body)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (InRange(ins, eh.HandlerStart, eh.HandlerEnd))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InRange(Instruction ins, Instruction start, Instruction? end)
    {
        var current = start;
        while (current is not null)
        {
            // [start, end): the end instruction itself is outside the region.
            if (end is not null && ReferenceEquals(current, end))
            {
                return false;
            }

            if (ReferenceEquals(current, ins))
            {
                return true;
            }

            current = current.Next;
        }

        return false;
    }

    private static string Describe(Instruction ins) => $"IL_{ins.Offset:x4} {ins.OpCode}";
}
