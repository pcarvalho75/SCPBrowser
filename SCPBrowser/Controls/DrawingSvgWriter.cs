// DrawingSvgWriter.cs
// Writes a WPF Drawing (what a DrawingContext recorded) as SVG, so a plot drawn for the screen can be saved as a
// vector figure without a second drawing routine.
// Location: SCPBrowser/Controls/DrawingSvgWriter.cs

using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace SCPBrowser
{
    /// <summary>
    /// Converts the drawings a DrawingContext produces into SVG: groups (transform, opacity, clip), geometries with a
    /// solid fill and pen, and text. Text becomes its glyph outlines, so the file renders exactly like the screen on any
    /// machine, whether or not it has the font; the price is that the words cannot be edited as text in an SVG editor.
    /// Only solid-color brushes are written (gradients and images fall back to no fill), which is all the plots use.
    /// </summary>
    internal static class DrawingSvgWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Write(Drawing drawing, Size size)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"{N(size.Width)}\" height=\"{N(size.Height)}\" viewBox=\"0 0 {N(size.Width)} {N(size.Height)}\">\n");
            int clipId = 0;
            Emit(drawing, sb, 1, ref clipId);
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        private static void Emit(Drawing drawing, StringBuilder sb, int depth, ref int clipId)
        {
            string indent = new string(' ', depth * 2);
            switch (drawing)
            {
                case DrawingGroup group:
                {
                    var attributes = new StringBuilder();
                    if (group.Transform != null && !group.Transform.Value.IsIdentity)
                        attributes.Append($" transform=\"{Matrix(group.Transform.Value)}\"");
                    if (group.Opacity < 1)
                        attributes.Append($" opacity=\"{N(group.Opacity)}\"");
                    if (group.ClipGeometry != null)
                    {
                        string id = $"clip{++clipId}";
                        sb.Append($"{indent}<clipPath id=\"{id}\"><path d=\"{PathData(group.ClipGeometry)}\"{GeometryTransform(group.ClipGeometry)}/></clipPath>\n");
                        attributes.Append($" clip-path=\"url(#{id})\"");
                    }
                    bool wrap = attributes.Length > 0;
                    if (wrap) sb.Append($"{indent}<g{attributes}>\n");
                    foreach (var child in group.Children)
                        Emit(child, sb, wrap ? depth + 1 : depth, ref clipId);
                    if (wrap) sb.Append($"{indent}</g>\n");
                    break;
                }
                case GeometryDrawing geometryDrawing when geometryDrawing.Geometry != null:
                {
                    string d = PathData(geometryDrawing.Geometry);
                    if (d.Length == 0) break;
                    sb.Append($"{indent}<path d=\"{d}\"{GeometryTransform(geometryDrawing.Geometry)}{Fill(geometryDrawing.Brush, geometryDrawing.Geometry)}{Stroke(geometryDrawing.Pen)}/>\n");
                    break;
                }
                case GlyphRunDrawing glyphs when glyphs.GlyphRun != null:
                {
                    var outline = glyphs.GlyphRun.BuildGeometry();
                    string d = PathData(outline);
                    if (d.Length == 0) break;
                    sb.Append($"{indent}<path d=\"{d}\"{GeometryTransform(outline)}{Fill(glyphs.ForegroundBrush, outline)}/>\n");
                    break;
                }
            }
        }

        private static string PathData(Geometry geometry)
        {
            var path = PathGeometry.CreateFromGeometry(geometry);
            var sb = new StringBuilder();
            foreach (var figure in path.Figures)
            {
                sb.Append('M').Append(P(figure.StartPoint));
                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            sb.Append('L').Append(P(line.Point));
                            break;
                        case PolyLineSegment poly:
                            foreach (var point in poly.Points) sb.Append('L').Append(P(point));
                            break;
                        case BezierSegment bezier:
                            sb.Append('C').Append(P(bezier.Point1)).Append(' ').Append(P(bezier.Point2)).Append(' ').Append(P(bezier.Point3));
                            break;
                        case PolyBezierSegment polyBezier:
                            for (int i = 0; i + 2 < polyBezier.Points.Count; i += 3)
                                sb.Append('C').Append(P(polyBezier.Points[i])).Append(' ').Append(P(polyBezier.Points[i + 1])).Append(' ').Append(P(polyBezier.Points[i + 2]));
                            break;
                        case QuadraticBezierSegment quad:
                            sb.Append('Q').Append(P(quad.Point1)).Append(' ').Append(P(quad.Point2));
                            break;
                        case PolyQuadraticBezierSegment polyQuad:
                            for (int i = 0; i + 1 < polyQuad.Points.Count; i += 2)
                                sb.Append('Q').Append(P(polyQuad.Points[i])).Append(' ').Append(P(polyQuad.Points[i + 1]));
                            break;
                        case ArcSegment arc:
                            sb.Append('A').Append(N(arc.Size.Width)).Append(' ').Append(N(arc.Size.Height)).Append(' ')
                              .Append(N(arc.RotationAngle)).Append(' ').Append(arc.IsLargeArc ? '1' : '0').Append(' ')
                              .Append(arc.SweepDirection == SweepDirection.Clockwise ? '1' : '0').Append(' ').Append(P(arc.Point));
                            break;
                    }
                }
                if (figure.IsClosed) sb.Append('Z');
            }
            return sb.ToString();
        }

        private static string GeometryTransform(Geometry geometry)
        {
            var transform = PathGeometry.CreateFromGeometry(geometry).Transform;
            return transform != null && !transform.Value.IsIdentity ? $" transform=\"{Matrix(transform.Value)}\"" : string.Empty;
        }

        private static string Fill(Brush? brush, Geometry geometry)
        {
            string rule = PathGeometry.CreateFromGeometry(geometry).FillRule == FillRule.EvenOdd ? " fill-rule=\"evenodd\"" : string.Empty;
            if (brush is not SolidColorBrush solid)
                return " fill=\"none\"";
            return $" fill=\"{Hex(solid.Color)}\"{Alpha("fill-opacity", solid.Color.A / 255.0 * solid.Opacity)}{rule}";
        }

        private static string Stroke(Pen? pen)
        {
            if (pen?.Brush is not SolidColorBrush solid || pen.Thickness <= 0)
                return string.Empty;
            string cap = pen.StartLineCap switch { PenLineCap.Round => "round", PenLineCap.Square => "square", _ => "butt" };
            string join = pen.LineJoin switch { PenLineJoin.Round => "round", PenLineJoin.Bevel => "bevel", _ => "miter" };
            return $" stroke=\"{Hex(solid.Color)}\" stroke-width=\"{N(pen.Thickness)}\" stroke-linecap=\"{cap}\" stroke-linejoin=\"{join}\"" +
                   Alpha("stroke-opacity", solid.Color.A / 255.0 * solid.Opacity);
        }

        private static string Alpha(string attribute, double alpha) => alpha < 0.999 ? $" {attribute}=\"{N(alpha)}\"" : string.Empty;

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static string Matrix(Matrix m) => $"matrix({N(m.M11)} {N(m.M12)} {N(m.M21)} {N(m.M22)} {N(m.OffsetX)} {N(m.OffsetY)})";

        private static string P(Point p) => N(p.X) + "," + N(p.Y);

        private static string N(double v) => Math.Round(v, 3).ToString("0.###", Inv);
    }
}
