using SkiaSharp;

namespace LibreBooth.Core.Layouts;

/// <summary>
/// Trouve les zones transparentes d'un cadre PNG : chacune devient l'emplacement d'une photo.
/// Les zones sont rangées dans l'ordre de lecture (de haut en bas, puis de gauche à droite).
/// </summary>
public static class ZoneDetector
{
    public static List<PhotoZone> Detect(string pngPath, byte alphaThreshold = 32, double minAreaFraction = 0.01)
    {
        using var bitmap = SKBitmap.Decode(pngPath) ?? throw new InvalidDataException("Image illisible : " + pngPath);
        return Detect(bitmap, alphaThreshold, minAreaFraction);
    }

    public static List<PhotoZone> Detect(SKBitmap bitmap, byte alphaThreshold = 32, double minAreaFraction = 0.01)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var step = Math.Max(1, Math.Min(width, height) / 300);
        var gridWidth = (width + step - 1) / step;
        var gridHeight = (height + step - 1) / step;

        // 1. Grille des cellules transparentes (échantillonnage au centre de chaque cellule).
        var transparent = new bool[gridWidth * gridHeight];
        for (var gy = 0; gy < gridHeight; gy++)
        {
            var py = Math.Min(height - 1, gy * step + step / 2);
            for (var gx = 0; gx < gridWidth; gx++)
            {
                var px = Math.Min(width - 1, gx * step + step / 2);
                transparent[gy * gridWidth + gx] = bitmap.GetPixel(px, py).Alpha < alphaThreshold;
            }
        }

        // 2. Regroupement des cellules voisines (parcours en largeur).
        var visited = new bool[transparent.Length];
        var zones = new List<PhotoZone>();
        var queue = new Queue<int>();
        var totalCells = (double)transparent.Length;

        for (var start = 0; start < transparent.Length; start++)
        {
            if (!transparent[start] || visited[start]) continue;

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, count = 0;
            visited[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                var x = index % gridWidth;
                var y = index / gridWidth;
                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x > 0) Visit(index - 1);
                if (x < gridWidth - 1) Visit(index + 1);
                if (y > 0) Visit(index - gridWidth);
                if (y < gridHeight - 1) Visit(index + gridWidth);
            }

            if (count / totalCells < minAreaFraction) continue;

            // 3. Conversion en pixels, avec un léger débord sous le cadre pour éviter tout liseré.
            var left = Math.Max(0, minX * step - step);
            var top = Math.Max(0, minY * step - step);
            var right = Math.Min(width, (maxX + 1) * step + step);
            var bottom = Math.Min(height, (maxY + 1) * step + step);
            if (right - left < width * 0.03 || bottom - top < height * 0.03) continue;
            zones.Add(new PhotoZone(left, top, right - left, bottom - top));
        }

        return SortReadingOrder(zones);

        void Visit(int neighbor)
        {
            if (!transparent[neighbor] || visited[neighbor]) return;
            visited[neighbor] = true;
            queue.Enqueue(neighbor);
        }
    }

    /// <summary>Range les zones par lignes (de haut en bas), puis de gauche à droite dans chaque ligne.</summary>
    public static List<PhotoZone> SortReadingOrder(IEnumerable<PhotoZone> zones)
    {
        var byTop = zones.OrderBy(z => z.Y + z.Height / 2.0).ToList();
        var rows = new List<List<PhotoZone>>();
        foreach (var zone in byTop)
        {
            var centerY = zone.Y + zone.Height / 2.0;
            var row = rows.LastOrDefault();
            if (row != null)
            {
                var rowCenter = row.Average(z => z.Y + z.Height / 2.0);
                var tolerance = Math.Min(row.Min(z => z.Height), zone.Height) / 2.0;
                if (Math.Abs(centerY - rowCenter) < tolerance)
                {
                    row.Add(zone);
                    continue;
                }
            }
            rows.Add(new List<PhotoZone> { zone });
        }
        return rows.SelectMany(r => r.OrderBy(z => z.X)).ToList();
    }
}
