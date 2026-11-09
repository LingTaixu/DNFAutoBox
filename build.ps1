param([switch]$Test)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $compiler)) { throw '.NET Framework compiler was not found.' }
$output = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
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
    "/resource:$taskRoot\assets\fonts\PressStart2P-Regular.ttf,AutoBox.PixelFont.ttf",
    "/resource:$taskRoot\assets\fonts\OFL-PressStart2P.txt,AutoBox.OFL-PressStart2P.txt",
    "/resource:$taskRoot\third_party\nes.css\LICENSE,AutoBox.NES-LICENSE.txt",
    "/resource:$taskRoot\assets\autobox.ico,AutoBox.App.ico"
)
Invoke-TaskCompiler (@('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/codepage:65001', "/out:$output\AutoBox.exe", "/win32manifest:$taskRoot\app.manifest", "/win32icon:$taskRoot\assets\autobox.ico", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll') + $uiResources + $sources)
Write-Host "Built: $output\AutoBox.exe"
if ($Test) {
    Invoke-TaskCompiler @('/nologo', '/target:exe', '/platform:anycpu', '/utf8output', '/codepage:65001', "/out:$output\AutoBox.Tests.exe", "/reference:$output\AutoBox.exe", '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', (Join-Path $taskRoot 'tests\MacroTests.cs'))
    $testProcess = Start-Process -FilePath "$output\AutoBox.Tests.exe" -Wait -PassThru -NoNewWindow
    if ($testProcess.ExitCode -ne 0) { throw 'Tests failed.' }
}
