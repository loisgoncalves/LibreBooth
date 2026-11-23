# Historique des versions

Chaque version a son propre numéro : le programme d'installation et le zip de la page « Releases » portent le
même. La version installée s'affiche dans l'admin › Système.

Numéro majeure.fonction.correctif : **majeure** quand les habitudes ou les fichiers de l'utilisateur changent,
**fonction** pour une nouveauté, **correctif** pour une correction (voir le README, « Numéros de version »).

## 2.0.0 – 23/11/2026

Version majeure : les dossiers des événements changent (les anciens sont convertis au premier lancement).

- **Choix de l'événement au lancement** : après le code PIN, LibreBooth propose d'ouvrir un événement existant
  (avec sa vignette et ses chiffres) ou d'en créer un nouveau : nom, date, et personnalisation de départ (le thème de
  LibreBooth ou celle d'un autre événement). Aussi depuis l'admin › Événement › « Changer d'événement… », où
  l'événement se renomme.
- **Dossiers d'événements plus simples** : `Evenements\2026-11-23 Mariage Julie et Tom\` avec `Photos`, `Montages`
  et `Photos refusées` ; ce dont LibreBooth a besoin (personnalisation, fiches des sessions) est rangé dans un
  dossier caché, `.librebooth`. Les événements des versions 1.x sont convertis au premier lancement, sans rien
  effacer.
- **Mises à jour depuis l'admin** : quand le PC est connecté à Internet, LibreBooth regarde au lancement si une
  nouvelle version est publiée sur GitHub. Admin › Système › Mises à jour › « Installer la mise à jour » la
  télécharge, contrôle son empreinte SHA-256 et l'installe, jamais pendant le photobooth. Photos, événements,
  réglages et pilotes sont gardés.
- **Désinstallation au choix** : tout garder (comme avant), supprimer les réglages, modèles, personnalisations,
  pilotes ajoutés et journaux, ou aussi les photos de tous les événements (après confirmation). Tout part dans la
  Corbeille de Windows ; le pilote de l'imprimante DNP reste installé.
- Le programme d'installation reconnaît une installation existante : il met à jour le même dossier et prévient
  avant d'installer une version plus ancienne.
- **Défilement au pavé tactile** : les pages et les listes défilent au rythme du geste, au lieu de partir d'un coup.
- **Envoi par e-mail retiré** (écran de saisie, réglages SMTP, file d'attente) : LibreBooth ne demande plus
  l'adresse des invités.
- **Numéros de version** : un seul numéro pour tout LibreBooth ; `installer\nouvelle-version.ps1` le fait monter
  selon le type de changement et prépare l'entrée de l'historique.

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
