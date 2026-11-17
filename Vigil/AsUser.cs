using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Vigil;

/// <summary>
/// Runs file work with the user's ordinary rights while Vigil itself runs with administrator rights.
///
/// The settings, the log and the update's download live in the user's own folders, which any program the user runs
/// can change. Such a program could swap one of those folders for a link to a folder of Windows, and a copy of
/// Vigil with administrator rights writing "its" log would then write, replace or delete Windows' files. Done as the
/// user, the same write is refused, as it would be for any other program the user runs.
///
/// The token used is the process's own, with the Administrators group set to deny only, every privilege but the
/// one to cross folders dropped, the integrity level brought down to medium, and the user as the owner of what it
/// creates. Without administrator rights there is nothing to drop, and the work runs as it is.
/// </summary>
public static class AsUser
{
    private static readonly Lazy<SafeAccessTokenHandle?> Token = new(CreateToken, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Why the user's token could not be made, when it could not (for the log and the diagnostics).</summary>
    public static string? Problem { get; private set; }

    /// <summary>Whether file work runs with the user's ordinary rights: false when Vigil has none to drop.</summary>
    public static bool Active => Token.Value is not null;

    public static void Run(Action action)
    {
        if (Token.Value is { } token) WindowsIdentity.RunImpersonated(token, action);
        else action();
    }

    public static T Run<T>(Func<T> func) => Token.Value is { } token ? WindowsIdentity.RunImpersonated(token, func) : func();

    private static SafeAccessTokenHandle? CreateToken()
    {
        if (!Elevation.IsElevated) return null;
        IntPtr process = IntPtr.Zero, restricted = IntPtr.Zero, impersonation = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery, out process)) throw new Win32Exception();

            byte[] admins = Sid(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            GCHandle adminsPin = GCHandle.Alloc(admins, GCHandleType.Pinned);
            try
            {
                var deny = new[] { new SidAndAttributes { Sid = adminsPin.AddrOfPinnedObject(), Attributes = 0 } };
                if (!CreateRestrictedToken(process, DisableMaxPrivilege, deny.Length, deny, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                adminsPin.Free();
            }

            if (!DuplicateTokenEx(restricted, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenTypeImpersonation, out impersonation))
            {
                throw new Win32Exception();
            }

            // Medium, the level of the programs the user runs: they cannot write what a higher level owns.
            byte[] medium = Sid(new SecurityIdentifier("S-1-16-8192"));
            GCHandle mediumPin = GCHandle.Alloc(medium, GCHandleType.Pinned);
            try
            {
                var label = new SidAndAttributes { Sid = mediumPin.AddrOfPinnedObject(), Attributes = SeGroupIntegrity };
                int size = Marshal.SizeOf<SidAndAttributes>() + medium.Length;
                if (!SetTokenInformation(impersonation, TokenIntegrityLevel, ref label, size)) throw new Win32Exception();
            }
            finally
            {
                mediumPin.Free();
            }

            // What it creates belongs to the user, not to the Administrators group (which is deny only here).
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                byte[] user = Sid(identity.User!);
                GCHandle userPin = GCHandle.Alloc(user, GCHandleType.Pinned);
                try
                {
                    IntPtr owner = userPin.AddrOfPinnedObject();
                    if (!SetTokenInformation(impersonation, TokenOwner, ref owner, IntPtr.Size)) throw new Win32Exception();
                }
                finally
                {
                    userPin.Free();
                }
            }

            var handle = new SafeAccessTokenHandle(impersonation);
            impersonation = IntPtr.Zero; // the handle owns it now
            return handle;
        }
        catch (Win32Exception e)
        {
            // Not logged here: the log itself runs through this class. Program logs it once the app has started.
            Problem = $"{e.NativeErrorCode}: {e.Message}";
            return null;
        }
        finally
        {
            if (impersonation != IntPtr.Zero) CloseHandle(impersonation);
            if (restricted != IntPtr.Zero) CloseHandle(restricted);
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    private static byte[] Sid(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumAllowed = 0x02000000;
    private const uint DisableMaxPrivilege = 0x1;
    private const int SecurityImpersonation = 2;
    private const int TokenTypeImpersonation = 2;
    private const int TokenOwner = 4;
    private const int TokenIntegrityLevel = 25;
    private const uint SeGroupIntegrity = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, int disableCount, SidAndAttributes[] sidsToDisable,
        int deleteCount, IntPtr privilegesToDelete, int restrictCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref SidAndAttributes info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref IntPtr info, int length);
}
