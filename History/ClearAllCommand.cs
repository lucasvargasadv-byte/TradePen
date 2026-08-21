using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TraderPen.History
{
    public class ClearAllCommand : IUndoableCommand
    {
        private readonly Canvas _canvas;
        private readonly List<UIElement> _removedElements;

        public ClearAllCommand(Canvas canvas)
        {
            _canvas = canvas;
            _removedElements = canvas.Children.OfType<UIElement>().ToList();
        }

        public void Execute()
        {
            _canvas.Children.Clear();
        }

        public void Undo()
        {
            foreach (var el in _removedElements)
            {
                if (!_canvas.Children.Contains(el))
                    _canvas.Children.Add(el);
            }
        }
    }
}