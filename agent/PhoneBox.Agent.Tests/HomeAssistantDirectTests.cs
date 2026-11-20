using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The hand-off that lets a paired phone reach Home Assistant itself while the computer is off: off unless switched on,
/// the phone's own address and token win, and only the widgets' entities go with it, with the widgets' switch rule.
/// </summary>
public sealed class HomeAssistantDirectTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-test-{Guid.NewGuid():N}.json");

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private JsonElement Direct(object homeAssistant, bool pageEnabled = true)
    {
        var cfg = new
        {
            homeAssistant,
            pages = new[]
            {
                new { id = "home", title = "Home", enabled = pageEnabled, widgets = new object[]
                {
                    new { id = "a", type = "ha", x = 0, y = 0, w = 6, h = 3, options = new { entities = "light.desk, sensor.temp", control = false } },
                    new { id = "b", type = "ha", x = 0, y = 3, w = 6, h = 3, options = new { items = new object[] { new { id = "light.desk", control = true }, new { id = "lock.door" } }, control = false } },
                    // Big buttons: one set to a Home Assistant entity, one to a shortcut (no entity of its own)
                    new { id = "c", type = "button", x = 6, y = 0, w = 3, h = 2, options = new { action = "ha", entity = " script.room_focus " } },
                    new { id = "d", type = "button", x = 6, y = 2, w = 3, h = 2, options = new { action = "shortcut", id = "x", entity = "switch.not_this" } },
                } },
                new { id = "other", title = "Other", enabled = true, widgets = Array.Empty<object>() },
            },
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(cfg));
        var store = new ConfigStore(_path);
        return JsonSerializer.SerializeToElement(HomeAssistant.DirectFor(store.Get()), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact]
    public void OffByDefault_HandsOutNothing()
    {
        var r = Direct(new { url = "http://192.168.1.10:8123", token = "secret" });
        Assert.False(r.GetProperty("enabled").GetBoolean());
        Assert.False(r.TryGetProperty("token", out _));
    }

    [Fact]
    public void Enabled_FallsBackToTheComputersAddressAndToken()
    {
        var r = Direct(new { url = "http://192.168.1.10:8123/", token = "secret", phoneDirect = true });
        Assert.True(r.GetProperty("enabled").GetBoolean());
        Assert.Equal("http://192.168.1.10:8123", r.GetProperty("url").GetString());
        Assert.Equal("secret", r.GetProperty("token").GetString());
    }

    [Fact]
    public void PhoneAddressAndTokenWin()
    {
        var r = Direct(new { url = "http://localhost:8123", token = "secret", phoneDirect = true, phoneUrl = " https://ha.example.net ", phoneToken = "phone-only" });
        Assert.Equal("https://ha.example.net", r.GetProperty("url").GetString());
        Assert.Equal("phone-only", r.GetProperty("token").GetString());
    }

    [Fact]
    public void OnlyWidgetEntitiesGo_WithTheSwitchRule()
    {
        var r = Direct(new { url = "http://192.168.1.10:8123", token = "secret", phoneDirect = true });
        var items = r.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("control").GetBoolean());
        Assert.Equal(4, items.Count);
        Assert.True(items["script.room_focus"]); // a Home Assistant Big button may always switch its entity
        Assert.True(items["light.desk"]); // one widget allows it
        Assert.False(items["sensor.temp"]);
        Assert.False(items["lock.door"]); // follows its widget's "control": false
    }

    [Fact]
    public void HiddenPages_HandOutNoEntities()
    {
        var r = Direct(new { url = "http://192.168.1.10:8123", token = "secret", phoneDirect = true }, pageEnabled: false);
        Assert.Empty(r.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http://192.168.1.10:8123", null)]
    [InlineData("ftp://192.168.1.10", "secret")]
    [InlineData("not a url", "secret")]
    public void IncompleteSettings_AreOff(string? url, string? token)
    {
        var r = Direct(new { url, token, phoneDirect = true });
        Assert.False(r.GetProperty("enabled").GetBoolean());
    }
}
