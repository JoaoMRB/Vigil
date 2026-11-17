using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vigil;

/// <summary>
/// <c>Vigil.exe --elevation-test report.txt</c>, run with administrator rights: checks that file work in the user's
/// folders runs with the user's rights (AsUser), by trying what a program out to misuse administrator mode would
/// try, and that the start-up task can be registered without a file. Writes PASS or FAIL per case and exits with 0
/// when all pass. It leaves nothing behind: its folder, its link and its task (Vigil-selftest) are removed.
/// </summary>
internal static class ElevationTest
{
    public static int Run(string reportPath)
    {
        var report = new List<string> { $"Vigil elevation test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}", "" };
        bool ok = true;
        void Check(string name, bool passed, string detail = "")
        {
            report.Add($"{(passed ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
            ok &= passed;
        }

        if (!Elevation.IsElevated)
        {
            File.WriteAllLines(reportPath, [.. report, "FAIL  needs administrator rights: run it from an elevated prompt"]);
            return 1;
        }

        Check("the user's token is made", AsUser.Active, AsUser.Problem ?? "");

        // 1. The user's own folder: the ordinary case must keep working, and what is made belongs to the user.
        string own = Path.Combine(Path.GetTempPath(), $"vigil-elevation-{Environment.ProcessId}.txt");
        try
        {
            AsUser.Run(() => File.WriteAllText(own, "test"));
            string? owner = new FileInfo(own).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            using WindowsIdentity me = WindowsIdentity.GetCurrent();
            Check("writes in the user's own folder", File.ReadAllText(own) == "test");
            Check("what it writes belongs to the user", owner == me.User?.Value, $"owner {owner}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Check("writes in the user's own folder", false, e.Message);
        }
        finally
        {
            File.Delete(own);
        }

        // 2. The attack: a link in the user's folder to a folder only administrators can write to.
        string guarded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), $"vigil-elevation-guarded-{Environment.ProcessId}");
        string link = Path.Combine(Path.GetTempPath(), $"vigil-elevation-link-{Environment.ProcessId}");
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            new DirectoryInfo(guarded).Create(security);
            using (Process mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{guarded}\"")
                   { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            {
                mklink.StandardOutput.ReadToEnd();
                mklink.WaitForExit();
            }
            Check("the link is made", Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));

            string through = Path.Combine(link, "planted.txt");
            // Without AsUser, administrator rights follow the link: this is what the change prevents.
            File.WriteAllText(through, "planted");
            Check("without AsUser, a write follows the link (the risk)", File.Exists(Path.Combine(guarded, "planted.txt")));
            File.Delete(Path.Combine(guarded, "planted.txt"));

            string? refused = null;
            try
            {
                AsUser.Run(() => File.WriteAllText(through, "planted"));
            }
            catch (UnauthorizedAccessException e)
            {
                refused = e.Message;
            }
            Check("with AsUser, the same write is refused", refused is not null && !File.Exists(Path.Combine(guarded, "planted.txt")));

            // The log's turn-over: a move onto a file behind the link.
            File.WriteAllText(Path.Combine(guarded, "vigil.1.log"), "keep");
            string source = Path.Combine(Path.GetTempPath(), $"vigil-elevation-move-{Environment.ProcessId}.log");
            File.WriteAllText(source, "x");
            bool moveRefused = false;
            try
            {
                AsUser.Run(() => File.Move(source, Path.Combine(link, "vigil.1.log"), overwrite: true));
            }
            catch (UnauthorizedAccessException)
            {
                moveRefused = true;
            }
            Check("with AsUser, replacing a file behind the link is refused",
                moveRefused && File.ReadAllText(Path.Combine(guarded, "vigil.1.log")) == "keep");
            if (File.Exists(source)) File.Delete(source);

            // Reading through the link is refused too (the diagnostics read the log's end).
            bool readRefused = false;
            try
            {
                AsUser.Run(() => File.ReadAllText(Path.Combine(link, "vigil.1.log")));
            }
            catch (UnauthorizedAccessException)
            {
                readRefused = true;
            }
            Check("with AsUser, reading a file behind the link is refused", readRefused);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Check("the link test ran", false, e.Message);
        }
        finally
        {
            // A junction is removed as a folder of its own, without touching where it points.
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(guarded)) Directory.Delete(guarded, recursive: true);
        }
        Check("nothing is left behind", !Directory.Exists(link) && !Directory.Exists(guarded));

        // 3. The task, registered without a file, then removed.
        const string TestTask = "Vigil-selftest";
        try
        {
            Autostart.RegisterTask(TestTask, Autostart.TaskXml());
            Check("the task is registered without a file", Schtasks($"/Query /TN \"{TestTask}\"") == 0);
        }
        catch (InvalidOperationException e)
        {
            Check("the task is registered without a file", false, e.Message);
        }
        finally
        {
            Schtasks($"/Delete /TN \"{TestTask}\" /F");
        }
        Check("the test task is removed", Schtasks($"/Query /TN \"{TestTask}\"") != 0);

        report.Add("");
        report.Add(ok ? "All passed." : "Some failed.");
        File.WriteAllLines(reportPath, report);
        return ok ? 0 : 1;
    }

    private static int Schtasks(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }
}
