using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace TraderPen.History
{
    public class ChangeSelectedSizeCommand : IUndoableCommand
    {
        private readonly Dictionary<Shape, double> _beforeStrokeThickness;
        private readonly Dictionary<Shape, double> _afterStrokeThickness;
        private readonly Dictionary<TextBlock, double> _beforeFontSizes;
        private readonly Dictionary<TextBlock, double> _afterFontSizes;
        private readonly Dictionary<Rectangle, double> _beforeCandleWidths;
        private readonly Dictionary<Rectangle, double> _afterCandleWidths;
        private readonly Dictionary<Rectangle, double> _beforeCandleLefts;
        private readonly Dictionary<Rectangle, double> _afterCandleLefts;
        private readonly Dictionary<Line, (double X1, double Y1, double X2, double Y2)> _beforeWickPoints;
        private readonly Dictionary<Line, (double X1, double Y1, double X2, double Y2)> _afterWickPoints;

        public ChangeSelectedSizeCommand(
            Dictionary<Shape, double> beforeStrokeThickness,
            Dictionary<Shape, double> afterStrokeThickness,
            Dictionary<TextBlock, double> beforeFontSizes,
            Dictionary<TextBlock, double> afterFontSizes,
            Dictionary<Rectangle, double> beforeCandleWidths,
            Dictionary<Rectangle, double> afterCandleWidths,
            Dictionary<Rectangle, double> beforeCandleLefts,
            Dictionary<Rectangle, double> afterCandleLefts,
            Dictionary<Line, (double X1, double Y1, double X2, double Y2)> beforeWickPoints,
            Dictionary<Line, (double X1, double Y1, double X2, double Y2)> afterWickPoints)
        {
            _beforeStrokeThickness = beforeStrokeThickness;
            _afterStrokeThickness = afterStrokeThickness;
            _beforeFontSizes = beforeFontSizes;
            _afterFontSizes = afterFontSizes;
            _beforeCandleWidths = beforeCandleWidths;
            _afterCandleWidths = afterCandleWidths;
            _beforeCandleLefts = beforeCandleLefts;
            _afterCandleLefts = afterCandleLefts;
            _beforeWickPoints = beforeWickPoints;
            _afterWickPoints = afterWickPoints;
        }

        public void Execute()
        {
            Apply(_afterStrokeThickness, _afterFontSizes, _afterCandleWidths, _afterCandleLefts, _afterWickPoints);
        }

        public void Undo()
        {
            Apply(_beforeStrokeThickness, _beforeFontSizes, _beforeCandleWidths, _beforeCandleLefts, _beforeWickPoints);
        }

        private static void Apply(
            Dictionary<Shape, double> strokeThickness,
            Dictionary<TextBlock, double> fontSizes,
            Dictionary<Rectangle, double> candleWidths,
            Dictionary<Rectangle, double> candleLefts,
            Dictionary<Line, (double X1, double Y1, double X2, double Y2)> wickPoints)
        {
            foreach (var pair in strokeThickness)
            {
                pair.Key.StrokeThickness = pair.Value;
            }

            foreach (var pair in fontSizes)
            {
                pair.Key.FontSize = pair.Value;
            }

            foreach (var pair in candleWidths)
            {
                pair.Key.Width = pair.Value;
            }

            foreach (var pair in candleLefts)
            {
                Canvas.SetLeft(pair.Key, pair.Value);
            }

            foreach (var pair in wickPoints)
            {
                pair.Key.X1 = pair.Value.X1;
                pair.Key.Y1 = pair.Value.Y1;
                pair.Key.X2 = pair.Value.X2;
                pair.Key.Y2 = pair.Value.Y2;
            }
        }
    }
}
