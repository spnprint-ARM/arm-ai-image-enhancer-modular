param(
    [string]$BuildRoot = (Join-Path $PSScriptRoot "build"),
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) "release\setup-modular-V1.11.exe"),
    [string]$IconPath = (Join-Path (Split-Path -Parent $PSScriptRoot) "assets\ARM.ico")
)
$ErrorActionPreference = "Stop"
$ModularRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if (!$BuildRoot.StartsWith($ModularRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "BuildRoot must be inside the Modular Edition folder."
}
$Compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$AppSource = Join-Path $ModularRoot "core\ModularApp.cs"
$SetupSource = Join-Path $PSScriptRoot "ModularSetup.cs"
$Manifest = Join-Path $PSScriptRoot "requireAdministrator.manifest"
$AppManifest = Join-Path $PSScriptRoot "asInvoker.manifest"
$Catalog = Join-Path $ModularRoot "module-catalog.json"
$QrPath = Join-Path $ModularRoot "assets\QR.jpg"
$Stage = Join-Path $BuildRoot "stage"
$AppExe = Join-Path $BuildRoot "ArmAIImageEnhancerModular.exe"
$Uninstaller = Join-Path $BuildRoot "UninstallModular.exe"
$Stub = Join-Path $BuildRoot "SetupStub.exe"
$Verifier = Join-Path $BuildRoot "PayloadVerifier.exe"
$Zip = Join-Path $BuildRoot "payload.zip"

foreach ($required in @($Compiler, $AppSource, $SetupSource, $Manifest, $AppManifest, $Catalog, $IconPath, $QrPath)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required build file was not found: $required" }
}
New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null
foreach ($generated in @($Stage, $AppExe, $Uninstaller, $Stub, $Verifier, $Zip, (Join-Path $BuildRoot "module-catalog.json"))) {
    $resolved = [IO.Path]::GetFullPath($generated)
    if (!$resolved.StartsWith($BuildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the Modular Edition build directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved).PSIsContainer) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        else { Remove-Item -LiteralPath $resolved -Force }
    }
}

# Compile the core without Python, PyTorch, BasicSR, or dynamic engine imports.
& $Compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$AppManifest" "/win32icon:$IconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/out:$AppExe" $AppSource
if ($LASTEXITCODE -ne 0) { throw "Modular core application compilation failed." }
Copy-Item -LiteralPath $Catalog -Destination (Join-Path $BuildRoot "module-catalog.json") -Force
$appCheck = Start-Process -FilePath $AppExe -ArgumentList "--self-check" -Wait -PassThru
if ($appCheck.ExitCode -ne 0) { throw "Core application self-check failed; setup was not assembled." }

& $Compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$Manifest" "/win32icon:$IconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Security.dll "/out:$Uninstaller" $SetupSource
if ($LASTEXITCODE -ne 0) { throw "Uninstaller compilation failed." }
& $Compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$AppManifest" "/win32icon:$IconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Security.dll "/out:$Stub" $SetupSource
if ($LASTEXITCODE -ne 0) { throw "Setup bootstrap compilation failed." }
& $Compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32manifest:$AppManifest" "/win32icon:$IconPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Security.dll "/out:$Verifier" $SetupSource
if ($LASTEXITCODE -ne 0) { throw "Payload verifier compilation failed." }

New-Item -ItemType Directory -Force -Path (Join-Path $Stage "_internal\assets"),(Join-Path $Stage "assets") | Out-Null
Copy-Item -LiteralPath $AppExe -Destination (Join-Path $Stage "ArmAIImageEnhancerModular.exe") -Force
Copy-Item -LiteralPath $Uninstaller -Destination (Join-Path $Stage "UninstallModular.exe") -Force
Copy-Item -LiteralPath $Catalog -Destination (Join-Path $Stage "module-catalog.json") -Force
Copy-Item -LiteralPath $IconPath -Destination (Join-Path $Stage "_internal\assets\ARM.ico") -Force
Copy-Item -LiteralPath $QrPath -Destination (Join-Path $Stage "assets\QR.jpg") -Force
if ((Get-FileHash -LiteralPath $QrPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Stage "assets\QR.jpg") -Algorithm SHA256).Hash) {
    throw "The PromptPay QR image changed during staging."
}
foreach ($required in @("ArmAIImageEnhancerModular.exe", "UninstallModular.exe", "module-catalog.json", "_internal\assets\ARM.ico", "assets\QR.jpg")) {
    if (!(Test-Path -LiteralPath (Join-Path $Stage $required))) { throw "Required staged file is missing: $required" }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip, [IO.Compression.CompressionLevel]::Optimal, $false)
if (!(Test-Path -LiteralPath $Zip) -or (Get-Item -LiteralPath $Zip).Length -le 0) { throw "Setup payload archive was not created." }

$Output = [IO.Path]::GetFullPath($Output)
$OutputParent = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $OutputParent | Out-Null
$Building = [IO.Path]::Combine($OutputParent, ([IO.Path]::GetFileNameWithoutExtension($Output) + ".new.exe"))
$Previous = $Output + ".previous"
if (Test-Path -LiteralPath $Building) { Remove-Item -LiteralPath $Building -Force }
$outStream = [IO.File]::Open($Building, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $stubStream = [IO.File]::OpenRead($Stub)
    try { $stubStream.CopyTo($outStream) } finally { $stubStream.Dispose() }
    $archiveOffset = $outStream.Position
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $zipStream = [IO.File]::OpenRead($Zip)
        try {
            $digest = $sha.ComputeHash($zipStream)
            $zipStream.Position = 0
            $zipStream.CopyTo($outStream)
        } finally { $zipStream.Dispose() }
    } finally { $sha.Dispose() }
    $writer = [IO.BinaryWriter]::new($outStream, [Text.Encoding]::ASCII, $true)
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes("ARMIMOD1"))
        $writer.Write([Int64]$archiveOffset)
        $writer.Write($digest)
        $writer.Flush()
    } finally { $writer.Dispose() }
} finally { $outStream.Dispose() }

# Verify the final self-extracting file before replacing the previous Modular
# setup build. The verification opens the exact installer users would run.
try {
    $verifyArguments = '--verify-payload "' + $Building + '"'
    $verify = Start-Process -FilePath $Verifier -ArgumentList $verifyArguments -Wait -PassThru
    if ($verify.ExitCode -ne 0) { throw "Assembled setup payload failed SHA-256 or required-file validation." }
} catch {
    if (Test-Path -LiteralPath $Building) { Remove-Item -LiteralPath $Building -Force }
    throw
}
if (Test-Path -LiteralPath $Previous) { Remove-Item -LiteralPath $Previous -Force }
if (Test-Path -LiteralPath $Output) { Move-Item -LiteralPath $Output -Destination $Previous }
try {
    Move-Item -LiteralPath $Building -Destination $Output
} catch {
    if ((Test-Path -LiteralPath $Previous) -and !(Test-Path -LiteralPath $Output)) { Move-Item -LiteralPath $Previous -Destination $Output }
    throw
}
if (Test-Path -LiteralPath $Previous) { Remove-Item -LiteralPath $Previous -Force }
Get-Item -LiteralPath $Output | Select-Object FullName, Length, LastWriteTime
