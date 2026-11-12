using System;
using System.Collections.Generic;
using Godot;

namespace Planet.Ui;

/// <summary>
/// Courbes des grandeurs suivies : chacune est ramenée à sa propre étendue (minimum en bas,
/// maximum en haut), si bien que des grandeurs d'unités différentes se lisent ensemble ; la
/// dernière valeur est écrite au bout de la courbe.
/// </summary>
public sealed partial class GraphControl : Control
{
    public List<TimeSeries> Series { get; set; } = new();

    public override void _Draw()
    {
        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0f, 0f, 0f, 0.25f));
        for (int k = 1; k < 4; k++)
            DrawLine(new Vector2(0, size.Y * k / 4), new Vector2(size.X, size.Y * k / 4), new Color(1f, 1f, 1f, 0.07f));
        Font font = ThemeDB.FallbackFont;
        const float labelWidth = 62f, pad = 5f;
        float width = Math.Max(10f, size.X - labelWidth);
        int row = 0;
        foreach (var series in Series)
        {
            if (!series.Shown || series.Count < 2)
                continue;
            var (min, max) = series.Range();
            double span = Math.Max(max - min, 1e-9);
            var color = new Color(series.Color.R, series.Color.G, series.Color.B);
            var points = new Vector2[series.Count];
            for (int i = 0; i < series.Count; i++)
                // (au début, les premiers points occupent déjà un dixième de la largeur)
                points[i] = new Vector2(width * i / (Math.Max(series.Count, TimeSeries.Capacity / 10) - 1),
                                        pad + (size.Y - 2 * pad) * (float)(1 - (series[i] - min) / span));
            DrawPolyline(points, color, 1.5f, true);
            // Dernière valeur, au bout de la courbe (décalée pour ne pas se chevaucher)
            string text = series.Last.ToString(series.Format, System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',')
                          + (series.Unit.Length > 0 ? " " + series.Unit : "");
            DrawString(font, new Vector2(width + 4, 12 + 13 * row++), text, HorizontalAlignment.Left, labelWidth, 11, color);
        }
    }
}
