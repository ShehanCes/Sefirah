using System.Globalization;
using System.Text.RegularExpressions;
using Sefirah.Data.Models;

namespace Sefirah.Platforms.Desktop.Features;

public sealed partial class BatteryFeature(
    ILogger<BatteryFeature> logger,
    ISessionManager sessionManager) : IBatteryFeature, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly Regex BatteryLineRegex = BatteryRegex();

    private BatteryState? lastBatteryState;
    private CancellationTokenSource? pollCts;
    private Task? pollTask;

    public Task InitializeAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return Task.CompletedTask;

        if (GetBatteryState() is null)
        {
            logger.Info("No internal battery detected; battery sync disabled");
            return Task.CompletedTask;
        }

        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        BroadcastBatteryStatus();

        pollCts = new CancellationTokenSource();
        pollTask = Task.Run(() => PollLoopAsync(pollCts.Token));
        logger.Info("macOS battery sync initialized");
        return Task.CompletedTask;
    }

    public void SendBatteryStatus(PairedDevice device)
    {
        if (!device.IsConnected)
            return;

        var batteryState = GetBatteryState();
        if (batteryState is null)
            return;

        device.SendMessage(batteryState);
    }

    public void Dispose()
    {
        sessionManager.ConnectionStatusChanged -= OnConnectionStatusChanged;
        pollCts?.Cancel();
        pollCts?.Dispose();
        pollCts = null;
    }

    private void OnConnectionStatusChanged(object? sender, PairedDevice device) =>
        SendBatteryStatus(device);

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                BroadcastBatteryStatus();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BroadcastBatteryStatus()
    {
        var batteryState = GetBatteryState();
        if (batteryState is null || !HasBatteryStateChanged(batteryState))
            return;

        lastBatteryState = batteryState;
        sessionManager.BroadcastMessage(batteryState);
    }

    private bool HasBatteryStateChanged(BatteryState batteryState) =>
        lastBatteryState is null ||
        lastBatteryState.BatteryLevel != batteryState.BatteryLevel ||
        lastBatteryState.IsCharging != batteryState.IsCharging;

    private BatteryState? GetBatteryState()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pmset",
                Arguments = "-g batt",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            // Example: "-InternalBattery-0 ... 82%; discharging; ..."
            //          "-InternalBattery-0 ... 100%; charged; ..."
            var match = BatteryLineRegex.Match(output);
            if (!match.Success)
                return null;

            if (!int.TryParse(match.Groups["level"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
                return null;

            var status = match.Groups["status"].Value;
            var isCharging = status.Contains("charg", StringComparison.OrdinalIgnoreCase) &&
                             !status.Contains("discharg", StringComparison.OrdinalIgnoreCase);

            return new BatteryState
            {
                BatteryLevel = Math.Clamp(level, 0, 100),
                IsCharging = isCharging || status.Contains("AC Power", StringComparison.OrdinalIgnoreCase)
            };
        }
        catch (Exception ex)
        {
            logger.Warn("Failed to read macOS battery status", ex);
            return null;
        }
    }

    [GeneratedRegex(
        @"InternalBattery[^\n]*?(?<level>\d+)\s*%;\s*(?<status>[^;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatteryRegex();
}
