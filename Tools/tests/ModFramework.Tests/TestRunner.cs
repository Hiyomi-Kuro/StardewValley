using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ModFramework.Tests;

internal sealed class TestRunner
{
    private readonly List<string> _failures = new();
    private int _passed;

    internal int Total => _passed + _failures.Count;

    internal void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine("PASS " + name.PadRight(46) + " " + detail);
            return;
        }
        _failures.Add(name + "  " + detail);
        Console.WriteLine("FAIL " + name.PadRight(46) + " " + detail);
    }

    internal void Equal(string name, string expected, string actual)
    {
        Check(name, string.Equals(expected, actual, StringComparison.Ordinal),
            "expected=\"" + expected + "\" actual=\"" + actual + "\"");
    }

    internal void EqualInt(string name, int expected, int actual)
    {
        Check(name, expected == actual, "expected=" + expected + " actual=" + actual);
    }

    internal void Contains(string name, string haystack, string needle)
    {
        Check(name, haystack != null && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0,
            "looking for \"" + needle + "\"");
    }

    internal int Summary()
    {
        Console.WriteLine();
        Console.WriteLine(Total + " checks, " + _passed + " passed, " + _failures.Count + " failed");
        for (var i = 0; i < _failures.Count; i++)
        {
            Console.WriteLine("  FAILED: " + _failures[i]);
        }
        return _failures.Count == 0 ? 0 : 1;
    }
}

internal static class Temp
{
    private static readonly List<string> Created = new();

    internal static string NewDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "modfw-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(path);
        Created.Add(path);
        return path;
    }

    internal static string WriteText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    internal static string ReadText(string path)
    {
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    internal static void Cleanup()
    {
        for (var i = 0; i < Created.Count; i++)
        {
            try
            {
                Directory.Delete(Created[i], true);
            }
            catch (Exception)
            {
                // best effort: a locked file must not fail the suite
            }
        }
    }
}
