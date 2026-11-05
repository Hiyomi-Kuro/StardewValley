using System;
using System.IO;

namespace MonoRuntimeProbe;

/// <summary>
/// Stand-in for the Phase 0 diagnostic probe.
///
/// The old loader called <c>MonoRuntimeProbe.Probe.Run(modsRoot + "/probe-output", false)</c>
/// for a bare <c>MonoRuntimeProbe.dll</c> in the mods root. This type keeps that exact shape so
/// the regression test can prove the call no longer happens: if it did, the marker file below
/// would exist.
/// </summary>
public static class Probe
{
    public static string Run(string outputDirectory, bool allowGameInvocation)
    {
        var directory = string.IsNullOrEmpty(outputDirectory) ? "." : outputDirectory;
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "probe-marker.txt");
        File.WriteAllText(marker,
            "MonoRuntimeProbe.Probe.Run executed | allowGameInvocation=" + allowGameInvocation);
        return Path.Combine(directory, "probe.txt");
    }
}
