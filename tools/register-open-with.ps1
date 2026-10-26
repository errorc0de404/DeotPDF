# Adds DeotPDF to the "Open with" list for .pdf files for the current user (HKCU, no admin rights).
# It does not make DeotPDF the default PDF app (spec 2.3, 4.6); Windows asks the user for that.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools/register-open-with.ps1 [-Exe <DeotPDF.exe>] [-Unregister]
# Default exe: DeotPDF.exe next to this script, else artifacts/publish/win-x64/DeotPDF.exe (a later publish to that folder keeps the registration valid).
param([string]$Exe = "", [switch]$Unregister)

$progId = "PdfNotes.Pdf"
$classes = "HKCU:\Software\Classes"
$appKey = "$classes\Applications\DeotPDF.exe"
# Before the rename to DeotPDF (2026-10-24) the exe was PdfNotes.App.exe; its entry is removed so the list does not show both.
$legacyAppKey = "$classes\Applications\PdfNotes.App.exe"
$capabilities = "HKCU:\Software\PdfNotes\Capabilities"

Add-Type -Namespace PdfNotes -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);'

function Remove-KeyIfPresent([string]$path) { if (Test-Path $path) { Remove-Item -Recurse -Force $path } }

if ($Unregister) {
    Remove-KeyIfPresent "$classes\$progId"
    Remove-KeyIfPresent $appKey
    Remove-KeyIfPresent $legacyAppKey
    Remove-KeyIfPresent "HKCU:\Software\PdfNotes"
    if (Test-Path "$classes\.pdf\OpenWithProgids") { Remove-ItemProperty -Path "$classes\.pdf\OpenWithProgids" -Name $progId -ErrorAction SilentlyContinue }
    Remove-ItemProperty -Path "HKCU:\Software\RegisteredApplications" -Name "PdfNotes" -ErrorAction SilentlyContinue
    [PdfNotes.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    "DeotPDF removed from Open with."
    return
}

# In a release zip the script sits next to DeotPDF.exe.
if ($Exe -eq "" -and (Test-Path (Join-Path $PSScriptRoot "DeotPDF.exe"))) { $Exe = Join-Path $PSScriptRoot "DeotPDF.exe" }
if ($Exe -eq "") { $Exe = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\publish\win-x64\DeotPDF.exe" }
$Exe = [System.IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $Exe)) { throw "Not found: $Exe (publish first)" }
$command = "`"$Exe`" `"%1`""

# Creates a key only when missing: New-Item -Force on an existing key can drop its values (other apps' entries).
function Ensure-Key([string]$path) { if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null } }

function Set-Default([string]$path, [string]$value) {
    Ensure-Key $path
    Set-ItemProperty -Path $path -Name "(default)" -Value $value
}

# The file type DeotPDF opens (the ProgId and the Software\PdfNotes key keep the old id, so re-running replaces an earlier registration).
Set-Default "$classes\$progId" "PDF (DeotPDF)"
Set-ItemProperty -Path "$classes\$progId" -Name "FriendlyTypeName" -Value "PDF (DeotPDF)"
Set-Default "$classes\$progId\DefaultIcon" "`"$Exe`",0"
Set-Default "$classes\$progId\shell\open\command" $command

# .pdf may be opened with it (the "Open with" list), without changing the default.
Ensure-Key "$classes\.pdf\OpenWithProgids"
New-ItemProperty -Path "$classes\.pdf\OpenWithProgids" -Name $progId -PropertyType None -Value ([byte[]]@()) -Force | Out-Null

# The application entry (name in the list, supported types).
Remove-KeyIfPresent $legacyAppKey
Set-Default "$appKey\shell\open\command" $command
Set-ItemProperty -Path $appKey -Name "FriendlyAppName" -Value "DeotPDF"
Ensure-Key "$appKey\SupportedTypes"
New-ItemProperty -Path "$appKey\SupportedTypes" -Name ".pdf" -PropertyType String -Value "" -Force | Out-Null

# Settings > Apps > Default apps can offer it.
Ensure-Key "$capabilities\FileAssociations"
Set-ItemProperty -Path $capabilities -Name "ApplicationName" -Value "DeotPDF"
Set-ItemProperty -Path $capabilities -Name "ApplicationDescription" -Value "PDF notes"
Set-ItemProperty -Path "$capabilities\FileAssociations" -Name ".pdf" -Value $progId
Ensure-Key "HKCU:\Software\RegisteredApplications"
Set-ItemProperty -Path "HKCU:\Software\RegisteredApplications" -Name "PdfNotes" -Value "Software\PdfNotes\Capabilities"

[PdfNotes.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) # SHCNE_ASSOCCHANGED
"DeotPDF added to Open with for .pdf: $Exe"
