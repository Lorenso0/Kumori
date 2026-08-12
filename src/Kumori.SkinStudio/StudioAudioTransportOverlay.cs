using Kumori.Skins;
using osu.Framework.Audio;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Audio;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osuTK;
using osuTK.Graphics;

namespace Kumori.SkinStudio;

internal partial class StudioAudioTransportOverlay : CompositeDrawable
{
    private readonly Action<string> report;
    private readonly Container audioHost;
    private readonly FillFlowContainer waveform;
    private readonly SpriteText title;
    private readonly SpriteText position;
    private readonly ITrackStore trackStore;
    private readonly string transportRoot;
    private Track? track;

    public StudioAudioTransportOverlay(
        AudioManager audio,
        string workspaceRoot,
        Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        this.report = report;
        transportRoot = Path.Combine(
            Path.GetFullPath(workspaceRoot),
            "audio-transport");
        Directory.CreateDirectory(transportRoot);
        var storage = new NativeStorage(transportRoot);
        trackStore = audio.GetTrackStore(new StorageBackedResourceStore(storage));
        RelativeSizeAxes = Axes.Both;
        Depth = -95;
        InternalChildren =
        [
            new Box
            {
                RelativeSizeAxes = Axes.Both,
                Colour = Colour4.Black.Opacity(0.78f),
            },
            new Container
            {
                RelativeSizeAxes = Axes.Both,
                Padding = new MarginPadding
                {
                    Horizontal = 180,
                    Vertical = 150,
                },
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    CornerRadius = 12,
                    Children =
                    [
                        new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.FromHex("#1B1925"),
                        },
                        new FillFlowContainer
                        {
                            RelativeSizeAxes = Axes.Both,
                            Padding = new MarginPadding(28),
                            Direction = FillDirection.Vertical,
                            Spacing = new Vector2(0, 14),
                            Children =
                            [
                                title = label("AUDIO TRANSPORT", 21, true),
                                position = label("00:00.000 / 00:00.000", 12, false),
                                waveform = new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    Height = 92,
                                    Direction = FillDirection.Horizontal,
                                    Spacing = new Vector2(2, 0),
                                },
                                new FillFlowContainer
                                {
                                    RelativeSizeAxes = Axes.X,
                                    AutoSizeAxes = Axes.Y,
                                    Direction = FillDirection.Horizontal,
                                    Spacing = new Vector2(8, 0),
                                    Children =
                                    [
                                        transportButton("Play", play),
                                        transportButton("Pause", pause),
                                        transportButton("−5 seconds", () => seekBy(-5_000)),
                                        transportButton("+5 seconds", () => seekBy(5_000)),
                                        transportButton("Restart", restart),
                                        transportButton("Stop", stop),
                                        transportButton("Close", Hide),
                                    ],
                                },
                                label(
                                    "This transport plays the effective draft file through lazer's real track backend. Normal sample tiles continue to use SkinnableSound.",
                                    11,
                                    false),
                            ],
                        },
                        audioHost = new Container
                        {
                            Alpha = 0,
                            Width = 1,
                            Height = 1,
                        },
                    ],
                },
            },
        ];
        Hide();
    }

    public void Present(
        string filename,
        byte[] bytes,
        SkinAudioAnalysis analysis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(analysis);
        disposeTrack();
        var resourceName =
            $"{SkinDraftWorkspaceService.Hash(bytes)}{Path.GetExtension(filename).ToLowerInvariant()}";
        var resourcePath = Path.Combine(transportRoot, resourceName);
        if (!File.Exists(resourcePath))
            File.WriteAllBytes(resourcePath, bytes);
        track = trackStore.Get(resourceName)
                ?? throw new InvalidDataException(
                    "Lazer's real track backend could not load the selected audio.");
        audioHost.Add(new DrawableTrack(track));
        title.Text = $"AUDIO TRANSPORT · {filename}";
        rebuildWaveform(analysis);
        updatePosition();
        Show();
        report($"Opened real-track transport for {filename}.");
    }

    protected override void Update()
    {
        base.Update();
        if (IsPresent)
            updatePosition();
    }

    public override void Hide()
    {
        pause();
        base.Hide();
    }

    private void play()
    {
        if (track is null)
            return;
        if (track.CurrentTime >= track.Length)
            track.Seek(0);
        track.Start();
        report("Audio transport playing.");
    }

    private void pause()
    {
        track?.Stop();
    }

    private void restart()
    {
        if (track is null)
            return;
        track.Seek(0);
        track.Start();
        report("Audio transport restarted.");
    }

    private void stop()
    {
        if (track is null)
            return;
        track.Stop();
        track.Seek(0);
        report("Audio transport stopped.");
    }

    private void seekBy(double delta)
    {
        if (track is null)
            return;
        track.Seek(ClampSeek(track.CurrentTime, delta, track.Length));
        updatePosition();
    }

    private void updatePosition()
    {
        position.Text = track is null
            ? "00:00.000 / 00:00.000"
            : $"{FormatPosition(track.CurrentTime)} / {FormatPosition(track.Length)}";
    }

    private void rebuildWaveform(SkinAudioAnalysis analysis)
    {
        waveform.Clear();
        var peak = Math.Max(analysis.Peak, 0.0001f);
        foreach (var value in analysis.Waveform)
        {
            waveform.Add(new Box
            {
                Width = 12,
                Height = Math.Max(3, 84 * Math.Clamp(value / peak, 0, 1)),
                Anchor = Anchor.CentreLeft,
                Origin = Anchor.CentreLeft,
                Colour = Colour4.FromHex("#FF84B7"),
            });
        }
    }

    private void disposeTrack()
    {
        track?.Stop();
        audioHost.Clear(disposeChildren: true);
        track = null;
    }

    internal static double ClampSeek(
        double current,
        double delta,
        double length) =>
        Math.Clamp(current + delta, 0, Math.Max(0, length));

    internal static string FormatPosition(double milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds))
            .ToString(@"mm\:ss\.fff");

    internal void PlayAcceptance() => play();

    internal void PauseAcceptance() => pause();

    internal void SeekAcceptance(double delta) => seekBy(delta);

    internal void RestartAcceptance() => restart();

    internal void StopAcceptance() => stop();

    internal bool AcceptanceIsRunning => track?.IsRunning == true;

    internal double AcceptanceCurrentTime => track?.CurrentTime ?? 0;

    internal double AcceptanceLength => track?.Length ?? 0;

    internal string AcceptanceTitle => title.Text.ToString();

    private static StudioActionButton transportButton(
        string text,
        Action action) =>
        new(text, action)
        {
            RelativeSizeAxes = Axes.None,
            Width = 132,
            Height = 38,
        };

    private static SpriteText label(string text, float size, bool bold) => new()
    {
        Text = text,
        Font = FontUsage.Default.With(
            size: size,
            weight: bold ? "SemiBold" : "Regular"),
        Colour = bold ? Colour4.White : Colour4.FromHex("#C6A8BA"),
    };
}
