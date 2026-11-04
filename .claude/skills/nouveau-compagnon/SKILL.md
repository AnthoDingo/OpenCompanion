---
name: nouveau-compagnon
description: Crée un nouveau compagnon OpenCompanion (sprites 32x32 de toutes les frames, firmware, appli, émulateur). À utiliser quand l'utilisateur demande d'ajouter, créer ou dessiner un compagnon / personnage pour l'écran.
---

# Créer un compagnon OpenCompanion

Deux dépôts : l'appli `E:\AnthoDingo\OpenCompanion` (sprites, appli WPF, émulateur) et le firmware
`E:\AnthoDingo\OpenCompanion-Firmware` (`src/main.cpp`, `src/sprites.h`, `src/companions/<nom>.h`). Relire chaque fichier avant de l'éditer :
le projet bouge souvent.

## 1. Dessiner les frames

Dossier `sprites/<nom>/` (nom en minuscules, sans accent : il devient `C_<NOM>` en C++). PNG 32x32, fond transparent,
**exactement les mêmes fichiers** que `sprites/pousse/` (vérifier avec `ls`) :

| Frame | Rôle |
|-------|------|
| `idle` | de face, yeux ouverts (aussi l'icône dans l'appli) |
| `look` | pupilles décalées d'1 px vers la droite |
| `blink` | yeux fermés (traits) |
| `squash` | écrasé (plus large, moins haut), yeux fermés : avant/après le saut |
| `jump` | un peu étiré, bouche ouverte (le firmware le monte de 2 px) |
| `away1`, `away2` | endormi, yeux fermés, petits « z » blancs en haut à droite (1 puis 2), léger souffle |
| `call1`, `call2` | casque gris (écouteurs + arceau + micro vers la bouche), bouche fermée / ouverte |
| `offline` | affaissé (large, bas), yeux fermés |
| `back` | vu de dos, sans visage (DND : le firmware dessine les mains et la feuille STOP dessus) |

Règles :
- Couleurs uniquement de `PALETTE` dans `sprites/png2h.py` (index = position). Contour `1` (26,28,44) d'1 px tout autour.
- **Ne pas utiliser les index 2-4** : ce vert est recoloré en violet Teams pour Pousse, partout.
- Besoin d'une couleur absente : l'**ajouter à la fin** de `PALETTE` (dans `png2h.py`), jamais au milieu.
- Le personnage tient dans la largeur, pieds vers y=30-31 (même ligne de sol que les autres), pour que les décors
  (portable de BUSY, câble OFFLINE, mains DND) tombent juste.

Le plus simple est de générer les PNG par code : `exemple_gen.py` (dans ce skill) a produit la chouette et le magicien.
Une fonction `(x, y, params) -> index` par personnage, avec contour automatique, `zzz` et un casque partagé. Copier ce fichier
dans le scratchpad, écrire la fonction du nouveau compagnon, ajouter la frame `back` (le script ne la génère pas),
puis `python <script> E:\AnthoDingo\OpenCompanion\sprites`. Regarder le résultat agrandi (PIL `resize(..., Image.NEAREST)`)
à côté de `sprites/pousse/*.png` avant de continuer.

## 2. Brancher le compagnon (toujours ajouter À LA FIN : l'index est le numéro envoyé à l'écran)

1. `sprites/png2h.py` : ajouter le nom à la fin de `COMPANIONS`, puis `python sprites/png2h.py` : il écrit
   `src/sprites.h` et `src/companions/<nom>.h` directement dans `../OpenCompanion-Firmware` (rien à recopier).
2. Firmware `src/main.cpp` :
   - `HAND[C_COUNT][3]` : ajouter `{contour, base, reflet}`, les index de palette des mains vues en DND ;
   - incrémenter `FW_VERSION` (ex. `0x0100` -> `0x0110`) : les sprites sont compilés dans le firmware ;
   - mettre à jour la liste des compagnons dans `README.md` du firmware (`0 Pousse, 1 chouette, ...`).
3. Appli `TeamsCompanion/CompanionsPage.xaml.cs` : ajouter à la fin de `All`
   `new Companion("<Nom affiché>", Sprite("<nom>/idle"), new Version(<nouvelle version>))`.
4. Appli `TeamsCompanion/Companion.cs` : `CompanionLink.ExpectedFirmware` = nouvelle version, plus une ligne de commentaire
   « X.Y = premier firmware qui embarque <Nom> ».
5. Émulateur `Emulator/Program.cs` : ajouter le nom à la fin de `Companions`, et dans `Hand` les mêmes couleurs que HAND,
   en RGB (`0xRRGGBB`).
6. `README.md` (section « Modifier le compagnon ») : citer le nouveau compagnon et sa version minimale.

Les PNG sont embarqués automatiquement (glob `..\sprites\**\*.png` dans les csproj) : rien à faire.

## 3. Vérifier

```
python sprites/png2h.py                                  # échoue si frame manquante ou couleur hors palette
cd ../OpenCompanion-Firmware && ~/.platformio/penv/Scripts/pio.exe run
cd ../OpenCompanion && dotnet build Emulator && dotnet build TeamsCompanion
dotnet run --project Emulator -- --snapshot preview.png   # grille compagnons x statuts : regarder l'image
```

Si l'appli tourne, son exe est verrouillé : compiler vers le scratchpad (`-o <scratchpad>/tc`) puis lancer
`TeamsCompanion.exe --test`. Contrôler sur `preview.png` que la nouvelle ligne est cohérente : mains DND bien placées,
casque sur les oreilles, rien qui dépasse. Ne flasher l'ESP et ne commiter que si l'utilisateur le demande ; lui donner sinon
le chemin `OpenCompanion-Firmware/.pio/build/esp32s3_lcd169/firmware.bin` (bouton « Installer depuis un fichier… »).
