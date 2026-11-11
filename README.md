# Prisme

Convertisseur de fichiers, retouche et téléchargeur de vidéos pour Windows 10/11. Gratuit, sans publicité, hors ligne : aucun fichier n'est envoyé sur Internet.

**[Télécharger la dernière version](https://github.com/mario07teck-sys/Prisme/releases/latest)** (`Prisme-Setup-x.y.z.exe`, installation sans droits administrateur). Prisme se met ensuite à jour tout seul.

- **Glisser-déposer** dans la fenêtre, ou **clic droit → Convertir avec Prisme ▸** dans l'Explorateur.
- **Retoucher** : garder un extrait d'une musique ou d'une vidéo (aperçu avec le moteur VLC intégré, frise à poignées), tourner une vidéo, changer le volume ou la vitesse, assembler plusieurs musiques en un seul fichier (album, playlist), recadrer une image (proportions carré, 16:9, story…) et la redimensionner.
- **PDF scanné → texte** avec la reconnaissance de texte intégrée à Windows.
- **Téléchargement** depuis un lien YouTube, X, TikTok, Instagram, Facebook, Vimeo, Twitch, SoundCloud… en MP4 (meilleure qualité, 1080p, 720p) ou MP3, avec un dossier au choix (par défaut « Téléchargements\Prisme »). Une playlist entière (YouTube, album SoundCloud ou Bandcamp) devient un téléchargement par élément ; sous-titres de l'auteur en option.
- **Historique** : les tâches terminées restent affichées d'une session à l'autre ; « Effacer les terminées » le vide.
- **Carte graphique** (réglage, désactivé par défaut) : encodage H.264/H.265 par NVIDIA, Intel ou AMD, avec retour au processeur en cas d'échec.
- Zone de notification, démarrage avec Windows (réglage), notifications de fin, détection des liens copiés.
- Interface vectorielle, nette à toutes les résolutions (DPI par écran).

## Formats

| Famille | Entrées | Sorties |
|---|---|---|
| Audio | mp3 wav flac ogg m4a aac wma opus aiff amr ac3 dts ape caf m4r mka wv au mp2 | MP3, WAV, FLAC, M4A, ALAC, OGG, OPUS, AIFF, WMA, AC3, sonnerie iPhone (M4R), MP4 fond noir |
| Vidéo | mp4 mkv avi mov webm wmv flv m4v 3gp mpg ts mts ogv vob asf rmvb divx f4v mxf… | MP4, MP4 léger, MP4 H.265, MP4 ≤ 10 Mo / ≤ 20 Mo (taille visée, pour Discord ou un e-mail), MKV, MKV pour TV (son DTS → Dolby Digital, image intacte), WEBM, MOV, AVI, WMV, MPEG, GIF, Animation WhatsApp (MP4 muet), WEBP animé, son seul (MP3, WAV, M4A, FLAC) |
| Image | png jpg webp bmp gif tiff ico heic avif tga psd dds qoi exr jp2 | PDF (plusieurs images = un PDF), PNG, JPG, WEBP, AVIF, GIF, BMP, TIFF, ICO ; GIF animé → MP4/WEBM/Animation WhatsApp |
| PDF | pdf | Word (par Microsoft Word s'il est installé), PowerPoint, PNG/JPG (une image par page), texte, séparer les pages, fusionner |
| Document | doc docx odt rtf txt md html wpd wps pages | PDF, Word, ODT, RTF, texte, Markdown, HTML, EPUB, images par page |
| Tableur | xls xlsx ods csv tsv numbers | PDF, Excel, ODS, CSV (UTF-8), HTML |
| Présentation | ppt pptx pps odp key | PDF, PowerPoint, ODP, images par diapo |
| Dessin | svg vsd vsdx pub cdr emf wmf odg | PDF, PNG, JPG |
| Sous-titres | srt vtt ass ssa | SRT, VTT, ASS |

Moteurs embarqués : FFmpeg (audio, vidéo, image), libVLC (aperçu de l'onglet Retoucher), yt-dlp (téléchargement, mis à jour automatiquement chaque jour), LibreOffice allégé (documents ; Microsoft Office est utilisé à sa place pour le PDF s'il est installé), PDFsharp et le moteur PDF de Windows.

## Développement

| Action | Commande |
|---|---|
| Moteurs (vendor\, non versionné) | `.\tools\update-engines.ps1` |
| Compiler + autotest + installeur | `.\build.ps1` → `dist\Prisme-Setup-<version>.exe` |
| Autotest seul | `src\bin\Debug\net10.0-windows10.0.19041.0\Prisme.exe --selftest` → `%TEMP%\prisme-selftest.txt` |
| Identité visuelle (icône, logo, images de l'installeur) | `.\tools\make-icon.ps1` |

Mises à jour : chaque version est une release GitHub `vX.Y.Z` avec l'installeur `Prisme-Setup-X.Y.Z.exe` joint ; la version installée la trouve (API GitHub), vérifie taille et SHA-256, puis l'installe en silence. Le numéro augmente à chaque livraison (1.2.0 → 1.2.1…).

Ligne de commande : `Prisme.exe [fichiers…]`, `--edit <fichier>` (onglet Retoucher), `--to <format> <fichiers…>`, `--download <lien> [best|1080|720|mp3]`, `--tray`, `--register-shell`, `--unregister`, `--autostart on|off`, `--selftest`.

Skills Claude du projet (`.claude/skills`) : `prisme-build`, `prisme-add-format`, `prisme-update-engines`.

Licences : FFmpeg (GPL, `engines\FFMPEG-LICENSE.txt`), LibreOffice (MPL 2.0, `engines\office\LICENSE.html`), yt-dlp (Unlicense), PDFsharp (MIT), libVLC et LibVLCSharp (LGPL 2.1).
