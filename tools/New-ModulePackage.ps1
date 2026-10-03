param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9-]{1,79}$')][string]$Id,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][uri]$DownloadUrl,
    [Parameter(Mandatory = $true)][string]$License,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release\modules')
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SourceDirectory).Path)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "SourceDirectory must be a folder: $source" }
if (!$DownloadUrl.IsAbsoluteUri -or $DownloadUrl.Scheme -ne 'https') { throw 'Module download URLs must use HTTPS.' }
if ([string]::IsNullOrWhiteSpace($License)) { throw 'A license identifier or notice is required.' }
if ($source -eq $output -or
    $source.StartsWith($output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output directory must not be inside the module source directory.'
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
$archive = Join-Path $output ("{0}-{1}-windows-x64.zip" -f $Id, $Version)
$temporary = $archive + '.new'
$metadata = [IO.Path]::ChangeExtension($archive, '.json')
foreach ($path in @($temporary, $metadata)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($source, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
$archiveInfo = Get-Item -LiteralPath $temporary
if ($archiveInfo.Length -le 0) { Remove-Item -LiteralPath $temporary -Force; throw 'The module archive is empty.' }
$hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
$previous = $archive + '.previous'
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Force }
if (Test-Path -LiteralPath $archive) { Move-Item -LiteralPath $archive -Destination $previous }
try {
    Move-Item -LiteralPath $temporary -Destination $archive
} catch {
    if ((Test-Path -LiteralPath $previous) -and !(Test-Path -LiteralPath $archive)) { Move-Item -LiteralPath $previous -Destination $archive }
    throw
}
if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Force }

$entry = [ordered]@{
    id = $Id
    version = $Version
    required = $false
    state = 'available'
    download = $DownloadUrl.AbsoluteUri
    archive_size_bytes = [long]$archiveInfo.Length
    sha256 = $hash
    license = $License.Trim()
    platform = 'windows-x64'
    published_utc = [DateTime]::UtcNow.ToString('o')
}
$entry | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadata -Encoding UTF8

Write-Output ("Archive: {0}" -f $archive)
Write-Output ("Catalog entry: {0}" -f $metadata)
Write-Output ("Size: {0} bytes" -f $archiveInfo.Length)
Write-Output ("SHA-256: {0}" -f $hash)
Write-Output 'Upload the archive to the exact HTTPS URL in the catalog entry, then verify the uploaded file hash before publishing the catalog.'
