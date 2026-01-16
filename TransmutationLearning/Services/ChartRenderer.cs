using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TransmutationLearning.Services
{
    /// <summary>
    /// Renders all charts (histograms, pie charts, dot plots, bar charts)
    /// </summary>
    public class ChartRenderer
    {
        private readonly ColorService _colorService;

        public ChartRenderer(ColorService colorService)
        {
            _colorService = colorService;
        }

        #region Histogram

        public void DrawHistogram(Canvas canvas, List<double> deltaValues, double threshold, HashSet<string> validCellTypes)
        {
            canvas.Children.Clear();

            if (deltaValues == null || deltaValues.Count == 0 || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
                return;

            var (binEdges, counts) = StatisticsHelper.ComputeHistogram(deltaValues, 40);
            if (counts.Length == 0) return;

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            double barWidth = width / counts.Length;
            int maxCount = counts.Max();

            double minDelta = binEdges[0];
            double maxDelta = binEdges[binEdges.Length - 1];

            // Draw bars
            for (int i = 0; i < counts.Length; i++)
            {
                double barHeight = maxCount > 0 ? (counts[i] * (height - 20) / maxCount) : 0;
                double binCenter = (binEdges[i] + binEdges[i + 1]) / 2;

                // Color based on threshold
                bool aboveThreshold = binCenter >= threshold;
                var brush = aboveThreshold
                    ? new SolidColorBrush(Color.FromRgb(74, 144, 226))
                    : new SolidColorBrush(Color.FromRgb(200, 200, 200));

                var rect = new Rectangle
                {
                    Width = Math.Max(barWidth - 1, 1),
                    Height = barHeight,
                    Fill = brush
                };

                Canvas.SetLeft(rect, i * barWidth);
                Canvas.SetBottom(rect, 20);
                canvas.Children.Add(rect);
            }

            // Draw threshold line
            if (threshold >= minDelta && threshold <= maxDelta)
            {
                double thresholdX = ((threshold - minDelta) / (maxDelta - minDelta)) * width;

                var line = new Line
                {
                    X1 = thresholdX,
                    Y1 = 0,
                    X2 = thresholdX,
                    Y2 = height - 20,
                    Stroke = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 4, 2 }
                };
                canvas.Children.Add(line);

                // Threshold label
                var label = new TextBlock
                {
                    Text = $"τ={threshold:F3}",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38))
                };
                Canvas.SetLeft(label, thresholdX + 3);
                Canvas.SetTop(label, 2);
                canvas.Children.Add(label);
            }

            // Draw x-axis labels
            DrawAxisLabel(canvas, minDelta.ToString("F2"), 0, height - 15);
            DrawAxisLabel(canvas, maxDelta.ToString("F2"), width - 30, height - 15);
        }

        private void DrawAxisLabel(Canvas canvas, string text, double x, double y)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100))
            };
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
            canvas.Children.Add(label);
        }

        #endregion

        #region Pie Charts

        public void DrawPieChart(Canvas canvas, List<CellTypeStatistics> stats)
        {
            canvas.Children.Clear();

            if (stats == null || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
                return;

            var filteredStats = stats.Where(s => s.RetainedCount > 0).ToList();
            if (filteredStats.Count == 0) return;

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            double centerX = width * 0.35;
            double centerY = height / 2;
            double radius = Math.Min(centerX, centerY) - 10;

            int total = filteredStats.Sum(s => s.RetainedCount);
            double startAngle = -90; // Start from top

            for (int i = 0; i < filteredStats.Count; i++)
            {
                var stat = filteredStats[i];
                double sweepAngle = (stat.RetainedCount * 360.0) / total;

                if (sweepAngle < 0.5) continue; // Skip tiny slices

                var color = _colorService.GetColor(stat.CellType);
                DrawPieSlice(canvas, centerX, centerY, radius, startAngle, sweepAngle, color);

                startAngle += sweepAngle;
            }

            // Draw legend
            double legendX = width * 0.55;
            double legendY = 5;
            double legendItemHeight = Math.Min(14, (height - 10) / filteredStats.Count);

            for (int i = 0; i < filteredStats.Count && legendY < height - 10; i++)
            {
                var stat = filteredStats[i];
                var color = _colorService.GetColor(stat.CellType);

                // Color box
                var rect = new Rectangle
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY);
                canvas.Children.Add(rect);

                // Label (truncate if needed)
                string label = stat.CellType.Length > 12
                    ? stat.CellType.Substring(0, 10) + "..."
                    : stat.CellType;

                var text = new TextBlock
                {
                    Text = $"{label} ({stat.RetainedCount})",
                    FontSize = 9,
                    Foreground = Brushes.Black
                };
                Canvas.SetLeft(text, legendX + 14);
                Canvas.SetTop(text, legendY - 1);
                canvas.Children.Add(text);

                legendY += legendItemHeight;
            }
        }

        public void DrawDistributionPieChart(Canvas canvas, Dictionary<string, int> distribution)
        {
            canvas.Children.Clear();

            if (distribution == null || distribution.Count == 0 || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
                return;

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            double centerX = width * 0.4;
            double centerY = height / 2;
            double radius = Math.Min(centerX, centerY) - 10;

            int total = distribution.Values.Sum();
            double startAngle = -90;

            var orderedItems = distribution.OrderByDescending(x => x.Value).ToList();

            for (int i = 0; i < orderedItems.Count; i++)
            {
                var kvp = orderedItems[i];
                double sweepAngle = (kvp.Value * 360.0) / total;

                if (sweepAngle < 0.5) continue;

                var color = _colorService.GetColor(kvp.Key);
                DrawPieSlice(canvas, centerX, centerY, radius, startAngle, sweepAngle, color);

                startAngle += sweepAngle;
            }

            // Draw legend
            double legendX = width * 0.55;
            double legendY = 5;
            double legendItemHeight = Math.Min(12, (height - 10) / orderedItems.Count);

            for (int i = 0; i < orderedItems.Count && legendY < height - 10; i++)
            {
                var kvp = orderedItems[i];
                var color = _colorService.GetColor(kvp.Key);

                var rect = new Rectangle
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(rect, legendX);
                Canvas.SetTop(rect, legendY);
                canvas.Children.Add(rect);

                string label = kvp.Key.Length > 10 ? kvp.Key.Substring(0, 8) + ".." : kvp.Key;
                var text = new TextBlock
                {
                    Text = $"{label} ({kvp.Value})",
                    FontSize = 8,
                    Foreground = Brushes.Black
                };
                Canvas.SetLeft(text, legendX + 12);
                Canvas.SetTop(text, legendY - 1);
                canvas.Children.Add(text);

                legendY += legendItemHeight;
            }
        }

        private void DrawPieSlice(Canvas canvas, double cx, double cy, double r, double startAngle, double sweepAngle, Color color)
        {
            if (sweepAngle >= 360)
            {
                // Full circle
                var ellipse = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Fill = new SolidColorBrush(color)
                };
                Canvas.SetLeft(ellipse, cx - r);
                Canvas.SetTop(ellipse, cy - r);
                canvas.Children.Add(ellipse);
                return;
            }

            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;

            double x1 = cx + r * Math.Cos(startRad);
            double y1 = cy + r * Math.Sin(startRad);
            double x2 = cx + r * Math.Cos(endRad);
            double y2 = cy + r * Math.Sin(endRad);

            bool largeArc = sweepAngle > 180;

            var path = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(color),
                Stroke = Brushes.White,
                StrokeThickness = 1
            };

            var figure = new PathFigure { StartPoint = new Point(cx, cy) };
            figure.Segments.Add(new LineSegment(new Point(x1, y1), false));
            figure.Segments.Add(new ArcSegment(
                new Point(x2, y2),
                new Size(r, r),
                0,
                largeArc,
                SweepDirection.Clockwise,
                false));
            figure.Segments.Add(new LineSegment(new Point(cx, cy), false));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            path.Data = geometry;

            canvas.Children.Add(path);
        }

        #endregion

        #region Dot Plot

        public void DrawDotPlot(Canvas canvas, List<ProteinStatistics> markers, List<string> cellTypes)
        {
            canvas.Children.Clear();

            if (markers == null || markers.Count == 0 || cellTypes == null || cellTypes.Count == 0)
                return;

            if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
                return;

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;

            // Layout parameters
            double labelWidth = 80;  // Space for protein names on left
            double labelHeight = 60; // Space for cell type labels on bottom
            double plotWidth = width - labelWidth - 10;
            double plotHeight = height - labelHeight - 10;

            // Limit to top 30 markers for readability
            var displayMarkers = markers.Take(30).ToList();

            double cellWidth = plotWidth / cellTypes.Count;
            double rowHeight = Math.Min(plotHeight / displayMarkers.Count, 18);
            double maxRadius = Math.Min(cellWidth, rowHeight) / 2 - 2;

            // Find expression range for color scaling
            double minExpr = double.MaxValue;
            double maxExpr = double.MinValue;
            foreach (var marker in displayMarkers)
            {
                foreach (var ct in cellTypes)
                {
                    if (marker.CellTypeMetrics.TryGetValue(ct, out var m) && m.MedianExpression > 0)
                    {
                        minExpr = Math.Min(minExpr, m.MedianExpression);
                        maxExpr = Math.Max(maxExpr, m.MedianExpression);
                    }
                }
            }
            if (minExpr == double.MaxValue) minExpr = 0;
            if (maxExpr == double.MinValue) maxExpr = 1;
            double exprRange = maxExpr - minExpr;
            if (exprRange < 0.1) exprRange = 1;

            // Draw dots
            for (int row = 0; row < displayMarkers.Count; row++)
            {
                var marker = displayMarkers[row];
                double y = 5 + row * rowHeight + rowHeight / 2;

                // Protein label
                var proteinLabel = new TextBlock
                {
                    Text = marker.ProteinName.Length > 10
                        ? marker.ProteinName.Substring(0, 8) + ".."
                        : marker.ProteinName,
                    FontSize = 9,
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Right,
                    Width = labelWidth - 5
                };
                Canvas.SetLeft(proteinLabel, 0);
                Canvas.SetTop(proteinLabel, y - 6);
                canvas.Children.Add(proteinLabel);

                // Dots for each cell type
                for (int col = 0; col < cellTypes.Count; col++)
                {
                    var cellType = cellTypes[col];
                    double x = labelWidth + col * cellWidth + cellWidth / 2;

                    if (marker.CellTypeMetrics.TryGetValue(cellType, out var metrics))
                    {
                        // Size based on detection rate (0-1)
                        double radius = Math.Max(2, metrics.DetectionRate * maxRadius);

                        // Color based on expression (blue gradient)
                        double normExpr = exprRange > 0
                            ? (metrics.MedianExpression - minExpr) / exprRange
                            : 0.5;
                        normExpr = Math.Max(0, Math.Min(1, normExpr));

                        byte r = (byte)(220 - normExpr * 170);  // 220 -> 50
                        byte g = (byte)(230 - normExpr * 150);  // 230 -> 80
                        byte b = (byte)(240 - normExpr * 40);   // 240 -> 200

                        var dot = new Ellipse
                        {
                            Width = radius * 2,
                            Height = radius * 2,
                            Fill = new SolidColorBrush(Color.FromRgb(r, g, b)),
                            Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                            StrokeThickness = 0.5,
                            ToolTip = $"{marker.ProteinName}\n{cellType}\nDetect: {metrics.DetectionRate:P0}\nExpr: {metrics.MedianExpression:F2}"
                        };
                        Canvas.SetLeft(dot, x - radius);
                        Canvas.SetTop(dot, y - radius);
                        canvas.Children.Add(dot);
                    }
                }
            }

            // Draw cell type labels at bottom
            for (int col = 0; col < cellTypes.Count; col++)
            {
                var cellType = cellTypes[col];
                double x = labelWidth + col * cellWidth + cellWidth / 2;

                var label = new TextBlock
                {
                    Text = cellType.Length > 12 ? cellType.Substring(0, 10) + ".." : cellType,
                    FontSize = 9,
                    Foreground = Brushes.Black,
                    RenderTransform = new RotateTransform(45)
                };
                Canvas.SetLeft(label, x - 5);
                Canvas.SetTop(label, plotHeight + 10);
                canvas.Children.Add(label);
            }

            // Draw legend if space permits
            if (width > 400)
            {
                DrawDotPlotLegend(canvas, width - 80, 5, maxRadius);
            }
        }

        private void DrawDotPlotLegend(Canvas canvas, double x, double y, double maxRadius)
        {
            // Size legend
            var sizeLabel = new TextBlock
            {
                Text = "Size: Detection",
                FontSize = 8,
                Foreground = Brushes.Gray
            };
            Canvas.SetLeft(sizeLabel, x);
            Canvas.SetTop(sizeLabel, y);
            canvas.Children.Add(sizeLabel);

            // Small dot (25%)
            var smallDot = new Ellipse
            {
                Width = maxRadius * 0.5,
                Height = maxRadius * 0.5,
                Fill = Brushes.LightGray,
                Stroke = Brushes.Gray,
                StrokeThickness = 0.5
            };
            Canvas.SetLeft(smallDot, x);
            Canvas.SetTop(smallDot, y + 15);
            canvas.Children.Add(smallDot);

            var smallLabel = new TextBlock { Text = "25%", FontSize = 7, Foreground = Brushes.Gray };
            Canvas.SetLeft(smallLabel, x + maxRadius * 0.5 + 3);
            Canvas.SetTop(smallLabel, y + 15);
            canvas.Children.Add(smallLabel);

            // Large dot (100%)
            var largeDot = new Ellipse
            {
                Width = maxRadius * 2,
                Height = maxRadius * 2,
                Fill = Brushes.LightGray,
                Stroke = Brushes.Gray,
                StrokeThickness = 0.5
            };
            Canvas.SetLeft(largeDot, x);
            Canvas.SetTop(largeDot, y + 30);
            canvas.Children.Add(largeDot);

            var largeLabel = new TextBlock { Text = "100%", FontSize = 7, Foreground = Brushes.Gray };
            Canvas.SetLeft(largeLabel, x + maxRadius * 2 + 3);
            Canvas.SetTop(largeLabel, y + 35);
            canvas.Children.Add(largeLabel);
        }

        #endregion

        #region Bar Charts

        public void DrawMarkersPerTypeChart(Canvas canvas, Dictionary<string, List<ProteinStatistics>> markersByCellType)
        {
            canvas.Children.Clear();

            if (markersByCellType == null || markersByCellType.Count == 0)
                return;

            if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
                return;

            var markerCounts = markersByCellType
                .Select(kvp => new { CellType = kvp.Key, Count = kvp.Value.Count })
                .OrderByDescending(x => x.Count)
                .ToList();

            if (markerCounts.Count == 0) return;

            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;

            int maxCount = markerCounts.Max(x => x.Count);
            double barHeight = Math.Min((height - 10) / markerCounts.Count, 20);
            double labelWidth = 100;
            double barAreaWidth = width - labelWidth - 40;

            for (int i = 0; i < markerCounts.Count; i++)
            {
                var item = markerCounts[i];
                double y = 5 + i * barHeight;

                // Cell type label
                var label = new TextBlock
                {
                    Text = item.CellType.Length > 15 ? item.CellType.Substring(0, 13) + ".." : item.CellType,
                    FontSize = 9,
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Right,
                    Width = labelWidth - 5
                };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y + 2);
                canvas.Children.Add(label);

                // Bar
                double barWidth = maxCount > 0 ? (item.Count * barAreaWidth / maxCount) : 0;
                var color = _colorService.GetColor(item.CellType);

                var bar = new Rectangle
                {
                    Width = Math.Max(barWidth, 2),
                    Height = barHeight - 4,
                    Fill = new SolidColorBrush(color),
                    RadiusX = 2,
                    RadiusY = 2
                };
                Canvas.SetLeft(bar, labelWidth);
                Canvas.SetTop(bar, y + 2);
                canvas.Children.Add(bar);

                // Count label
                var countLabel = new TextBlock
                {
                    Text = item.Count.ToString(),
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.Black
                };
                Canvas.SetLeft(countLabel, labelWidth + barWidth + 5);
                Canvas.SetTop(countLabel, y + 2);
                canvas.Children.Add(countLabel);
            }
        }

        #endregion
    }
}
