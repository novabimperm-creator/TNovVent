param(
    [int]$Major = 0
)

# Папка, где лежит скрипт (корень проекта)
$ProjectDir = $PSScriptRoot
$versionFilePath = Join-Path -Path $ProjectDir -ChildPath "Properties\VersionInfo.cs"

Write-Host "=== UpdateVersion.ps1 ==="
Write-Host "ProjectDir (from PSScriptRoot): $ProjectDir"

if ($Major -eq 0) {
    if (-not (Test-Path -Path $versionFilePath)) {
        throw "VersionInfo.cs not found at $versionFilePath"
    }

    $existingContent = Get-Content -Path $versionFilePath -Raw
    if ($existingContent -match 'AssemblyVersion\("(\d+)\.') {
        $Major = [int]$Matches[1]
        Write-Host "Major (from VersionInfo.cs): $Major"
    } else {
        throw "Could not read major version from $versionFilePath"
    }
} else {
    Write-Host "Major (from parameter): $Major"
}

# Текущая дата и время
$date = Get-Date
$year = $date.ToString("yy")              # 26 для 2026
$monthDay = $date.ToString("MMdd")        # 0710
$minutesFromMidnight = $date.Hour * 60 + $date.Minute

$version = "$Major.$year.$monthDay.$minutesFromMidnight"
Write-Host "Generated version: $version"
Write-Host "Target file: $versionFilePath"

# Создаём папку Properties, если её нет
$propertiesDir = Join-Path -Path $ProjectDir -ChildPath "Properties"
if (-not (Test-Path -Path $propertiesDir)) {
    Write-Host "Creating Properties directory..."
    New-Item -ItemType Directory -Path $propertiesDir -Force | Out-Null
}

# Содержимое файла с атрибутами версии
$content = @"
using System.Reflection;

[assembly: AssemblyVersion("$version")]
[assembly: AssemblyFileVersion("$version")]
"@

# Запись файла (UTF-8 без BOM)
Set-Content -Path $versionFilePath -Value $content -Encoding UTF8
Write-Host "File written successfully."
Write-Host "=========================="
