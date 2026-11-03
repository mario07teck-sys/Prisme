---
name: prisme-update-engines
description: Mettre à jour ou réinstaller les moteurs embarqués de Prisme (FFmpeg, yt-dlp, LibreOffice) dans vendor\. À utiliser quand vendor\ est vide, quand un site de vidéos ne marche plus dans une version installée ancienne, ou pour « mets à jour FFmpeg / LibreOffice ».
---

# Mettre à jour les moteurs de Prisme

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\update-engines.ps1              # tous les moteurs
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\update-engines.ps1 -Only ytdlp  # un seul moteur
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\update-engines.ps1 -Only office -OfficeVersion 26.8.2
```

- **yt-dlp** : chez l'utilisateur, l'appli se met à jour toute seule au plus une fois par jour (`yt-dlp -U`, possible parce que Prisme est installé dans un dossier inscriptible). Ici, on met juste à jour la copie embarquée dans l'installeur.
- **FFmpeg** : build « essentials » de gyan.dev. Après la mise à jour, vérifier que les encodeurs utilisés sont toujours présents (`libx265`, `libaom-av1`, `libwebp`, `libvpx-vp9`, `libmp3lame`, `libopus`, `alac`, `wmav2`).
- **LibreOffice** : la version stable se trouve sur https://download.documentfoundation.org/libreoffice/stable/. Le script vérifie la signature de The Document Foundation, extrait le MSI sans l'installer, puis retire l'aide, les dictionnaires, les langues autres que français et anglais, et Python : environ 550 Mo au lieu de 1,5 Go.

Toujours finir par le skill `prisme-build`. L'autotest confirme alors que chaque conversion marche encore avec les nouveaux moteurs.
