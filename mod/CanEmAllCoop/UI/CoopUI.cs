using System;

namespace CanEmAllCoop.UI;

public static class CoopUI
{
    /// <summary>Set by the scaling module; shown in the menu and HUD, e.g. "N=2 k=1.74". Null / empty = nothing.</summary>
    public static Func<string> PaceText;

    internal static string GetPaceText()
    {
        try { return PaceText?.Invoke(); }
        catch (Exception) { return null; }
    }
}
