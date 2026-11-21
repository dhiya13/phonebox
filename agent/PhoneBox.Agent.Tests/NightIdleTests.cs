using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>Night mode's idle option (OLED burn-in protection): off by default, kept on save, minutes clamped.</summary>
public sealed class NightIdleTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-test-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    private AgentConfig Load(object night)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new
        {
            night,
            pages = new[] { new { id = "home", title = "Home", enabled = true, widgets = Array.Empty<object>() } },
        }));
        return new ConfigStore(_path).Get();
    }

    [Fact]
    public void OffByDefault() => Assert.False(Load(new { enabled = true }).Night.Idle);

    [Fact]
    public void KeptWhenSet()
    {
        var n = Load(new { enabled = false, idle = true, idleMin = 7 }).Night;
        Assert.True(n.Idle);
        Assert.Equal(7, n.IdleMin);
        Assert.False(n.Enabled);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(999, 240)]
    public void MinutesAreClamped(int given, int kept) => Assert.Equal(kept, Load(new { idle = true, idleMin = given }).Night.IdleMin);

    [Fact]
    public void ThePhoneGetsIt()
    {
        var cfg = Load(new { idle = true, idleMin = 3 });
        var json = JsonSerializer.Serialize(cfg.ToPublic(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"idle\":true", json);
        Assert.Contains("\"idleMin\":3", json);
    }
}
