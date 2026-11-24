# Fichiers des fabricants (non fournis)

Ce dossier sert seulement à **compiler** LibreBooth avec les pilotes déjà en place. Les fichiers des fabricants n'y
sont jamais publiés (ils sont exclus par `.gitignore`) : ils appartiennent à Canon et à DNP.

| Dossier | Fichiers | Où les trouver |
|---|---|---|
| `drivers\canon\` | `EDSDK.dll`, `EdsImage.dll` (version **32 bits**), facultatifs : seulement pour le pilote de secours « SDK Canon » | SDK Canon EDSDK, gratuit sur inscription au programme développeurs de Canon |
| `drivers\dnp\` | `cspstat.dll` (facultatif) | API d'état des imprimantes DNP, auprès de DNP |

Présents ici, ils sont copiés à côté de `LibreBooth.exe` à la compilation. La version publique se fabrique sans eux
(`.\installer\fabriquer-installation.ps1 -Public`), même s'ils sont là.

Pour **utiliser** LibreBooth, inutile de compiler : copiez ces fichiers dans `C:\Users\<nom>\LibreBooth\Pilotes`
(voir le README à la racine).
