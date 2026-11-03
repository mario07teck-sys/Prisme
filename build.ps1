# Compile Prisme, lance l'autotest sur la version publiée, puis fabrique dist\Prisme-Setup-<version>.exe
# Usage : .\build.ps1            (tout)
#         .\build.ps1 -NoTest    (sans l'autotest)
param([switch]$NoTest)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

foreach ($f in 'vendor\ffmpeg.exe', 'vendor\yt-dlp.exe', 'vendor\office\program\soffice.exe') {
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
  Write-Host '-> Autotest (toutes les conversions)'
  $report = "$env:TEMP\prisme-selftest.txt"
  Remove-Item $report -ErrorAction SilentlyContinue
  $p = Start-Process "$root\publish\Prisme.exe" -ArgumentList '--selftest' -Wait -PassThru
  if (-not (Test-Path $report)) { throw "L'autotest s'est arrêté sans rapport (code $($p.ExitCode))" }
  Get-Content $report -Encoding UTF8 | Where-Object { $_ -match 'ÉCHEC|TOUT' }
  if ($p.ExitCode -ne 0) { throw "Autotest : $($p.ExitCode) échec(s), voir $env:TEMP\prisme-selftest.txt" }
}

Write-Host "-> Installeur"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 introuvable : winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DAppVersion=$version" "$root\installer\Prisme.iss"
if ($LASTEXITCODE) { throw "Échec d'Inno Setup" }

$setup = Get-Item "$root\dist\Prisme-Setup-$version.exe"
Write-Host ("OK : {0} ({1:N0} Mo)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
