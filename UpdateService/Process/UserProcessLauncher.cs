// UpdateService/Process/UserProcessLauncher.cs
// The service runs as SYSTEM.  To show a WPF window to the currently
// logged-in user we must:
//   1. Find the active console session ID  (WTSGetActiveConsoleSessionId)
//   2. Get a user token for that session   (WTSQueryUserToken)
//   3. Create a process in that session    (CreateProcessAsUser)
// This is the standard "Session 0 isolation" technique for Windows services.

using System.ComponentModel;
using System.Runtime.InteropServices;
using UpdateService.Logging;

namespace UpdateService.Process;

/// <summary>
/// Launches an executable in the context of the currently active desktop user,
/// even though the calling process is running as SYSTEM in Session 0.
/// </summary>
public static class UserProcessLauncher
{
    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Starts <paramref name="exePath"/> as the logged-in console user.
    /// </summary>
    /// <param name="exePath">Full path to the executable to launch.</param>
    /// <param name="arguments">Command-line arguments to pass.</param>
    /// <returns>True if the process was created; false if no user is logged in.</returns>
    public static bool LaunchAsActiveUser(string exePath, string arguments = "")
    {
        LogConfig.ServiceLog.Information(
            "UserProcessLauncher: launching '{Exe}' as active user.", exePath);

        var sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            LogConfig.ServiceLog.Warning(
                "UserProcessLauncher: no active console session — user may not be logged in.");
            return false;
        }

        LogConfig.ServiceLog.Debug(
            "UserProcessLauncher: active console session ID = {SessionId}.", sessionId);

        var userToken = IntPtr.Zero;
        try
        {
            // Obtain an impersonation token for the user in the active session.
            if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken))
            {
                LogConfig.ServiceLog.Error(
                    "UserProcessLauncher: WTSQueryUserToken failed. Error: {Err}",
                    Marshal.GetLastWin32Error());
                return false;
            }

            // Duplicate it to a primary token suitable for CreateProcessAsUser.
            if (!NativeMethods.DuplicateTokenEx(
                    userToken,
                    NativeMethods.TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out var primaryToken))
            {
                LogConfig.ServiceLog.Error(
                    "UserProcessLauncher: DuplicateTokenEx failed. Error: {Err}",
                    Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                var si = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>() };
                var commandLine = string.IsNullOrWhiteSpace(arguments)
                    ? $"\"{exePath}\""
                    : $"\"{exePath}\" {arguments}";

                var created = NativeMethods.CreateProcessAsUser(
                    primaryToken,
                    null,                       // application name derived from commandLine
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.NORMAL_PRIORITY_CLASS,
                    IntPtr.Zero,
                    null,
                    ref si,
                    out var pi);

                if (!created)
                {
                    var err = Marshal.GetLastWin32Error();
                    LogConfig.ServiceLog.Error(
                        "UserProcessLauncher: CreateProcessAsUser failed. Error: {Err}", err);
                    throw new Win32Exception(err);
                }

                // Close the process and thread handles — we don't need to track them.
                NativeMethods.CloseHandle(pi.hProcess);
                NativeMethods.CloseHandle(pi.hThread);

                LogConfig.ServiceLog.Information(
                    "UserProcessLauncher: '{Exe}' launched in session {SessionId}.", exePath, sessionId);
                return true;
            }
            finally
            {
                NativeMethods.CloseHandle(primaryToken);
            }
        }
        finally
        {
            if (userToken != IntPtr.Zero)
                NativeMethods.CloseHandle(userToken);
        }
    }

    // ── Native interop ───────────────────────────────────────────────────────

    private static class NativeMethods
    {
        public const uint TOKEN_ALL_ACCESS         = 0xF01FF;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint NORMAL_PRIORITY_CLASS    = 0x00000020;

        public enum SECURITY_IMPERSONATION_LEVEL { SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation }
        public enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint dwX, dwY, dwXSize, dwYSize;
            public uint dwXCountChars, dwYCountChars;
            public uint dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(
            IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
            SECURITY_IMPERSONATION_LEVEL ImpersonationLevel, TOKEN_TYPE TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(
            IntPtr hToken, string? lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
            bool bInheritHandles, uint dwCreationFlags,
            IntPtr lpEnvironment, string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    }
}
