using ApiJiraTools.Models;
using SkiaSharp;

namespace ApiJiraTools.Services;

/// <summary>
/// Genera un PNG del burndown (ideal vs. actual) con SkiaSharp.
/// </summary>
public sealed class BurndownChartService
{
    public byte[] RenderPng(BurndownData data, string title)
    {
        const int Width = 900;
        const int Height = 520;
        const float MarginLeft = 60;
        const float MarginRight = 30;
        const float MarginTop = 70;
        const float MarginBottom = 55;

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        // Títulos
        using var titlePaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var titleFont = new SKFont(SKTypeface.Default, 22) { Embolden = true };
        canvas.DrawText(title, MarginLeft, 32, SKTextAlign.Left, titleFont, titlePaint);

        var subtitle = $"{data.SprintStart:dd/MM} → {data.SprintEnd:dd/MM}   |   Total proyectado: {data.TotalSp:0.#} SP";
        using var subFont = new SKFont(SKTypeface.Default, 13);
        using var subPaint = new SKPaint { Color = SKColors.DarkGray, IsAntialias = true };
        canvas.DrawText(subtitle, MarginLeft, 54, SKTextAlign.Left, subFont, subPaint);

        // Plot area
        float plotX = MarginLeft;
        float plotY = MarginTop;
        float plotW = Width - MarginLeft - MarginRight;
        float plotH = Height - MarginTop - MarginBottom;

        var points = data.DataPoints;
        if (points.Count < 2)
        {
            using var errPaint = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
            using var errFont = new SKFont(SKTypeface.Default, 16);
            canvas.DrawText("Sin suficientes datos para graficar", Width / 2f, Height / 2f, SKTextAlign.Center, errFont, errPaint);
            using var img0 = surface.Snapshot();
            using var data0 = img0.Encode(SKEncodedImageFormat.Png, 90);
            return data0.ToArray();
        }

        double maxY = Math.Max(data.TotalSp, points.Max(p => p.RemainingActual ?? 0));
        if (maxY <= 0) maxY = 1;
        int n = points.Count;

        float XAt(int i) => plotX + (float)i / (n - 1) * plotW;
        float YAt(double v) => plotY + plotH - (float)(v / maxY) * plotH;

        // Ejes + grid
        using var gridPaint = new SKPaint { Color = SKColors.LightGray, IsAntialias = true, StrokeWidth = 1 };
        using var axisPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 1.5f };
        using var tickFont = new SKFont(SKTypeface.Default, 11);
        using var tickPaint = new SKPaint { Color = SKColors.DimGray, IsAntialias = true };

        int yTicks = 5;
        for (int i = 0; i <= yTicks; i++)
        {
            double v = maxY * i / yTicks;
            float y = YAt(v);
            canvas.DrawLine(plotX, y, plotX + plotW, y, gridPaint);
            canvas.DrawText($"{v:0.#}", plotX - 8, y + 4, SKTextAlign.Right, tickFont, tickPaint);
        }

        // Ejes principales
        canvas.DrawLine(plotX, plotY, plotX, plotY + plotH, axisPaint);
        canvas.DrawLine(plotX, plotY + plotH, plotX + plotW, plotY + plotH, axisPaint);

        // Ticks de fechas en X (cada ~5 puntos)
        int step = Math.Max(1, n / 7);
        for (int i = 0; i < n; i += step)
        {
            float x = XAt(i);
            canvas.DrawLine(x, plotY + plotH, x, plotY + plotH + 4, axisPaint);
            canvas.DrawText(points[i].Date.ToString("dd/MM"), x, plotY + plotH + 20, SKTextAlign.Center, tickFont, tickPaint);
        }

        // Línea ideal (punteada, verde)
        using var idealPaint = new SKPaint
        {
            Color = new SKColor(46, 150, 84),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            PathEffect = SKPathEffect.CreateDash(new float[] { 8, 6 }, 0)
        };
        using (var path = new SKPath())
        {
            path.MoveTo(XAt(0), YAt(points[0].RemainingIdeal));
            for (int i = 1; i < n; i++)
                path.LineTo(XAt(i), YAt(points[i].RemainingIdeal));
            canvas.DrawPath(path, idealPaint);
        }

        // Línea actual (azul, sólida). Solo dibuja donde hay datos.
        using var actualPaint = new SKPaint
        {
            Color = new SKColor(37, 99, 235),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
        };
        using var dotPaint = new SKPaint
        {
            Color = new SKColor(37, 99, 235),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        var actualIdx = new List<int>();
        for (int i = 0; i < n; i++)
            if (points[i].RemainingActual.HasValue) actualIdx.Add(i);

        if (actualIdx.Count >= 2)
        {
            using var path = new SKPath();
            path.MoveTo(XAt(actualIdx[0]), YAt(points[actualIdx[0]].RemainingActual!.Value));
            for (int k = 1; k < actualIdx.Count; k++)
            {
                int i = actualIdx[k];
                path.LineTo(XAt(i), YAt(points[i].RemainingActual!.Value));
            }
            canvas.DrawPath(path, actualPaint);
        }
        foreach (var i in actualIdx)
            canvas.DrawCircle(XAt(i), YAt(points[i].RemainingActual!.Value), 4, dotPaint);

        // Leyenda
        float legendX = plotX + plotW - 220;
        float legendY = plotY + 6;
        using var legendBg = new SKPaint { Color = new SKColor(0xFA, 0xFA, 0xFA), IsAntialias = true };
        using var legendBorder = new SKPaint { Color = SKColors.LightGray, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 1 };
        canvas.DrawRoundRect(legendX, legendY, 210, 54, 6, 6, legendBg);
        canvas.DrawRoundRect(legendX, legendY, 210, 54, 6, 6, legendBorder);
        using var legendFont = new SKFont(SKTypeface.Default, 12);
        using var legendPaint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        // Ideal sample
        canvas.DrawLine(legendX + 10, legendY + 18, legendX + 40, legendY + 18, idealPaint);
        canvas.DrawText("Ideal", legendX + 48, legendY + 22, SKTextAlign.Left, legendFont, legendPaint);

        // Actual sample
        canvas.DrawLine(legendX + 10, legendY + 40, legendX + 40, legendY + 40, actualPaint);
        canvas.DrawCircle(legendX + 25, legendY + 40, 3.5f, dotPaint);
        canvas.DrawText("Real", legendX + 48, legendY + 44, SKTextAlign.Left, legendFont, legendPaint);

        using var img = surface.Snapshot();
        using var pngData = img.Encode(SKEncodedImageFormat.Png, 90);
        return pngData.ToArray();
    }
}
