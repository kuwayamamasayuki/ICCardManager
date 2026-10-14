# Issue #2267: Reads the mapped-drive hand-off file written by the original (non-elevated) user.
# The installer runs this script ELEVATED (ExecAndCaptureOutput) from its protected {tmp} folder.
# It is not run with -File: the installer reads the text and runs it as a script block
# (& ([ScriptBlock]::Create(...))), so an execution policy set by Group Policy (AllSigned / Restricted),
# which takes precedence over -ExecutionPolicy Bypass for script files, does not block it.
# In ConstrainedLanguage mode (AppLocker / WDAC script rules) Add-Type is not allowed: RESULT=ERROR (fail-closed).
#
# First, nothing is read unless the user of the interactive session (WTSQuerySessionInformationW on the current
# session) is the elevated user. This excludes elevation with another account ("over the shoulder": a standard
# user's session, an administrator's credentials) regardless of the file's attributes: there, net use would send
# the administrator's credentials to a server the standard user chose. With the same account, the user only
# connects with their own credentials to servers they chose themselves. If the session user cannot be determined,
# nothing is read (RESULT=SESSION_USER, fail-closed).
#
# The checks below remain as defense in depth.
# The ownership check and the read are done on ONE handle, so the file cannot be swapped between them:
#   - CreateFileW sharing only FILE_SHARE_READ (no one can write, delete or rename it while it is open)
#     and FILE_FLAG_OPEN_REPARSE_POINT (a link is opened as itself, not followed)
#   - the handle must not be a reparse point, and must have exactly one hard link
#     (a hard link to someone else's file has two or more)
#   - the owner of the handle must be the elevated user (the same account as the original user).
#     A non-elevated process cannot set another account as the owner (that needs SeRestorePrivilege).
#   - the DACL of the handle must not allow anyone other than the owner, SYSTEM and Administrators to
#     write, append, delete, change permissions or take ownership. Together with the owner check, the content
#     can only have been written by the owner (the same account) or an administrator. Without this, a file that
#     the elevated admin owns but an ordinary user can modify (e.g. a log the admin created under a folder that
#     grants Users "Modify") could be moved to the hand-off name and rewritten.
#   - only then the content is read from the same handle, and written to -OutPath
#     (the installer passes a file in its protected {tmp} folder, which the original user cannot write)
# Output (ASCII only, so the console code page does not matter):
#   RESULT=OK after the verified content has been written to -OutPath (UTF-8 with BOM)
#   RESULT=<reason> without writing anything on failure (fail-closed)
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$OutPath
)

$ErrorActionPreference = 'Stop'
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class IccmHandoffFile
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    // WTS_CURRENT_SERVER_HANDLE (IntPtr.Zero), WTS_CURRENT_SESSION (-1). Null on failure.
    static string QuerySession(int infoClass)
    {
        IntPtr buffer;
        int bytes;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, infoClass, out buffer, out bytes)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    // "DOMAIN\user" of the interactive session this process runs in (WTSDomainName = 7, WTSUserName = 5).
    // For a local account the domain is the computer name, the same as WindowsIdentity.Name. Null if unknown.
    public static string SessionUser()
    {
        string user = QuerySession(5);
        string domain = QuerySession(7);
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(domain)) return null;
        return domain + "\\" + user;
    }
}
'@

    # The interactive session's user must be the elevated user (see the header). Compared ignoring case.
    $sessionUser = [IccmHandoffFile]::SessionUser()
    $elevatedUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrEmpty($sessionUser) -or -not [string]::Equals($sessionUser, $elevatedUser, [System.StringComparison]::OrdinalIgnoreCase)) { 'RESULT=SESSION_USER'; exit 1 }

    # GENERIC_READ (0x80000000; written in decimal because 0x80000000 is a negative Int32 in PowerShell),
    # FILE_SHARE_READ only (1; an anti-virus scanner reading the new file must not make this fail),
    # OPEN_EXISTING (3), FILE_FLAG_OPEN_REPARSE_POINT (0x00200000).
    # Retried a few times because a scanner may hold the file open for a moment right after it was written.
    $handle = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $handle = [IccmHandoffFile]::CreateFileW($Path, [uint32]2147483648, 1, [IntPtr]::Zero, 3, 0x00200000, [IntPtr]::Zero)
        if (-not $handle.IsInvalid) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($handle.IsInvalid) { 'RESULT=OPEN'; exit 1 }
    $stream = New-Object System.IO.FileStream($handle, [System.IO.FileAccess]::Read)
    try {
        $info = New-Object IccmHandoffFile+Info
        if (-not [IccmHandoffFile]::GetFileInformationByHandle($handle, [ref]$info)) { 'RESULT=INFO'; exit 1 }
        if (($info.FileAttributes -band 0x400) -ne 0) { 'RESULT=REPARSE'; exit 1 }
        if ($info.NumberOfLinks -ne 1) { 'RESULT=LINKS'; exit 1 }

        $security = $stream.GetAccessControl()
        $owner = $security.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
        if ($owner -eq 'S-1-5-32-544') { 'RESULT=OWNER_ADMINISTRATORS'; exit 1 }
        if ($owner -ne [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { 'RESULT=OWNER_MISMATCH'; exit 1 }

        # WriteData (0x2), AppendData (0x4), DELETE (0x10000), WRITE_DAC (0x40000), WRITE_OWNER (0x80000),
        # GENERIC_ALL (0x10000000), GENERIC_WRITE (0x40000000)
        $writeMask = 0x2 -bor 0x4 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
        $trusted = @($owner, 'S-1-5-18', 'S-1-5-32-544')
        foreach ($rule in $security.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
            if ($trusted -contains $rule.IdentityReference.Value) { continue }
            if (([long][int]$rule.FileSystemRights -band $writeMask) -ne 0) { 'RESULT=DACL'; exit 1 }
        }

        $reader = New-Object System.IO.StreamReader($stream, $true)
        $text = $reader.ReadToEnd()
        [System.IO.File]::WriteAllText($OutPath, $text, (New-Object System.Text.UTF8Encoding $true))
        'RESULT=OK'
        exit 0
    }
    finally {
        $stream.Dispose()
    }
}
catch {
    'RESULT=ERROR'
    exit 1
}
