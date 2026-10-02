using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace TraderPen.History
{
    public class EditLineEndpointCommand : IUndoableCommand
    {
        private readonly Line _line;
        private readonly bool _isStart;
        private readonly Point _before;
        private readonly Point _after;
        private readonly TextBlock? _label;
        private readonly Point _beforeLabelPosition;
        private readonly Point _afterLabelPosition;

        public EditLineEndpointCommand(
            Line line,
            bool isStart,
            Point before,
            Point after,
            TextBlock? label = null,
            Point beforeLabelPosition = default,
            Point afterLabelPosition = default)
        {
            _line = line;
            _isStart = isStart;
            _before = before;
            _after = after;
            _label = label;
            _beforeLabelPosition = beforeLabelPosition;
            _afterLabelPosition = afterLabelPosition;
        }

        public void Execute()
        {
            SetEndpoint(_after);
            SetLabelPosition(_afterLabelPosition);
        }

        public void Undo()
        {
            SetEndpoint(_before);
            SetLabelPosition(_beforeLabelPosition);
        }

        private void SetLabelPosition(Point position)
        {
            if (_label == null) return;

            Canvas.SetLeft(_label, position.X);
            Canvas.SetTop(_label, position.Y);
        }

        private void SetEndpoint(Point point)
        {
            if (_isStart)
            {
                _line.X1 = point.X;
                _line.Y1 = point.Y;
            }
            else
            {
                _line.X2 = point.X;
                _line.Y2 = point.Y;
            }
        }
    }
}
