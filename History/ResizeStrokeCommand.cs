using System.Windows;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TraderPen.History
{
    public class ResizeStrokeCommand : IUndoableCommand
    {
        private readonly UIElement _element;
        private readonly Transform? _before;
        private readonly Transform _after;
        private readonly Dictionary<Shape, double> _beforeStrokeThickness;
        private readonly Dictionary<Shape, double> _afterStrokeThickness;
        private readonly Dictionary<TextBlock, double> _beforeFontSizes;
        private readonly Dictionary<TextBlock, double> _afterFontSizes;
        private readonly Dictionary<TextBlock, Transform?> _beforeTextTransforms;
        private readonly Dictionary<TextBlock, Transform?> _afterTextTransforms;

        public ResizeStrokeCommand(
            UIElement element,
            Transform? before,
            Transform after,
            Dictionary<Shape, double> beforeStrokeThickness,
            Dictionary<Shape, double> afterStrokeThickness,
            Dictionary<TextBlock, double> beforeFontSizes,
            Dictionary<TextBlock, double> afterFontSizes,
            Dictionary<TextBlock, Transform?> beforeTextTransforms,
            Dictionary<TextBlock, Transform?> afterTextTransforms)
        {
            _element = element;
            _before = before;
            _after = after;
            _beforeStrokeThickness = beforeStrokeThickness;
            _afterStrokeThickness = afterStrokeThickness;
            _beforeFontSizes = beforeFontSizes;
            _afterFontSizes = afterFontSizes;
            _beforeTextTransforms = beforeTextTransforms;
            _afterTextTransforms = afterTextTransforms;
        }

        public void Execute()
        {
            _element.RenderTransform = _after.Clone();
            ApplyStrokeThickness(_afterStrokeThickness);
            ApplyFontSizes(_afterFontSizes);
            ApplyTextTransforms(_afterTextTransforms);
        }

        public void Undo()
        {
            _element.RenderTransform = _before?.Clone();
            ApplyStrokeThickness(_beforeStrokeThickness);
            ApplyFontSizes(_beforeFontSizes);
            ApplyTextTransforms(_beforeTextTransforms);
        }

        private static void ApplyStrokeThickness(Dictionary<Shape, double> values)
        {
            foreach (var pair in values)
            {
                pair.Key.StrokeThickness = pair.Value;
            }
        }

        private static void ApplyFontSizes(Dictionary<TextBlock, double> values)
        {
            foreach (var pair in values)
            {
                pair.Key.FontSize = pair.Value;
            }
        }

        private static void ApplyTextTransforms(Dictionary<TextBlock, Transform?> values)
        {
            foreach (var pair in values)
            {
                pair.Key.RenderTransform = pair.Value?.Clone();
            }
        }
    }
}
