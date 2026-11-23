using SkiaSharp;

namespace LibreBooth.Core.Design;

/// <summary>Décors dessinés par LibreBooth (filets, feuillage, confettis, néon, ligne, bandeau).</summary>
internal static class Decorations
{
    /// <summary>Ce qui passe sous les photos : dégradés, bandeaux, confettis.</summary>
    public static void DrawBackground(SKCanvas canvas, DecorStyle decor, EventDesign design, DesignAreas areas, TextBlock texts, bool bookmark)
    {
        var background = ColorUtil.Parse(design.BackgroundColor, SKColors.White);
        var accent = ColorUtil.Parse(design.AccentColor, SKColors.Gray);
        var accent2 = ColorUtil.Parse(design.Accent2Color, accent);
        switch (decor)
        {
            case DecorStyle.Neon:
                Gradient(canvas, areas.Canvas, ColorUtil.Mix(background, SKColors.Black, 0.3f), ColorUtil.Mix(background, accent, 0.16f));
                Glow(canvas, new SKPoint(areas.Canvas.Width * 0.82f, areas.Canvas.Height * 0.12f), Math.Max(areas.Canvas.Width, areas.Canvas.Height) * 0.55f, accent2.WithAlpha(46));
                Glow(canvas, new SKPoint(areas.Canvas.Width * 0.15f, areas.Canvas.Height * 0.92f), Math.Max(areas.Canvas.Width, areas.Canvas.Height) * 0.5f, accent.WithAlpha(40));
                break;
            case DecorStyle.Minimal:
                Gradient(canvas, areas.Canvas, ColorUtil.Mix(background, SKColors.White, 0.07f), ColorUtil.Mix(background, SKColors.Black, 0.18f));
                break;
            case DecorStyle.Band:
                Bands(canvas, areas, accent, accent2);
                break;
            case DecorStyle.Confetti:
                Confetti(canvas, areas, texts, accent, accent2, bookmark);
                break;
        }
    }

    /// <summary>Ce qui passe par-dessus le fond et autour des photos : filets, cadres néon, lignes, feuillage.</summary>
    public static void DrawForeground(SKCanvas canvas, DecorStyle decor, EventDesign design, DesignAreas areas, TextBlock texts, bool bookmark)
    {
        var accent = ColorUtil.Parse(design.AccentColor, SKColors.Gray);
        var accent2 = ColorUtil.Parse(design.Accent2Color, accent);
        switch (decor)
        {
            case DecorStyle.Lines:
                Lines(canvas, design, areas, texts, accent, bookmark);
                break;
            case DecorStyle.Neon:
                NeonFrames(canvas, design, areas, accent, accent2, bookmark);
                break;
            case DecorStyle.Minimal:
                MinimalLines(canvas, areas, texts, accent, bookmark);
                break;
            case DecorStyle.Leaves:
                Leaves(canvas, areas, texts, accent, accent2, bookmark);
                break;
        }
    }

    // ---------------------------------------------------------------- Filets (mariage)

    private static void Lines(SKCanvas canvas, EventDesign design, DesignAreas areas, TextBlock texts, SKColor accent, bool bookmark)
    {
        using var thick = Stroke(accent, bookmark ? 2.2f : 2.6f);
        using var thin = Stroke(accent.WithAlpha(190), 1.3f);
        if (bookmark)
        {
            canvas.DrawRect(DesignLayout.Inset(areas.Canvas, 18), thick);
        }
        else
        {
            canvas.DrawRect(DesignLayout.Inset(areas.Canvas, 20), thick);
            canvas.DrawRect(DesignLayout.Inset(areas.Canvas, 29), thin);
        }

        var offset = bookmark ? 9f : 13f;
        using var hair = Stroke(accent, 1.7f);
        foreach (var photo in areas.Photos)
        {
            var rect = DesignLayout.Inflate(photo, offset + design.PhotoBorder);
            var radius = design.PhotoCornerRadius > 0 ? design.PhotoCornerRadius + design.PhotoBorder + offset : 0;
            canvas.DrawRoundRect(rect, radius, radius, hair);
        }

        var detail = texts.DetailLines.FirstOrDefault();
        if (detail != null) FlankingLines(canvas, detail, areas.Stacked ? areas.TitleArea : areas.DetailsArea, accent, bookmark);
        if (bookmark)
        {
            Eyelet(canvas, new SKPoint(areas.Canvas.MidX, areas.Safe.Top + 22), accent);
            if (areas.LogoArea == null) BottomOrnament(canvas, new SKPoint(areas.Canvas.MidX, areas.Safe.Bottom - 16), accent);
        }
    }

    /// <summary>Trois losanges entre deux traits, en bas du marque-page (répond à l'œillet du haut).</summary>
    private static void BottomOrnament(SKCanvas canvas, SKPoint center, SKColor color)
    {
        using var stroke = Stroke(color, 1.6f);
        using var fill = Fill(color);
        canvas.DrawLine(center.X - 84, center.Y, center.X - 26, center.Y, stroke);
        canvas.DrawLine(center.X + 26, center.Y, center.X + 84, center.Y, stroke);
        Diamond(canvas, new SKPoint(center.X - 14, center.Y), 4, fill);
        Diamond(canvas, center, 6, fill);
        Diamond(canvas, new SKPoint(center.X + 14, center.Y), 4, fill);
    }

    /// <summary>Deux traits fins terminés par un losange, de part et d'autre de la ligne (la date en général).</summary>
    private static void FlankingLines(SKCanvas canvas, PlacedLine line, SKRect area, SKColor color, bool bookmark)
    {
        var ink = line.Ink;
        var y = (ink.Top + ink.Bottom) / 2f;
        var gap = bookmark ? 16f : 30f;
        var room = Math.Min(ink.Left - area.Left, area.Right - ink.Right) - gap - 12;
        var length = Math.Min(bookmark ? 64f : 170f, room);
        if (length < 24) return;

        using var stroke = Stroke(color, 1.6f);
        using var fill = Fill(color);
        canvas.DrawLine(ink.Left - gap - length, y, ink.Left - gap, y, stroke);
        canvas.DrawLine(ink.Right + gap, y, ink.Right + gap + length, y, stroke);
        Diamond(canvas, new SKPoint(ink.Left - gap - length, y), bookmark ? 5f : 7f, fill);
        Diamond(canvas, new SKPoint(ink.Right + gap + length, y), bookmark ? 5f : 7f, fill);
    }

    /// <summary>Petit anneau en haut du marque-page, façon œillet à ruban, entre deux traits.</summary>
    private static void Eyelet(SKCanvas canvas, SKPoint center, SKColor color)
    {
        using var stroke = Stroke(color, 2.2f);
        using var fill = Fill(color);
        canvas.DrawCircle(center, 13, stroke);
        canvas.DrawLine(center.X - 74, center.Y, center.X - 24, center.Y, stroke);
        canvas.DrawLine(center.X + 24, center.Y, center.X + 74, center.Y, stroke);
        Diamond(canvas, new SKPoint(center.X - 80, center.Y), 5, fill);
        Diamond(canvas, new SKPoint(center.X + 80, center.Y), 5, fill);
    }

    private static void Diamond(SKCanvas canvas, SKPoint center, float size, SKPaint paint)
    {
        using var path = new SKPath();
        path.MoveTo(center.X - size, center.Y);
        path.LineTo(center.X, center.Y - size);
        path.LineTo(center.X + size, center.Y);
        path.LineTo(center.X, center.Y + size);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    // ---------------------------------------------------------------- Néon (fête)

    private static void NeonFrames(SKCanvas canvas, EventDesign design, DesignAreas areas, SKColor accent, SKColor accent2, bool bookmark)
    {
        for (var i = 0; i < areas.Photos.Count; i++)
        {
            var photo = areas.Photos[i];
            var corner = design.PhotoCornerRadius;
            if (bookmark)
            {
                // Les photos du marque-page sont proches : un seul cadre chacune, en alternant les couleurs.
                var offset = 9f + design.PhotoBorder;
                NeonRect(canvas, DesignLayout.Inflate(photo, offset), corner > 0 ? corner + offset : 0, i % 2 == 0 ? accent : accent2, 2.6f);
            }
            else
            {
                var first = 15f + design.PhotoBorder;
                var second = 28f + design.PhotoBorder;
                NeonRect(canvas, DesignLayout.Inflate(photo, first), corner > 0 ? corner + first : 0, accent, 3.4f);
                NeonRect(canvas, DesignLayout.Inflate(photo, second), corner > 0 ? corner + second : 0, accent2, 2.2f);
            }
        }

        if (bookmark)
        {
            var center = new SKPoint(areas.Canvas.MidX, areas.Safe.Top + 22);
            using var glow = GlowStroke(accent2, 2.4f);
            using var core = Stroke(ColorUtil.Mix(accent2, SKColors.White, 0.55f), 2.4f);
            canvas.DrawCircle(center, 13, glow);
            canvas.DrawCircle(center, 13, core);
            if (areas.LogoArea == null)
            {
                // Trait lumineux en bas, qui répond à l'anneau du haut.
                var y = areas.Safe.Bottom - 16;
                using var lineGlow = GlowStroke(accent, 2.6f);
                using var lineCore = Stroke(ColorUtil.Mix(accent, SKColors.White, 0.55f), 2.6f);
                lineCore.StrokeCap = SKStrokeCap.Round;
                canvas.DrawLine(center.X - 70, y, center.X + 70, y, lineGlow);
                canvas.DrawLine(center.X - 70, y, center.X + 70, y, lineCore);
            }
        }
    }

    private static void NeonRect(SKCanvas canvas, SKRect rect, float radius, SKColor color, float width)
    {
        using var glow = GlowStroke(color, width);
        canvas.DrawRoundRect(rect, radius, radius, glow);
        using var core = Stroke(ColorUtil.Mix(color, SKColors.White, 0.55f), width);
        canvas.DrawRoundRect(rect, radius, radius, core);
    }

    private static SKPaint GlowStroke(SKColor color, float width) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width * 3.4f,
        Color = color.WithAlpha(165),
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, width * 2.8f),
    };

    // ---------------------------------------------------------------- Ligne fine (entreprise)

    private static void MinimalLines(SKCanvas canvas, DesignAreas areas, TextBlock texts, SKColor accent, bool bookmark)
    {
        using var stroke = Stroke(accent, 3f);
        var half = bookmark ? 60f : 90f;
        var title = texts.TitleLines.LastOrDefault();
        var detail = texts.DetailLines.FirstOrDefault();
        if (bookmark)
        {
            if (title != null) canvas.DrawLine(title.CenterX - half, title.Ink.Bottom + 30, title.CenterX + half, title.Ink.Bottom + 30, stroke);
            if (detail != null && !areas.Stacked) canvas.DrawLine(detail.CenterX - half, detail.Ink.Top - 28, detail.CenterX + half, detail.Ink.Top - 28, stroke);
            if (areas.LogoArea == null)
            {
                var y = areas.Safe.Bottom - 16;
                canvas.DrawLine(areas.Canvas.MidX - 24, y, areas.Canvas.MidX + 24, y, stroke);
            }
            return;
        }

        if (title != null && detail != null)
        {
            var y = (title.Ink.Bottom + detail.Ink.Top) / 2f;
            canvas.DrawLine(title.CenterX - half, y, title.CenterX + half, y, stroke);
        }
        else if (title != null || detail != null)
        {
            var line = (title ?? detail)!;
            canvas.DrawLine(line.CenterX - half, line.Ink.Top - 24, line.CenterX + half, line.Ink.Top - 24, stroke);
        }
    }

    // ---------------------------------------------------------------- Bandeau (entreprise)

    private static void Bands(SKCanvas canvas, DesignAreas areas, SKColor accent, SKColor accent2)
    {
        if (areas.Bands.Count == 0) return;
        var photosTop = areas.Photos.Count > 0 ? areas.Photos.Min(p => p.Top) : areas.Canvas.MidY;
        using var fill = Fill(accent);
        using var edge = Fill(accent2);
        foreach (var band in areas.Bands)
        {
            if (band.Height <= 0) continue;
            canvas.DrawRect(band, fill);
            // Filet de la seconde couleur du côté des photos.
            var edgeRect = band.Bottom <= photosTop + 1
                ? new SKRect(band.Left, band.Bottom - 6, band.Right, band.Bottom)
                : new SKRect(band.Left, band.Top, band.Right, band.Top + 6);
            canvas.DrawRect(edgeRect, edge);
        }
    }

    // ---------------------------------------------------------------- Confettis (anniversaire)

    private static void Confetti(SKCanvas canvas, DesignAreas areas, TextBlock texts, SKColor accent, SKColor accent2, bool bookmark)
    {
        var palette = new[]
        {
            accent, accent2, ColorUtil.ShiftHue(accent, 50), ColorUtil.ShiftHue(accent2, 75), ColorUtil.ShiftHue(accent, 205),
        };
        var random = new Random(20261122);
        var width = areas.Canvas.Width;
        var height = areas.Canvas.Height;
        var count = (int)(width * height / (bookmark ? 3800f : 4600f));
        var avoid = texts.Lines.Select(l => DesignLayout.Inflate(l.Ink, 16)).ToList();
        if (areas.LogoArea is { } logo) avoid.Add(DesignLayout.Inflate(logo, 10));
        var hidden = areas.Photos.Select(p => DesignLayout.Inset(p, 8)).ToList();

        for (var i = 0; i < count; i++)
        {
            // Les tirages au sort se font toujours dans le même ordre : le décor est identique à chaque impression.
            var x = (float)random.NextDouble() * width;
            var y = (float)random.NextDouble() * height;
            var size = (bookmark ? 5f : 6f) + (float)random.NextDouble() * (bookmark ? 8f : 11f);
            var shape = random.Next(4);
            var rotation = random.Next(180);
            var color = palette[random.Next(palette.Length)];
            var bounds = SKRect.Create(x - size * 1.4f, y - size * 1.4f, size * 2.8f, size * 2.8f);
            if (hidden.Any(p => p.Contains(x, y)) || avoid.Any(a => a.IntersectsWith(bounds))) continue;

            using var paint = Fill(color.WithAlpha(235));
            canvas.Save();
            canvas.Translate(x, y);
            canvas.RotateDegrees(rotation);
            switch (shape)
            {
                case 0:
                case 1:
                    canvas.DrawCircle(0, 0, size * 0.6f, paint);
                    break;
                case 2:
                    canvas.DrawRoundRect(SKRect.Create(-size * 1.2f, -size * 0.35f, size * 2.4f, size * 0.7f), size * 0.3f, size * 0.3f, paint);
                    break;
                default:
                    using (var ring = Stroke(color.WithAlpha(235), Math.Max(2.5f, size * 0.22f)))
                    {
                        canvas.DrawCircle(0, 0, size * 0.55f, ring);
                    }
                    break;
            }
            canvas.Restore();
        }
    }

    // ---------------------------------------------------------------- Feuillage (mariage champêtre)

    private static void Leaves(SKCanvas canvas, DesignAreas areas, TextBlock texts, SKColor accent, SKColor accent2, bool bookmark)
    {
        var stem = ColorUtil.Mix(accent, SKColors.Black, 0.12f);
        var title = texts.TitleLines.FirstOrDefault();

        if (bookmark)
        {
            // Couronne au sommet, deux brins de part et d'autre de la date, et une petite couronne en bas.
            var top = new SKPoint(areas.Canvas.MidX, areas.Safe.Top + 34);
            Sprig(canvas, new SKPoint(top.X - 10, top.Y), new SKPoint(top.X - 170, top.Y - 18), 18, stem, accent, accent2, 7, 34);
            Sprig(canvas, new SKPoint(top.X + 10, top.Y), new SKPoint(top.X + 170, top.Y - 18), -18, stem, accent, accent2, 7, 34);
            var detail = texts.DetailLines.FirstOrDefault();
            if (detail != null) FlankingSprigs(canvas, detail, areas.DetailsArea, stem, accent, accent2, 100, 24);
            if (areas.LogoArea == null)
            {
                var bottom = new SKPoint(areas.Canvas.MidX, areas.Safe.Bottom - 22);
                Sprig(canvas, new SKPoint(bottom.X - 8, bottom.Y), new SKPoint(bottom.X - 110, bottom.Y + 10), -14, stem, accent, accent2, 5, 26);
                Sprig(canvas, new SKPoint(bottom.X + 8, bottom.Y), new SKPoint(bottom.X + 110, bottom.Y + 10), 14, stem, accent, accent2, 5, 26);
            }
            return;
        }

        if (title != null) FlankingSprigs(canvas, title, areas.TitleArea, stem, accent, accent2, 300, 44);
    }

    private static void FlankingSprigs(SKCanvas canvas, PlacedLine line, SKRect area, SKColor stem, SKColor leaf, SKColor leaf2, float maxLength, float leafLength)
    {
        var ink = line.Ink;
        var y = (ink.Top + ink.Bottom) / 2f;
        var gap = 26f;
        // La feuille de la pointe dépasse un peu du bout de la tige.
        var room = Math.Min(ink.Left - area.Left, area.Right - ink.Right) - gap - leafLength * 0.6f;
        var length = Math.Min(maxLength, room);
        if (length < 60) return;
        var count = Math.Max(4, (int)(length / (leafLength * 0.75f)));
        var scale = Math.Min(1f, length / maxLength + 0.3f);
        Sprig(canvas, new SKPoint(ink.Left - gap, y + 4), new SKPoint(ink.Left - gap - length, y - 10), 22, stem, leaf, leaf2, count, leafLength * scale);
        Sprig(canvas, new SKPoint(ink.Right + gap, y + 4), new SKPoint(ink.Right + gap + length, y - 10), -22, stem, leaf, leaf2, count, leafLength * scale);
    }

    /// <summary>Un brin : une tige courbe et des feuilles en alternance, plus petites vers la pointe.</summary>
    private static void Sprig(SKCanvas canvas, SKPoint start, SKPoint end, float bend, SKColor stemColor, SKColor leaf, SKColor leaf2, int count, float leafLength)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;
        var mid = new SKPoint((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        var control = new SKPoint(mid.X - dy / length * bend, mid.Y + dx / length * bend);

        using (var path = new SKPath())
        using (var stroke = Stroke(stemColor, Math.Max(1.6f, leafLength * 0.07f)))
        {
            stroke.StrokeCap = SKStrokeCap.Round;
            path.MoveTo(start);
            path.QuadTo(control, end);
            canvas.DrawPath(path, stroke);
        }

        for (var i = 0; i < count; i++)
        {
            var t = 0.1f + 0.85f * i / Math.Max(1, count - 1);
            var point = Quad(start, control, end, t);
            var tangent = QuadTangent(start, control, end, t);
            var angle = MathF.Atan2(tangent.Y, tangent.X) * 180f / MathF.PI;
            var side = i % 2 == 0 ? 1 : -1;
            var size = leafLength * (1.05f - 0.45f * t);
            using var paint = Fill((i % 3 == 1 ? leaf2 : leaf).WithAlpha(235));
            canvas.Save();
            canvas.Translate(point.X, point.Y);
            canvas.RotateDegrees(angle + side * 40);
            using var leafPath = LeafPath(size, size * 0.44f);
            canvas.DrawPath(leafPath, paint);
            canvas.Restore();
        }

        // Feuille de la pointe, dans l'axe de la tige.
        var tip = QuadTangent(start, control, end, 1f);
        canvas.Save();
        canvas.Translate(end.X, end.Y);
        canvas.RotateDegrees(MathF.Atan2(tip.Y, tip.X) * 180f / MathF.PI);
        using (var tipPaint = Fill(leaf.WithAlpha(235)))
        using (var tipPath = LeafPath(leafLength * 0.55f, leafLength * 0.26f))
        {
            canvas.DrawPath(tipPath, tipPaint);
        }
        canvas.Restore();
    }

    private static SKPath LeafPath(float length, float width)
    {
        var path = new SKPath();
        path.MoveTo(0, 0);
        path.QuadTo(length * 0.45f, -width, length, 0);
        path.QuadTo(length * 0.45f, width, 0, 0);
        path.Close();
        return path;
    }

    private static SKPoint Quad(SKPoint a, SKPoint c, SKPoint b, float t)
    {
        var u = 1 - t;
        return new SKPoint(u * u * a.X + 2 * u * t * c.X + t * t * b.X, u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y);
    }

    private static SKPoint QuadTangent(SKPoint a, SKPoint c, SKPoint b, float t) =>
        new(2 * (1 - t) * (c.X - a.X) + 2 * t * (b.X - c.X), 2 * (1 - t) * (c.Y - a.Y) + 2 * t * (b.Y - c.Y));

    // ---------------------------------------------------------------- Outils

    private static void Gradient(SKCanvas canvas, SKRect rect, SKColor top, SKColor bottom)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Bottom), new[] { top, bottom }, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(rect, paint);
    }

    private static void Glow(SKCanvas canvas, SKPoint center, float radius, SKColor color)
    {
        using var shader = SKShader.CreateRadialGradient(center, radius, new[] { color, color.WithAlpha(0) }, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawCircle(center, radius, paint);
    }

    private static SKPaint Stroke(SKColor color, float width) =>
        new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = color };

    private static SKPaint Fill(SKColor color) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
}
