namespace ModFramework.Runtime;

internal sealed class DependencyProblem
{
    internal DependencyProblem(string code, string message)
    {
        Code = code;
        Message = message;
    }

    internal string Code { get; }

    internal string Message { get; }
}

/// <summary>
/// Result of dependency resolution: a deterministic load order plus everything that
/// prevents a mod from running. Order never depends on file-system enumeration order:
/// nodes are always visited in ordinal id order.
/// </summary>
internal sealed class DependencyPlan
{
    internal List<ModManifest> Order { get; } = new();

    internal Dictionary<string, DependencyProblem> Problems { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, List<string>> OptionalAbsent { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal List<string> Cycles { get; } = new();
}

/// <summary>
/// Validates declared dependencies, rejects cycles and produces a topological order.
///
/// Edges point from a dependency to its dependent, so "B depends on A" becomes A -&gt; B and
/// a cycle is a back edge in that graph.
/// </summary>
internal static class DependencyResolver
{
    internal static DependencyPlan Resolve(List<ModManifest> mods)
    {
        var plan = new DependencyPlan();
        var byId = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mods.Count; i++)
        {
            byId[mods[i].Id] = mods[i];
        }

        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mods.Count; i++)
        {
            dependents[mods[i].Id] = new List<string>();
            indegree[mods[i].Id] = 0;
        }

        for (var i = 0; i < mods.Count; i++)
        {
            var mod = mods[i];
            for (var d = 0; d < mod.Dependencies.Count; d++)
            {
                var dep = mod.Dependencies[d];
                ModManifest? target;
                if (!byId.TryGetValue(dep.Id, out target))
                {
                    if (dep.Optional)
                    {
                        List<string>? list;
                        if (!plan.OptionalAbsent.TryGetValue(mod.Id, out list))
                        {
                            list = new List<string>();
                            plan.OptionalAbsent[mod.Id] = list;
                        }
                        list.Add(dep.Id);
                        continue;
                    }
                    SetProblem(plan, mod.Id, ErrorCodes.DependencyMissing,
                        "required dependency \"" + dep.Id + "\" is not installed");
                    continue;
                }

                if (!dep.Range.IsSatisfiedBy(target.Version))
                {
                    SetProblem(plan, mod.Id, ErrorCodes.DependencyVersionMismatch,
                        "dependency \"" + dep.Id + "\" is version " + target.VersionText +
                        ", which does not satisfy \"" + dep.Range.Describe() + "\"");
                    continue;
                }

                dependents[dep.Id].Add(mod.Id);
                indegree[mod.Id] = indegree[mod.Id] + 1;
            }
        }

        DetectCycles(mods, dependents, plan);

        // Kahn's algorithm over the problem-free nodes, always taking the smallest ready id.
        var ready = new List<string>();
        for (var i = 0; i < mods.Count; i++)
        {
            var id = mods[i].Id;
            if (indegree[id] == 0 && !plan.Problems.ContainsKey(id))
            {
                ready.Add(id);
            }
        }
        ready.Sort(StringComparer.Ordinal);

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            emitted.Add(id);
            plan.Order.Add(byId[id]);

            var list = dependents[id];
            var newlyReady = new List<string>();
            for (var i = 0; i < list.Count; i++)
            {
                var child = list[i];
                indegree[child] = indegree[child] - 1;
                if (indegree[child] == 0 && !plan.Problems.ContainsKey(child) && !emitted.Contains(child))
                {
                    newlyReady.Add(child);
                }
            }
            if (newlyReady.Count > 0)
            {
                for (var i = 0; i < newlyReady.Count; i++)
                {
                    ready.Add(newlyReady[i]);
                }
                ready.Sort(StringComparer.Ordinal);
            }
        }

        // Anything not emitted either has a problem or sits in/behind a cycle. A node that
        // only depends on a problem node keeps indegree > 0 and is reported as blocked.
        var leftovers = new List<string>();
        for (var i = 0; i < mods.Count; i++)
        {
            var id = mods[i].Id;
            if (emitted.Contains(id))
            {
                continue;
            }
            if (!plan.Problems.ContainsKey(id))
            {
                SetProblem(plan, id, ErrorCodes.DependencyFailed,
                    "a dependency did not load, so this mod is not started");
            }
            leftovers.Add(id);
        }
        leftovers.Sort(StringComparer.Ordinal);
        for (var i = 0; i < leftovers.Count; i++)
        {
            plan.Order.Add(byId[leftovers[i]]);
        }

        return plan;
    }

    private static void SetProblem(DependencyPlan plan, string id, string code, string message)
    {
        if (!plan.Problems.ContainsKey(id))
        {
            plan.Problems[id] = new DependencyProblem(code, message);
        }
    }

    private static void DetectCycles(
        List<ModManifest> mods,
        Dictionary<string, List<string>> dependents,
        DependencyPlan plan)
    {
        var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        for (var i = 0; i < mods.Count; i++)
        {
            color[mods[i].Id] = 0;
        }
        for (var i = 0; i < mods.Count; i++)
        {
            var id = mods[i].Id;
            if (color[id] == 0)
            {
                Visit(id, dependents, color, stack, plan);
            }
        }
    }

    private static void Visit(
        string id,
        Dictionary<string, List<string>> dependents,
        Dictionary<string, int> color,
        List<string> stack,
        DependencyPlan plan)
    {
        color[id] = 1;
        stack.Add(id);

        List<string>? children;
        if (dependents.TryGetValue(id, out children) && children != null)
        {
            var sorted = new List<string>(children);
            sorted.Sort(StringComparer.Ordinal);
            for (var i = 0; i < sorted.Count; i++)
            {
                var child = sorted[i];
                int state;
                if (!color.TryGetValue(child, out state))
                {
                    state = 0;
                }
                if (state == 1)
                {
                    var cycle = new List<string>();
                    var started = false;
                    for (var s = 0; s < stack.Count; s++)
                    {
                        if (!started && string.Equals(stack[s], child, StringComparison.OrdinalIgnoreCase))
                        {
                            started = true;
                        }
                        if (started)
                        {
                            cycle.Add(stack[s]);
                        }
                    }
                    cycle.Add(child);
                    var text = "";
                    for (var c = 0; c < cycle.Count; c++)
                    {
                        if (c > 0)
                        {
                            text += " -> ";
                        }
                        text += cycle[c];
                    }
                    plan.Cycles.Add(text);
                    for (var c = 0; c < cycle.Count - 1; c++)
                    {
                        SetProblem(plan, cycle[c], ErrorCodes.DependencyCycle,
                            "dependency cycle: " + text);
                    }
                }
                else if (state == 0)
                {
                    Visit(child, dependents, color, stack, plan);
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        color[id] = 2;
    }
}
