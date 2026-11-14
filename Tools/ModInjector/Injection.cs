using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModInjector;

/// <summary>
/// Injection of the two Phase 2.0 game hooks.
///
/// The rules here exist because an <c>InsertBefore</c> on "the first ret" or "the first
/// instruction" is <b>not</b> the same claim as "every normal return path runs the hook":
///
///   * a branch, switch or leave that targets a <c>ret</c> instruction would skip an instruction
///     inserted in front of that <c>ret</c>, so every such edge is re-targeted to the hook call;
///   * an exception-handler boundary that points at the same <c>ret</c> is moved to the hook call,
///     so the new code is never silently pulled inside (or out of) a protected region;
///   * a <c>ret</c> inside a catch/filter/finally body is an <b>error</b> path returning normally:
///     it is deliberately left without the hook, and if it is the only return the build fails
///     closed instead of guessing;
///   * inserting instructions changes short-branch distances, so every short branch in a patched
///     method is widened to its long form (deterministically, before the writer encodes it).
///
/// Everything here is structural: no name-only matching, no "first overload".
/// </summary>
internal static class GameMethodInjector
{
    internal const string GameRunnerType = "StardewValley.GameRunner";
    internal const string InitializeMethod = "Initialize";
    internal const string UpdateMethod = "Update";
    internal const string GameTimeType = "Microsoft.Xna.Framework.GameTime";

    /// <summary>
    /// Resolves one method by type, name, exact return type and exact parameter list.
    /// Throws (build failure) when it is absent or ambiguous.
    /// </summary>
    internal static MethodDefinition RequireMethod(
        ModuleDefinition module,
        string typeName,
        string methodName,
        string returnType,
        params string[] parameterTypes)
    {
        var type = module.GetType(typeName)
                   ?? throw new InvalidOperationException("type not found: " + typeName);

        var matches = new List<MethodDefinition>();
        for (var i = 0; i < type.Methods.Count; i++)
        {
            var candidate = type.Methods[i];
            if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal) ||
                candidate.IsStatic ||
                !string.Equals(candidate.ReturnType.FullName, returnType, StringComparison.Ordinal) ||
                candidate.Parameters.Count != parameterTypes.Length)
            {
                continue;
            }

            var ok = true;
            for (var p = 0; p < parameterTypes.Length; p++)
            {
                if (!string.Equals(candidate.Parameters[p].ParameterType.FullName, parameterTypes[p],
                        StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                "method not found with the exact signature " + returnType + " " + typeName + "::" +
                methodName + "(" + string.Join(", ", parameterTypes) + "); candidates named \"" +
                methodName + "\": " + DescribeMethods(type, methodName));
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                "ambiguous target: " + matches.Count + " methods match " + typeName + "::" + methodName +
                "(" + string.Join(", ", parameterTypes) + ")");
        }

        var method = matches[0];
        if (!method.HasBody)
        {
            throw new InvalidOperationException("target has no body: " + method.FullName);
        }

        if (method.Body.Instructions.Count == 0)
        {
            throw new InvalidOperationException("target has an empty body: " + method.FullName);
        }

        return method;
    }

    private static string DescribeMethods(TypeDefinition type, string name)
    {
        var parts = new List<string>();
        for (var i = 0; i < type.Methods.Count; i++)
        {
            var m = type.Methods[i];
            if (!string.Equals(m.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var parameters = new List<string>();
            for (var p = 0; p < m.Parameters.Count; p++)
            {
                parameters.Add(m.Parameters[p].ParameterType.FullName);
            }

            parts.Add(m.ReturnType.FullName + "(" + string.Join(",", parameters) + ")");
        }

        return parts.Count == 0 ? "(none)" : string.Join(" | ", parts);
    }

    /// <summary>
    /// Inserts <c>call hook</c> at every normal-completion return of <paramref name="method"/>.
    ///
    /// Returns the number of inserted calls. Throws when the method offers no normal return path,
    /// or when a <c>ret</c> sits inside a protected region in a way this injector does not model.
    /// </summary>
    internal static int InjectCompletionHook(MethodDefinition method, MethodReference hook)
    {
        var body = method.Body;

        // A ret strictly inside a try region is not valid IL, but fail closed rather than guess.
        var returns = new List<Instruction>();
        foreach (var ins in body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ret)
            {
                returns.Add(ins);
            }
        }

        if (returns.Count == 0)
        {
            throw new InvalidOperationException(
                "normal completion hook requires at least one ret instruction: " + method.FullName);
        }

        var normalReturns = new List<Instruction>();
        for (var i = 0; i < returns.Count; i++)
        {
            var ret = returns[i];
            if (IsInsideTry(ret, body))
            {
                throw new InvalidOperationException(
                    "unsupported control flow: a ret inside a try region in " + method.FullName +
                    " (IL_" + ret.Offset.ToString("x4") + ")");
            }

            if (IsInsideHandler(ret, body))
            {
                // Exception path returning normally: must not report "the game launched".
                continue;
            }

            normalReturns.Add(ret);
        }

        if (normalReturns.Count == 0)
        {
            throw new InvalidOperationException(
                "the only return instructions of " + method.FullName +
                " are inside exception handlers, so no normal completion path exists");
        }

        var il = body.GetILProcessor();
        for (var i = 0; i < normalReturns.Count; i++)
        {
            var ret = normalReturns[i];
            var call = il.Create(OpCodes.Call, hook);
            il.InsertBefore(ret, call);

            // Every edge that jumped straight at the ret now lands on the hook call instead.
            RetargetEdges(body, ret, call);
            // Handler/try ends that named the ret now end before the hook call, so the call is
            // outside the protected region exactly like the ret was.
            MoveBoundaries(body, ret, call);
        }

        WidenShortBranches(method);
        return normalReturns.Count;
    }

    /// <summary>
    /// Inserts <c>call hook</c> as the first instruction of <paramref name="method"/> (entry hook).
    /// Returns the number of inserted calls (always 1).
    /// </summary>
    internal static int InjectEntryHook(MethodDefinition method, MethodReference hook)
    {
        var body = method.Body;
        if (body.Instructions.Count == 0)
        {
            throw new InvalidOperationException("target has an empty body: " + method.FullName);
        }

        var first = body.Instructions[0];
        if (IsInsideHandler(first, body) || IsInsideTry(first, body))
        {
            throw new InvalidOperationException(
                "unsupported control flow: the first instruction of " + method.FullName +
                " is inside a protected region");
        }

        var il = body.GetILProcessor();
        // Entry hooks run on method entry only; a branch back to the old first instruction is a
        // loop edge, not a second entry, so it is deliberately left pointing at the old target.
        il.InsertBefore(first, il.Create(OpCodes.Call, hook));
        WidenShortBranches(method);
        return 1;
    }

    /// <summary>Number of direct calls to <paramref name="declaringType"/>.<paramref name="name"/>.</summary>
    internal static int CountCalls(MethodDefinition method, string declaringType, string name)
    {
        var count = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) &&
                ins.Operand is MethodReference mr &&
                string.Equals(mr.DeclaringType.FullName, declaringType, StringComparison.Ordinal) &&
                string.Equals(mr.Name, name, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when every normal return of <paramref name="method"/> is immediately preceded by the
    /// named call and there is no other call to it. Used by the idempotent patch path: a method
    /// that only has <i>some</i> of its returns hooked is a partial patch and is refused.
    /// </summary>
    internal static bool HasCompleteCompletionHook(MethodDefinition method, string declaringType, string name)
    {
        var normalReturns = 0;
        foreach (var ins in method.Body.Instructions)
        {
            if (ins.OpCode.Code != Code.Ret || IsInsideHandler(ins, method.Body) || IsInsideTry(ins, method.Body))
            {
                continue;
            }

            normalReturns++;
            var previous = ins.Previous;
            while (previous != null && previous.OpCode == OpCodes.Nop)
            {
                previous = previous.Previous;
            }

            if (previous is null || previous.OpCode != OpCodes.Call ||
                previous.Operand is not MethodReference mr ||
                !string.Equals(mr.DeclaringType.FullName, declaringType, StringComparison.Ordinal) ||
                !string.Equals(mr.Name, name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return normalReturns > 0 && CountCalls(method, declaringType, name) == normalReturns;
    }

    /// <summary>True when the first effective instruction of the method is exactly the named call.</summary>
    internal static bool HasEntryHook(MethodDefinition method, string declaringType, string name)
    {
        var first = method.Body.Instructions.FirstOrDefault(i => i.OpCode != OpCodes.Nop);
        return first is not null && first.OpCode == OpCodes.Call &&
               first.Operand is MethodReference mr &&
               string.Equals(mr.DeclaringType.FullName, declaringType, StringComparison.Ordinal) &&
               string.Equals(mr.Name, name, StringComparison.Ordinal) &&
               CountCalls(method, declaringType, name) == 1;
    }

    private static bool IsInsideTry(Instruction ins, MethodBody body)
    {
        for (var i = 0; i < body.ExceptionHandlers.Count; i++)
        {
            var eh = body.ExceptionHandlers[i];
            if (InRange(ins, eh.TryStart, eh.TryEnd))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideHandler(Instruction ins, MethodBody body)
    {
        for (var i = 0; i < body.ExceptionHandlers.Count; i++)
        {
            var eh = body.ExceptionHandlers[i];
            if (InRange(ins, eh.HandlerStart, eh.HandlerEnd))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Instruction identity by list position. Cecil only assigns real offsets while writing, so
    /// structural comparisons must not rely on <see cref="Instruction.Offset"/>.
    /// </summary>
    private static bool InRange(Instruction ins, Instruction start, Instruction? end)
    {
        // Walk by reference: start/end are instructions owned by the same body as ins.
        var current = start;
        while (current != null)
        {
            // The region is [start, end): an instruction that *is* the end is outside it. Check the
            // end first, otherwise a ret that a handler ends on would look like it were inside.
            if (end != null && ReferenceEquals(current, end))
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

    private static void RetargetEdges(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var ins in body.Instructions)
        {
            if (ReferenceEquals(ins.Operand, from))
            {
                ins.Operand = to;
                continue;
            }

            if (ins.Operand is Instruction[] targets)
            {
                for (var i = 0; i < targets.Length; i++)
                {
                    if (ReferenceEquals(targets[i], from))
                    {
                        targets[i] = to;
                    }
                }
            }
        }
    }

    private static void MoveBoundaries(MethodBody body, Instruction from, Instruction to)
    {
        foreach (var eh in body.ExceptionHandlers)
        {
            if (ReferenceEquals(eh.TryStart, from))
            {
                throw new InvalidOperationException(
                    "unsupported control flow: an exception-handler try region starts at a ret");
            }

            if (ReferenceEquals(eh.HandlerStart, from))
            {
                throw new InvalidOperationException(
                    "unsupported control flow: an exception-handler handler starts at a ret");
            }

            if (ReferenceEquals(eh.TryEnd, from))
            {
                eh.TryEnd = to;
            }

            if (eh.HandlerEnd != null && ReferenceEquals(eh.HandlerEnd, from))
            {
                eh.HandlerEnd = to;
            }
        }
    }

    /// <summary>
    /// Widens every short branch of a patched method to its long form.
    ///
    /// Inserting instructions moves every following instruction, and a short branch holds a signed
    /// byte displacement; Cecil does not widen a branch that no longer fits, so a method that
    /// silently truncated here would fail on device as an invalid program. Widening is
    /// unconditional and deterministic: it cannot depend on an offset that is only known after
    /// the final layout.
    /// </summary>
    internal static void WidenShortBranches(MethodDefinition method)
    {
        foreach (var ins in method.Body.Instructions)
        {
            var longForm = LongForm(ins.OpCode);
            if (longForm != null)
            {
                ins.OpCode = longForm.Value;
            }
        }
    }

    private static OpCode? LongForm(OpCode op)
    {
        switch (op.Code)
        {
            case Code.Br_S: return OpCodes.Br;
            case Code.Brfalse_S: return OpCodes.Brfalse;
            case Code.Brtrue_S: return OpCodes.Brtrue;
            case Code.Beq_S: return OpCodes.Beq;
            case Code.Bge_S: return OpCodes.Bge;
            case Code.Bgt_S: return OpCodes.Bgt;
            case Code.Ble_S: return OpCodes.Ble;
            case Code.Blt_S: return OpCodes.Blt;
            case Code.Bne_Un_S: return OpCodes.Bne_Un;
            case Code.Bge_Un_S: return OpCodes.Bge_Un;
            case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
            case Code.Ble_Un_S: return OpCodes.Ble_Un;
            case Code.Blt_Un_S: return OpCodes.Blt_Un;
            case Code.Leave_S: return OpCodes.Leave;
            default: return null;
        }
    }
}
