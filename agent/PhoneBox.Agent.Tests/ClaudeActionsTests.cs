using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Claude actions widget: Continue / Retry reach exactly the session whose Stop hook waits, and only while a widget that
/// allows answering is on a page a phone shows, a phone is there and nobody is at the PC.
/// </summary>
public sealed class ClaudeActionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-claude-actions-" + Guid.NewGuid().ToString("N"));
    private double _idle = 600;
    private bool _phone = true;

    public ClaudeActionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Pages = """
        "pages": [
          { "id": "p", "title": "P", "widgets": [
            { "id": "ca", "type": "claude-actions", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": true } },
            { "id": "off", "type": "claude-actions", "x": 6, "y": 0, "w": 6, "h": 4, "options": { "control": false } } ] },
          { "id": "hidden", "title": "H", "enabled": false, "widgets": [
            { "id": "ca", "type": "claude-actions", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": true } } ] } ]
        """;

    private ConfigStore Config(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return new ConfigStore(path);
    }

    private ClaudeActions Make(string? json = null) =>
        new(Config(json ?? "{" + Pages + "}"), new SessionMonitor(NullLogger<SessionMonitor>.Instance),
            new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json")), NullLogger<ClaudeActions>.Instance)
        {
            IdleSeconds = () => _idle,
            PhoneConnected = () => _phone,
            Poll = TimeSpan.FromMilliseconds(10),
        };

    private static JsonElement Stop(string session = "s-1") =>
        JsonSerializer.SerializeToElement(new { session_id = session, cwd = @"C:\Work\phonebox", hook_event_name = "Stop", stop_hook_active = false });

    private static async Task<HeldSession> Held(ClaudeActions a)
    {
        for (var i = 0; i < 500; i++)
        {
            if (a.Snapshot() is [var h]) return h;
            await Task.Delay(10);
        }
        throw new TimeoutException("the session never waited");
    }

    private static JsonObject? Output(object? o) => o is null ? null : JsonSerializer.SerializeToNode(o)!.AsObject();

    [Theory]
    [InlineData("continue", ClaudeActions.ContinueReason)]
    [InlineData("retry", ClaudeActions.RetryReason)]
    public async Task ContinueAndRetryReachTheWaitingSessionAsABlockDecision(string action, string reason)
    {
        var a = Make();
        var hook = a.HoldAsync(Stop(), default);
        var h = await Held(a);
        Assert.Equal("s-1", h.Session);
        Assert.Equal("phonebox", h.Project);
        Assert.Equal((200, (string?)null), a.Decide(null, "p/ca", h.Id, action));
        var output = Output(await hook);
        Assert.Equal("block", (string?)output!["decision"]);
        Assert.Equal(reason, (string?)output["reason"]);
        Assert.Empty(a.Snapshot());
        // the hook has answered: a second press finds nothing to answer
        Assert.Equal(404, a.Decide(null, "p/ca", h.Id, action).Status);
    }

    [Fact]
    public async Task DoneLetsTheSessionStopAsUsual()
    {
        var a = Make();
        var hook = a.HoldAsync(Stop(), default);
        var h = await Held(a);
        Assert.Equal(200, a.Decide(null, "p/ca", h.Id, "done").Status);
        Assert.Null(await hook);
    }

    [Theory]
    [InlineData("p/off")]     // the widget's own switch is off
    [InlineData("hidden/ca")] // on a page that is turned off
    [InlineData("p/gone")]    // no such widget
    [InlineData(null)]        // no widget named: the card has no Continue / Retry
    public async Task OnlyAWidgetThatAllowsAnsweringMayAnswer(string? widget)
    {
        var a = Make();
        var hook = a.HoldAsync(Stop(), default);
        var h = await Held(a);
        Assert.Equal(403, a.Decide(null, widget, h.Id, "continue").Status);
        Assert.Single(a.Snapshot()); // still waiting for an answer that is allowed
        a.Decide(null, "p/ca", h.Id, "done");
        Assert.Null(await hook);
    }

    [Fact]
    public async Task AnUnknownActionIsRefused()
    {
        var a = Make();
        var hook = a.HoldAsync(Stop(), default);
        var h = await Held(a);
        Assert.Equal(400, a.Decide(null, "p/ca", h.Id, "rm -rf").Status);
        a.Decide(null, "p/ca", h.Id, "done");
        await hook;
    }

    [Fact]
    public async Task NothingWaitsWithoutAWidgetThatAllowsIt()
    {
        var off = Make("""{ "pages": [ { "id": "p", "title": "P", "widgets": [ { "id": "ca", "type": "claude-actions", "x": 0, "y": 0, "w": 6, "h": 4, "options": {} } ] } ] }""");
        Assert.Null(await off.HoldAsync(Stop(), default));
        Assert.Empty(off.Snapshot());
    }

    [Fact]
    public async Task NothingWaitsWhileSomeoneIsAtThePcOrNoPhoneIsThere()
    {
        _idle = 5;
        Assert.Null(await Make().HoldAsync(Stop(), default));
        _idle = 600;
        _phone = false;
        Assert.Null(await Make().HoldAsync(Stop(), default));
    }

    [Fact]
    public async Task TouchingThePcEndsTheWait()
    {
        var a = Make();
        var hook = a.HoldAsync(Stop(), default);
        await Held(a);
        _idle = 0; // a key or the mouse at the PC: the terminal is yours again
        Assert.Null(await hook.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(a.Snapshot());
    }

    [Fact]
    public async Task TurningTheWidgetOffEndsTheWait()
    {
        var path = Path.Combine(_dir, "live.json");
        File.WriteAllText(path, "{" + Pages + "}");
        var cfg = new ConfigStore(path);
        var a = new ClaudeActions(cfg, new SessionMonitor(NullLogger<SessionMonitor>.Instance),
            new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events2.json")), NullLogger<ClaudeActions>.Instance)
        { IdleSeconds = () => _idle, PhoneConnected = () => _phone, Poll = TimeSpan.FromMilliseconds(10) };
        var hook = a.HoldAsync(Stop(), default);
        await Held(a);
        Assert.Null(cfg.Save("{" + Pages.Replace("\"control\": true", "\"control\": false") + "}"));
        Assert.Null(await hook.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AnInterruptedSessionEndsTheWait()
    {
        var a = Make();
        using var cts = new CancellationTokenSource();
        var hook = a.HoldAsync(Stop(), cts.Token);
        await Held(a);
        cts.Cancel(); // Esc in Claude Code ends the hook process, and with it the request
        Assert.Null(await hook.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(a.Snapshot());
    }

    [Fact]
    public void AWidgetCountsOnlyOnAPageSomePhoneShows()
    {
        var shown = new ConfigStore(Path.Combine(_dir, "x.json")).Get();
        Assert.False(ClaudeActions.AnyWidgetAllows(shown)); // the default layout has no such widget
        var c = Config("""
            { "phones": [ { "id": "phone-1", "pages": ["other"] } ],
              "pages": [
                { "id": "p", "title": "P", "widgets": [ { "id": "ca", "type": "claude-actions", "x": 0, "y": 0, "w": 6, "h": 4, "options": { "control": true } } ] },
                { "id": "other", "title": "O", "widgets": [] } ] }
            """).Get();
        Assert.False(ClaudeActions.AnyWidgetAllows(c));
        Assert.False(ClaudeActions.WidgetAllows(c, "phone-1", "p/ca"));
        c.Phones[0].Pages = ["p"];
        Assert.True(ClaudeActions.AnyWidgetAllows(c));
        Assert.True(ClaudeActions.WidgetAllows(c, "phone-1", "p/ca"));
    }

    [Fact]
    public void ACardInASmartStackCounts()
    {
        var c = Config("""
            { "pages": [ { "id": "p", "title": "P", "widgets": [ { "id": "st", "type": "stack", "x": 0, "y": 0, "w": 6, "h": 4,
                "options": { "widgets": [ { "id": "c1", "type": "claude-actions", "options": { "control": true } } ] } } ] } ] }
            """).Get();
        Assert.True(ClaudeActions.AnyWidgetAllows(c));
        Assert.True(ClaudeActions.WidgetAllows(c, null, "p/st:c1"));
    }

    [Fact]
    public void InstallAddsBothHooksAndKeepsTheUsersOwn()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ] } }""");
        Assert.False(ClaudeHook.StopInstalled(path));
        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        Assert.True(ClaudeHook.Installed(path));
        Assert.True(ClaudeHook.StopInstalled(path));
        var stop = JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count);
        var ours = stop[1]!["hooks"]![0]!;
        Assert.Contains(ClaudeActions.Endpoint, (string?)ours["command"]);
        Assert.Equal(60 * 60 + 30, (int)ours["timeout"]!); // the longest wait Settings allows
        Assert.Null(stop[1]!["matcher"]);
        // installing again replaces ours instead of adding a second one
        Assert.Null(ClaudeHook.Configure(true, 18765, 30, path));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]!.AsArray().Count);
        Assert.Null(ClaudeHook.Configure(false, 18765, 30, path));
        Assert.False(ClaudeHook.Installed(path));
        Assert.False(ClaudeHook.StopInstalled(path));
        Assert.Equal("say done", (string?)JsonNode.Parse(File.ReadAllText(path))!["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void AHookFromAnOlderVersionHasNoStopEntry()
    {
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, """{ "hooks": { "PermissionRequest": [ { "matcher": "*", "hooks": [ { "type": "command", "command": "curl.exe -s -X POST http://127.0.0.1:18765/api/hooks/claude/permission" } ] } ] } }""");
        Assert.True(ClaudeHook.Installed(path));
        Assert.False(ClaudeHook.StopInstalled(path));
    }

    [Fact]
    public void AStopEntryOfAnotherShapeIsLeftForTheUserToFix()
    {
        var path = Path.Combine(_dir, "odd.json");
        File.WriteAllText(path, """{ "hooks": { "Stop": "nope" } }""");
        Assert.NotNull(ClaudeHook.Configure(true, 18765, 30, path));
        Assert.Contains("\"nope\"", File.ReadAllText(path));
    }
}
