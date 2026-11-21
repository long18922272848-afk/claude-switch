param([switch]$Test, [string]$OutputName = 'claude-switch.exe')
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or $OutputName -notlike '*.exe') { throw 'OutputName must be a local executable filename' }
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') { throw 'Build requires 64-bit Windows and .NET Framework 4.8 or newer.' }
$framework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
if (-not $framework -or $framework.Release -lt 528040) { throw '.NET Framework 4.8 or newer is required.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The Windows .NET Framework C# compiler was not found.' }
Push-Location $PSScriptRoot
try {
    $refs = @('/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll', '/reference:System.Web.Extensions.dll')
    $wpf = Join-Path (Split-Path $compiler) 'WPF'
    $refs += @("/reference:$wpf\UIAutomationClient.dll", "/reference:$wpf\UIAutomationTypes.dll", "/reference:$wpf\WindowsBase.dll")
    foreach ($art in @('OfficeRoom.png', 'OfficeDeskLive.png', 'OfficeDeskDusty.png')) {
        $refs += "/resource:$art,$art"
    }
    $sources = @('AssemblyInfo.cs', 'ClaudeAccountSync.cs', 'Panel.cs', 'Pet.cs', 'PetInteraction.cs', 'WorkbenchChrome.cs', 'PetGallery.cs', 'OfficeHistory.cs', 'OfficeMap.cs', 'Relay.cs', 'RelayState.cs', 'RelayUi.cs', 'Knowledge.cs', 'LiveKnowledge.cs', 'CodeKnowledgeHook.cs', 'IdleActivity.cs', 'EmailPrivacy.cs', 'Usage.cs', 'UsageHistoryReader.cs', 'UsageUi.cs', 'UsageDesktopReader.cs', 'UsageMonitor.cs', 'TokenStatistics.cs', 'TokenStatisticsUi.cs', 'TokenHeatmapUi.cs')
    # Release builds never generate PDBs or embed a debug path. This legacy compiler
    # includes a PE build timestamp, so repeated compilations need not have equal hashes.
    $options = @('/nologo', '/platform:x64', '/optimize+', '/debug-', '/utf8output')
    & $compiler @options /target:winexe "/out:$OutputName" /win32icon:icon.ico @refs @sources
    if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed' }
    if ($Test) {
        & $compiler @options /target:exe /main:ClaudeAccountSync.RegressionTests /out:RegressionTests.exe @refs @sources RegressionTests.cs RelayStateTests.cs RelayTests.cs KnowledgeTests.cs LiveKnowledgeTests.cs CodeKnowledgeHookTests.cs EmailPrivacyTests.cs UsageTests.cs UsageHistoryReaderTests.cs UsageDesktopReaderTests.cs UsageMonitorTests.cs PetInteractionTests.cs TokenStatisticsTests.cs OfficeTests.cs
        if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
        & .\RegressionTests.exe
        if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed' }
    }
    Write-Output "Built $OutputName for Windows x64 (.NET Framework 4.8+)."
} finally { Pop-Location }
