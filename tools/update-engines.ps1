# (Re)télécharge les moteurs embarqués dans vendor\ : FFmpeg, yt-dlp, LibreOffice allégé.
# Usage : .\tools\update-engines.ps1 [-Only ffmpeg|ytdlp|office] [-OfficeVersion 26.8.2]
param([ValidateSet('all', 'ffmpeg', 'ytdlp', 'office')][string]$Only = 'all', [string]$OfficeVersion = '26.8.2')
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

if ($Only -in 'all', 'office') {
  Write-Host "-> LibreOffice $OfficeVersion (extrait sans installation, puis allégé)"
  $msi = "$work\lo.msi"
  Invoke-WebRequest "https://download.documentfoundation.org/libreoffice/stable/$OfficeVersion/win/x86_64/LibreOffice_${OfficeVersion}_Win_x86-64.msi" -OutFile $msi
  $sig = Get-AuthenticodeSignature $msi
  if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'The Document Foundation') { throw "Signature LibreOffice invalide : $($sig.Status)" }
  $x = "$work\lo"
  Start-Process msiexec -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$x`"" -Wait
  $o = "$vendor\office"; if (Test-Path $o) { Remove-Item $o -Recurse -Force }
  $py = Get-ChildItem "$x\program" -Directory -Filter 'python-core-*' | ForEach-Object FullName
  robocopy "$x\program" "$o\program" /E /NFL /NDL /NJH /NJS /XD "$x\program\resource" $py | Out-Null
  robocopy "$x\program\resource\fr" "$o\program\resource\fr" /E /NFL /NDL /NJH /NJS | Out-Null
  robocopy "$x\share" "$o\share" /E /NFL /NDL /NJH /NJS /XD "$x\share\extensions" "$x\share\gallery" "$x\share\template" "$x\share\autotext" "$x\share\wizards" "$x\share\tipoftheday" | Out-Null
  robocopy "$x\presets" "$o\presets" /E /NFL /NDL /NJH /NJS | Out-Null
  robocopy "$x\Fonts" "$o\share\fonts\truetype" /E /NFL /NDL /NJH /NJS | Out-Null   # polices Carlito/Caladea = métriques de Calibri/Cambria
  robocopy "$x\System64" "$o\program" /NFL /NDL /NJH /NJS | Out-Null              # runtime VC++ : marche sans rien installer
  Copy-Item "$x\LICENSE.html", "$x\NOTICE" $o
  Get-ChildItem "$o\share\config" -Filter 'images_*.zip' | Where-Object Name -NotMatch 'colibre\.zip$' | Remove-Item
  Get-ChildItem "$o\share\registry\res" -File | Where-Object Name -NotMatch '_(fr|en-US)\.xcd$' | Remove-Item
  Remove-Item "$o\program\python*.exe", "$o\program\python*.dll" -ErrorAction SilentlyContinue
  '{0:N0} Mo' -f ((Get-ChildItem $o -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Moteurs à jour. Lance .\build.ps1 pour vérifier (autotest) et refaire l''installeur.' -ForegroundColor Green
