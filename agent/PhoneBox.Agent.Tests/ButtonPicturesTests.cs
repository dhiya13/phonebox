using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// A Big button's own picture: stored once by content, served only while a button uses it, cleaned up when none does
/// (after a grace period for uploads whose layout is not saved yet).
/// </summary>
public sealed class ButtonPicturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"phonebox-pics-{Guid.NewGuid():N}");
    private readonly string _cfg = Path.Combine(Path.GetTempPath(), $"phonebox-test-{Guid.NewGuid():N}.json");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        try { File.Delete(_cfg); } catch { }
    }

    private AgentConfig Config(params object[] buttonOptions)
    {
        var widgets = buttonOptions.Select((o, i) => (object)new { id = "b" + i, type = "button", x = 0, y = i, w = 3, h = 1, options = o }).ToList();
        widgets.Add(new
        {
            id = "stack", type = "stack", x = 6, y = 0, w = 3, h = 3,
            options = new { widgets = new object[] { new { id = "card", type = "button", enabled = true, options = new { picture = "abcdefabcdefabcd" } } } },
        });
        File.WriteAllText(_cfg, JsonSerializer.Serialize(new { pages = new[] { new { id = "home", title = "Home", enabled = true, widgets } } }));
        return new ConfigStore(_cfg).Get();
    }

    [Fact]
    public void SameBytes_SameId_StoredOnce()
    {
        var pics = new ButtonPictures(_dir);
        var (a, errA) = pics.Save("image/png", Png);
        var (b, errB) = pics.Save("image/png", Png);
        Assert.Null(errA); Assert.Null(errB);
        Assert.Equal(a, b);
        Assert.True(ButtonPictures.ValidId(a));
        Assert.Single(Directory.GetFiles(_dir));
        Assert.Equal(Png, pics.Read(a!)!.Value.Bytes);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")] // not an SVG document
    public void NotAPicture_IsRefused(string type)
    {
        var (id, error) = new ButtonPictures(_dir).Save(type, "<html><script>alert(1)</script></html>"u8.ToArray());
        Assert.Null(id);
        Assert.NotNull(error);
    }

    [Fact]
    public void TooLarge_IsRefused()
    {
        var (id, error) = new ButtonPictures(_dir).Save("image/png", null);
        Assert.Null(id);
        Assert.Contains("larger", error);
    }

    [Theory]
    [InlineData("../../config")]
    [InlineData("ABCDEFABCDEFABCD")]
    [InlineData("abc")]
    public void OddIds_ReadNothing(string id) => Assert.Null(new ButtonPictures(_dir).Read(id));

    [Fact]
    public void Used_ListsButtonsAndStackCards()
    {
        var used = ButtonPictures.Used(Config(new { picture = "0123456789abcdef" }, new { picture = "../evil" }, new { }));
        Assert.Equal(["0123456789abcdef", "abcdefabcdefabcd"], used.Order().ToArray());
    }

    [Fact]
    public void Cleanup_KeepsUsedAndFresh_DropsOldUnused()
    {
        var pics = new ButtonPictures(_dir);
        var (used, _) = pics.Save("image/png", Png);
        var (old, _) = pics.Save("image/png", [.. Png, 9]);
        var (fresh, _) = pics.Save("image/png", [.. Png, 8]);
        foreach (var f in Directory.GetFiles(_dir).Where(f => !f.Contains(fresh!)))
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow - ButtonPictures.Grace - TimeSpan.FromHours(1));
        pics.Cleanup(new HashSet<string> { used! });
        Assert.NotNull(pics.Read(used!));
        Assert.NotNull(pics.Read(fresh!));
        Assert.Null(pics.Read(old!));
    }
}
