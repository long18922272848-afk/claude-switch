param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$refs = @('/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll', '/reference:System.Web.Extensions.dll', "/reference:$wpf\UIAutomationClient.dll", "/reference:$wpf\UIAutomationTypes.dll", "/reference:$wpf\WindowsBase.dll")
$buildText = Get-Content -LiteralPath (Join-Path $projectRoot 'build.ps1') -Raw
$match = [regex]::Match($buildText, '(?m)^\s*\$sources\s*=\s*@\(([^\r\n]+)\)')
if (-not $match.Success) { throw 'Cannot find current production source list' }
$sources = @([regex]::Matches($match.Groups[1].Value, "'([^']+\.cs)'") | ForEach-Object { Join-Path $projectRoot $_.Groups[1].Value })
$executable = Join-Path $PSScriptRoot 'native-popup-smoke.exe'
& $compiler /nologo /target:exe /main:ClaudeAccountSync.NativePopupSmoke "/out:$executable" @refs @sources (Join-Path $PSScriptRoot 'native-popup-smoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'Isolated popup smoke compilation failed' }
# Only a dedicated, nonactivating owner at least 20,000 pixels outside all screens is shown.
# The production Panel/Engine and real account state are never instantiated or read.
& $executable $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Native popup smoke failed; inspect native-popup-smoke-report.txt' }
