param(
    # correctif : 2.1.0 → 2.1.1 (correction sans nouveauté)
    # fonction  : 2.1.1 → 2.2.0 (nouvelle fonction ou changement visible)
    # majeure   : 2.2.0 → 3.0.0 (changement qui modifie les habitudes ou les fichiers de l'utilisateur)
    [Parameter(Mandatory)]
    [ValidateSet('correctif', 'fonction', 'majeure')]
    [string]$Type,
    # Première ligne de l'historique pour cette version (facultatif : à compléter ensuite dans CHANGELOG.md).
    [string]$Note
)
# Passe LibreBooth à la version suivante : numéro dans Directory.Build.props (programme, bibliothèque et programme
# d'installation) et nouvelle entrée en haut de CHANGELOG.md. Ensuite : compléter l'historique, puis fabriquer
# l'installation avec installer\fabriquer-installation.ps1 (-Public pour la version de GitHub).
#   .\installer\nouvelle-version.ps1 -Type correctif -Note "Correction de …"
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$props = Join-Path $root 'Directory.Build.props'
$changelog = Join-Path $root 'CHANGELOG.md'

$text = [System.IO.File]::ReadAllText($props)
$match = [regex]::Match($text, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
if (-not $match.Success) { throw "Numéro de version introuvable dans $props" }
$major, $minor, $patch = 1..3 | ForEach-Object { [int]$match.Groups[$_].Value }
$old = "$major.$minor.$patch"
switch ($Type) {
    'correctif' { $patch++ }
    'fonction' { $minor++; $patch = 0 }
    'majeure' { $major++; $minor = 0; $patch = 0 }
}
$new = "$major.$minor.$patch"

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($props, $text.Replace($match.Value, "<Version>$new</Version>"), $utf8)

# Nouvelle entrée juste avant la plus récente de l'historique.
$log = [System.IO.File]::ReadAllText($changelog)
$nl = if ($log.Contains("`r`n")) { "`r`n" } else { "`n" }
$today = (Get-Date).ToString('dd/MM/yyyy', [System.Globalization.CultureInfo]::InvariantCulture)
$entry = "## $new – $today$nl$nl- $(if ($Note) { $Note } else { 'À compléter.' })$nl$nl"
$first = [regex]::Match($log, '(?m)^## ')
$log = if ($first.Success) { $log.Insert($first.Index, $entry) } else { $log.TrimEnd() + "$nl$nl" + $entry }
[System.IO.File]::WriteAllText($changelog, $log, $utf8)

Write-Host "LibreBooth $old → $new ($Type)."
Write-Host "Reste à faire : compléter CHANGELOG.md, puis .\installer\fabriquer-installation.ps1 (et -Public pour GitHub)."
