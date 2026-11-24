# Composants tiers

LibreBooth est publié sous licence MIT (voir `LICENSE`). Les versions à télécharger contiennent aussi les
composants libres suivants, chacun sous sa propre licence :

| Composant | Auteur | Licence | Source |
|---|---|---|---|
| .NET 10 (moteur d'exécution) et WPF | Microsoft et la .NET Foundation | MIT | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf |
| SkiaSharp 3.119.4 (et la bibliothèque Skia) | Microsoft (Skia : Google) | MIT (Skia : BSD 3 clauses) | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm 8.4.2 | .NET Foundation | MIT | https://github.com/CommunityToolkit/dotnet |
| Serilog 4.4.0 et Serilog.Sinks.File 7.0.0 | Serilog Contributors | Apache 2.0 | https://github.com/serilog/serilog |

Pour les tests seulement (non distribués) : xUnit (Apache 2.0), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT).

## Non inclus

Ces fichiers appartiennent à leurs fabricants et **ne sont ni dans ce dépôt ni dans les versions publiées**.
Chaque utilisateur se les procure lui-même, à la source (voir le README) :

- **SDK Canon EDSDK** (`EDSDK.dll`, `EdsImage.dll`), facultatif depuis la 3.1.0 : © Canon Inc., fourni par le
  programme développeurs de Canon. Seul l'utilisateur qui le choisit en secours le télécharge chez Canon ; LibreBooth
  ne fait qu'en extraire les deux fichiers dont il a besoin, sur son PC ;
- **pilote Windows de la DS620** : © Dai Nippon Printing / DNP Imagingcomm. Le programme d'installation le
  télécharge sur le serveur de DNP au moment de l'installation et vérifie son empreinte ; il n'est pas redistribué ;
- **API d'état DNP** (`cspstat.dll`) : © Dai Nippon Printing / DNP Imagingcomm.

Sans le SDK Canon, LibreBooth pilote le Canon par le pilote intégré à Windows (Windows Portable Devices), avec les
codes des commandes Canon EOS décrits par le projet libre gPhoto2 (http://www.gphoto.org/). Aucun code de gPhoto2
n'est repris.

Canon, EOS, DNP et les noms de produits cités sont des marques de leurs propriétaires. LibreBooth n'est ni
affilié à ces sociétés, ni approuvé par elles.
