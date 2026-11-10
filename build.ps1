param([switch]$Test, [switch]$InteractiveTest, [string]$OutputName = 'auto-tools.exe', [ValidateSet('anycpu', 'x86', 'x64')][string]$TestPlatform = 'anycpu')
$ErrorActionPreference = 'Stop'
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or [IO.Path]::GetExtension($OutputName) -ne '.exe') {
    throw 'OutputName must be an executable file name without a directory.'
}
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $compiler)) { throw '.NET Framework compiler was not found.' }
$output = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$applicationPath = Join-Path $output $OutputName
$sources = @(Get-ChildItem -Path (Join-Path $taskRoot 'src\*.cs') | ForEach-Object { $_.FullName })
function Invoke-TaskCompiler([string[]]$CompilerArguments) {
    $responseFile = [IO.Path]::GetTempFileName()
    try {
        $quotedArguments = @($CompilerArguments | ForEach-Object { '"' + $_ + '"' })
        [IO.File]::WriteAllLines($responseFile, $quotedArguments, [Text.UTF8Encoding]::new($true))
        $compilerProcess = Start-Process -FilePath $compiler -ArgumentList ('@"' + $responseFile + '"') -Wait -PassThru -NoNewWindow
        if ($compilerProcess.ExitCode -ne 0) { throw "Compiler failed with exit code $($compilerProcess.ExitCode)." }
    }
    finally { Remove-Item $responseFile -ErrorAction SilentlyContinue }
}
$uiResources = @(
    "/resource:$taskRoot\assets\fonts\PressStart2P-Regular.ttf,AutoTools.PixelFont.ttf",
    "/resource:$taskRoot\assets\fonts\OFL-PressStart2P.txt,AutoTools.OFL-PressStart2P.txt",
    "/resource:$taskRoot\third_party\nes.css\LICENSE,AutoTools.NES-LICENSE.txt",
    "/resource:$taskRoot\assets\auto-tools.ico,AutoTools.App.ico"
)
Invoke-TaskCompiler (@('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$applicationPath", "/win32manifest:$taskRoot\app.manifest", "/win32icon:$taskRoot\assets\auto-tools.ico", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll') + $uiResources + $sources)
Write-Host "Built: $applicationPath"
if ($Test -or $InteractiveTest) {
    $testSources = @(Get-ChildItem -Path (Join-Path $taskRoot 'tests\*.cs') | ForEach-Object { $_.FullName })
    Invoke-TaskCompiler (@('/nologo', '/target:exe', "/platform:$TestPlatform", '/utf8output', '/codepage:65001', "/out:$output\AutoTools.Tests.exe", "/reference:$applicationPath", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll') + $testSources)
    $testArguments = @{ FilePath = "$output\AutoTools.Tests.exe"; Wait = $true; PassThru = $true; NoNewWindow = $true }
    if ($InteractiveTest) { $testArguments.ArgumentList = '--interactive' }
    $testProcess = Start-Process @testArguments
    if ($testProcess.ExitCode -ne 0) { throw 'Tests failed.' }
}
