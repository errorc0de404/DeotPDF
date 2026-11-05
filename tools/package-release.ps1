# Builds the release packages of DeotPDF (the app and the deot CLI) for one system into artifacts/release.
# Used by .github/workflows/release.yml and runnable locally (PowerShell 7, or Windows PowerShell 5.1 on Windows):
#   pwsh tools/package-release.ps1 -Rid win-x64      # DeotPDF-<v>-win-x64.zip (needs the .NET 10 runtime) and -portable.zip
#   pwsh tools/package-release.ps1 -Rid linux-x64    # DeotPDF-<v>-linux-x64.tar.gz
#   pwsh tools/package-release.ps1 -Rid osx-arm64    # DeotPDF-<v>-macos-arm64.zip (DeotPDF.app inside), osx-x64 likewise
# -Stage folders builds the folders only, -Stage zip only archives folders already there (the CI signs the Windows
# files in between); the default does both. Each package keeps its top folder and gets SHA256SUMS-<v>-<rid>.txt.
# macOS and Linux packages are self-contained. The macOS app has no developer ID signature (decision 18): on a Mac it is
# signed ad hoc so Apple silicon runs it, and the first start needs right-click > Open. Build the Linux package on
# Linux and the macOS ones on a Mac (file modes, codesign); other systems can build them for a look.
# Windows restores in locked mode against the tracked lock files (win-x64). Other runtimes are not in them, so those
# restores write their own lock file under each project's obj folder and leave the tracked ones alone.
param(
    [Parameter(Mandatory)] [ValidateSet("win-x64", "linux-x64", "osx-x64", "osx-arm64")] [string]$Rid,
    [string]$Version,
    [string]$OutDir = "artifacts/release",
    [ValidateSet("all", "folders", "zip")] [string]$Stage = "all"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$Version = $Version.Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "bad version '$Version'" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$onWindows = [Environment]::OSVersion.Platform -eq "Win32NT"

# An .icns from the PNG images of the Windows icon (16 to 256 pixels); Retina sizes reuse the larger images.
function Write-Icns([string]$ico, [string]$target) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $root $ico))
    $images = @{}
    $count = [BitConverter]::ToUInt16($bytes, 4)
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + 16 * $i
        $size = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        $length = [BitConverter]::ToInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToInt32($bytes, $entry + 12)
        $images[$size] = $bytes[$offset..($offset + $length - 1)]
    }
    $types = [ordered]@{ "icp4" = 16; "icp5" = 32; "icp6" = 64; "ic07" = 128; "ic08" = 256; "ic11" = 32; "ic12" = 64; "ic13" = 256 }
    $body = New-Object IO.MemoryStream
    foreach ($type in $types.Keys) {
        if (-not $images.ContainsKey($types[$type])) { continue }
        [byte[]]$png = $images[$types[$type]]
        $body.Write([Text.Encoding]::ASCII.GetBytes($type), 0, 4)
        $body.Write((BigEndian (8 + $png.Length)), 0, 4)
        $body.Write($png, 0, $png.Length)
    }
    $out = New-Object IO.MemoryStream
    $out.Write([Text.Encoding]::ASCII.GetBytes("icns"), 0, 4)
    $out.Write((BigEndian (8 + $body.Length)), 0, 4)
    $body.WriteTo($out)
    [IO.File]::WriteAllBytes($target, $out.ToArray())
}

function BigEndian([long]$value) {
    $b = [BitConverter]::GetBytes([uint32]$value)
    [Array]::Reverse($b)
    return ,$b
}

$system = switch -Wildcard ($Rid) { "win-*" { "windows" } "linux-*" { "linux" } "osx-*" { "macos" } }
$arch = $Rid.Split("-")[1]
# Package name => self-contained.
$packages = switch ($system) {
    "windows" { [ordered]@{ "DeotPDF-$Version-win-x64" = $false; "DeotPDF-$Version-win-x64-portable" = $true } }
    "linux" { [ordered]@{ "DeotPDF-$Version-linux-$arch" = $true } }
    "macos" { [ordered]@{ "DeotPDF-$Version-macos-$arch" = $true } }
}

function Build-Folder([string]$name, [bool]$selfContained) {
    $top = Join-Path $OutDir $name
    if (Test-Path $top) { Remove-Item -Recurse -Force $top }
    New-Item -ItemType Directory -Force $top | Out-Null
    # The app files: next to the readme on Windows and Linux, inside DeotPDF.app/Contents/MacOS on macOS.
    $bin = if ($system -eq "macos") { Join-Path $top "DeotPDF.app/Contents/MacOS" } else { $top }
    $restore = if ($system -eq "windows") { "-p:RestoreLockedMode=true" } else { "-p:NuGetLockFilePath=obj/packages.$Rid.lock.json" }
    # The app and deot share one folder, so both resolve the same package versions (the shared projects pin them).
    foreach ($project in @("src/PdfNotes.Desktop/PdfNotes.Desktop.csproj", "tools/PdfNotes.Cli/PdfNotes.Cli.csproj")) {
        dotnet publish $project -c Release -r $Rid --self-contained $selfContained.ToString().ToLowerInvariant() $restore "-p:Version=$Version" -o $bin
        if ($LASTEXITCODE -ne 0) { throw "publish failed: $project" }
    }
    Get-ChildItem $bin -Filter *.pdb | Remove-Item

    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $top
    $licenses = Join-Path $top "licenses"
    foreach ($part in @("PDFium", "Clipper2", "ModelContextProtocol", "Avalonia", "SkiaSharp", "FluentUISystemIcons")) {
        New-Item -ItemType Directory -Force "$licenses/$part" | Out-Null
    }
    Copy-Item third_party/PDFium/LICENSE, third_party/PDFium/licenses/* "$licenses/PDFium"
    Copy-Item third_party/Clipper2/License.txt "$licenses/Clipper2"
    Copy-Item third_party/ModelContextProtocol/LICENSE "$licenses/ModelContextProtocol"
    Copy-Item third_party/Avalonia/* "$licenses/Avalonia"
    Copy-Item third_party/SkiaSharp/* "$licenses/SkiaSharp"
    Copy-Item third_party/FluentUISystemIcons/LICENSE "$licenses/FluentUISystemIcons"
    $readme = [IO.File]::ReadAllText("$root/tools/release-readme-$system.txt").Replace("{VERSION}", $Version)
    if ($system -eq "windows") {
        # The Windows readme keeps CRLF and a BOM (Notepad), the others LF without one.
        [IO.File]::WriteAllText((Join-Path $top "README.txt"), $readme.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object Text.UTF8Encoding $true))
    } else {
        [IO.File]::WriteAllText((Join-Path $top "README.txt"), $readme.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
    }

    switch ($system) {
        "windows" { Copy-Item tools/register-open-with.ps1 $top }
        "linux" {
            Copy-Item packaging/linux/deotpdf.desktop, packaging/linux/register-open-with.sh $top
            Copy-Item src/PdfNotes.Desktop/Assets/DeotPDF.svg (Join-Path $top "deotpdf.svg")
            if (-not $onWindows) { chmod +x (Join-Path $top "DeotPDF") (Join-Path $top "deot") (Join-Path $top "register-open-with.sh") }
        }
        "macos" {
            $contents = Join-Path $top "DeotPDF.app/Contents"
            $plist = [IO.File]::ReadAllText("$root/packaging/macos/Info.plist").Replace("{EXECUTABLE}", "DeotPDF").Replace("{VERSION}", $Version)
            [IO.File]::WriteAllText((Join-Path $contents "Info.plist"), $plist, (New-Object Text.UTF8Encoding $false))
            New-Item -ItemType Directory -Force (Join-Path $contents "Resources") | Out-Null
            Write-Icns "src/PdfNotes.Desktop/Assets/DeotPDF.ico" (Join-Path $contents "Resources/DeotPDF.icns")
            if (Get-Command codesign -ErrorAction SilentlyContinue) {
                codesign --force --deep --sign - (Join-Path $top "DeotPDF.app")
                if ($LASTEXITCODE -ne 0) { throw "codesign failed" }
            }
        }
    }
}

# The archive keeps the top folder. Zip entries are added one by one because ZipFile.CreateFromDirectory on Windows
# PowerShell 5.1 writes backslashes into the entry names.
function Write-Archive([string]$name) {
    $top = Join-Path $OutDir $name
    if (-not (Test-Path $top)) { throw "missing $top" }
    if ($system -eq "linux") {
        $archive = Join-Path $OutDir "$name.tar.gz"
        if (Test-Path $archive) { Remove-Item -Force $archive }
        tar -C $OutDir -czf $archive $name
        if ($LASTEXITCODE -ne 0) { throw "tar failed" }
        return $archive
    }
    $archive = Join-Path $OutDir "$name.zip"
    if (Test-Path $archive) { Remove-Item -Force $archive }
    if ($system -eq "macos" -and (Get-Command ditto -ErrorAction SilentlyContinue)) {
        ditto -c -k --keepParent $top $archive # keeps file modes and the app's signature
        if ($LASTEXITCODE -ne 0) { throw "ditto failed" }
        return $archive
    }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $top -Recurse -File | Sort-Object FullName) {
            $entry = "$name/" + $file.FullName.Substring($top.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
    return $archive
}

$sums = @()
foreach ($name in $packages.Keys) {
    if ($Stage -ne "zip") { Build-Folder $name $packages[$name] }
    if ($Stage -eq "folders") { continue }
    $archive = Write-Archive $name
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$hash *$(Split-Path $archive -Leaf)"
}
if ($sums.Count -gt 0) {
    $sumFile = Join-Path $OutDir "SHA256SUMS-$Version-$Rid.txt"
    $sums | Set-Content $sumFile -Encoding ascii
    Get-Content $sumFile
}
