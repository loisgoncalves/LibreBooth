# Composants tiers

LibreBooth est publié sous licence MIT (voir `LICENSE`). Les versions à télécharger contiennent aussi les
composants libres suivants, chacun sous sa propre licence :

| Composant | Auteur | Licence | Source |
|---|---|---|---|
| .NET 10 (moteur d'exécution) et WPF | Microsoft et la .NET Foundation | MIT | https://github.com/dotnet/runtime · https://github.com/dotnet/wpf |
| SkiaSharp 3.119.4 (et la bibliothèque Skia) | Microsoft (Skia : Google) | MIT (Skia : BSD 3 clauses) | https://github.com/mono/SkiaSharp |
| MailKit 4.18.2 et MimeKit | Jeffrey Stedfast | MIT | https://github.com/jstedfast/MailKit |
| CommunityToolkit.Mvvm 8.4.2 | .NET Foundation | MIT | https://github.com/CommunityToolkit/dotnet |
| Serilog 4.4.0 et Serilog.Sinks.File 7.0.0 | Serilog Contributors | Apache 2.0 | https://github.com/serilog/serilog |
| System.Security.Cryptography.ProtectedData | Microsoft | MIT | https://github.com/dotnet/runtime |

Pour les tests seulement (non distribués) : xUnit (Apache 2.0), Microsoft.NET.Test.Sdk (MIT), coverlet (MIT).

## Non inclus

Ces fichiers appartiennent à leurs fabricants et **ne sont ni dans ce dépôt ni dans les versions publiées**.
Chaque utilisateur se les procure lui-même (voir le README) :

- **SDK Canon EDSDK** (`EDSDK.dll`, `EdsImage.dll`) : © Canon Inc., fourni par le programme développeurs de Canon ;
- **API d'état DNP** (`cspstat.dll`) et **pilote Windows de la DS620** : © Dai Nippon Printing / DNP Imagingcomm.

Canon, EOS, DNP et les noms de produits cités sont des marques de leurs propriétaires. LibreBooth n'est ni
affilié à ces sociétés, ni approuvé par elles.
