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
                case "--help":
                case "-h":
                    Console.WriteLine("ModFramework.Tests [--repo-root <dir>] --fixture-mod-dir <dir>");
                    Console.WriteLine("  --repo-root        repository root (default: current directory)");
                    Console.WriteLine("  --fixture-mod-dir  directory containing FixtureMod.dll and FixtureMod.Dependency.dll");
                    return 0;
                default:
                    Console.WriteLine("unknown argument: " + args[i]);
                    return 2;
            }
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
                if (helloWorldDir.Length > 0)
                {
                    HostTests.HelloWorld(runner, helloWorldDir);
                }
                else
                {
                    Console.WriteLine("SKIP hello_world_package_present             --hello-world-dir not provided (needs the game assemblies to build)");
                }
            }
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
