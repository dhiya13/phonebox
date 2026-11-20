using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Music plays, a video starts over it (Windows makes the video "current") and then stops: the agent must keep showing
/// the music as playing, so a "media playing" rule does not end and the panel does not go back home.
/// </summary>
public sealed class MediaSessionPickTests
{
    private const string Spotify = "Spotify.exe", Chrome = "Chrome";

    [Fact]
    public void ThePlayingCurrentSession_IsUsed() =>
        Assert.Null(MediaSessionPick.Pick([(Spotify, true), (Chrome, true)], Chrome, Spotify));

    [Fact]
    public void AVideoThatStopped_GivesWayToTheMusicStillPlaying() =>
        Assert.Equal(0, MediaSessionPick.Pick([(Spotify, true), (Chrome, false)], Chrome, Chrome));

    [Fact]
    public void TheSessionAlreadyShown_WinsAmongSeveralPlaying() =>
        Assert.Equal(2, MediaSessionPick.Pick([(Spotify, true), (Chrome, false), ("VLC", true)], Chrome, "VLC"));

    [Fact]
    public void NothingPlaying_KeepsTheCurrentSession() =>
        Assert.Null(MediaSessionPick.Pick([(Spotify, false), (Chrome, false)], Chrome, Spotify));

    [Fact]
    public void NoCurrentSession_StillFindsOneThatPlays() =>
        Assert.Equal(1, MediaSessionPick.Pick([(Chrome, false), (Spotify, true)], null, null));
}
