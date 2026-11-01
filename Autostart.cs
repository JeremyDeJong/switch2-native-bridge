using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Switch2Bridge;

/// <summary>
/// Start with Windows through a Task Scheduler logon task that runs with highest
/// privileges. The bridge needs elevation, and a logon task gets it without a UAC
/// prompt at every sign-in, which a Run-key entry could not.
/// </summary>
static class Autostart
{
    const string TaskName = "Switch2 Native Bridge";

    static (int ExitCode, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// <summary>The command the task runs, or null when there is no task.</summary>
    static string? RegisteredCommand()
    {
        var (code, xml) = Schtasks("/Query", "/TN", TaskName, "/XML");
        if (code != 0) return null;
        var m = Regex.Match(xml, "<Command>(.*?)</Command>", RegexOptions.Singleline);
        return m.Success ? SecurityElementUnescape(m.Groups[1].Value.Trim()) : "";
    }

    static string SecurityElementUnescape(string s) =>
        s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    public static bool IsEnabled => RegisteredCommand() != null;

    /// <summary>Points an existing task at this exe if it was moved.</summary>
    public static void RepairIfMoved()
    {
        var command = RegisteredCommand();
        if (command != null && !string.Equals(command, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Updating the start-with-Windows task to this exe's location.");
            Enable();
        }
    }

    public static void Enable()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(Environment.ProcessPath!);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Bridges Switch 2 controllers over Bluetooth to Steam as wired controllers.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"switch2bridge-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            var (code, output) = Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
            if (code != 0) throw new InvalidOperationException($"Could not create the startup task: {output.Trim()}");
        }
        finally
        {
            File.Delete(file);
        }
        Log.Ok("Start with Windows: on.");
    }

    public static void Disable()
    {
        var (code, output) = Schtasks("/Delete", "/TN", TaskName, "/F");
        if (code != 0 && IsEnabled) throw new InvalidOperationException($"Could not remove the startup task: {output.Trim()}");
        Log.Ok("Start with Windows: off.");
    }
}
