using System.Windows;
using System.Windows.Controls;

namespace TraderPen.History
{
    public class AddStrokeCommand : IUndoableCommand
    {
        private readonly Canvas _canvas;
        private readonly UIElement _element;

        public AddStrokeCommand(Canvas canvas, UIElement element)
        {
            _canvas = canvas;
            _element = element;
        }

        public void Execute()
        {
            if (!_canvas.Children.Contains(_element))
            {
                _canvas.Children.Add(_element);
            }
        }

        public void Undo()
        {
            if (_canvas.Children.Contains(_element))
            {
                _canvas.Children.Remove(_element);
            }
        }
    }
}