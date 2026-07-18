using Sefirah.Data.Models.Actions;

namespace Sefirah.Platforms.Desktop;

public class DefaultActions
{
    public static IReadOnlyList<BaseAction> GetDefaultActions()
    {
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                // Ctrl+Cmd+Q — true Lock Screen (pmset displaysleepnow only sleeps the display)
                // ProcessStartInfo.Arguments needs Windows-style escapes, not shell single quotes
                new ProcessAction
                {
                    Id = "lock",
                    Name = "Lock Screen",
                    Path = "osascript",
                    Arguments = "-e \"tell application \\\"System Events\\\" to keystroke \\\"q\\\" using {control down, command down}\"",
                },
                new ProcessAction { Id = "hibernate", Name = "Sleep", Path = "pmset", Arguments = "sleepnow" },
                new ProcessAction
                {
                    Id = "logoff",
                    Name = "Log Off",
                    Path = "osascript",
                    Arguments = "-e \"tell application \\\"System Events\\\" to log out\"",
                },
                // shutdown(8) needs root; System Events matches Apple menu Restart / Shut Down
                new ProcessAction
                {
                    Id = "restart",
                    Name = "Restart",
                    Path = "osascript",
                    Arguments = "-e \"tell application \\\"System Events\\\" to restart\"",
                },
                new ProcessAction
                {
                    Id = "shutdown",
                    Name = "Shutdown",
                    Path = "osascript",
                    Arguments = "-e \"tell application \\\"System Events\\\" to shut down\"",
                },
            ];
        }

        return
        [
            new ProcessAction { Id = "lock", Name = "Lock Screen", Path = "loginctl", Arguments = "lock-session" },
            new ProcessAction { Id = "hibernate", Name = "Hibernate", Path = "systemctl", Arguments = "hibernate" },
            new ProcessAction { Id = "logoff", Name = "Log Off", Path = "loginctl", Arguments = "terminate-session" },
            new ProcessAction { Id = "restart", Name = "Restart", Path = "shutdown", Arguments = "-r now" },
            new ProcessAction { Id = "shutdown", Name = "Shutdown", Path = "shutdown", Arguments = "-h now" },
        ];
    }
}
