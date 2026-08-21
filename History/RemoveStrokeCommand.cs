using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows;

namespace TraderPen.History
{
    public class RemoveStrokeCommand : IUndoableCommand
    {
        private readonly Canvas _canvas;
        private readonly UIElement _element;

        public RemoveStrokeCommand(Canvas canvas, UIElement element)
        {
            _canvas = canvas;
            _element = element;
        }

        public void Execute()
        {
            if (_canvas.Children.Contains(_element))
            {
                _canvas.Children.Remove(_element);
            }
        }

        public void Undo()
        {
            if (!_canvas.Children.Contains(_element))
            {
                _canvas.Children.Add(_element);
            }
        }
    }
}