# Historique des versions

Chaque version a son propre numéro : le programme d'installation et le zip de la page « Releases » portent le
même. La version installée s'affiche dans l'admin › Système.

## 1.6.0 – 23/11/2026

- **Installation guidée des pilotes** : le programme d'installation télécharge le pilote Windows 11 de la DNP DS620
  sur le site officiel de DNP, contrôle son empreinte SHA-256, puis l'installe.
- **SDK Canon en deux clics** : à la fin de l'installation, « Ouvrir la page Canon » puis « Choisir le SDK
  téléchargé… » ; LibreBooth extrait lui-même EDSDK.dll et EdsImage.dll (32 bits) du zip de Canon.
- Le même ajout depuis LibreBooth : admin › Appareil photo › « Ajouter le SDK Canon… », et dans la vérification de
  l'installation. L'appareil photo est relancé aussitôt, sans redémarrer LibreBooth.
- La désinstallation retire aussi les fichiers des fabricants posés à côté de LibreBooth.exe.

## 1.5.0 – 23/11/2026

Première version publiée sur GitHub.

- **Pilotes des fabricants à part** : les fichiers de Canon (SDK EDSDK) et de DNP ne sont pas redistribués. On les
  copie, sans droits d'administrateur, dans le dossier `Pilotes` des données de LibreBooth
  (`C:\Users\<nom>\LibreBooth\Pilotes`) ; LibreBooth et sa vérification indiquent où les trouver.
- Programme d'installation public : il fonctionne sans le pilote de la DNP, et sa vérification finale dit ce qui
  manque.
- Licence MIT, liste des composants tiers.

## 1.4.0 – 23/11/2026

- **L'admin d'abord** : code PIN au lancement, puis l'admin seule, dans une fenêtre normale. Le photobooth plein
  écran démarre avec « Démarrer le photobooth ». Après un plantage, LibreBooth repart directement sur le photobooth.
- **Quitter facilement** : bouton « Quitter LibreBooth » dans l'admin ; sur l'accueil du photobooth, un bouton rond
  demande le code PIN puis propose « Revenir à l'admin » ou « Quitter LibreBooth ».
- **Personnalisation** : choix du mode « Thème LibreBooth » ou « Mes propres images » (fonds, logo, image d'accueil,
  avec leurs vignettes).
- **Vérification de l'installation** : dossiers, fichiers, pilotes, imprimante et appareil photo, avec ce qu'il
  faut faire en cas de problème.
- Nouveau logo, crédits.

## 1.3.0 – 22/11/2026

- Bouton **« Recommencer »** : l'invité refait sa série tant qu'il n'a ni imprimé ni demandé d'e-mail ; la série
  refusée est gardée dans le dossier `refusees` de l'événement.
- **Souris** utilisable comme l'écran tactile : le pointeur apparaît quand elle bouge.
- **Écrans aux couleurs de l'événement** (thème, fond PNG ou cadre) et image d'accueil propre à chaque événement.

## 1.2.0 – 22/11/2026

- **Programme d'installation** : LibreBooth, raccourcis, désinstallation propre.
- Dossier des données dans `C:\Users\<nom>\LibreBooth` : la protection « Accès contrôlé aux dossiers » de Windows
  bloque les logiciels inconnus dans Documents, souvent synchronisé par OneDrive.
- **Coupe 2 pouces de la DNP** : les marque-pages sortent coupés en deux, grâce au bloc de réglages propre au pilote
  DNP.

## 1.1.0 – 22/11/2026

- **Personnalisation des impressions** : deux modèles (10x15 une photo, marque-page 3 photos), six thèmes, textes,
  polices, couleurs, décor, fond PNG avec gabarit, logo ; l'invité choisit ses exemplaires.

## 1.0.0 – 22/11/2026

- Première version : appareil Canon EOS (SDK EDSDK), imprimante DNP, accueil, live view, compte à rebours, montage,
  impression, administration, envoi par e-mail (désactivé par défaut).
