using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SCPBrowser
{
    /// <summary>
    /// Manages polygon selection and gating operations for scatter plot.
    /// </summary>
    public class SelectionManager
    {
        private readonly List<Point> _polygonPointsScreen;
        private readonly List<Point> _polygonPointsData;
        private readonly Polyline _drawingPolyline;
        private readonly Polygon _selectionPolygon;
        private readonly Ellipse _startPointIndicator;

        private bool _isDrawing;
        private const double CloseThreshold = 15.0;

        public bool IsDrawing => _isDrawing;
        public List<Point> PolygonPointsScreen => _polygonPointsScreen;
        public List<Point> PolygonPointsData => _polygonPointsData;

        public SelectionManager()
        {
            _polygonPointsScreen = new List<Point>();
            _polygonPointsData = new List<Point>();

            _drawingPolyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed
            };

            _selectionPolygon = new Polygon
            {
                Stroke = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(30, 37, 99, 235)),
                Visibility = Visibility.Collapsed
            };

            _startPointIndicator = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed
            };
        }

        public Polyline GetDrawingPolyline() => _drawingPolyline;
        public Polygon GetSelectionPolygon() => _selectionPolygon;
        public Ellipse GetStartPointIndicator() => _startPointIndicator;

        public void StartDrawing(Point startPoint)
        {
            _isDrawing = true;
            _polygonPointsScreen.Clear();
            _polygonPointsScreen.Add(startPoint);

            _drawingPolyline.Points.Clear();
            _drawingPolyline.Points.Add(startPoint);
            _drawingPolyline.Visibility = Visibility.Visible;

            _selectionPolygon.Visibility = Visibility.Collapsed;
            _startPointIndicator.Visibility = Visibility.Visible;
        }

        public void UpdateDrawing(Point currentPoint)
        {
            if (!_isDrawing || _polygonPointsScreen.Count == 0)
                return;

            double distance = Math.Sqrt(
                Math.Pow(currentPoint.X - _polygonPointsScreen.Last().X, 2) +
                Math.Pow(currentPoint.Y - _polygonPointsScreen.Last().Y, 2)
            );

            if (distance > 3)
            {
                _polygonPointsScreen.Add(currentPoint);
                _drawingPolyline.Points.Add(currentPoint);
            }

            double distanceToStart = Math.Sqrt(
                Math.Pow(currentPoint.X - _polygonPointsScreen[0].X, 2) +
                Math.Pow(currentPoint.Y - _polygonPointsScreen[0].Y, 2)
            );

            _startPointIndicator.Fill = distanceToStart < CloseThreshold
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(220, 38, 38));
        }

        public void FinishDrawing()
        {
            _isDrawing = false;
            _startPointIndicator.Visibility = Visibility.Collapsed;
            _drawingPolyline.Visibility = Visibility.Collapsed;

            if (_polygonPointsScreen.Count < 3)
            {
                _polygonPointsScreen.Clear();
                return;
            }

            var simplifiedPoints = SimplifyPolygon(_polygonPointsScreen);
            _polygonPointsScreen.Clear();
            _polygonPointsScreen.AddRange(simplifiedPoints);

            _selectionPolygon.Points.Clear();
            foreach (var point in simplifiedPoints)
                _selectionPolygon.Points.Add(point);

            _selectionPolygon.Visibility = Visibility.Visible;
        }

        public void CancelDrawing()
        {
            _isDrawing = false;
            _drawingPolyline.Visibility = Visibility.Collapsed;
            _startPointIndicator.Visibility = Visibility.Collapsed;
        }

        public void ClearSelection()
        {
            _polygonPointsScreen.Clear();
            _polygonPointsData.Clear();
            _selectionPolygon.Visibility = Visibility.Collapsed;
            _drawingPolyline.Visibility = Visibility.Collapsed;
            _startPointIndicator.Visibility = Visibility.Collapsed;
        }

        public void StoreDataCoordinates(List<Point> dataPoints)
        {
            _polygonPointsData.Clear();
            if (dataPoints == null || dataPoints.Count == 0)
                return;

            _polygonPointsData.AddRange(dataPoints);
        }

        public void RedrawSelectionFromDataCoordinates(Func<Point, Point> dataToScreenConverter)
        {
            if (_polygonPointsData.Count < 3)
                return;

            _polygonPointsScreen.Clear();
            _selectionPolygon.Points.Clear();

            foreach (var dataPoint in _polygonPointsData)
            {
                Point screenPoint = dataToScreenConverter(dataPoint);
                _polygonPointsScreen.Add(screenPoint);
                _selectionPolygon.Points.Add(screenPoint);
            }

            _selectionPolygon.Visibility = Visibility.Visible;
        }

        public bool IsPointInSelection(Point point)
        {
            return IsPointInPolygon(point, _polygonPointsScreen);
        }

        public void SetPolygonPointsData(List<Point> points)
        {
            _polygonPointsData.Clear();
            if (points == null || points.Count == 0)
                return;

            _polygonPointsData.AddRange(points);
            _selectionPolygon.Points.Clear();

            foreach (var p in points)
                _selectionPolygon.Points.Add(p);

            _selectionPolygon.Visibility = Visibility.Visible;
        }

        public void SetPolygonPointsScreen(List<Point> points)
        {
            _polygonPointsScreen.Clear();
            if (points == null || points.Count == 0)
                return;

            _polygonPointsScreen.AddRange(points);
            _selectionPolygon.Points.Clear();

            foreach (var p in points)
                _selectionPolygon.Points.Add(p);

            _selectionPolygon.Visibility = Visibility.Visible;
        }

        private List<Point> SimplifyPolygon(List<Point> points, double tolerance = 3.0)
        {
            if (points.Count < 3)
                return points;

            var simplified = new List<Point> { points[0] };

            for (int i = 1; i < points.Count - 1; i++)
            {
                double distance = Math.Sqrt(
                    Math.Pow(points[i].X - simplified.Last().X, 2) +
                    Math.Pow(points[i].Y - simplified.Last().Y, 2)
                );

                if (distance >= tolerance)
                    simplified.Add(points[i]);
            }

            simplified.Add(points.Last());
            return simplified;
        }

        private bool IsPointInPolygon(Point point, List<Point> polygon)
        {
            if (polygon.Count < 3)
                return false;

            int intersections = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                Point p1 = polygon[i];
                Point p2 = polygon[(i + 1) % polygon.Count];

                if (p1.Y == p2.Y)
                    continue;

                if (point.Y < Math.Min(p1.Y, p2.Y) || point.Y >= Math.Max(p1.Y, p2.Y))
                    continue;

                double x = p1.X + (point.Y - p1.Y) * (p2.X - p1.X) / (p2.Y - p1.Y);

                if (x > point.X)
                    intersections++;
            }

            return (intersections % 2) == 1;
        }
    }
}
