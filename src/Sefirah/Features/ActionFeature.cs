using Sefirah.Data.Models;
using Sefirah.Data.Models.Actions;
using Sefirah.Services;

namespace Sefirah.Features;

public class ActionFeature(
    IGeneralSettingsService generalSettingsService,
    IUserSettingsService userSettingsService,
    ISessionManager sessionManager,
    ILogger logger) : IActionFeature
{
    public Task InitializeAsync()
    {
        sessionManager.ConnectionStatusChanged += OnConnectionStatusChanged;
        if (ApplicationData.Current.LocalSettings.Values["DefaultActionsLoaded"] is null)
        {
            ApplicationData.Current.LocalSettings.Values["DefaultActionsLoaded"] = true;
            var defaultActions = DefaultActionsProvider.GetDefaultActions();
            userSettingsService.GeneralSettingsService.Actions = [.. defaultActions];
        }
        else
        {
            MigrateDesktopDefaultActions();
        }

        return Task.CompletedTask;
    }

    // Replace Linux defaults (or broken osascript quoting) with current platform defaults on macOS.
    private void MigrateDesktopDefaultActions()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var defaults = DefaultActionsProvider.GetDefaultActions()
            .OfType<ProcessAction>()
            .ToDictionary(a => a.Id);

        if (defaults.Count == 0)
        {
            return;
        }

        var actions = generalSettingsService.Actions.ToList();
        var changed = false;

        for (var i = 0; i < actions.Count; i++)
        {
            if (actions[i] is not ProcessAction existing
                || !defaults.TryGetValue(existing.Id, out var corrected)
                || !NeedsMacOsActionMigration(existing))
            {
                continue;
            }

            actions[i] = new ProcessAction
            {
                Id = existing.Id,
                Name = corrected.Name,
                Path = corrected.Path,
                Arguments = corrected.Arguments,
                StartInDirectory = existing.StartInDirectory,
                EnvironmentVariables = existing.EnvironmentVariables,
                UseShellExecute = existing.UseShellExecute,
                CreateNoWindow = existing.CreateNoWindow,
            };
            changed = true;
            logger.Info($"Migrated action '{existing.Id}' from '{existing.Path} {existing.Arguments}' to '{corrected.Path} {corrected.Arguments}'");
        }

        if (changed)
        {
            userSettingsService.GeneralSettingsService.Actions = actions;
        }
    }

    private static bool NeedsMacOsActionMigration(ProcessAction existing)
    {
        // Linux defaults previously shipped for all Desktop builds
        if (existing.Path is "loginctl" or "systemctl")
        {
            return true;
        }

        // macOS shutdown(8) requires root; use System Events instead
        if (existing.Path == "shutdown" && existing.Id is "restart" or "shutdown")
        {
            return true;
        }

        // Shell-style single quotes are not parsed by ProcessStartInfo.Arguments
        return existing.Path == "osascript"
            && existing.Arguments.Contains("'tell application", StringComparison.Ordinal);
    }

    private void OnConnectionStatusChanged(object? sender, PairedDevice device)
    {
        if (device.IsConnected)
        {
            var actions = generalSettingsService.Actions;
            foreach (var action in actions)
            {
                var actionMessage = new ActionInfo { ActionId = action.Id, ActionName = action.Name };
                device.SendMessage(actionMessage);
            }
        }
    }

    public void HandleActionMessage(ActionInfo action)
    {
        logger.Info($"Executing action: {action.ActionName}");
        var actionToExecute = generalSettingsService.Actions.FirstOrDefault(a => a.Id == action.ActionId);

        if (actionToExecute is not null && actionToExecute is ProcessAction processAction)
        {
            processAction.ExecuteAsync();
        }
    }
}
