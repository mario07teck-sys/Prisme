---
name: prisme-update-engines
description: Mettre à jour ou réinstaller les moteurs embarqués de Prisme (FFmpeg, yt-dlp) dans vendor\. À utiliser quand vendor\ est vide, quand un site de vidéos ne marche plus dans une version installée ancienne, ou pour « mets à jour FFmpeg ». LibreOffice n'est plus embarqué (voir la fin).
---

# Mettre à jour les moteurs de Prisme

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\update-engines.ps1              # tous les moteurs
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\update-engines.ps1 -Only ytdlp  # un seul moteur
```

- **yt-dlp** : chez l'utilisateur, l'appli se met à jour toute seule au plus une fois par jour (`yt-dlp -U`, possible parce que Prisme est installé dans un dossier inscriptible). Ici, on met juste à jour la copie embarquée dans l'installeur.
- **FFmpeg** : build « essentials » de gyan.dev. Après la mise à jour, vérifier que les encodeurs utilisés sont toujours présents (`libx265`, `libaom-av1`, `libwebp`, `libvpx-vp9`, `libmp3lame`, `libopus`, `alac`, `wmav2`).
- **LibreOffice** : plus dans l'installeur. Prisme le propose et le télécharge lui-même au premier document qui en a besoin (`src\Core\OfficeSetup.cs` : dernière version de https://download.documentfoundation.org/libreoffice/stable/, signature de The Document Foundation vérifiée, MSI extrait sans installation puis allégé à ≈ 550 Mo, dans `%LOCALAPPDATA%\Prisme\office`). Version de repli : constante `Fallback`. Essai réel sans toucher à ce dossier : `Prisme.exe --selftest-office <dossier>` → `%TEMP%\prisme-selftest-office.txt`.

Toujours finir par le skill `prisme-build`. L'autotest confirme alors que chaque conversion marche encore avec les nouveaux moteurs.
