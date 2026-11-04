# OpenCompanion

Un petit compagnon animé sur un écran ESP32 qui reflète votre statut Microsoft Teams, sans accès au tenant ni API Graph.

![Rendu de l'écran pour chaque statut](preview.png)

## Fonctionnement

L'application Windows (`TeamsCompanion/`) déduit le statut localement, puis l'envoie à l'ESP32-S3 par USB. La carte se présente comme un périphérique HID nommé « OpenCompanion » : aucun pilote ni port COM à choisir, l'application la trouve toute seule.

| Détection sur le PC                         | Statut envoyé | Fond de l'écran |
|---------------------------------------------|---------------|-----------------|
| Teams n'est pas lancé                       | `NOTEAMS`     | lavande         |
| Teams utilise la caméra                     | `VIDEO`       | orange          |
| Teams utilise le micro                      | `CALL`        | orange          |
| Aucune activité clavier/souris depuis 5 min | `AWAY`        | jaune           |
| Sinon                                       | `AVAILABLE`   | vert            |

Le statut est renvoyé toutes les 10 s. Sans message pendant 35 s (PC en veille, câble débranché), l'ESP32 passe hors ligne : fond gris et prise débranchée, sans texte.

**Limite :** en détection locale, les statuts choisis à la main dans Teams (Occupé, Ne pas déranger…) ne sont connus que des serveurs Microsoft. Pour les récupérer, voir le **mode Graph** ci-dessous.

### Mode Graph (statut réel, optionnel)

`dotnet run -- --graph` lit le vrai statut via Microsoft Graph (`/me/presence`) au lieu de le déduire localement — donc aussi Occupé et Ne pas déranger.

- Pas besoin d'être admin ni d'enregistrer une application : l'app emprunte un client public Microsoft et demande le scope `Presence.Read`, qui se consent soi-même.
- À la première exécution, une fenêtre affiche un lien et un code : connectez-vous avec votre compte Teams et cliquez « Accepter ». Le jeton est ensuite mis en cache (chiffré par Windows), sans reconnexion aux lancements suivants.
- Occupé s'affiche en rouge (`BUSY`) ; Ne pas déranger (`DND`) aussi, et le compagnon tourne le dos. Un appel en cours reste prioritaire (`CALL` / `VIDEO`).
- Je reviens tout de suite (`BRB`) : fond jaune, le compagnon sort de l'écran et laisse un sablier ; il repasse une tête par le bord de temps en temps.

### Langue

Le menu **Langue** de la page **Paramètres** change la langue de l'application et celle des libellés affichés sur l'ESP (fr, en, de, es, it, pt). L'application envoie le texte traduit avec le statut (`STATUT|Libellé`) : le changement est immédiat, **sans reflasher**, et le choix est mémorisé.

Sans l'application (moniteur série), le firmware affiche ses libellés par défaut, en français. Les traductions se trouvent uniquement dans `Localize` (`TeamsCompanion/Companion.cs`). La police de l'écran est sans accents (« Occupe », « Ne pas deranger »).

## Composants

| Qté | Composant | Référence |
|-----|-----------|-----------|
| 1 | Carte ESP32-S3 avec écran IPS 1,69" 240×280 intégré (ST7789V2) | **Waveshare ESP32-S3-LCD-1.69** (non tactile) |
| 1 | Câble USB-C **avec données** | — |

Aucun câblage ni soudure : l'écran est intégré à la carte. La batterie est inutile ici, car le statut arrive par le câble USB.

**Version de la carte :** une étiquette « V2QC » indique la version V2, celle configurée par défaut. Pour l'ancienne version, mettez `SYS_EN_PIN=35` et `BUZZER_PIN=33` dans le `platformio.ini` du dépôt firmware. La broche SYS_EN garde la carte alimentée, et le buzzer est coupé pour éviter qu'il chauffe.

Si les couleurs sont inversées, retirez `-D TFT_INVERSION_ON=1` dans ce même `platformio.ini`.

### Boîtier imprimé en 3D

`boitier/boitier.scad` (OpenSCAD, paramétrique) contient trois pièces, déjà exportées en STL :

- `coque.stl` : à imprimer face avant sur le plateau, sans supports.
- `couvercle.stl` : il s'emboîte à l'arrière, et ses 4 pions entrent dans les trous de la carte pour la maintenir. Retirez-le pour atteindre le bouton BOOT.
- `tenon.stl` : la pièce qui se clipse dans la mortaise du dessous (12 × 5 mm, 9 mm de profondeur, avec deux crans). Pour le support de votre écran, unissez le module `tenon()` à votre pièce.

Avant d'imprimer, mesurez `front_stack` (épaisseur de la vitre et de l'écran) et `back_gap` (hauteur des composants au dos), puis réexportez : `openscad -D 'part="coque"' -o boitier/coque.stl boitier/boitier.scad`.

## Installation

### ESP32

Le firmware est dans un dépôt séparé : [OpenCompanion-Firmware](https://github.com/AnthoDingo/OpenCompanion-Firmware). Suivez son README pour compiler et flasher la carte (PlatformIO). Une fois un premier firmware flashé, l'application installe les mises à jour suivantes toute seule (voir plus bas).

### Application Windows

Prérequis : la nouvelle version de Teams.

Téléchargez `OpenCompanion-Setup.exe` depuis les [releases](https://github.com/AnthoDingo/OpenCompanion/releases) et lancez-le. L'installation se fait pour l'utilisateur courant, sans droits administrateur, et n'a pas besoin de .NET (le runtime est inclus). Une case, cochée par défaut, lance OpenCompanion au démarrage de Windows ; l'option se change ensuite dans **Paramètres** > **Démarrage** (une mise à jour ne la modifie pas). Une nouvelle version s'installe par-dessus l'ancienne, qu'il faut quitter avant. La désinstallation (Paramètres > Applications) supprime aussi les réglages, le jeton Microsoft Graph et esptool.

L'application vérifie au démarrage, puis toutes les 6 heures, s'il existe une nouvelle version de l'application ou du firmware, et l'annonce par une notification Windows (une fois par version). Pour l'application, le bouton **Télécharger et installer** de la page **Paramètres** télécharge l'installeur, vérifie sa signature (ECDSA, même clé que les firmwares), l'installe et relance l'application. Rien n'est installé sans ce clic, ni si la signature est invalide.

L'installeur n'a pas de signature Authenticode : au premier lancement, Windows SmartScreen affiche « Windows a protégé votre ordinateur ». Cliquez sur **Informations complémentaires**, puis **Exécuter quand même**.

Pour lancer l'application depuis les sources (prérequis : [.NET 10](https://dotnet.microsoft.com/download)) :

```
cd TeamsCompanion
dotnet run
```

L'application démarre dans la zone de notification. Son icône prend la couleur du statut, et son info-bulle affiche le statut et indique si l'écran est branché. Clic droit pour **Ouvrir** ou **Quitter**.

Quand vous verrouillez la session Windows, l'écran affiche **Absent** ou **De retour dans quelques minutes**, au choix dans **Paramètres** > **Session verrouillée**, jusqu'au déverrouillage. Un appel ou une visio en cours reste affiché. Teams lui-même n'est pas modifié.

L'application attend que l'écran soit branché et se reconnecte toute seule s'il est débranché. Une seule instance peut tourner à la fois. `dotnet run -- --test` lance l'auto-test.

### Mise à jour du firmware depuis l'application

Quand une release plus récente que le firmware branché existe sur le repo GitHub, la carte **Mise à jour du firmware** de la page **Paramètres** affiche un bouton **Installer le firmware X.Y**. L'application télécharge `firmware.bin` et esptool (l'outil officiel d'Espressif, téléchargé une seule fois dans `%LOCALAPPDATA%\OpenCompanion`). Elle redémarre ensuite l'écran en mode téléchargement, sans toucher au bouton BOOT, puis le flashe.

Pour installer un firmware compilé soi-même, le bouton **Installer depuis un fichier…** (visible dès que l'écran est branché) accepte le `firmware.bin` de l'application produit par PlatformIO (`.pio/build/<env>/firmware.bin`). L'application refuse tout fichier qui n'est pas une application ESP32-S3, ainsi que les images fusionnées (bootloader + partitions), qu'elle écraserait au mauvais endroit.

Pour publier une release, dans le dépôt firmware, mettez à jour `FW_VERSION` dans `src/main.cpp` (`0x0110` pour 1.1), puis poussez le tag correspondant : `git tag v1.1 && git push origin v1.1`. Son workflow `firmware` crée la release avec un seul binaire : la langue de l'écran vient de l'application, pas du firmware. L'application lit les releases sans authentification, donc le dépôt firmware doit être public.

Pour publier une release de l'application, poussez un tag `vX.Y.Z` sur ce dépôt : `git tag v1.6.0 && git push origin v1.6.0`. Le workflow `app` publie l'application en version autonome, compile l'installeur (`TeamsCompanion/installer.iss`, Inno Setup), le signe avec le secret `APP_SIGNING_KEY` (la clé privée `signing.pem` du dépôt firmware) et joint `OpenCompanion-Setup.exe`, `OpenCompanion-Setup.exe.sig` et `TeamsCompanion.zip` à la release. Sans ce secret, le workflow échoue sur un tag. Pour compiler l'installeur en local :

```
dotnet publish TeamsCompanion -c Release -r win-x64 --self-contained -o publish
iscc /DAppVersion=1.6.0 TeamsCompanion\installer.iss
```

Il est produit dans `publish-installer\`.

## Modifier le compagnon

Six compagnons au choix dans la page **Compagnons** de l'application (menu de gauche) : **Pousse**, **Chouette**, **Magicien**, **Trombone**, **Calculatrice** et **Panda**. Le choix est mémorisé et envoyé à l'écran avec le statut (`STATUT|Libellé|N`).

Les sprites sont des PNG 32×32 dans `sprites/<compagnon>/`, affichés 6 fois plus grands. Chaque compagnon a les mêmes frames (`idle`, `look`, `blink`, `squash`, `jump`, `away1`, `away2`, `call1`, `call2`, `offline`). Ils se retouchent avec n'importe quel éditeur pixel art (Aseprite, Piskel…), en gardant :

- un fond transparent ;
- uniquement les couleurs de la palette définie dans `png2h.py`.

Après modification, régénérez les en-têtes du firmware : `src/sprites.h` (palette, liste des compagnons) et un fichier par compagnon, `src/companions/<compagnon>.h`. Le script les écrit directement dans le dépôt firmware cloné à côté de celui-ci (`../OpenCompanion-Firmware`), ou dans le dossier `src` passé en argument :

```
python sprites/png2h.py
```

Pour un nouveau compagnon : créez son dossier avec les mêmes frames, ajoutez-le **à la fin** de `COMPANIONS` dans `png2h.py`, dans la liste `All` de `CompanionsPage.xaml.cs` et dans `Companions` de l'émulateur. La frame `back.png` le montre de dos (Ne pas déranger).

Un nouveau fichier `wave.png` (à créer pour chaque compagnon) devient la frame `F_WAVE`, à ajouter dans une séquence `A_*` de `src/main.cpp` du dépôt firmware. Les couleurs du corps et des fonds se règlent dans la fonction `draw()` du même fichier.

## Émulateur

`Emulator/` affiche l'écran de l'ESP dans une fenêtre Windows, avec un bouton par statut et une liste pour choisir le compagnon. Il permet de tester sprites, couleurs et animations sans la carte.

```
dotnet run --project Emulator
```

`dotnet run --project Emulator -- --snapshot preview.png` génère l'aperçu en haut de ce README.

L'émulateur reproduit la fonction `draw()` du firmware ([OpenCompanion-Firmware](https://github.com/AnthoDingo/OpenCompanion-Firmware), `src/main.cpp`) à partir des mêmes PNG. Une modification des séquences, des couleurs ou de la disposition doit être faite aux deux endroits.
