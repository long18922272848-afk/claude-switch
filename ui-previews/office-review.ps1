param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$refs = @('/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll', '/reference:System.Web.Extensions.dll', "/reference:$wpf\UIAutomationClient.dll", "/reference:$wpf\UIAutomationTypes.dll", "/reference:$wpf\WindowsBase.dll")
$buildText = Get-Content -LiteralPath (Join-Path $projectRoot 'build.ps1') -Raw
$line = [regex]::Match($buildText, '(?m)^\s*\$sources\s*=\s*@\(([^\r\n]+)\)')
if (-not $line.Success) { throw 'Production source list missing' }
$sources = @([regex]::Matches($line.Groups[1].Value, "'([^']+\.cs)'") | ForEach-Object { Join-Path $projectRoot $_.Groups[1].Value })
$executable = Join-Path $env:TEMP ('cas-office-review-' + [guid]::NewGuid().ToString('N') + '.exe')
$resources = @('OfficeRoom.png','OfficeDeskLive.png','OfficeDeskDusty.png') | ForEach-Object {
    $resourcePath = Join-Path $projectRoot $_
    if (Test-Path -LiteralPath $resourcePath) { '/resource:' + $resourcePath + ',' + $_ }
}
try {
    & $compiler /nologo /target:exe /main:ClaudeAccountSync.OfficeReview "/out:$executable" @resources @refs @sources (Join-Path $PSScriptRoot 'office-review.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Office review compilation failed' }
    & $executable $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw 'Office review failed' }
}
finally { if (Test-Path -LiteralPath $executable) { Remove-Item -LiteralPath $executable } }
