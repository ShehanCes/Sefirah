using System.Globalization;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Reads and controls the default macOS output volume via osascript (Core Audio).
/// </summary>
internal sealed partial class MacOsAudioClient(ILogger<MacOsAudioClient> logger) : IAsyncDisposable
{
    private const string DefaultDeviceId = "default";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Lock sync = new();
    private CancellationTokenSource? pollCts;
    private Task? pollTask;
    private PulseAudioSink? lastSink;
    private bool isAvailable;

    public event Action<IReadOnlyList<PulseAudioSink>>? SinksChanged;
#pragma warning disable CS0067 // Only one default output device is exposed on macOS.
    public event Action<string>? SinkRemoved;
#pragma warning restore CS0067

    public bool IsAvailable => isAvailable;

    public void Initialize()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            var sink = ReadDefaultSink();
            if (sink is null)
            {
                logger.Warn("Unable to read macOS output volume; Audio Sync is disabled.");
                return;
            }

            lock (sync)
                lastSink = sink;

            isAvailable = true;
            SinksChanged?.Invoke([sink]);

            pollCts = new CancellationTokenSource();
            pollTask = Task.Run(() => PollLoopAsync(pollCts.Token));
            logger.Info("macOS system volume sync initialized");
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to initialize macOS audio client", ex);
            isAvailable = false;
        }
    }

    public IReadOnlyList<PulseAudioSink> GetCurrentSinks()
    {
        if (!isAvailable)
            return [];

        var sink = ReadDefaultSink();
        if (sink is null)
            return [];

        lock (sync)
            lastSink = sink;

        return [sink];
    }

    public void SetVolume(string sinkName, float volumeScalar)
    {
        if (!isAvailable)
            return;

        var percent = (int)Math.Clamp(Math.Round(volumeScalar * 100f), 0, 100);
        RunOsascript($"set volume output volume {percent}");
        NotifyIfChanged();
    }

    public void ToggleMute(string sinkName)
    {
        if (!isAvailable)
            return;

        var muted = ReadMuted();
        RunOsascript(muted
            ? "set volume without output muted"
            : "set volume with output muted");
        NotifyIfChanged();
    }

    public void SetDefaultSink(string sinkName)
    {
        // Only one logical output device is exposed.
    }

    public async ValueTask DisposeAsync()
    {
        isAvailable = false;
        if (pollCts is not null)
        {
            await pollCts.CancelAsync().ConfigureAwait(false);
            pollCts.Dispose();
            pollCts = null;
        }

        if (pollTask is not null)
        {
            try { await pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            pollTask = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                NotifyIfChanged();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void NotifyIfChanged()
    {
        var sink = ReadDefaultSink();
        if (sink is null)
            return;

        PulseAudioSink? previous;
        lock (sync)
        {
            previous = lastSink;
            lastSink = sink;
        }

        if (previous is null ||
            Math.Abs(previous.Volume - sink.Volume) > 0.005f ||
            previous.IsMuted != sink.IsMuted ||
            !string.Equals(previous.Description, sink.Description, StringComparison.Ordinal))
        {
            SinksChanged?.Invoke([sink]);
        }
    }

    private PulseAudioSink? ReadDefaultSink()
    {
        try
        {
            var output = RunOsascript(
                "set vol to output volume of (get volume settings)\n" +
                "set muted to output muted of (get volume settings)\n" +
                "return (vol as text) & \"|\" & (muted as text)");
            if (string.IsNullOrWhiteSpace(output))
                return null;

            var parts = output.Split('|', 2, StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                return null;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
                return null;

            var muted = parts[1].Equals("true", StringComparison.OrdinalIgnoreCase);
            var deviceName = MacOsNative.GetDefaultOutputDeviceName();
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = "System Output";

            return new PulseAudioSink(
                DefaultDeviceId,
                deviceName,
                Math.Clamp(percent / 100f, 0f, 1f),
                muted,
                IsDefault: true);
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to read macOS volume settings", ex);
            return null;
        }
    }

    private bool ReadMuted()
    {
        var output = RunOsascript("output muted of (get volume settings)");
        return output.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private string RunOsascript(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start osascript");
        var stdout = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(3000);
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"osascript failed: {err}");
        }

        return stdout;
    }
}
