# Historique des versions

Chaque version a son propre numéro : le programme d'installation et le zip de la page « Releases » portent le
même. La version installée s'affiche dans l'admin › Système.

Numéro majeure.fonction.correctif : **majeure** quand les habitudes ou les fichiers de l'utilisateur changent,
**fonction** pour une nouveauté, **correctif** pour une correction (voir le README, « Numéros de version »).

## 3.1.0 – 24/11/2026

Plus besoin du SDK Canon : LibreBooth pilote l'appareil photo par le pilote intégré à Windows.

- **Canon sans SDK** : live view, photos avec ou sans autofocus, reconnexion après un débranchement, tout passe par
  le pilote de Windows (Windows 10 et 11). Plus rien à télécharger chez Canon, plus d'inscription. Essayé avec un
  EOS 1300D.
- **Tous les booths passent au pilote de Windows**, y compris après une mise à jour : un SDK Canon déjà ajouté reste
  en place, mais ne sert plus que si on le choisit.
- **SDK Canon en secours** : admin › Appareil photo › « Pilote du Canon ». Si Windows ne sait pas piloter un modèle,
  « SDK Canon (secours) » remet l'ancien fonctionnement ; le SDK s'ajoute alors depuis cette même page.
- **Option « Copie sur la carte SD » retirée** : les photos sont enregistrées sur le PC seulement, avec les deux
  pilotes. Si elle était cochée, elle est simplement ignorée.
- **Pré-versions** : admin › Système › Mises à jour › « Proposer aussi les pré-versions », coupé au départ. Allumé,
  l'admin propose aussi les versions d'essai publiées sur GitHub, avec une mise en garde avant de les installer.
- **Retour à la version stable** : tant qu'une pré-version est installée, l'accueil le rappelle (« Pré-version
  installée »), et Système › Mises à jour propose « Revenir à la version stable » (téléchargement contrôlé comme une
  mise à jour).
- **Programme d'installation** : plus d'étape « SDK Canon ». Lancé par l'admin pour revenir à la version stable, il ne
  redemande plus de confirmer le retour à une version plus ancienne.
- **Vérification de l'installation** et accueil de l'admin : le SDK Canon n'est demandé que s'il est choisi.

## 3.0.0 – 24/11/2026

Version majeure : l'admin change d'allure et se parcourt autrement. Les réglages, les événements et le photobooth
des invités restent les mêmes : rien n'est converti.

- **Admin modernisée** : un menu à gauche (Accueil, Événement, Personnalisation, Appareil photo, Impression, Écrans,
  Système), réduit aux icônes sur un petit écran ; des cartes, des interrupteurs expliqués en une phrase, et en bas
  de chaque page l'état de l'appareil, de l'imprimante et du papier.
- **Accueil « Prêt pour l'événement ? »** : l'événement ouvert et ses chiffres, puis un point par élément à vérifier
  (appareil photo, imprimante, papier, formats proposés, limite d'impressions, espace disque, mise à jour), en vert,
  orange ou rouge, avec un bouton vers la bonne page. Raccourcis : photo de test, essai d'impression, réimpression,
  vérification de l'installation.
- **Clair ou sombre** : admin › Système › Apparence, appliqué tout de suite à toutes les fenêtres de l'admin, barre
  de titre comprise.
- **Même style partout** : code PIN, choix de l'événement, « Revenir à l'admin ou quitter », vérification de
  l'installation, éditeur de modèle. Les confirmations deviennent des fenêtres LibreBooth dont les boutons disent
  l'action (« Quitter LibreBooth », « Supprimer », « Démarrer quand même »).
- **Espace disque surveillé** : sous 2 Go libres sur le disque des photos, « Démarrer le photobooth » prévient (on
  peut démarrer quand même). Sous 300 Mo, le photobooth ne lance plus de session : l'accueil affiche « Le photobooth
  fait une pause » et un message pour l'animateur, puis les sessions reprennent dès que de la place est libérée. La
  vérification de l'installation suit les mêmes seuils.
- Les modèles et cadres PNG rejoignent la page Personnalisation, le journal la page Système.
- **Windows 10** : quand les icônes de Windows 11 manquent, l'admin prend celles de Windows 10.

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
