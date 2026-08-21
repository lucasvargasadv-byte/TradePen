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
                if (el.RenderTransform is TranslateTransform t)
                {
                    t.X += _delta.X;
                    t.Y += _delta.Y;
                }
            }
        }

        public void Undo()
        {
            foreach (var el in _elements)
            {
                if (el.RenderTransform is TranslateTransform t)
                {
                    t.X -= _delta.X;
                    t.Y -= _delta.Y;
                }
            }
        }
    }
}