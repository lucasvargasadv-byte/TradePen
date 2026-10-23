using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TraderPen.History
{
    public class AddMultipleStrokesCommand : IUndoableCommand
    {
        private readonly Canvas _canvas;
        private readonly List<UIElement> _elements;

        public AddMultipleStrokesCommand(Canvas canvas, IEnumerable<UIElement> elements)
        {
            _canvas = canvas;
            _elements = elements.ToList();
        }

        public void Execute()
        {
            foreach (var element in _elements)
            {
                if (!_canvas.Children.Contains(element))
                {
                    _canvas.Children.Add(element);
                }
            }
        }

        public void Undo()
        {
            foreach (var element in _elements)
            {
                if (_canvas.Children.Contains(element))
                {
                    _canvas.Children.Remove(element);
                }
            }
        }
    }
}
