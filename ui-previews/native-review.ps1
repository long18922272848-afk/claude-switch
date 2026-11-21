param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$refs = @('/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll', '/reference:System.Web.Extensions.dll', "/reference:$wpf\UIAutomationClient.dll", "/reference:$wpf\UIAutomationTypes.dll", "/reference:$wpf\WindowsBase.dll")
# Read the current production source list; compile a separate entry point and output.
# The harness uses isolated fixture accounts and synthetic Token data. It never calls
# Form.Show / OnLoad, starts a message pump, or refreshes the live Token service.
$buildText = Get-Content -LiteralPath (Join-Path $projectRoot 'build.ps1') -Raw
$match = [regex]::Match($buildText, '(?m)^\s*\$sources\s*=\s*@\(([^\r\n]+)\)')
if (-not $match.Success) { throw 'Cannot find current production source list' }
$sources = @([regex]::Matches($match.Groups[1].Value, "'([^']+\.cs)'") | ForEach-Object { Join-Path $projectRoot $_.Groups[1].Value })
if ($sources.Count -lt 10) { throw 'Incomplete production source list' }
foreach ($art in @('OfficeRoom.png', 'OfficeDeskLive.png', 'OfficeDeskDusty.png')) {
    $refs += "/resource:$(Join-Path $projectRoot $art),$art"
}
$executable = Join-Path $PSScriptRoot 'native-review.exe'
& $compiler /nologo /target:exe /main:ClaudeAccountSync.NativeReview "/out:$executable" @refs @sources (Join-Path $PSScriptRoot 'native-review.cs')
if ($LASTEXITCODE -ne 0) { throw 'Isolated review compilation failed' }
& $executable $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw "Native review reported findings (exit $LASTEXITCODE); inspect native-review-report.txt" }
