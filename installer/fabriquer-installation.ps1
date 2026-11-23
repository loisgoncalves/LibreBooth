param(
    [string]$Configuration = 'Release',
    # Version publique (GitHub) : sans les fichiers de Canon et de DNP, qui ne peuvent pas être redistribués.
    [switch]$Public,
    # Dossier du pilote Windows 11 de la DNP DS620 (contient dpinst64.exe et DPDS620.inf) : version privée seulement.
    [string]$DriverFolder = "$PSScriptRoot\..\Win_11_DP-DS620_Driver-1-3\DP-DS620_Driver\11\DriverPackage",
    # Remplacer une version déjà fabriquée (à éviter : une version livrée ne doit pas changer de contenu).
    [switch]$Force
)
# Fabrique le programme d'installation (LibreBooth-<version>-Installation.exe), le zip et leurs empreintes SHA-256.
#   .\installer\fabriquer-installation.ps1            version privée (publish\), avec les pilotes Canon et DNP de drivers\
#   .\installer\fabriquer-installation.ps1 -Public    version publique (publish\github\), sans aucun fichier de Canon ou de DNP
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = (Resolve-Path "$PSScriptRoot\..").Path
$appProject = Join-Path $root 'src\LibreBooth.App\LibreBooth.App.csproj'
$installerProject = Join-Path $PSScriptRoot 'LibreBooth.Installer\LibreBooth.Installer.csproj'
# Numéro de version unique de LibreBooth (Directory.Build.props, changé par installer\nouvelle-version.ps1).
$version = ([xml](Get-Content -Raw (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Numéro de version illisible dans Directory.Build.props : $version" }
$outFolder = if ($Public) { Join-Path $root 'publish\github' } else { Join-Path $root 'publish' }
$publish = Join-Path $outFolder 'LibreBooth'
$setupFile = Join-Path $outFolder "LibreBooth-$version-Installation.exe"
$zipFile = Join-Path $outFolder "LibreBooth-$version-win-x86.zip"
$vendorFiles = 'EDSDK.dll', 'EdsImage.dll', 'cspstat.dll'

foreach ($file in $setupFile, $zipFile) {
    if ((Test-Path $file) -and -not $Force) { throw "$file existe déjà : changez le numéro de version (ou -Force pour une version pas encore livrée)." }
}
if (-not $Public -and -not (Test-Path (Join-Path $DriverFolder 'dpinst64.exe'))) { throw "Pilote DNP introuvable : $DriverFolder" }
New-Item -ItemType Directory -Force $outFolder | Out-Null

Write-Host "1/5 Publication de LibreBooth $version ($(if ($Public) { 'version publique, sans pilotes' } else { 'version privée, avec pilotes' }))…"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
$bundle = if ($Public) { 'false' } else { 'true' }
# Version publique : ni fichiers de débogage, ni chemin du dossier de compilation dans les binaires
# (ils contiendraient le nom du compte Windows de la personne qui compile).
$buildOptions = @()
if ($Public) {
    $buildOptions = @('-p:DebugType=none', '-p:DebugSymbols=false', "-p:PathMap=$root=C:\LibreBooth")
    dotnet clean $appProject -c $Configuration | Out-Null
    dotnet clean $installerProject -c $Configuration | Out-Null
}
dotnet publish $appProject -c $Configuration -o $publish "-p:BundleVendorDrivers=$bundle" @buildOptions | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Publication de LibreBooth impossible' }
if ($Public) { Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force }
foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    $source = Join-Path $root $file
    if (Test-Path $source) { Copy-Item $source (Join-Path $publish ($file -replace '^LICENSE$', 'LICENSE.txt')) -Force }
}
if ($Public) {
    $leaked = Get-ChildItem $publish -Recurse -File | Where-Object { $vendorFiles -contains $_.Name }
    if ($leaked) { throw "Fichiers de fabricants dans la version publique : $($leaked.Name -join ', ')" }
    @"
LibreBooth $version – pilotes à ajouter

Les fichiers de Canon et de DNP ne sont pas fournis avec LibreBooth : ils appartiennent à leurs fabricants.
Le programme d'installation (LibreBooth-$version-Installation.exe) s'occupe des deux premiers points.

1. Appareil photo Canon (obligatoire) : téléchargez le SDK Canon EDSDK pour Windows (gratuit, sur inscription
   chez Canon : https://www.canon-europe.com/business/imaging-solutions/sdk/). Puis, dans LibreBooth :
   admin > Appareil photo > « Ajouter le SDK Canon… », et choisissez le zip téléchargé : LibreBooth en extrait
   lui-même EDSDK.dll et EdsImage.dll (version 32 bits) et les range dans C:\Users\<nom>\LibreBooth\Pilotes.
2. Imprimante DNP DS620 : installez son pilote Windows depuis la page Téléchargements de DNP
   (https://dnpphoto.eu/downloads/).
3. Facultatif : cspstat.dll (API d'état des imprimantes DNP), à copier dans le dossier Pilotes, pour lire le
   compteur de tirages et les erreurs de l'imprimante.

Pour tout contrôler : menu Démarrer > LibreBooth > « Vérifier l'installation de LibreBooth ».
"@ | Set-Content -Encoding UTF8 (Join-Path $publish 'PILOTES-A-AJOUTER.txt')
}

Write-Host '2/5 Compilation du programme d''installation…'
$installerOut = Join-Path $root 'publish\_installation'
dotnet build $installerProject -c $Configuration -o $installerOut -p:Version=$version @buildOptions | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Compilation du programme d''installation impossible' }

if ($Public) {
    # Garde-fou : aucun fichier compilé ou texte de la version publique ne doit contenir le nom du compte Windows ni
    # celui du PC. Ils sont cherchés comme mots entiers : le nom du compte Windows peut être le début de celui du
    # compte GitHub, qui figure dans l'adresse publique du dépôt (mises à jour), sans que ce soit une fuite.
    $own = @(Get-ChildItem $publish -Recurse -File | Where-Object { $_.Name -like 'LibreBooth*' -or $_.Extension -in '.json', '.txt', '.md', '.config' })
    $own += Get-Item (Join-Path $installerOut 'LibreBooth-Installation.exe')
    foreach ($name in @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ }) {
        $word = '(?<![\p{L}\p{N}])' + [regex]::Escape($name) + '(?![\p{L}\p{N}])'
        foreach ($encoding in 'ascii', 'unicode') {
            $hits = $own | Select-String -Pattern $word -Encoding $encoding -List
            if ($hits) { throw "Le nom du compte Windows ou du PC se trouve dans : $(($hits.Path | Sort-Object -Unique) -join ', ')" }
        }
    }
}

Write-Host '3/5 Archive du contenu…'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('librebooth-installation-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$payloadZip = "$staging.zip"
try {
    New-Item -ItemType Directory -Force (Join-Path $staging 'LibreBooth') | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination (Join-Path $staging 'LibreBooth') -Recurse -Force
    if (-not $Public) {
        New-Item -ItemType Directory -Force (Join-Path $staging 'PiloteDNP') | Out-Null
        Copy-Item -Path (Join-Path $DriverFolder '*') -Destination (Join-Path $staging 'PiloteDNP') -Recurse -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    Write-Host '4/5 Assemblage de l''exécutable…'
    $exeBytes = [System.IO.File]::ReadAllBytes((Join-Path $installerOut 'LibreBooth-Installation.exe'))
    $zipLength = (Get-Item $payloadZip).Length
    $stream = [System.IO.File]::Create($setupFile)
    try {
        $stream.Write($exeBytes, 0, $exeBytes.Length)
        $source = [System.IO.File]::OpenRead($payloadZip)
        try { $source.CopyTo($stream) } finally { $source.Dispose() }
        $marker = [System.Text.Encoding]::ASCII.GetBytes('LBSETUP1')
        $stream.Write($marker, 0, 8)
        $length = [BitConverter]::GetBytes([long]$zipLength)
        $stream.Write($length, 0, 8)
    }
    finally { $stream.Dispose() }
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
    Remove-Item -Force $payloadZip -ErrorAction SilentlyContinue
}

Write-Host '5/5 Zip et empreintes SHA-256…'
if (Test-Path $zipFile) { Remove-Item -Force $zipFile }
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zipFile, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$sums = foreach ($file in $setupFile, $zipFile) {
    '{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
$sums | Set-Content -Encoding ASCII (Join-Path $outFolder "LibreBooth-$version-SHA256.txt")
foreach ($file in $setupFile, $zipFile) { Write-Host ("Terminé : {0} ({1:N1} Mo)" -f $file, ((Get-Item $file).Length / 1MB)) }
