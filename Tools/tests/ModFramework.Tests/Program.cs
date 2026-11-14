using System;
using System.IO;

namespace ModFramework.Tests;

internal static class Program
{
    internal static int Main(string[] args)
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var fixtureDir = "";
        var keepTemp = false;
        var helloWorldDir = "";
        var diffRefsDir = "";
        var p114Dir = "";
        var eventFixtureDir = "";
        var legacyModDir = "";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo-root":
                    if (i + 1 < args.Length) { repoRoot = args[++i]; }
                    break;
                case "--fixture-mod-dir":
                    if (i + 1 < args.Length) { fixtureDir = args[++i]; }
                    break;
                case "--keep-temp":
                    keepTemp = true;
                    break;
                case "--hello-world-dir":
                    if (i + 1 < args.Length) { helloWorldDir = args[++i]; }
                    break;
                case "--diff-refs":
                    if (i + 1 < args.Length) { diffRefsDir = args[++i]; }
                    break;
                case "--p114-dir":
                    if (i + 1 < args.Length) { p114Dir = args[++i]; }
                    break;
                case "--event-fixture-dir":
                    if (i + 1 < args.Length) { eventFixtureDir = args[++i]; }
                    break;
                case "--legacy-mod-dir":
                    if (i + 1 < args.Length) { legacyModDir = args[++i]; }
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("ModFramework.Tests [--repo-root <dir>] --fixture-mod-dir <dir>");
                    Console.WriteLine("  --repo-root        repository root (default: current directory)");
                    Console.WriteLine("  --fixture-mod-dir  directory containing FixtureMod.dll and FixtureMod.Dependency.dll");
                    Console.WriteLine("  --hello-world-dir  build output of ExampleMod.HelloWorld (optional)");
                    Console.WriteLine("  --p114-dir         staged P114.RuntimeProbe mod package (optional, Phase 1.1.4)");
                    Console.WriteLine("  --event-fixture-dir  EventMod.A/Z/Fault builds (optional, Phase 2.0)");
                    Console.WriteLine("  --legacy-mod-dir     LegacyFixtureMod built against the frozen 1.0.0 contract");
                    Console.WriteLine("  --diff-refs <dir>  run only the read-only parser differential over <dir>");
                    return 0;
                default:
                    Console.WriteLine("unknown argument: " + args[i]);
                    return 2;
            }
        }

        if (diffRefsDir.Length > 0)
        {
            // Read-only evidence run: no mod is loaded, no file is modified.
            return RefsDifferential.Run(diffRefsDir);
        }

        var runner = new TestRunner();
        try
        {
            Console.WriteLine("repo root : " + repoRoot);
            Console.WriteLine("fixtures  : " + (fixtureDir.Length == 0 ? "(none)" : fixtureDir));
            Console.WriteLine();

            UnitTests.Run(runner, repoRoot);

            if (fixtureDir.Length == 0)
            {
                runner.Check("host_tests_configured", false, "--fixture-mod-dir was not provided");
            }
            else
            {
                HostTests.Run(runner, fixtureDir);
                // Phase 1.1.1: dependency pre-propagation, secure content identity, host
                // fail-closed matching, PE/CLI bounds, framework-version state and redaction.
                Phase111Tests.Run(runner, fixtureDir);
                // Phase 1.1.2: snapshot binding (TOCTOU), ContentIdentity fault branches, the
                // positive host-share path, ECMA-335 assembly flags and fixed-point convergence.
                Phase112Tests.Run(runner, fixtureDir);
                // Phase 1.1.3: snapshot deduplication integrity (a fingerprint is a candidate
                // index, never a sharing permission), cross-simple-name and private-assembly
                // collisions, the independent snapshot integrity verifier and the post-load
                // assembly identity check.
                Phase113Tests.Run(runner, fixtureDir);
                if (helloWorldDir.Length > 0)
                {
                    HostTests.HelloWorld(runner, helloWorldDir);
                }
                else
                {
                    Console.WriteLine("SKIP hello_world_package_present             --hello-world-dir not provided (needs the game assemblies to build)");
                }
            }

            // Phase 1.1.4: the safe runtime probe through the real loader chain. Requires the
            // staged P114.RuntimeProbe package; when it is absent the suite is reported as
            // SKIPPED (never as passed) so a CI run without the build is not a false positive.
            if (p114Dir.Length > 0)
            {
                Phase114Tests.Run(runner, p114Dir);
            }
            else
            {
                Console.WriteLine("SKIP p114_probe_suite                        --p114-dir not provided (needs the P114.RuntimeProbe build)");
            }

            // Phase 2.0: the event contract. Dispatcher-level checks always run; the loader-level
            // checks need the fixture mods, and the binary-compatibility check needs the mod built
            // against the frozen 1.0.0 contract.
            Phase20Tests.Run(runner, eventFixtureDir, legacyModDir);
        }
        finally
        {
            if (keepTemp)
            {
                Console.WriteLine("temp directories kept for inspection");
            }
            else
            {
                Temp.Cleanup();
            }
        }

        return runner.Summary();
    }
}
