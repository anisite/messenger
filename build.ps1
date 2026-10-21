# Produit dist\ avec MessengerWrapper.exe (autonome, aucun runtime .NET requis) + install.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish "$root\src\MessengerWrapper.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw 'publish a échoué' }

Remove-Item "$dist\*.pdb", "$dist\*.xml" -ErrorAction SilentlyContinue
Copy-Item "$root\install.ps1" $dist
Write-Host "`nPrêt : $dist  (copie ce dossier sur chaque machine puis lance install.ps1)"
