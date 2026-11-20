using System.Security.Cryptography;

namespace PhoneBox.Agent;

/// <summary>
/// Pictures uploaded for a Big button itself (Layout › Big button › Picture), so a button needs no shortcut to carry
/// one. Stored under the config folder as icons/buttons/&lt;id&gt;.&lt;ext&gt;, where the id is the start of the
/// picture's SHA-256: the same picture is stored once, a new picture gets a new id (the phone may cache it for good),
/// and the id is all a button's "picture" option holds. The phone may read a picture only while a button uses it.
/// </summary>
public sealed class ButtonPictures(string dir)
{
    private readonly ShortcutIconUploads _store = new(dir);
    private readonly string _dir = dir;

    /// <summary>Pictures uploaded less than this long ago survive a cleanup: the layout that uses them may not be saved yet.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    public static bool ValidId(string? id) => id is { Length: 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string IdOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    /// <summary>The picture ids the Big buttons in <paramref name="cfg"/> use (any page, Smart Stack cards included).</summary>
    public static HashSet<string> Used(AgentConfig cfg) =>
        cfg.Widgets().Where(w => w.Type == "button").Select(w => w.Str("picture")).OfType<string>().Where(ValidId).ToHashSet(StringComparer.Ordinal);

    /// <returns>The new picture's id, or an error message.</returns>
    public (string? Id, string? Error) Save(string? contentType, byte[]? bytes)
    {
        if (bytes is null) return (null, $"The picture is larger than {ShortcutIconUploads.MaxBytes / 1024} KB");
        var id = bytes.Length == 0 ? "" : IdOf(bytes);
        var error = _store.Save(bytes.Length == 0 ? "empty" : id, contentType, bytes);
        return error is null ? (id, null) : (null, error.Replace("icon", "picture"));
    }

    public (byte[] Bytes, string ContentType)? Read(string id) => ValidId(id) ? _store.Read(id) : null;

    /// <summary>Deletes pictures no button uses, except fresh uploads (see <see cref="Grace"/>).</summary>
    public void Cleanup(IReadOnlySet<string> used)
    {
        if (!Directory.Exists(_dir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (used.Contains(id) || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Grace) continue;
                _store.Remove(id);
            }
        }
        catch { }
    }
}
