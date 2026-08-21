using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TraderPen.History
{
    public class MoveStrokeCommand : IUndoableCommand
    {
        private readonly UIElement _element;
        private readonly Vector _displacement;

        public MoveStrokeCommand(UIElement element, Vector displacement)
        {
            _element = element;
            _displacement = displacement;
        }

        public void Execute()
        {
            ApplyTranslation(_displacement);
        }

        public void Undo()
        {
            ApplyTranslation(-_displacement);
        }

        private void ApplyTranslation(Vector delta)
        {
            if (_element.RenderTransform is TranslateTransform translate)
            {
                translate.X += delta.X;
                translate.Y += delta.Y;
            }
            else if (_element.RenderTransform is TransformGroup group)
            {
                var t = group.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (t != null)
                {
                    t.X += delta.X;
                    t.Y += delta.Y;
                }
            }
            else
            {
                _element.RenderTransform = new TranslateTransform(delta.X, delta.Y);
            }
        }
    }
}