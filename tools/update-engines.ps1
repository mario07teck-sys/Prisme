# (Re)télécharge les moteurs embarqués dans vendor\ : FFmpeg, yt-dlp.
# LibreOffice n'est plus embarqué : Prisme le télécharge lui-même au premier document qui en a besoin (src\Core\OfficeSetup.cs).
# Usage : .\tools\update-engines.ps1 [-Only ffmpeg|ytdlp]
param([ValidateSet('all', 'ffmpeg', 'ytdlp')][string]$Only = 'all')
$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
$vendor = Join-Path (Split-Path $PSScriptRoot) 'vendor'
$work = Join-Path $env:TEMP "prisme-engines-$(Get-Random)"   # téléchargements : dossier à part, jamais exécutés sur place
New-Item -ItemType Directory -Force $vendor, $work | Out-Null

if ($Only -in 'all', 'ytdlp') {
  Write-Host '-> yt-dlp'
  Invoke-WebRequest https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe -OutFile "$vendor\yt-dlp.exe"
  & "$vendor\yt-dlp.exe" --version
}

if ($Only -in 'all', 'ffmpeg') {
  Write-Host '-> FFmpeg (build « essentials » de gyan.dev)'
  Invoke-WebRequest https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip -OutFile "$work\ff.zip"
  Expand-Archive "$work\ff.zip" "$work\ff" -Force
  Get-ChildItem "$work\ff" -Recurse -Filter ffmpeg.exe | Select-Object -First 1 | Copy-Item -Destination $vendor
  Get-ChildItem "$work\ff" -Recurse -Filter LICENSE | Select-Object -First 1 | Copy-Item -Destination "$vendor\FFMPEG-LICENSE"
  & "$vendor\ffmpeg.exe" -hide_banner -version | Select-Object -First 1
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Moteurs à jour. Lance .\build.ps1 pour vérifier (autotest) et refaire l''installeur.' -ForegroundColor Green
