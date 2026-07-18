using Sefirah.Data.Models;
using Sefirah.Platforms.Desktop.Services;

namespace Sefirah.Platforms.Desktop.Features;

public sealed class MediaFeature(
    ILogger<MediaFeature> logger,
    ILoggerFactory loggerFactory,
    ISessionManager sessionManager,
    IDeviceManager deviceManager) : IMediaFeature, IAsyncDisposable
{
    private readonly PulseAudioClient? pulseAudioClient =
        OperatingSystem.IsLinux() ? new PulseAudioClient(loggerFactory.CreateLogger<PulseAudioClient>()) : null;
    private readonly MacOsAudioClient? macOsAudioClient =
        OperatingSystem.IsMacOS() ? new MacOsAudioClient(loggerFactory.CreateLogger<MacOsAudioClient>()) : null;

    private readonly Dictionary<string, PulseAudioSink> knownSinks = new(StringComparer.Ordinal);
    private readonly Lock knownSinksSyncRoot = new();

    private bool IsAudioAvailable =>
        pulseAudioClient?.IsAvailable == true || macOsAudioClient?.IsAvailable == true;

    public Task InitializeAsync()
    {
        try
        {
            if (pulseAudioClient is not null)
            {
                pulseAudioClient.Initialize();
                if (!pulseAudioClient.IsAvailable)
                    return Task.CompletedTask;

                logger.Info("PulseAudio system volume sync initialized");
                SyncAudioDevicesToConnectedPeers();
                pulseAudioClient.SinksChanged += OnSinksChanged;
                pulseAudioClient.SinkRemoved += OnSinkRemoved;
            }
            else if (macOsAudioClient is not null)
            {
                macOsAudioClient.Initialize();
                if (!macOsAudioClient.IsAvailable)
                    return Task.CompletedTask;

                SyncAudioDevicesToConnectedPeers();
                macOsAudioClient.SinksChanged += OnSinksChanged;
                macOsAudioClient.SinkRemoved += OnSinkRemoved;
            }
            else
            {
                return Task.CompletedTask;
            }

            sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to initialize system volume sync", ex);
        }

        return Task.CompletedTask;
    }

    public Task HandleMediaActionAsync(MediaAction mediaAction)
    {
        if (pulseAudioClient is { IsAvailable: true })
        {
            switch (mediaAction.ActionType)
            {
                case MediaActionType.DefaultDevice:
                    pulseAudioClient.SetDefaultSink(mediaAction.Source);
                    break;
                case MediaActionType.VolumeUpdate when mediaAction.Value.HasValue:
                    pulseAudioClient.SetVolume(mediaAction.Source, Convert.ToSingle(mediaAction.Value.Value));
                    break;
                case MediaActionType.ToggleMute:
                    pulseAudioClient.ToggleMute(mediaAction.Source);
                    break;
            }
        }
        else if (macOsAudioClient is { IsAvailable: true })
        {
            switch (mediaAction.ActionType)
            {
                case MediaActionType.DefaultDevice:
                    macOsAudioClient.SetDefaultSink(mediaAction.Source);
                    break;
                case MediaActionType.VolumeUpdate when mediaAction.Value.HasValue:
                    macOsAudioClient.SetVolume(mediaAction.Source, Convert.ToSingle(mediaAction.Value.Value));
                    break;
                case MediaActionType.ToggleMute:
                    macOsAudioClient.ToggleMute(mediaAction.Source);
                    break;
            }
        }

        return Task.CompletedTask;
    }

    private void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        if (!device.IsConnected || !device.DeviceSettings.AudioSync)
            return;

        if (!IsAudioAvailable)
        {
            logger.Warn("Audio sync is enabled for this device but system audio is unavailable on this machine.");
            return;
        }

        SyncAudioDevicesToDevice(device);
    }

    private void OnSinksChanged(IReadOnlyList<PulseAudioSink> sinks)
    {
        foreach (var sink in sinks)
        {
            bool isNew;
            PulseAudioSink? previous;
            lock (knownSinksSyncRoot)
            {
                isNew = !knownSinks.TryGetValue(sink.Name, out previous);
                knownSinks[sink.Name] = sink;
            }

            if (isNew)
                SendAudioDeviceUpdate(sink.ToAudioDeviceInfo(AudioInfoType.New));
            else if (previous is not null && HasSinkStateChanged(previous, sink))
                SendAudioDeviceUpdate(sink.ToAudioDeviceInfo(AudioInfoType.Active));
        }
    }

    private void OnSinkRemoved(string sinkName)
    {
        lock (knownSinksSyncRoot)
            knownSinks.Remove(sinkName);

        SendAudioDeviceUpdate(new AudioDeviceInfo
        {
            InfoType = AudioInfoType.Removed,
            DeviceId = sinkName
        });
    }

    private void SyncAudioDevicesToConnectedPeers()
    {
        foreach (var device in deviceManager.PairedDevices)
            SyncAudioDevicesToDevice(device);
    }

    private void SyncAudioDevicesToDevice(PairedDevice device)
    {
        if (!device.IsConnected || !device.DeviceSettings.AudioSync || !IsAudioAvailable)
            return;

        var sinks = GetCurrentSinks();
        logger.Info($"Syncing {sinks.Count} audio device(s) to {device.Name}");
        foreach (var sink in sinks)
        {
            device.SendMessage(sink.ToAudioDeviceInfo(AudioInfoType.New));
            lock (knownSinksSyncRoot)
                knownSinks[sink.Name] = sink;
        }
    }

    private IReadOnlyList<PulseAudioSink> GetCurrentSinks()
    {
        if (pulseAudioClient is { IsAvailable: true })
            return pulseAudioClient.GetCurrentSinks();
        if (macOsAudioClient is { IsAvailable: true })
            return macOsAudioClient.GetCurrentSinks();
        return [];
    }

    private static bool HasSinkStateChanged(PulseAudioSink previous, PulseAudioSink current) =>
        Math.Abs(previous.Volume - current.Volume) > 0.005f ||
        previous.IsMuted != current.IsMuted ||
        previous.IsDefault != current.IsDefault;

    private void SendAudioDeviceUpdate(AudioDeviceInfo audioDevice)
    {
        try
        {
            foreach (var device in deviceManager.PairedDevices)
            {
                if (device.IsConnected && device.DeviceSettings.AudioSync && IsAudioAvailable)
                    device.SendMessage(audioDevice);
            }
        }
        catch (Exception ex)
        {
            logger.Error("Error sending audio device update", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        sessionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;

        if (pulseAudioClient is not null)
        {
            pulseAudioClient.SinksChanged -= OnSinksChanged;
            pulseAudioClient.SinkRemoved -= OnSinkRemoved;
            await pulseAudioClient.DisposeAsync().ConfigureAwait(false);
        }

        if (macOsAudioClient is not null)
        {
            macOsAudioClient.SinksChanged -= OnSinksChanged;
            macOsAudioClient.SinkRemoved -= OnSinkRemoved;
            await macOsAudioClient.DisposeAsync().ConfigureAwait(false);
        }
    }
}
