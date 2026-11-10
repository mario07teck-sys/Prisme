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

5. **Publier** (demande de l'utilisateur, 2026-11-10) : **toujours les deux fichiers** dans la release GitHub, `dist\Prisme-Update-<version>-<enginesId>.exe` (environ 75 Mo, sans moteurs) **et** `dist\Prisme-Setup-<version>.exe` (complet). Les Prisme dont `engines.id` correspond prennent la légère tout seuls ; les Prisme ≤ 1.2.4 (sans `engines.id`) ne savent prendre que le complet et resteraient bloqués sans lui.
   - Dernier `enginesId` publié : `a4a66033dd0a` (v1.2.5, v1.2.6). S'il change (FFmpeg, LibreOffice ou libvlc mis à jour), le noter ici.
   - Notes de release : commencer par « Télécharger : `Prisme-Setup-<version>.exe` ci-dessous (installeur complet). Si Prisme est déjà installé, il se met à jour tout seul. »
   - Pousser aussi le code (`git push origin main`) et créer la release avec `gh release create v<version> --target main`.

## Pièges connus
- `vendor\` (moteurs) n'est pas versionné : s'il manque, lancer le skill `prisme-update-engines`.
- Fichiers `.ps1` : les enregistrer en UTF-8 **avec BOM**, sinon Windows PowerShell 5.1 abîme les accents.
- Une build de dev (`src\bin\Debug`) réenregistre le menu clic droit vers elle-même au lancement ; réinstaller (ou lancer la version installée) remet le bon chemin.
