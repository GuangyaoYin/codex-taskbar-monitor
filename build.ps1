param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '64-bit .NET Framework C# compiler was not found.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'release\CodexTaskbarMonitor-1.2.0-win-x64' }
$taskOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskWpf = Join-Path (Split-Path -Parent $taskCompiler) 'WPF'
$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$taskArguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/langversion:5','/codepage:65001',
    ('/out:' + (Join-Path $taskOutput 'CodexTaskbarMonitor.exe')),
    ('/win32manifest:' + (Join-Path $taskRoot 'app.manifest')),
    '/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll',
    ('/r:' + (Join-Path $taskWpf 'UIAutomationClient.dll')),
    ('/r:' + (Join-Path $taskWpf 'UIAutomationTypes.dll')),
    ('/r:' + (Join-Path $taskWpf 'WindowsBase.dll')))
& $taskCompiler @taskArguments @taskSources
if ($LASTEXITCODE -ne 0) { throw ('Compilation failed: ' + $LASTEXITCODE) }
Copy-Item -LiteralPath (Join-Path $taskRoot '使用说明.html') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'CodexTaskbarMonitor.exe.config') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'THIRD_PARTY_NOTICES.txt') -Destination $taskOutput -Force
$taskArchive = Join-Path (Split-Path -Parent $taskOutput) ((Split-Path -Leaf $taskOutput) + '.zip')
Compress-Archive -LiteralPath $taskOutput -DestinationPath $taskArchive -Force
Get-FileHash -LiteralPath (Join-Path $taskOutput 'CodexTaskbarMonitor.exe'),$taskArchive -Algorithm SHA256 | Format-Table -AutoSize
Write-Output ('Ready: ' + $taskOutput)
