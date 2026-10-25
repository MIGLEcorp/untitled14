using System.Diagnostics;

namespace MBrowser;

/// <summary>One UI timer; no polling when all short animations have finished.</summary>
internal static class UiMotion
{
    private sealed record Track(long Started, int Duration, Action<float> Draw);
    private static readonly Dictionary<Control, Track> Tracks = [];
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 15 };
    internal static bool Enabled => SystemInformation.IsMenuAnimationEnabled;
    static UiMotion() => Timer.Tick += (_, _) =>
    {
        foreach (var (owner, track) in Tracks.ToArray())
        {
            if (owner.IsDisposed || owner.Disposing) { Tracks.Remove(owner); continue; }
            float t = Math.Clamp((Stopwatch.GetTimestamp() - track.Started) / (float)Stopwatch.Frequency * 1000 / track.Duration, 0, 1);
            track.Draw(1 - MathF.Pow(1 - t, 3));
            if (t >= 1 && Tracks.GetValueOrDefault(owner) == track) Tracks.Remove(owner);
        }
        if (Tracks.Count == 0) Timer.Stop();
    };
    internal static void Start(Control owner, int milliseconds, Action<float> draw)
    {
        Stop(owner);
        if (!Enabled) { draw(1); return; }
        Tracks[owner] = new(Stopwatch.GetTimestamp(), milliseconds, draw);
        owner.Disposed -= OwnerDisposed; owner.Disposed += OwnerDisposed;
        draw(0); Timer.Start();
    }
    private static void OwnerDisposed(object? sender, EventArgs e) { if (sender is Control owner) Stop(owner); }
    internal static void Stop(Control owner) { Tracks.Remove(owner); if (Tracks.Count == 0) Timer.Stop(); }
    internal static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.A + (to.A - from.A) * amount), (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount), (int)(from.B + (to.B - from.B) * amount));
    internal static void Reveal(Control control)
    {
        Point target = control.Location;
        Start(control, 170, progress =>
        {
            Rectangle previous = control.Bounds;
            control.Location = new Point(target.X, target.Y + (int)(10 * (1 - progress)));
            control.Parent?.Invalidate(Rectangle.Union(previous, control.Bounds), true);
        });
    }
}
