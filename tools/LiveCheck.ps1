param([Parameter(Mandatory=$true)][int]$MonitorProcessId,[string]$ExecutablePath,[string]$ValidationDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
public static class LiveMonitorWindows {
    public delegate bool EnumProc(IntPtr window, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr value);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    public static IntPtr Find(int process) {
        IntPtr found=IntPtr.Zero;
        EnumWindows(delegate(IntPtr w,IntPtr v) {
            uint id;GetWindowThreadProcessId(w,out id);
            if(id!=(uint)process || !IsWindowVisible(w))return true;
            StringBuilder title=new StringBuilder(128);GetWindowText(w,title,128);
            if(title.ToString()=="任务栏状态条"){found=w;return false;} return true;
        },IntPtr.Zero);return found;
    }
}
'@ -ReferencedAssemblies System.Drawing
[LiveMonitorWindows]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskProcess=Get-Process -Id $MonitorProcessId
$taskExpected=if($ExecutablePath){[IO.Path]::GetFullPath($ExecutablePath)}else{Join-Path $taskRoot 'release\CodexTaskbarMonitor-1.2.0-win-x64\CodexTaskbarMonitor.exe'}
if(-not $ValidationDirectory){$ValidationDirectory=Join-Path $taskRoot 'validation\v1.2'}
New-Item -ItemType Directory -Path $ValidationDirectory -Force | Out-Null
if ($taskProcess.Path -ne $taskExpected) { throw 'The PID is not the expected monitor executable.' }
$taskCpuBefore=$taskProcess.TotalProcessorTime.TotalMilliseconds
$taskWatch=[System.Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds 10
$taskProcess.Refresh()
$taskElapsed=$taskWatch.Elapsed.TotalMilliseconds
$taskCpu=$taskProcess.TotalProcessorTime.TotalMilliseconds-$taskCpuBefore
$taskWindow=[LiveMonitorWindows]::Find($MonitorProcessId)
if ($taskWindow -eq [IntPtr]::Zero) { throw 'The monitor overlay was not visible.' }
$taskRectangle=New-Object LiveMonitorWindows+Rect
[LiveMonitorWindows]::GetWindowRect($taskWindow,[ref]$taskRectangle) | Out-Null
$taskWidth=$taskRectangle.R-$taskRectangle.L
$taskHeight=$taskRectangle.B-$taskRectangle.T
$taskBitmap=New-Object System.Drawing.Bitmap($taskWidth,$taskHeight)
$taskGraphics=[System.Drawing.Graphics]::FromImage($taskBitmap)
try {
    $taskGraphics.CopyFromScreen($taskRectangle.L,$taskRectangle.T,0,0,$taskBitmap.Size)
    $taskBitmap.Save((Join-Path $ValidationDirectory 'live-window.png'))
} finally { $taskGraphics.Dispose();$taskBitmap.Dispose() }
$taskForegroundProcess=[uint32]0
[LiveMonitorWindows]::GetWindowThreadProcessId([LiveMonitorWindows]::GetForegroundWindow(),[ref]$taskForegroundProcess) | Out-Null
$taskRunKey=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try { $taskStartup=if ($taskRunKey) { $taskRunKey.GetValue('CodexTaskbarMonitor') } else { $null } }
finally { if ($taskRunKey) { $taskRunKey.Dispose() } }
$taskResult=[ordered]@{
    Pid=$MonitorProcessId;Visible=$true;X=$taskRectangle.L;Y=$taskRectangle.T;Width=$taskWidth;Height=$taskHeight
    WorkingSetMiB=[Math]::Round($taskProcess.WorkingSet64/1MB,1)
    PrivateMemoryMiB=[Math]::Round($taskProcess.PrivateMemorySize64/1MB,1)
    CpuPercentOfMachine=[Math]::Round(100*$taskCpu/$taskElapsed/[Environment]::ProcessorCount,3)
    CpuPercentOfOneCore=[Math]::Round(100*$taskCpu/$taskElapsed,2)
    MeasurementSeconds=[Math]::Round($taskElapsed/1000,2)
    ForegroundIsMonitor=($taskForegroundProcess -eq $MonitorProcessId)
    StartupEnabled=($null -ne $taskStartup)
}
$taskJson=$taskResult | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $ValidationDirectory 'live-check.json') -Value $taskJson -Encoding utf8
$taskJson
