using Bfm.Core;
using Bfm.Interop;

namespace Bfm.UI;

/// <summary>
/// One-click toggle between two playback devices (by default: the connected headset and the speakers).
/// The pair can be pinned in Settings; otherwise it is picked from the active outputs on each click.
/// </summary>
internal static class AudioSwitch
{
    public sealed record State(AudioEndpoint? Current, AudioEndpoint? Next, bool CurrentIsHeadset);

    static readonly string[] HeadsetWords = { "headphone", "headset", "earphone", "airpods", "buds", "hyperx", "hands-free" };
    static readonly string[] SpeakerWords = { "speaker" };

    public static bool LooksLikeHeadset(string name) => HeadsetWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    public static State Read(Settings settings)
    {
        var outputs = CoreAudio.List(render: true);
        var defaultId = CoreAudio.GetDefaultId(render: true);
        var current = outputs.FirstOrDefault(o => o.Id == defaultId);
        var (a, b) = Pair(settings, outputs);
        AudioEndpoint? next = null;
        if (a is not null && b is not null) next = current?.Id == a.Id ? b : a;
        else next = outputs.FirstOrDefault(o => o.Id != defaultId);
        return new State(current, next, current is not null && LooksLikeHeadset(current.Name));
    }

    static (AudioEndpoint? A, AudioEndpoint? B) Pair(Settings settings, List<AudioEndpoint> outputs)
    {
        var a = outputs.FirstOrDefault(o => o.Id == settings.AudioA);
        var b = outputs.FirstOrDefault(o => o.Id == settings.AudioB);
        a ??= outputs.FirstOrDefault(o => LooksLikeHeadset(o.Name) && o.Id != b?.Id);
        b ??= outputs.FirstOrDefault(o => SpeakerWords.Any(w => o.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) && o.Id != a?.Id);
        return (a, b);
    }

    public static bool Toggle(Settings settings)
    {
        var s = Read(settings);
        return s.Next is not null && CoreAudio.SetDefault(s.Next.Id);
    }

    /// <summary>"Headphones (HyperX Cloud III Wireless)" → "Headphones"; keeps chips short.</summary>
    public static string Short(string name)
    {
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }
}
