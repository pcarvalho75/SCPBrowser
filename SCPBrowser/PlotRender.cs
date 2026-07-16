using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser
{
    /// <summary>
    /// Handles rendering of scatter plot including axes, grid, data points, and color legend
    /// </summary>
    public class PlotRenderer
    {
        private const double PlotMarginLeft = 80;
        private const double PlotMarginRight = 20;
        private const double PlotMarginTop = 20;
        private const double PlotMarginBottom = 60;

        private const double MarkerSize = 10;
        private const int NumTicksX = 5;
        private const int NumTicksY = 5;

        // Selection styling constants
        private static readonly Color UnselectedGray = Color.FromRgb(210, 210, 210);
        private const double UnselectedOpacity = 0.5;

        private double _minX, _maxX, _minY, _maxY;
        private bool _useLogLog;

        /// <summary>
        /// When true the underlying data is a gene × sample matrix (no peptide/precursor dimension), so the axis
        /// titles read "Proteins" / "Total Intensity" instead of "Peptides" / "Total Ion Current".
        /// </summary>
        public bool IsGeneMatrix { get; set; }

        public double MinX => _minX;
        public double MaxX => _maxX;
        public double MinY => _minY;
        public double MaxY => _maxY;

        public void CalculateAxisRanges(List<int> peptideCounts, List<double> ticValues, bool useLogLog)
        {
            _useLogLog = useLogLog;

            double minX = peptideCounts.Min();
            double maxX = peptideCounts.Max();
            double minY = ticValues.Where(v => v > 0).Min();
            double maxY = ticValues.Max();

            if (useLogLog)
            {
                minX = Math.Log10(Math.Max(minX, 1));
                maxX = Math.Log10(maxX);
                minY = Math.Log10(Math.Max(minY, 1));
                maxY = Math.Log10(maxY);
            }

            _minX = minX;
            _maxX = maxX;
            _minY = minY;
            _maxY = maxY;
        }




        public Point DataToScreen(double dataX, double dataY, double canvasWidth, double canvasHeight)
        {
            double plotWidth = canvasWidth - PlotMarginLeft - PlotMarginRight;
            double plotHeight = canvasHeight - PlotMarginTop - PlotMarginBottom;

            if (_useLogLog)
            {
                dataX = Math.Log10(Math.Max(dataX, 1));
                dataY = Math.Log10(Math.Max(dataY, 1));
            }

            double screenX = PlotMarginLeft + (dataX - _minX) / (_maxX - _minX) * plotWidth;
            double screenY = canvasHeight - PlotMarginBottom - ((dataY - _minY) / (_maxY - _minY) * plotHeight);

            return new Point(screenX, screenY);
        }

        public Point ScreenToData(Point screenPoint, double canvasWidth, double canvasHeight)
        {
            double plotWidth = canvasWidth - PlotMarginLeft - PlotMarginRight;
            double plotHeight = canvasHeight - PlotMarginTop - PlotMarginBottom;

            double x = (screenPoint.X - PlotMarginLeft) / plotWidth * (_maxX - _minX) + _minX;
            double y = (canvasHeight - PlotMarginBottom - screenPoint.Y) / plotHeight * (_maxY - _minY) + _minY;

            return new Point(x, y);
        }

        public bool IsPointInPlotArea(Point screenPoint, double canvasWidth, double canvasHeight)
        {
            return screenPoint.X >= PlotMarginLeft &&
                   screenPoint.X <= canvasWidth - PlotMarginRight &&
                   screenPoint.Y >= PlotMarginTop &&
                   screenPoint.Y <= canvasHeight - PlotMarginBottom;
        }

        public void DrawAxesAndGrid(Canvas canvas, double canvasWidth, double canvasHeight)
        {
            double plotWidth = canvasWidth - PlotMarginLeft - PlotMarginRight;
            double plotHeight = canvasHeight - PlotMarginTop - PlotMarginBottom;

            if (plotWidth <= 0 || plotHeight <= 0)
                return;

            var axisBrush = new SolidColorBrush(Colors.Black);
            var gridBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0));

            // Draw axes
            var leftAxis = new Line
            {
                X1 = PlotMarginLeft,
                Y1 = PlotMarginTop,
                X2 = PlotMarginLeft,
                Y2 = canvasHeight - PlotMarginBottom,
                Stroke = axisBrush,
                StrokeThickness = 2
            };
            canvas.Children.Add(leftAxis);

            var bottomAxis = new Line
            {
                X1 = PlotMarginLeft,
                Y1 = canvasHeight - PlotMarginBottom,
                X2 = canvasWidth - PlotMarginRight,
                Y2 = canvasHeight - PlotMarginBottom,
                Stroke = axisBrush,
                StrokeThickness = 2
            };
            canvas.Children.Add(bottomAxis);

            // Draw X-axis ticks and labels
            for (int i = 0; i <= NumTicksX; i++)
            {
                double fraction = (double)i / NumTicksX;
                double x = PlotMarginLeft + fraction * plotWidth;
                double value = _minX + fraction * (_maxX - _minX);

                var gridLine = new Line
                {
                    X1 = x,
                    Y1 = PlotMarginTop,
                    X2 = x,
                    Y2 = canvasHeight - PlotMarginBottom,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                };
                canvas.Children.Add(gridLine);

                var tick = new Line
                {
                    X1 = x,
                    Y1 = canvasHeight - PlotMarginBottom,
                    X2 = x,
                    Y2 = canvasHeight - PlotMarginBottom + 5,
                    Stroke = axisBrush,
                    StrokeThickness = 2
                };
                canvas.Children.Add(tick);

                string labelText = _useLogLog ? Math.Pow(10, value).ToString("N0") : value.ToString("N0");
                var label = new TextBlock
                {
                    Text = labelText,
                    FontSize = 10,
                    Foreground = axisBrush
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, x - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, canvasHeight - PlotMarginBottom + 8);
                canvas.Children.Add(label);
            }

            // Draw Y-axis ticks and labels
            for (int i = 0; i <= NumTicksY; i++)
            {
                double fraction = (double)i / NumTicksY;
                double y = canvasHeight - PlotMarginBottom - fraction * plotHeight;
                double value = _minY + fraction * (_maxY - _minY);

                var gridLine = new Line
                {
                    X1 = PlotMarginLeft,
                    Y1 = y,
                    X2 = canvasWidth - PlotMarginRight,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                };
                canvas.Children.Add(gridLine);

                var tick = new Line
                {
                    X1 = PlotMarginLeft - 5,
                    Y1 = y,
                    X2 = PlotMarginLeft,
                    Y2 = y,
                    Stroke = axisBrush,
                    StrokeThickness = 2
                };
                canvas.Children.Add(tick);

                string labelText = _useLogLog ? Math.Pow(10, value).ToString("E1") : value.ToString("E1");
                var label = new TextBlock
                {
                    Text = labelText,
                    FontSize = 10,
                    Foreground = axisBrush
                };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, PlotMarginLeft - label.DesiredSize.Width - 8);
                Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
                canvas.Children.Add(label);
            }

            // Draw axis labels
            string xBase = IsGeneMatrix ? "Number of Proteins" : "Number of Peptides";
            var xLabel = new TextBlock
            {
                Text = _useLogLog ? $"{xBase} [Log Scale]" : xBase,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = axisBrush
            };
            xLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(xLabel, PlotMarginLeft + plotWidth / 2 - xLabel.DesiredSize.Width / 2);
            Canvas.SetTop(xLabel, canvasHeight - 20);
            canvas.Children.Add(xLabel);

            string yBase = IsGeneMatrix ? "Total Intensity" : "Total Ion Current (MS1 Area)";
            var yLabel = new TextBlock
            {
                Text = _useLogLog ? $"{yBase} [Log Scale]" : yBase,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = axisBrush,
                RenderTransform = new RotateTransform(-90)
            };
            yLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(yLabel, 15);
            Canvas.SetTop(yLabel, PlotMarginTop + plotHeight / 2 + yLabel.DesiredSize.Width / 2);
            canvas.Children.Add(yLabel);
        }

        public List<DataPoint> DrawDataPoints(Canvas canvas, List<string> rawFiles, List<int> peptideCounts,
            List<double> ticValues, List<int> proteinCounts, List<double> contaminantRatios,
            double canvasWidth, double canvasHeight)
        {
            var dataPoints = new List<DataPoint>();
            double maxRatio = contaminantRatios.Max();
            if (maxRatio < 0.01) maxRatio = 0.05;

            for (int i = 0; i < rawFiles.Count; i++)
            {
                if (ticValues[i] <= 0)
                    continue;

                Point screenPos = DataToScreen(peptideCounts[i], ticValues[i], canvasWidth, canvasHeight);

                double normalizedRatio = contaminantRatios[i] / maxRatio;
                Color markerColor = ColorMapper.GetViridisColor(normalizedRatio);

                var ellipse = new Ellipse
                {
                    Width = MarkerSize,
                    Height = MarkerSize,
                    Fill = new SolidColorBrush(markerColor),
                    Stroke = new SolidColorBrush(Color.FromRgb(50, 50, 50)),
                    StrokeThickness = 1,
                    Opacity = 1.0
                };

                Canvas.SetLeft(ellipse, screenPos.X - MarkerSize / 2);
                Canvas.SetTop(ellipse, screenPos.Y - MarkerSize / 2);
                canvas.Children.Add(ellipse);

                dataPoints.Add(new DataPoint
                {
                    RunName = rawFiles[i],
                    PeptideCount = peptideCounts[i],
                    TicValue = ticValues[i],
                    ProteinCount = proteinCounts[i],
                    ContaminantRatio = contaminantRatios[i],
                    XScreen = screenPos.X,
                    YScreen = screenPos.Y,
                    Visual = ellipse,
                    BaseColor = markerColor,
                    IsSelected = true
                });
            }

            return dataPoints;
        }
    }
}