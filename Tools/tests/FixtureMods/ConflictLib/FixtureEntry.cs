using System.IO;
using ModFramework.Runtime;

namespace ConflictLib;

/// <summary>
/// Build variant marker. <c>Variant</c> is set by the driver through
/// <c>-p:Variant=VARIANT_X</c> etc.; the default is <c>A</c>.
///
/// The constant is referenced from <see cref="Entry.Initialize"/> so the compiler emits
/// different IL for each variant: that is what makes "same assembly identity, different
/// content" testable without editing the file afterwards.
/// </summary>
public static class Variant
{
#if VARIANT_B
    public const string Tag = "B";
#elif VARIANT_X
    public const string Tag = "X";
#elif VARIANT_Y
    public const string Tag = "Y";
#else
    public const string Tag = "A";
#endif

    public static string Describe()
    {
        return "variant-" + Tag;
    }
}

/// <summary>
/// Entry used by the version-gate and conflict fixtures. It leaves a marker file so a test can
/// tell "the assembly was never loaded" apart from "it loaded but Initialize did not run".
/// </summary>
public sealed class Entry : IModEntry
{
    public void Initialize(IModContext context)
    {
        context.Log.Info("conflict fixture \"" + context.ModId + "\" initialized, " + Variant.Describe());
        File.WriteAllText(Path.Combine(context.ModDirectory, "entry-ran.txt"),
            context.ModId + "|" + Variant.Tag);
    }
}
