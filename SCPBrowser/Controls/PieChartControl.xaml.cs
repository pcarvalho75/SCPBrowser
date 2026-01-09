using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser.Controls
{
    public partial class PieChartControl : UserControl
    {
        public PieChartControl()
        {
            InitializeComponent();
            SizeChanged += PieChartControl_SizeChanged;
        }

        private Dictionary<string, int> _currentCounts;
        private Dictionary<string, Color> _currentColorMap;

        public void UpdateDistribution(Dictionary<string, int> counts, Dictionary<string, Color> colorMap)
        {
            _currentCounts = counts;
            _currentColorMap = colorMap;
            DrawPie();
        }

        public void Clear()
        {
            _currentCounts = null;
            _currentColorMap = null;
            PieCanvas.Children.Clear();
        }

        private void PieChartControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_currentCounts != null && _currentColorMap != null)
            {
                DrawPie();
            }
        }

        private void DrawPie()
        {
            PieCanvas.Children.Clear();

            if (_currentCounts == null || _currentCounts.Count == 0 || _currentColorMap == null)
                return;

            double canvasWidth = PieCanvas.ActualWidth;
            double canvasHeight = PieCanvas.ActualHeight;

            if (canvasWidth < 20 || canvasHeight < 20)
                return;

            // Calculate pie dimensions - fit to available space with padding
            double padding = 10;
            double diameter = Math.Min(canvasWidth, canvasHeight) - (padding * 2);
            double radius = diameter / 2;
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;

            int total = _currentCounts.Values.Sum();
            if (total == 0)
                return;

            double startAngle = -90; // Start from top

            foreach (var kvp in _currentCounts.OrderBy(k => k.Key))
            {
                string category = kvp.Key;
                int count = kvp.Value;
                double percentage = (double)count / total;
                double sweepAngle = percentage * 360;

                if (sweepAngle < 0.5) // Skip tiny slices
                    continue;

                // Get color from color map, or use gray for unknown
                Color sliceColor = _currentColorMap.TryGetValue(category, out var color)
                    ? color
                    : Color.FromRgb(180, 180, 180);

                // Create pie slice
                var slice = CreatePieSlice(centerX, centerY, radius, startAngle, sweepAngle, sliceColor);

                // Add tooltip
                string tooltipText = $"{category}\n{count} cells ({percentage:P1})";
                slice.ToolTip = new ToolTip
                {
                    Content = tooltipText,
                    FontSize = 11
                };

                PieCanvas.Children.Add(slice);
                startAngle += sweepAngle;
            }
        }

        private Path CreatePieSlice(double centerX, double centerY, double radius,
            double startAngle, double sweepAngle, Color fillColor)
        {
            // Convert angles to radians
            double startRad = startAngle * Math.PI / 180;
            double endRad = (startAngle + sweepAngle) * Math.PI / 180;

            // Calculate start and end points
            double startX = centerX + radius * Math.Cos(startRad);
            double startY = centerY + radius * Math.Sin(startRad);
            double endX = centerX + radius * Math.Cos(endRad);
            double endY = centerY + radius * Math.Sin(endRad);

            // Determine if this is a large arc (> 180 degrees)
            bool isLargeArc = sweepAngle > 180;

            // Build the path
            var pathFigure = new PathFigure
            {
                StartPoint = new Point(centerX, centerY),
                IsClosed = true
            };

            // Line from center to start of arc
            pathFigure.Segments.Add(new LineSegment(new Point(startX, startY), true));

            // Arc segment
            pathFigure.Segments.Add(new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            });

            var pathGeometry = new PathGeometry();
            pathGeometry.Figures.Add(pathFigure);

            return new Path
            {
                Data = pathGeometry,
                Fill = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                Cursor = Cursors.Hand
            };
        }
    }
}