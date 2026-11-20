namespace PhoneBox.Agent;

/// <summary>
/// Which Windows media session the agent shows and controls (see MediaMonitor.PickSession). Windows' "current" session
/// is the one that started last and it stays current when it pauses or ends, so on its own it would make music that
/// still plays look stopped after a video started over it ends. Kept apart from MediaMonitor so it builds and is
/// tested on every platform.
/// </summary>
public static class MediaSessionPick
{
    /// <summary>
    /// Index of the session to use among <paramref name="sessions"/> (app id, playing), or null for Windows' current
    /// one: the current session when it plays, else the one shown last if it plays, else the first that plays.
    /// </summary>
    public static int? Pick(IReadOnlyList<(string App, bool Playing)> sessions, string? currentApp, string? shownApp)
    {
        int Find(string? app)
        {
            if (app is null) return -1;
            for (var i = 0; i < sessions.Count; i++) if (sessions[i].App == app && sessions[i].Playing) return i;
            return -1;
        }
        if (Find(currentApp) >= 0) return null;
        var shown = Find(shownApp);
        if (shown >= 0) return shown;
        for (var i = 0; i < sessions.Count; i++) if (sessions[i].Playing) return i;
        return null;
    }
}
