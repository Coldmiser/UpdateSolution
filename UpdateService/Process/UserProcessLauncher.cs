// UpdateService/Process/UserProcessLauncher.cs
// The service runs as SYSTEM.  To show a WPF window to the currently
// logged-in user we must:
//   1. Find an active user session (console OR Remote Desktop)
//   2. Get a user token for that session   (WTSQueryUserToken)
//   3. Create a process in that session    (CreateProcessAsUser)
// This is the standard "Session 0 isolation" technique for Windows services.
//
// Session discovery order:
//   a. WTSGetActiveConsoleSessionId() — fast path for local console logins.
//   b. WTSEnumerateSessions()         — fallback for RDP / fast-user-switching,
//      where the console session exists but has no token (ERROR_NO_TOKEN / 1008).

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
    /// Starts <paramref name="exePath"/> as the logged-in user.
    /// Works for both local console sessions and Remote Desktop sessions.
    /// </summary>
    /// <param name="exePath">Full path to the executable to launch.</param>
    /// <param name="arguments">Command-line arguments to pass.</param>
    /// <returns>True if the process was created; false if no user is logged in.</returns>
    public static bool LaunchAsActiveUser(string exePath, string arguments = "")
    {
        LogConfig.ServiceLog.Information(
            "UserProcessLauncher: launching '{Exe}' as active user.", exePath);

        var sessionId = FindActiveUserSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            LogConfig.ServiceLog.Warning(
                "UserProcessLauncher: no active user session found — user may not be logged in.");
            return false;
        }

        LogConfig.ServiceLog.Debug(
            "UserProcessLauncher: using session ID = {SessionId}.", sessionId);

        var userToken = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WTSQueryUserToken(sessionId, out userToken))
            {
                LogConfig.ServiceLog.Error(
                    "UserProcessLauncher: WTSQueryUserToken failed. Error: {Err}",
                    Marshal.GetLastWin32Error());
                return false;
            }

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
                // Build the user's own environment block so the notifier gets
                // correct %APPDATA%, %TEMP%, etc. rather than SYSTEM's paths.
                NativeMethods.CreateEnvironmentBlock(out var envBlock, primaryToken, false);

                try
                {
                    var si = new NativeMethods.STARTUPINFO
                    {
                        cb        = Marshal.SizeOf<NativeMethods.STARTUPINFO>(),
                        lpDesktop = @"winsta0\default"  // interactive desktop in the user's session
                    };

                    var commandLine = string.IsNullOrWhiteSpace(arguments)
                        ? $"\"{exePath}\""
                        : $"\"{exePath}\" {arguments}";

                    var created = NativeMethods.CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.NORMAL_PRIORITY_CLASS,
                        envBlock,   // user's environment block (not SYSTEM's)
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

                    NativeMethods.CloseHandle(pi.hProcess);
                    NativeMethods.CloseHandle(pi.hThread);

                    LogConfig.ServiceLog.Information(
                        "UserProcessLauncher: '{Exe}' launched in session {SessionId}.", exePath, sessionId);
                    return true;
                }
                finally
                {
                    if (envBlock != IntPtr.Zero)
                        NativeMethods.DestroyEnvironmentBlock(envBlock);
                }
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

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds an active user session suitable for process launch.
    /// Tries the console session first; falls back to WTSEnumerateSessions
    /// so that Remote Desktop and fast-user-switching sessions are covered.
    /// Returns 0xFFFFFFFF if no usable session is found.
    /// </summary>
    private static uint FindActiveUserSessionId()
    {
        // Fast path: physical console session.
        var consoleId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (consoleId != 0xFFFFFFFF)
        {
            // Probe whether we can actually get a token (fails with 1008 for
            // locked/disconnected console sessions and for RDP-only machines).
            if (NativeMethods.WTSQueryUserToken(consoleId, out var probe))
            {
                NativeMethods.CloseHandle(probe);
                LogConfig.ServiceLog.Debug(
                    "UserProcessLauncher: console session {Id} has a valid user token.", consoleId);
                return consoleId;
            }

            LogConfig.ServiceLog.Debug(
                "UserProcessLauncher: console session {Id} has no token (err {Err}) — " +
                "falling back to session enumeration.",
                consoleId, Marshal.GetLastWin32Error());
        }

        // Fallback: enumerate all sessions and pick the first WTSActive non-zero one.
        // This covers Remote Desktop and fast-user-switching scenarios.
        var pSessions = IntPtr.Zero;
        uint count = 0;

        if (!NativeMethods.WTSEnumerateSessions(IntPtr.Zero, 0, 1, ref pSessions, ref count))
        {
            LogConfig.ServiceLog.Warning(
                "UserProcessLauncher: WTSEnumerateSessions failed. Error: {Err}",
                Marshal.GetLastWin32Error());
            return 0xFFFFFFFF;
        }

        try
        {
            var size = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (uint i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(
                    IntPtr.Add(pSessions, (int)(i * size)));

                // Skip Session 0 (SYSTEM) and any non-active sessions.
                if (info.SessionId == 0 ||
                    info.State != NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive)
                    continue;

                LogConfig.ServiceLog.Debug(
                    "UserProcessLauncher: found active session {Id} ({Station}).",
                    info.SessionId, info.pWinStationName);
                return info.SessionId;
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(pSessions);
        }

        return 0xFFFFFFFF;
    }

    // ── Native interop ───────────────────────────────────────────────────────

    private static class NativeMethods
    {
        public const uint TOKEN_ALL_ACCESS           = 0xF01FF;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint NORMAL_PRIORITY_CLASS      = 0x00000020;

        public enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous, SecurityIdentification, SecurityImpersonation, SecurityDelegation
        }

        public enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation }

        public enum WTS_CONNECTSTATE_CLASS
        {
            WTSActive, WTSConnected, WTSConnectQuery, WTSShadow,
            WTSDisconnected, WTSIdle, WTSListen, WTSReset, WTSDown, WTSInit
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WTS_SESSION_INFO
        {
            public uint   SessionId;
            public string pWinStationName;
            public WTS_CONNECTSTATE_CLASS State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int    cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public uint   dwX, dwY, dwXSize, dwYSize;
            public uint   dwXCountChars, dwYCountChars;
            public uint   dwFillAttribute, dwFlags;
            public short  wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint   dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // WTSGetActiveConsoleSessionId lives in kernel32.dll, NOT Wtsapi32.dll.
        [DllImport("kernel32.dll")]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSEnumerateSessions(
            IntPtr hServer, uint Reserved, uint Version,
            ref IntPtr ppSessionInfo, ref uint pCount);

        [DllImport("Wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr pMemory);

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

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(
            out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
    }
}
