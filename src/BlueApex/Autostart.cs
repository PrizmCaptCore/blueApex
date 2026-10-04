using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BlueApex;

/// <summary>
/// Run-at-login as a Task Scheduler "at logon" task for the current user. A Run-key
/// entry would do the same, but Windows holds those back for ~10 s after logon; a
/// logon task starts at once, which matters for the startup movie. The task runs
/// with the user's ordinary rights and needs no admin to create. The app is started
/// with "--autostart" so it knows this was a logon, not a manual launch.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "BlueApex";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "BlueApex";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                dynamic service = Service();
                dynamic folder = service.GetFolder("\\");
                folder.GetTask(TaskName);
                return true;
            }
            catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <param name="exePath">What the task starts; the running executable unless migrating an old entry.</param>
    public static void Set(bool enabled, string? exePath = null)
    {
        // An entry left by versions before the task existed is retired either way.
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.DeleteValue(RunValue, throwOnMissingValue: false);

        dynamic service = Service();
        dynamic folder = service.GetFolder("\\");
        if (!enabled)
        {
            try { folder.DeleteTask(TaskName, 0); }
            catch (Exception ex) when (ex is COMException or System.IO.FileNotFoundException) { }
            return;
        }

        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "BlueApex를 로그인할 때 시작합니다.";
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S"; // never kill it for running "too long"
        task.Settings.StartWhenAvailable = true;
        task.Settings.MultipleInstances = 3; // TASK_INSTANCES_IGNORE_NEW
        dynamic trigger = task.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = Environment.UserDomainName + "\\" + Environment.UserName;
        dynamic action = task.Actions.Create(0); // TASK_ACTION_EXEC
        var exe = exePath ?? Environment.ProcessPath!;
        action.Path = exe;
        action.Arguments = "--autostart";
        action.WorkingDirectory = System.IO.Path.GetDirectoryName(exe);
        task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        task.Principal.RunLevel = 0; // TASK_RUNLEVEL_LUA: ordinary rights
        folder.RegisterTaskDefinition(TaskName, task, 6 /* TASK_CREATE_OR_UPDATE */, null, null, 3, null);
    }

    /// <summary>Moves an old Run-key entry over to the task, once.</summary>
    public static void MigrateFromRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunValue) is not string value) return;
        try
        {
            // Keep pointing at whatever the old entry started (the installed copy), not necessarily this exe.
            var exe = value.Trim().StartsWith('"') ? value.Trim()[1..value.Trim().IndexOf('"', 1)] : value.Trim().Split(' ')[0];
            Set(true, System.IO.File.Exists(exe) ? exe : null);
            Log.Write("autostart: moved from Run key to a logon task");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Write($"autostart migration failed: {ex.Message}");
        }
    }

    private static object Service()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }
}
