# Compile Prisme, lance l'autotest sur la version publiée, puis fabrique dist\Prisme-Setup-<version>.exe et la mise à jour légère
# avec le moteur d'installeur commun (..\installeur\fabriquer.ps1)
# Usage : .\build.ps1            (tout)
#         .\build.ps1 -NoTest    (sans l'autotest)
param([switch]$NoTest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

# LibreOffice n'est plus embarqué : Prisme le télécharge au premier document qui en a besoin (src\Core\OfficeSetup.cs)
foreach ($f in 'vendor\ffmpeg.exe', 'vendor\yt-dlp.exe') {
  if (-not (Test-Path "$root\$f")) { throw "Moteur manquant : $f (voir README, section Moteurs)" }
}

[xml]$proj = Get-Content "$root\src\Prisme.csproj" -Encoding UTF8
$version = $proj.Project.PropertyGroup.Version | Select-Object -First 1
Write-Host "== Prisme $version ==" -ForegroundColor Cyan

# seulement la copie de publication : la version installée et ses tâches ne sont pas touchées
Get-Process Prisme -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\publish\*" } | Stop-Process -Force
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }

Write-Host '-> Publication (.NET 10 autonome, un seul exe + moteurs)'
dotnet publish "$root\src\Prisme.csproj" -c Release -r win-x64 --self-contained true -nologo -v q `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o "$root\publish"
if ($LASTEXITCODE) { throw 'Échec de la compilation' }

if (-not $NoTest) {
  # documents : LibreOffice de %LOCALAPPDATA%\Prisme\office, téléchargé sans question au premier autotest qui en manque
  Write-Host '-> Autotest (toutes les conversions)'
  $report = "$env:TEMP\prisme-selftest.txt"
  Remove-Item $report -ErrorAction SilentlyContinue
  $p = Start-Process "$root\publish\Prisme.exe" -ArgumentList '--selftest' -Wait -PassThru
  if (-not (Test-Path $report)) { throw "L'autotest s'est arrêté sans rapport (code $($p.ExitCode))" }
  Get-Content $report -Encoding UTF8 | Where-Object { $_ -match 'ÉCHEC|TOUT' }
  if ($p.ExitCode -ne 0) { throw "Autotest : $($p.ExitCode) échec(s), voir $env:TEMP\prisme-selftest.txt" }
}

# empreinte des moteurs : un Prisme installé avec la même peut prendre la mise à jour légère (sans moteurs)
# yt-dlp est exclu : il se met à jour tout seul après installation
$eng = Get-ChildItem "$root\publish\engines", "$root\publish\libvlc" -Recurse -File | Where-Object Name -ne 'yt-dlp.exe' | Sort-Object FullName |
  ForEach-Object { $_.FullName.Substring($root.Length) + ':' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
$sha = [Security.Cryptography.SHA256]::Create()
$enginesId = (-join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($eng -join "`n")) | ForEach-Object { $_.ToString('x2') })).Substring(0, 12)
[IO.File]::WriteAllText("$root\publish\engines.id", $enginesId)

# installeurs : moteur commun ..\installeur (fenêtre aux couleurs de Prisme), plus d'Inno Setup
$fab = Join-Path (Split-Path $root) 'installeur\fabriquer.ps1'
$cfg = Join-Path (Split-Path $root) 'installeur\applis\prisme'
if (-not (Test-Path $fab)) { throw "Moteur d'installeur introuvable : $fab" }
Write-Host '-> Installeur complet'
& $fab -Appli $cfg -Charge "$root\publish" -Version $version -Sortie "$root\dist\Prisme-Setup-$version.exe"
# mise à jour légère : Prisme.exe (et engines.id) seulement, les moteurs déjà installés sont gardés ; refuse de s'installer sans Prisme
Write-Host '-> Mise à jour légère'
& $fab -Appli $cfg -Charge "$root\publish" -Version $version -Sortie "$root\dist\Prisme-Update-$version-$enginesId.exe" -Leger -Exclure engines, libvlc

foreach ($f in "Prisme-Setup-$version.exe", "Prisme-Update-$version-$enginesId.exe") {
  $setup = Get-Item "$root\dist\$f"
  # relit le pied et décompresse toute la charge, sans rien installer
  $v = Start-Process $setup.FullName -ArgumentList '--verifier-charge' -Wait -PassThru -RedirectStandardOutput "$env:TEMP\prisme-verif.txt"
  if ($v.ExitCode -ne 0) { Get-Content "$env:TEMP\prisme-verif.txt" -Encoding UTF8 | Select-Object -Last 5; throw "Charge illisible : $f" }
  Write-Host ("OK : {0} ({1:N0} Mo)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
}
Write-Host 'Publier les DEUX fichiers dans la release GitHub (le léger sert aux Prisme qui ont déjà ces moteurs).'
