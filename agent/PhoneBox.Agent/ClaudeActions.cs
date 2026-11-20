using System.Collections.Concurrent;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <param name="Session">Claude Code's session id: the hook call that holds the session is the only way back into it.</param>
/// <param name="Title">The chat's title, only while Settings › Claude Code › "Show session titles on the phone" is on.</param>
public sealed record HeldSession(string Id, string Session, string Project, string? Title, long CreatedAt, long ExpiresAt);

/// <summary>
/// Continue / Retry for the Claude actions widget, through Claude Code's own Stop hook. When a session finishes a turn
/// while you are away from the PC, the hook call waits here for a while; the widget shows that session, and Continue or
/// Retry is returned to that one hook call as <c>{"decision":"block","reason":…}</c>, which makes exactly that session
/// carry on. Nothing is typed anywhere, so nothing can land in another window or tab.
///
/// The hook answers at once with nothing (Claude Code stops as usual) unless a Claude actions widget with "Allow
/// answering from the phone" is on an enabled page a phone shows, a phone is connected and nobody has touched the
/// PC's keyboard or mouse for a minute. The wait ends with nothing as soon as someone touches the PC, the phone goes
/// away, the widget no longer allows it, Done is pressed, or the time in Settings › Claude Code runs out.
/// </summary>
public sealed class ClaudeActions(ConfigStore config, SessionMonitor sessions, PushStore events, ILogger<ClaudeActions> log)
{
    public const string Endpoint = "/api/hooks/claude/stop";
    public const string WidgetType = "claude-actions";

    /// <summary>The PC must have been idle this long before a finished session waits for the phone.</summary>
    internal const double AwaySeconds = 60;
    /// <summary>Input at the PC more recent than this ends the wait: you are back, and the terminal is yours.</summary>
    internal const double BackSeconds = 3;
    /// <summary>A phone that has not been seen for this long ends the wait (it is not there to answer).</summary>
    internal const long PhoneGoneMs = 15_000;

    internal const string ContinueReason =
        "The user pressed Continue for this session on their PhoneBox panel. Carry on with the task from where you stopped.";
    internal const string RetryReason =
        "The user pressed Retry for this session on their PhoneBox panel. Try the last step that failed again, then carry on with the task.";

    internal Func<double> IdleSeconds { get; set; } = Idle.Seconds;
    internal Func<bool> PhoneConnected { get; set; } = () => ApprovalStore.PhoneConnected;
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    internal TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);

    private sealed record Pending(HeldSession Held, TaskCompletionSource<string?> Action);
    private readonly ConcurrentDictionary<string, Pending> _held = new();

    private long Now => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The sessions waiting for Continue / Retry, oldest first; sent to the phone with the sessions.</summary>
    public IReadOnlyList<HeldSession> Snapshot()
    {
        var now = Now;
        return _held.Values.Select(p => p.Held).Where(h => h.ExpiresAt > now).OrderBy(h => h.CreatedAt).ToList();
    }

    /// <summary>A Claude actions widget that allows answering is on an enabled page that some phone shows.</summary>
    public static bool AnyWidgetAllows(AgentConfig c)
    {
        var pages = c.Phones.Count == 0 ? c.Pages : c.Phones.SelectMany(p => c.PagesFor(p.Id)).Distinct();
        return PanelWidgets.All(pages, WidgetType).Any(w => w.Widget.Bool("control") == true);
    }

    /// <summary>The widget a phone request names, if it is there for that phone and allows answering.</summary>
    public static bool WidgetAllows(AgentConfig c, string? deviceId, string? widgetKey) =>
        PanelWidgets.Find(c, deviceId, widgetKey, WidgetType)?.Bool("control") == true;

    /// <summary>Handles one Stop hook call: the JSON for the hook to print, or null to let Claude Code stop as usual.</summary>
    public async Task<object?> HoldAsync(JsonElement input, CancellationToken aborted)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        var sessionId = Str(input, "session_id");
        if (string.IsNullOrEmpty(sessionId)) return null;
        var cfg = config.Get();
        if (!AnyWidgetAllows(cfg) || !PhoneConnected() || IdleSeconds() < AwaySeconds) return null;

        var cwd = Str(input, "cwd") ?? "";
        var session = sessions.Snapshot().FirstOrDefault(s => s.Id == "claude:" + sessionId);
        var now = Now;
        var held = new HeldSession(ApprovalStore.NewId(), sessionId, session?.Name ?? ClaudeHook.ProjectName(cwd), session?.Title,
            now, now + ClaudeConfig.ClampHold(cfg.Claude.HoldMinutes) * 60_000L);
        var pending = new Pending(held, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _held[held.Id] = pending;
        events.Emit("claude-held", JsonSerializer.SerializeToElement(new { id = held.Id }));
        log.LogInformation("claude: session in {Project} waits for Continue / Retry from the phone ({Id})", held.Project, held.Id);
        try
        {
            var phoneSeen = Now;
            while (!pending.Action.Task.IsCompleted)
            {
                await Task.WhenAny(pending.Action.Task, Task.Delay(Poll, aborted));
                if (pending.Action.Task.IsCompleted) break;
                aborted.ThrowIfCancellationRequested();
                if (PhoneConnected()) phoneSeen = Now;
                var why = Now >= held.ExpiresAt ? "time is up"
                    : IdleSeconds() < BackSeconds ? "someone is at the PC"
                    : Now - phoneSeen > PhoneGoneMs ? "the phone went away"
                    : !AnyWidgetAllows(config.Get()) ? "the widget no longer allows it"
                    : null;
                if (why is not null && pending.Action.TrySetResult(null))
                    log.LogInformation("claude: {Id} stops as usual ({Why})", held.Id, why);
            }
        }
        // the hook went away: Claude Code was interrupted (Esc) or closed
        catch (OperationCanceledException) { pending.Action.TrySetResult(null); }
        finally
        {
            _held.TryRemove(held.Id, out _);
            events.Emit("claude-held-done", JsonSerializer.SerializeToElement(new { id = held.Id }));
        }
        return await pending.Action.Task switch
        {
            "continue" => new { decision = "block", reason = ContinueReason },
            "retry" => new { decision = "block", reason = RetryReason },
            _ => null,
        };
    }

    /// <summary>Continue / Retry / Done from a phone, for the widget it names; the status code and an error to show.</summary>
    public (int Status, string? Error) Decide(string? deviceId, string? widgetKey, string id, string? action)
    {
        if (action is not ("continue" or "retry" or "done")) return (400, "unknown action");
        if (!WidgetAllows(config.Get(), deviceId, widgetKey))
            return (403, "answering is off: turn on \"Allow answering from the phone\" in the Claude actions widget's settings");
        if (!_held.TryGetValue(id, out var p) || p.Held.ExpiresAt <= Now) return (404, "this session is no longer waiting");
        if (!p.Action.TrySetResult(action)) return (404, "this session is no longer waiting");
        log.LogInformation("claude: {Action} for {Id} from the phone", action, id);
        return (200, null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
