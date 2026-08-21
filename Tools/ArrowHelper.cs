using System;
using System.Windows;
using System.Windows.Media;

namespace TraderPen.Tools
{
    public static class ArrowHelper
    {
        public static PathGeometry CreateArrowGeometry(Point start, Point end, double headLength = 15, double headAngle = 25)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = start, IsClosed = false };

            // Linha principal
            figure.Segments.Add(new LineSegment(end, true));

            // Cálculo do ângulo da seta
            double theta = Math.Atan2(end.Y - start.Y, end.X - start.X);

            // Cálculo dos dois pontos da ponta
            double angle1 = theta + (Math.PI / 180) * (180 - headAngle);
            double angle2 = theta - (Math.PI / 180) * (180 - headAngle);

            Point pt1 = new Point(
                end.X + headLength * Math.Cos(angle1),
                end.Y + headLength * Math.Sin(angle1));

            Point pt2 = new Point(
                end.X + headLength * Math.Cos(angle2),
                end.Y + headLength * Math.Sin(angle2));

            // Adiciona as linhas da ponta
            figure.Segments.Add(new LineSegment(pt1, true));
            figure.Segments.Add(new LineSegment(end, false));
            figure.Segments.Add(new LineSegment(pt2, true));

            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}