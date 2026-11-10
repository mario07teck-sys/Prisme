---
name: prisme-build
description: Compiler Prisme, lancer l'autotest de toutes les conversions et fabriquer l'installeur dist\Prisme-Setup-<version>.exe. À utiliser pour « compile », « fais l'installeur », « nouvelle version », « publie une mise à jour » de Prisme.
---

# Compiler et livrer Prisme

1. **Version** : si c'est une nouvelle version, monter `<Version>` dans `src/Prisme.csproj` (format x.y.z). L'installeur et l'écran « Réglages » la reprennent tout seuls.
2. **Construire** depuis la racine du projet :
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
   ```
   Le script ferme Prisme s'il tourne, publie en .NET 10 autonome (`publish\`), lance `Prisme.exe --selftest` sur la version publiée (toutes les conversions de la matrice, sur des fichiers générés) puis compile `installer\Prisme.iss` avec Inno Setup 6.
3. **Si l'autotest échoue** : lire `%TEMP%\prisme-selftest.txt` (une ligne `ÉCHEC …` par conversion cassée), corriger, relancer. Ne jamais livrer avec `-NoTest`.
4. **Vérifier l'installeur** : `dist\Prisme-Setup-<version>.exe`, environ 240 Mo, installation par utilisateur sans droits admin dans `%LOCALAPPDATA%\Programs\Prisme`. Pour un test d'installation silencieux : `dist\Prisme-Setup-<version>.exe /VERYSILENT /SUPPRESSMSGBOXES`.

5. **Publier** (demande de l'utilisateur, 2026-11-06) : **uniquement la mise à jour légère** `dist\Prisme-Update-<version>-<enginesId>.exe` (environ 75 Mo, sans moteurs) dans la release GitHub, **tant que `enginesId` est le même que celui du dernier installeur complet publié** (`a4a66033dd0a`, v1.2.5). Les Prisme installés dont `engines.id` correspond la prennent tout seuls.
   - Si `enginesId` a changé (FFmpeg, LibreOffice ou libvlc mis à jour) : la légère ne sert à personne. Publier **l'installeur complet** `Prisme-Setup-<version>.exe` (avec la légère), puis noter le nouvel `enginesId` ici.
   - Notes de release : pour une nouvelle installation, donner le lien vers le dernier installeur complet, par exemple `https://github.com/mario07teck-sys/Prisme/releases/download/v1.2.5/Prisme-Setup-1.2.5.exe` (il se met ensuite à jour tout seul).

## Pièges connus
- `vendor\` (moteurs) n'est pas versionné : s'il manque, lancer le skill `prisme-update-engines`.
- Fichiers `.ps1` : les enregistrer en UTF-8 **avec BOM**, sinon Windows PowerShell 5.1 abîme les accents.
- Une build de dev (`src\bin\Debug`) réenregistre le menu clic droit vers elle-même au lancement ; réinstaller (ou lancer la version installée) remet le bon chemin.
