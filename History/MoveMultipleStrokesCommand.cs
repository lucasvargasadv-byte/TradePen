using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace TraderPen.History
{
    // Move um GRUPO de elementos juntos, e permite desfazer/refazer
    // o movimento do grupo inteiro com um único Ctrl+Z / Ctrl+Y.
    public class MoveMultipleStrokesCommand : IUndoableCommand
    {
        private readonly List<UIElement> _elements;
        private readonly Vector _delta;
        private bool _isFirstExecute = true;

        public MoveMultipleStrokesCommand(List<UIElement> elements, Vector delta)
        {
            _elements = elements;
            _delta = delta;
        }

        public void Execute()
        {
            // Na primeira vez, o movimento já foi aplicado visualmente durante o
            // próprio arraste do mouse — não precisamos aplicar de novo aqui.
            // Só a partir de um Redo (Ctrl+Y) é que precisamos reaplicar o delta.
            if (_isFirstExecute)
            {
                _isFirstExecute = false;
                return;
            }

            foreach (var el in _elements)
            {
                ApplyTranslation(el, _delta);
            }
        }

        public void Undo()
        {
            foreach (var el in _elements)
            {
                ApplyTranslation(el, -_delta);
            }
        }

        private static void ApplyTranslation(UIElement element, Vector delta)
        {
            if (element.RenderTransform is TranslateTransform translate)
            {
                translate.X += delta.X;
                translate.Y += delta.Y;
            }
            else if (element.RenderTransform is TransformGroup group)
            {
                var groupTranslate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (groupTranslate == null)
                {
                    groupTranslate = new TranslateTransform();
                    group.Children.Add(groupTranslate);
                }
                groupTranslate.X += delta.X;
                groupTranslate.Y += delta.Y;
            }
        }
    }
}