using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ModInjector;
using Assembly = System.Reflection.Assembly;

namespace InjectorTests;

/// <summary>
/// Phase 2.0 injection tests.
///
/// Every case builds a small synthetic assembly with Cecil, applies one injection rule, then
/// loads the written file and <b>executes</b> it. Structural checks alone would not prove that a
/// branch which jumps straight at a ret now runs the hook, and a single real method would not
/// cover multiple returns, handler boundaries or rejection of a wrong signature.
///
/// Offline: no game assembly, no device.
/// </summary>
internal static class Program
{
    private const string SyntheticType = "Synthetic.Synthetic";

    private static int _failures;
    private static int _checks;

    internal static int Main()
    {
        var work = Path.Combine(Path.GetTempPath(),
            "modinjector-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);
        try
        {
            SingleReturn(work);
            MultipleReturns(work);
            BranchStraightToRet(work);
            RetInsideCatchIsNotNormalCompletion();
            HandlerBoundaryMovesWithTheHook(work);
            EntryHook(work);
            WrongSignatureRejected();
            AmbiguousOverloadRejected();
            PartialPatchDetected(work);
            ShortBranchesAreWidened(work);
        }
        finally
        {
            try
            {
                Directory.Delete(work, true);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        Console.WriteLine();
        Console.WriteLine(_checks + " checks, " + (_checks - _failures) + " passed, " + _failures + " failed");
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ cases

    private static void SingleReturn(string work)
    {
        var name = "single";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ret);

        var inserted = GameMethodInjector.InjectCompletionHook(method, hook);
        Check(name + "_one_call_inserted", inserted == 1, "inserted=" + inserted);
        Check(name + "_ret_preceded_by_call",
            method.Body.Instructions[method.Body.Instructions.Count - 2].OpCode == OpCodes.Call,
            "IL: " + Describe(method));

        var loaded = Load(Write(module, work, name));
        InvokeTarget(loaded, null);
        Check(name + "_hook_executed", HookCount(loaded) == 1, "hooks=" + HookCount(loaded));
    }

    private static void MultipleReturns(string work)
    {
        var name = "multiret";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: true);
        var il = method.Body.GetILProcessor();

        // if (v != 0) goto positive; return;              <- ret A (fall-through)
        // positive: if (v <= 0) goto end; return;          <- ret B, and a branch straight at ret C
        // end: return;                                     <- ret C
        var positive = il.Create(OpCodes.Nop);
        var retA = il.Create(OpCodes.Ret);
        var retB = il.Create(OpCodes.Ret);
        var end = il.Create(OpCodes.Ret);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, positive);
        il.Append(retA);
        il.Append(positive);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, end);
        il.Emit(OpCodes.Nop);
        il.Append(retB);
        il.Append(end);

        var inserted = GameMethodInjector.InjectCompletionHook(method, hook);
        Check(name + "_call_per_normal_ret", inserted == 3, "inserted=" + inserted);
        Check(name + "_no_edge_targets_ret", !AnyEdgeTargetsRet(method), "all edges re-targeted");

        var loaded = Load(Write(module, work, name));
        InvokeTarget(loaded, 0);
        Check(name + "_zero_path_runs_hook", HookCount(loaded) == 1, "hooks=" + HookCount(loaded));
        InvokeTarget(loaded, 5);
        Check(name + "_positive_path_runs_hook", HookCount(loaded) == 2, "hooks=" + HookCount(loaded));
        InvokeTarget(loaded, -1);
        Check(name + "_negative_path_runs_hook", HookCount(loaded) == 3, "hooks=" + HookCount(loaded));
    }

    private static void BranchStraightToRet(string work)
    {
        var name = "branch-to-ret";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);

        il.Emit(OpCodes.Br, ret);
        il.Emit(OpCodes.Nop);
        il.Append(ret);

        GameMethodInjector.InjectCompletionHook(method, hook);
        Check(name + "_branch_retargeted",
            !ReferenceEquals(method.Body.Instructions[0].Operand, ret), "edge now targets the hook call");

        var loaded = Load(Write(module, work, name));
        InvokeTarget(loaded, null);
        Check(name + "_hook_executed_on_jump_path", HookCount(loaded) == 1, "hooks=" + HookCount(loaded));
    }

    private static void RetInsideCatchIsNotNormalCompletion()
    {
        var module = NewModule("ret-in-catch");
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();

        var tryStart = il.Create(OpCodes.Nop);
        var finalRet = il.Create(OpCodes.Ret);
        var leave = il.Create(OpCodes.Leave, finalRet);
        var handler = il.Create(OpCodes.Pop);
        var handlerRet = il.Create(OpCodes.Ret);

        il.Append(tryStart);
        il.Append(leave);
        il.Append(handler);
        il.Append(handlerRet);
        il.Append(finalRet);

        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = finalRet,
            CatchType = module.ImportReference(typeof(Exception)),
        });

        var inserted = GameMethodInjector.InjectCompletionHook(method, hook);
        Check("ret_in_catch_only_normal_ret_hooked", inserted == 1, "inserted=" + inserted);
        Check("ret_in_catch_handler_ret_untouched",
            handlerRet.Previous is null || handlerRet.Previous.OpCode != OpCodes.Call,
            "handler ret is not preceded by the hook call");
    }

    private static void HandlerBoundaryMovesWithTheHook(string work)
    {
        var name = "eh-boundary";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();

        var tryStart = il.Create(OpCodes.Nop);
        var ret = il.Create(OpCodes.Ret);
        var leave = il.Create(OpCodes.Leave, ret);
        var handler = il.Create(OpCodes.Pop);
        var handlerLeave = il.Create(OpCodes.Leave, ret);

        il.Append(tryStart);
        il.Append(leave);
        il.Append(handler);
        il.Append(handlerLeave);
        il.Append(ret);

        // The handler region deliberately *ends at* the ret: a hook call inserted in front of that
        // ret would otherwise be pulled inside the protected region.
        method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handler,
            HandlerStart = handler,
            HandlerEnd = ret,
            CatchType = module.ImportReference(typeof(Exception)),
        });

        GameMethodInjector.InjectCompletionHook(method, hook);

        var eh = method.Body.ExceptionHandlers[0];
        var hookCall = ret.Previous!;
        Check(name + "_handler_end_moved_to_call", ReferenceEquals(eh.HandlerEnd, hookCall),
            "HandlerEnd == hook call");

        var loaded = Load(Write(module, work, name));
        InvokeTarget(loaded, null);
        Check(name + "_hook_executed", HookCount(loaded) == 1, "hooks=" + HookCount(loaded));
    }

    private static void EntryHook(string work)
    {
        var name = "entry";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Nop);
        il.Emit(OpCodes.Ret);

        var inserted = GameMethodInjector.InjectEntryHook(method, hook);
        Check(name + "_one_call_inserted", inserted == 1, "inserted=" + inserted);

        var loaded = Load(Write(module, work, name));
        InvokeTarget(loaded, null);
        InvokeTarget(loaded, null);
        Check(name + "_hook_per_entry", HookCount(loaded) == 2, "hooks=" + HookCount(loaded));
    }

    private static void WrongSignatureRejected()
    {
        var module = NewModule("wrong-signature");
        var type = AddType(module, "GameRunner", "Synthetic");
        var method = new MethodDefinition("Initialize",
            MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Int32);
        method.Body.GetILProcessor().Emit(OpCodes.Ret);
        type.Methods.Add(method);

        var rejected = Throws(() => GameMethodInjector.RequireMethod(
            module, "Synthetic.GameRunner", "Initialize", "System.Void"));
        Check("wrong_return_type_rejected", rejected, "int Initialize() must not match void Initialize()");

        var rejectedArity = Throws(() => GameMethodInjector.RequireMethod(
            module, "Synthetic.GameRunner", "Initialize", "System.Void", "System.Int32"));
        Check("wrong_parameter_list_rejected", rejectedArity, "no void Initialize(int) exists");
    }

    private static void AmbiguousOverloadRejected()
    {
        var module = NewModule("ambiguous");
        var type = AddType(module, "GameRunner", "Synthetic");
        for (var i = 0; i < 2; i++)
        {
            var method = new MethodDefinition("Update",
                MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
            method.Body.GetILProcessor().Emit(OpCodes.Ret);
            type.Methods.Add(method);
        }

        var rejected = Throws(() => GameMethodInjector.RequireMethod(
            module, "Synthetic.GameRunner", "Update", "System.Void"));
        Check("ambiguous_overload_rejected", rejected, "two identical signatures must fail closed");
    }

    private static void PartialPatchDetected(string work)
    {
        var name = "partial";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();
        var first = il.Create(OpCodes.Ret);
        var second = il.Create(OpCodes.Ret);
        il.Append(first);
        il.Emit(OpCodes.Nop);
        il.Append(second);

        GameMethodInjector.InjectCompletionHook(method, hook);
        var complete = GameMethodInjector.HasCompleteCompletionHook(method, SyntheticType, "Hook");
        Check(name + "_complete_patch_recognized", complete, "two rets, two calls");

        // Remove one hook call: the exact shape a half-applied patch leaves behind.
        var toRemove = second.Previous!;
        method.Body.GetILProcessor().Remove(toRemove);
        var partial = GameMethodInjector.HasCompleteCompletionHook(method, SyntheticType, "Hook");
        Check(name + "_partial_patch_detected", !partial, "one ret without its call");
    }

    private static void ShortBranchesAreWidened(string work)
    {
        var name = "widen";
        var module = NewModule(name);
        var type = AddType(module, "Synthetic");
        var hook = AddHook(type);
        var method = AddTarget(module, type, withIntParameter: false);
        var il = method.Body.GetILProcessor();
        var ret = il.Create(OpCodes.Ret);
        il.Emit(OpCodes.Br_S, ret);
        il.Emit(OpCodes.Nop);
        il.Append(ret);

        GameMethodInjector.InjectCompletionHook(method, hook);
        Check(name + "_br_s_widened_to_br", method.Body.Instructions[0].OpCode == OpCodes.Br,
            method.Body.Instructions[0].OpCode.ToString());

        var loaded = Load(Write(module, work, name));
        InvokeTarget(loaded, null);
        Check(name + "_executes_after_widening", HookCount(loaded) == 1, "hooks=" + HookCount(loaded));
    }

    // ------------------------------------------------------------------ fixtures

    private static ModuleDefinition NewModule(string name)
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("InjectorTest." + name.Replace('-', '_'), new Version(1, 0, 0, 0)),
            name + ".dll", ModuleKind.Dll);
        return assembly.MainModule;
    }

    private static TypeDefinition AddType(ModuleDefinition module, string name, string ns = "Synthetic")
    {
        var type = new TypeDefinition(ns, name,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(type);
        return type;
    }

    private static MethodDefinition AddHook(TypeDefinition type)
    {
        var counter = new FieldDefinition("HookCount",
            FieldAttributes.Public | FieldAttributes.Static, type.Module.TypeSystem.Int32);
        type.Fields.Add(counter);

        var hook = new MethodDefinition("Hook",
            MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        var il = hook.Body.GetILProcessor();
        il.Emit(OpCodes.Ldsfld, counter);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, counter);
        il.Emit(OpCodes.Ret);
        type.Methods.Add(hook);
        return hook;
    }

    private static MethodDefinition AddTarget(ModuleDefinition module, TypeDefinition type,
        bool withIntParameter)
    {
        var method = new MethodDefinition("Target",
            MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.InitLocals = true;
        if (withIntParameter)
        {
            method.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None,
                module.TypeSystem.Int32));
        }

        type.Methods.Add(method);
        return method;
    }

    private static string Write(ModuleDefinition module, string work, string name)
    {
        var path = Path.Combine(work, name + ".dll");
        module.Write(path);
        return path;
    }

    private static Assembly Load(string path)
    {
        return Assembly.LoadFrom(path);
    }

    private static void InvokeTarget(Assembly assembly, int? value)
    {
        var type = assembly.GetType(SyntheticType)
                   ?? throw new InvalidOperationException("synthetic type missing in " + assembly.FullName);
        var method = type.GetMethod("Target")
                     ?? throw new InvalidOperationException("Target missing");
        method.Invoke(null, value.HasValue ? new object[] { value.Value } : null);
    }

    private static int HookCount(Assembly assembly)
    {
        var type = assembly.GetType(SyntheticType)
                   ?? throw new InvalidOperationException("synthetic type missing");
        var field = type.GetField("HookCount") ?? throw new InvalidOperationException("HookCount missing");
        return (int)field.GetValue(null)!;
    }

    private static bool AnyEdgeTargetsRet(MethodDefinition method)
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
                return true;
            }
        }

        return false;
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string Describe(MethodDefinition method)
    {
        var parts = new List<string>();
        foreach (var ins in method.Body.Instructions)
        {
            parts.Add(ins.OpCode.ToString());
        }

        return string.Join(",", parts);
    }

    private static void Check(string name, bool ok, string detail)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine((ok ? "PASS " : "FAIL ") + name.PadRight(46) + " " + detail);
    }
}
