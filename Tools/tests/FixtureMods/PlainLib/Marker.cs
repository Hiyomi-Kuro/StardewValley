namespace PlainLib;

/// <summary>
/// Marker type for the reference-free fixture. Never executed by the loader: the host-collision
/// test only needs the file to exist with a conflicting assembly identity.
/// </summary>
public static class Marker
{
    public const string Text = "plain-lib-marker";

    public static string Describe()
    {
        return Text;
    }
}
