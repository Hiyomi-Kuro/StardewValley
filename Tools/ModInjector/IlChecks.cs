using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModInjector;

/// <summary>
/// 注入后的结构校验。
///
/// 存在的理由：ModInjector 已经因为"生成的 IL 没有被校验"出过三次事故
/// （MVID 赋值被注释、if (minimal) 被注释、leave 用在无保护区）。
/// 这些都应该在构建阶段报错，而不是等设备上一句 InvalidProgramException。
/// </summary>
internal static class IlChecks
{
    public static void Validate(ModuleDefinition module, MethodDefinition start, MethodDefinition hook,
        Guid originalMvid)
    {
        var problems = new List<string>();

        if (module.GetType("ModFramework", "Bootstrap") is null)
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

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "generated IL failed validation:" + Environment.NewLine + "  - " +
                string.Join(Environment.NewLine + "  - ", problems));
        }
    }

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
            case Code.Ldloc:
            case Code.Ldloc_S:
            case Code.Ldloc_0:
            case Code.Ldloc_1:
            case Code.Ldloc_2:
            case Code.Ldloc_3:
            case Code.Ldloca:
            case Code.Ldloca_S:
                return (0, 1);
            case Code.Stloc:
            case Code.Stloc_S:
            case Code.Stloc_0:
            case Code.Stloc_1:
            case Code.Stloc_2:
            case Code.Stloc_3:
            case Code.Pop:
                return (1, 0);
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
            case Code.Castclass:
                // castclass pops one object reference and pushes one reference.
                return (1, 1);
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

    private static string Describe(Instruction ins) => $"IL_{ins.Offset:x4} {ins.OpCode}";
}
