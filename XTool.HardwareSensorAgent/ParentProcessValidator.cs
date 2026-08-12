using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace XTool.HardwareSensorAgent;

internal static class ParentProcessValidator
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    public static Process Validate(
        int processId,
        long expectedStartTimeUtcTicks,
        string expectedExecutablePath)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                throw new InvalidOperationException("主程序已经退出。");
            }

            if (process.SessionId != Process.GetCurrentProcess().SessionId)
            {
                throw new InvalidOperationException("主程序不在当前登录会话中。");
            }

            string executablePath = Path.GetFullPath(process.MainModule?.FileName
                ?? throw new InvalidOperationException("无法读取主程序路径。"));
            if (!string.Equals(executablePath, Path.GetFullPath(expectedExecutablePath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(executablePath), "XTool.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("父进程路径与启动请求中的 XTool 主程序不匹配。");
            }

            SecurityIdentifier? parentSid = GetProcessUserSid(process);
            SecurityIdentifier? currentSid = WindowsIdentity.GetCurrent().User;
            if (parentSid is null || currentSid is null || !parentSid.Equals(currentSid))
            {
                throw new InvalidOperationException("主程序与传感器代理不属于同一 Windows 用户。");
            }

            // PID 可能被系统复用，因此必须同时验证精确的启动时间。
            if (process.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
            {
                throw new InvalidOperationException("主程序 PID 已被复用或启动时间不匹配。");
            }
            return process;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private static SecurityIdentifier? GetProcessUserSid(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, TokenQuery, out SafeAccessTokenHandle tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取主程序访问令牌。");
        }

        using (tokenHandle)
        {
            GetTokenInformation(tokenHandle, TokenUser, IntPtr.Zero, 0, out uint requiredLength);
            int firstError = Marshal.GetLastWin32Error();
            if (requiredLength == 0 && firstError != 122)
            {
                throw new Win32Exception(firstError, "无法确定主程序用户信息长度。");
            }

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenUser, buffer, requiredLength, out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取主程序用户信息。");
                }

                TokenUserNative tokenUser = Marshal.PtrToStructure<TokenUserNative>(buffer);
                return new SecurityIdentifier(tokenUser.User.Sid);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUserNative
    {
        public SidAndAttributes User;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);
}
