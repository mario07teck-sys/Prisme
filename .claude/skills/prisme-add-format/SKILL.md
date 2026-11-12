---
name: prisme-add-format
description: Ajouter un format d'entrée ou de sortie à Prisme (audio, vidéo, image, PDF, document, tableur, présentation, dessin, sous-titres). À utiliser pour « ajoute le format X », « Prisme devrait convertir Y en Z », « supporte les fichiers .abc ».
---

# Ajouter un format à Prisme

Tout part de `src/Core/Formats.cs` : le menu clic droit, le sélecteur de fichiers, les boutons de l'interface et l'autotest se construisent à partir de ce catalogue. Il n'y a rien d'autre à déclarer.

## Format d'entrée (Prisme sait lire un nouveau type de fichier)
1. Ajouter l'extension au bon tableau (`AudioExts`, `VideoExts`, `ImageExts`, `TextExts`, `SheetExts`, `SlidesExts`, `DrawingExts`, `SubtitleExts`).
2. Vérifier que le moteur sait le lire :
   - audio, vidéo, images : `vendor\ffmpeg.exe -hide_banner -decoders` ;
   - documents : LibreOffice (`%LOCALAPPDATA%\Prisme\office\program\soffice.exe --headless --convert-to pdf fichier`, téléchargé par Prisme au premier document).
3. Si l'extension est un alias d'un format existant (comme jpeg et jpg), l'ajouter au `switch` de `same` dans `TargetsFor`, pour ne pas proposer « X → X ».

## Format de sortie
1. Ajouter un `Target(Id, Label, Ext, Hint)` dans la liste de la bonne famille. `Id` doit être unique dans la famille : c'est lui qui passe dans `--to <Id>` depuis le menu clic droit.
2. Brancher la conversion :
   - **FFmpeg** : ajouter un cas `(Kind.X, "id") => new[] { … }` dans `Converter.FfmpegArgs`. Les images avec transparence vers un format sans alpha doivent passer par le filtre `white`.
   - **LibreOffice** : si l'extension seule ne suffit pas, ajouter le nom du filtre d'export dans `Converter.LoFilter`. Les filtres se testent à la main avec `soffice --headless --convert-to "ext:Filtre" …`.
   - Un traitement à part (PDFsharp, Windows.Data.Pdf) se branche dans le `switch` de `Converter.Run`.
3. **Tester** : `dotnet build` puis `bin\Debug\…\Prisme.exe --selftest`. L'autotest convertit chaque fichier d'exemple vers **tous** les formats proposés, donc le nouveau est testé d'office. Pour un nouveau type d'entrée, ajouter un fichier d'exemple généré dans `SelfTest.cs` (liste `sources`).
4. Mettre à jour le tableau des formats du `README.md`.
