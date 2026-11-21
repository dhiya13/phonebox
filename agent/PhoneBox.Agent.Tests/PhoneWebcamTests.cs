using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>The phone webcam takes pictures only for a widget that allows it, one phone at a time, and never shows an old one as live.</summary>
public sealed class PhoneWebcamTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phonebox-webcam-{Guid.NewGuid():N}.json");
    private readonly FakeOutput _out = new();
    private long _now = 100_000;
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class FakeOutput : IWebcamOutput
    {
        public readonly List<int> Pushed = [];
        public int Stops;
        public string State => "obs";
        public void Push(byte[] jpeg) => Pushed.Add(jpeg.Length);
        public void Stop() => Stops++;
        public bool Armed;
        public int Readers { get; set; } = -1;
        public void Arm(bool on) { Armed = on; if (!on) Readers = -1; else if (Readers < 0) Readers = 0; }
        public void Dispose() { }
    }

    private PhoneWebcam Make(bool enabledPage = true)
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "home", "title": "Home", "widgets": [] }, { "id": "p", "title": "P", "enabled": {{(enabledPage ? "true" : "false")}}, "widgets": [
                { "id": "cam", "type": "webcam", "x": 0, "y": 0, "w": 4, "h": 4, "options": { "facing": "front" } },
                { "id": "local", "type": "webcam", "x": 4, "y": 0, "w": 4, "h": 4, "options": { "pc": false } },
                { "id": "d", "type": "dictate", "x": 8, "y": 0, "w": 4, "h": 3, "options": {} } ] } ] }
            """);
        return new PhoneWebcam(new ConfigStore(_path), NullLogger.Instance, _out) { Now = () => _now };
    }

    private static string Json(object body) => JsonSerializer.Serialize(body);

    private readonly string _dir = Directory.CreateTempSubdirectory("phonebox-webcam-").FullName;

    private (PhoneWebcam Cam, PushStore Push) MakeAuto(string options = """{ "auto": true }""", bool enabledPage = true)
    {
        File.WriteAllText(_path, $$"""
            { "pages": [ { "id": "home", "title": "Home", "widgets": [] }, { "id": "live", "title": "Live", "enabled": {{(enabledPage ? "true" : "false")}}, "widgets": [
                { "id": "cam", "type": "webcam", "x": 0, "y": 0, "w": 12, "h": 6, "options": {{options}} } ] } ] }
            """);
        var push = new PushStore(NullLogger<PushStore>.Instance, Path.Combine(_dir, "events.json"));
        return (new PhoneWebcam(new ConfigStore(_path), NullLogger.Instance, _out, push, timer: false) { Now = () => _now }, push);
    }

    private static List<bool> WantedEvents(PushStore push) =>
        push.Since(0).Where(e => e.Kind == "webcam").Select(e => e.Data!.Value.GetProperty("wanted").GetBoolean()).ToList();

    [Theory]
    [InlineData("""{ "facing": "front" }""", true)]       // auto-start is off by default
    [InlineData("""{ "auto": true, "pc": false }""", true)] // the picture may not go to the computer
    [InlineData("""{ "auto": true }""", false)]            // on a disabled page
    public void TheCameraIsOnlyArmedForAnEnabledAutoStartWidget(string options, bool enabledPage)
    {
        var (cam, _) = MakeAuto(options, enabledPage);
        using (cam)
        {
            cam.Watch();
            Assert.False(_out.Armed);
        }
    }

    [Fact]
    public void AnAppReadingTheCameraAsksThePhoneAndLettingGoEndsIt()
    {
        var (cam, push) = MakeAuto();
        using (cam)
        {
            cam.Watch();
            Assert.True(_out.Armed);
            Assert.False(cam.Wanted);
            _out.Readers = 1;
            cam.Watch();                       // a glance (an app reading the formats) isn't enough
            Assert.False(cam.Wanted);
            _now += (long)PhoneWebcam.WantDelay.TotalMilliseconds;
            cam.Watch();
            Assert.True(cam.Wanted);
            Assert.Equal([true], WantedEvents(push));

            _out.Readers = 0;                  // the app reopens the camera (another resolution): keep going
            cam.Watch();
            _now += 1000;
            _out.Readers = 1;
            cam.Watch();
            Assert.True(cam.Wanted);

            _out.Readers = 0;
            cam.Watch();
            _now += (long)PhoneWebcam.ReleaseDelay.TotalMilliseconds;
            cam.Watch();
            Assert.False(cam.Wanted);
            Assert.Equal([true, false], WantedEvents(push));
        }
    }

    [Fact]
    public void SwitchingAutoStartOffEndsTheRequestAtOnce()
    {
        var (cam, push) = MakeAuto();
        using (cam)
        {
            cam.Watch();
            _out.Readers = 1;
            cam.Watch();
            _now += (long)PhoneWebcam.WantDelay.TotalMilliseconds;
            cam.Watch();
            Assert.True(cam.Wanted);
            File.WriteAllText(_path, """{ "pages": [ { "id": "home", "title": "Home", "widgets": [] } ] }""");
            _now += 10;
            WaitFor(() => { cam.Watch(); return !_out.Armed; });
            Assert.False(cam.Wanted);
            Assert.Equal([true, false], WantedEvents(push));
        }
    }

    private static void WaitFor(Func<bool> ok)
    {
        // the config store picks up a file change on its own (a file watcher)
        for (var i = 0; i < 100 && !ok(); i++) Thread.Sleep(50);
        Assert.True(ok());
    }

    [Fact]
    public void TheStateTellsThePhoneWhetherAnAppWantsTheCamera()
    {
        var (cam, _) = MakeAuto();
        using (cam)
        {
            Assert.Contains("\"wanted\":false", Json(cam.Status()));
        }
    }

    [Fact]
    public void APictureFromItsWidgetGoesToTheVirtualCamera()
    {
        using var cam = Make();
        var (status, body) = cam.Accept(null, "p/cam", Jpeg);
        Assert.Equal(200, status);
        Assert.Equal("""{"ok":true,"pc":"obs"}""", Json(body));
        Assert.Equal([Jpeg.Length], _out.Pushed);
        Assert.True(cam.Live);
        Assert.Same(Jpeg, cam.Latest());
    }

    [Theory]
    [InlineData("p/nope")]   // no such widget
    [InlineData("p/d")]      // a widget of another type
    [InlineData("p/local")]  // "Send the picture to the computer" is off
    [InlineData(null)]
    public void OnlyAWidgetThatAllowsItMaySend(string? widget)
    {
        using var cam = Make();
        Assert.Equal(403, cam.Accept(null, widget, Jpeg).Status);
        Assert.Empty(_out.Pushed);
        Assert.False(cam.Live);
    }

    [Fact]
    public void AWidgetOnADisabledPageMayNotSend()
    {
        using var cam = Make(enabledPage: false);
        Assert.Equal(403, cam.Accept(null, "p/cam", Jpeg).Status);
    }

    [Fact]
    public void OnlyJpegPicturesOfACameraSizeAreTaken()
    {
        using var cam = Make();
        Assert.Equal(415, cam.Accept(null, "p/cam", "<html>"u8.ToArray()).Status);
        var huge = new byte[PhoneWebcam.MaxFrameBytes + 1];
        Jpeg.CopyTo(huge, 0);
        Assert.Equal(413, cam.Accept(null, "p/cam", huge).Status);
        Assert.Empty(_out.Pushed);
    }

    [Fact]
    public void OnePhoneAtATime()
    {
        using var cam = Make();
        Assert.Equal(200, cam.Accept("phone-a", "p/cam", Jpeg).Status);
        Assert.Equal(409, cam.Accept("phone-b", "p/cam", Jpeg).Status);
        // phone A went quiet: phone B may take over
        _now += (long)PhoneWebcam.Stale.TotalMilliseconds;
        Assert.Equal(200, cam.Accept("phone-b", "p/cam", Jpeg).Status);
    }

    [Fact]
    public void WithoutNewPicturesTheStreamEnds()
    {
        using var cam = Make();
        cam.Accept(null, "p/cam", Jpeg);
        _now += 1000;
        cam.Expire();
        Assert.True(cam.Live);
        _now += (long)PhoneWebcam.Stale.TotalMilliseconds;
        cam.Expire();
        Assert.False(cam.Live);
        Assert.Null(cam.Latest());
        Assert.Equal(1, _out.Stops);
    }

    [Fact]
    public void OnlyThePhoneThatSendsCanStopIt()
    {
        using var cam = Make();
        cam.Accept("phone-a", "p/cam", Jpeg);
        cam.Stop("phone-b");
        Assert.True(cam.Live);
        cam.Stop("phone-a");
        Assert.False(cam.Live);
        Assert.Equal(1, _out.Stops);
    }

    [Fact]
    public async Task TheMjpegStreamGetsEachNewPictureAndEndsWithTheCamera()
    {
        using var cam = Make();
        cam.Accept(null, "p/cam", Jpeg);
        var first = await cam.NextAsync(0, TimeSpan.FromSeconds(5), default);
        Assert.NotNull(first);
        var waiting = cam.NextAsync(first.Value.Seq, TimeSpan.FromSeconds(5), default);
        Assert.False(waiting.IsCompleted);
        cam.Accept(null, "p/cam", Jpeg);
        Assert.Equal(first.Value.Seq + 1, (await waiting)!.Value.Seq);
        var ended = cam.NextAsync(first.Value.Seq + 1, TimeSpan.FromSeconds(5), default);
        cam.Stop(null);
        Assert.Null(await ended);
    }

    [Fact]
    public void TheQueueLayoutMatchesObs()
    {
        // 80-byte header aligned to 96, then three 1280 × 720 NV12 frames behind 32-byte frame headers, 32-byte aligned
        var (offsets, size) = PhoneWebcam.ObsQueueLayout(1280, 720);
        Assert.Equal([96u, 1_382_528u, 2_764_960u], offsets);
        Assert.Equal(4_147_392u, size);
    }

    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)] // white
    [InlineData(0, 0, 0, 16, 128, 128)]        // black
    [InlineData(255, 0, 0, 82, 90, 240)]       // red
    public void PixelsBecomeNv12(byte r, byte g, byte b, byte y, byte u, byte v)
    {
        const int w = 4, h = 2;
        var bgrx = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { bgrx[i * 4] = b; bgrx[i * 4 + 1] = g; bgrx[i * 4 + 2] = r; }
        var nv12 = new byte[w * h * 3 / 2];
        PhoneWebcam.BgrxToNv12(bgrx, w * 4, w, h, nv12);
        Assert.All(nv12[..(w * h)], p => Assert.Equal(y, p));
        Assert.Equal([u, v, u, v], nv12[(w * h)..]);
    }

    [Fact]
    public void OnlyTheUploadIsReachableOverWifi()
    {
        Assert.True(Lan.AllowedOverLan(new PathString("/api/webcam/frame")));
        Assert.True(Lan.AllowedOverLan(new PathString("/api/webcam/stop")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/webcam/mjpeg")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/webcam/latest.jpg")));
        Assert.False(Lan.AllowedOverLan(new PathString("/api/webcam/status")));
    }
}
