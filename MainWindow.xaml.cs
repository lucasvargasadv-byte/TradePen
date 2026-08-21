using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using TraderPen.History;
using TraderPen.Input;
using TraderPen.Overlay;
using TraderPen.Tools;

namespace TraderPen
{
    public partial class MainWindow : Window
    {
        private enum BoardMode
        {
            Transparent,
            Whiteboard,
            Blackboard
        }

        private HotkeyManager? _hotkeys;
        private bool _drawingMode = false;

        private ToolType _currentTool = ToolType.Pen;
        private Color _currentColor = (Color)ColorConverter.ConvertFromString("#FF4444");
        private double _currentThickness = 4; // Média por padrão
        private Point _startPoint;

        private UIElement? _currentShape;

        private BoardMode _boardMode = BoardMode.Transparent;

        // ---- Seleção múltipla / Arraste em grupo ----
        private readonly List<UIElement> _selectedElements = new();
        private readonly List<Rectangle> _selectionBoxes = new();
        private Point _dragStartPoint;
        private Vector _totalDragDisplacement;
        private bool _isDraggingElement = false;

        // ---- Marquee (caixa de seleção por área) ----
        private bool _isMarqueeSelecting = false;
        private Point _marqueeStartPoint;
        private Rectangle? _marqueeBox;

        private readonly List<Point> _pathPoints = new();
        private Grid? _activePathGroup;
        private Path? _activePathElement;

        private readonly UndoManager _undoManager = new();

        private bool _isDraggingToolbar = false;
        private Point _toolbarDragStart;

        // ---- Minimizar toolbar (bolinha flutuante estilo Epic Pen) ----
        private bool _toolbarMinimized = false;
        private bool _isDraggingBubble = false;
        private bool _bubbleDragged = false;
        private Point _bubbleDragStart;

        public MainWindow()
        {
            InitializeComponent();

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            SourceInitialized += MainWindow_SourceInitialized;
            KeyDown += MainWindow_KeyDown;

            DrawCanvas.MouseRightButtonDown += DrawCanvas_MouseRightButtonDown;
            DrawCanvas.MouseRightButtonUp += DrawCanvas_MouseRightButtonUp;
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.EnableClickThrough(hwnd);

            _hotkeys = new HotkeyManager(this);
            _hotkeys.ToggleModeRequested += ToggleMode;

            HighlightToolButton(_currentTool.ToString());
            HighlightColorButton("Red");
            HighlightThicknessButton("4");
        }

        private void ToggleMode()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _drawingMode = !_drawingMode;

            if (_drawingMode)
            {
                NativeMethods.DisableClickThrough(hwnd);
                this.Activate();
                this.Focus();

                ModeIndicator.Text = $"DRAWING MODE | Ferramenta: {_currentTool} (Pressione F9 para soltar o mouse)";

                // Restaura a barra do jeito que o usuário deixou: minimizada (bolinha) ou aberta.
                if (_toolbarMinimized)
                {
                    MinimizedBubble.Visibility = Visibility.Visible;
                }
                else
                {
                    ToolbarBorder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                FinalizePathDrawing();
                ClearSelection();
                NativeMethods.EnableClickThrough(hwnd);
                ModeIndicator.Text = "MOUSE MODE (Pressione F9 para desenhar)";
            }

            UpdateCrosshairVisibility();
        }

        private void UpdateCrosshairVisibility()
        {
            bool showCrosshair = _drawingMode && _currentTool != ToolType.Select;
            CrosshairV.Visibility = showCrosshair ? Visibility.Visible : Visibility.Collapsed;
            CrosshairH.Visibility = showCrosshair ? Visibility.Visible : Visibility.Collapsed;
            DrawCanvas.Cursor = showCrosshair ? Cursors.None : Cursors.Arrow;
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_drawingMode) return;
            if (e.OriginalSource is TextBox) return;

            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                if (_pathPoints.Count > 0)
                {
                    FinalizePathDrawing();
                    return;
                }
            }

            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ClearSelection();
                _undoManager.Undo();
            }
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ClearSelection();
                _undoManager.Redo();
            }
            else if (e.Key == Key.Escape)
            {
                ClearSelection();
            }
            else if (e.Key == Key.S) SwitchTool(ToolType.Select);
            else if (e.Key == Key.D1 || e.Key == Key.NumPad1) SwitchTool(ToolType.Pen);
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2) SwitchTool(ToolType.Highlighter);
            else if (e.Key == Key.D3 || e.Key == Key.NumPad3) SwitchTool(ToolType.Rectangle);
            else if (e.Key == Key.D4 || e.Key == Key.NumPad4) SwitchTool(ToolType.Ellipse);
            else if (e.Key == Key.D5 || e.Key == Key.NumPad5) SwitchTool(ToolType.Line);
            else if (e.Key == Key.D6 || e.Key == Key.NumPad6) SwitchTool(ToolType.Arrow);
            else if (e.Key == Key.D7 || e.Key == Key.NumPad7) SwitchTool(ToolType.Path);
            else if (e.Key == Key.D8 || e.Key == Key.NumPad8) SwitchTool(ToolType.Candle);
            else if (e.Key == Key.D9 || e.Key == Key.NumPad9) SwitchTool(ToolType.FVG);
            else if (e.Key == Key.T) SwitchTool(ToolType.Text);
            else if (e.Key == Key.E) SwitchTool(ToolType.Eraser);
            else if (e.Key == Key.M) SwitchTool(ToolType.Select);
            else if (e.Key == Key.R) ChangeColor("#FF4444", "Red");
            else if (e.Key == Key.G) ChangeColor("#00C853", "Green");
        }

        private void SwitchTool(ToolType tool)
        {
            if (_currentTool == ToolType.Path && tool != ToolType.Path)
            {
                FinalizePathDrawing();
            }

            if (_currentTool == ToolType.Select && tool != ToolType.Select)
            {
                ClearSelection();
            }

            _currentTool = tool;
            ModeIndicator.Text = $"DRAWING MODE | Ferramenta: {_currentTool} (Pressione F9 para soltar o mouse)";
            HighlightToolButton(tool.ToString());
            UpdateCrosshairVisibility();
        }

        private void HighlightToolButton(string tagValue)
        {
            foreach (var child in ToolContainer.Children)
            {
                if (child is Button btn)
                {
                    bool isSelected = btn.Tag?.ToString() == tagValue;
                    btn.BorderBrush = isSelected ? Brushes.Cyan : (Brush)new BrushConverter().ConvertFrom("#444444")!;
                    btn.BorderThickness = new Thickness(isSelected ? 2 : 1);
                    btn.Background = isSelected ? (Brush)new BrushConverter().ConvertFrom("#005A9E")! : (Brush)new BrushConverter().ConvertFrom("#2A2A2A")!;
                }
            }
        }

        private void HighlightColorButton(string tagValue)
        {
            foreach (var child in ColorContainer.Children)
            {
                if (child is Button btn)
                {
                    bool isSelected = btn.Tag?.ToString() == tagValue;
                    btn.BorderBrush = isSelected ? Brushes.White : (Brush)new BrushConverter().ConvertFrom("#555555")!;
                    btn.BorderThickness = new Thickness(isSelected ? 2 : 1);
                }
            }
        }

        private void HighlightThicknessButton(string tagValue)
        {
            foreach (var child in ThicknessContainer.Children)
            {
                if (child is Button btn)
                {
                    bool isSelected = btn.Tag?.ToString() == tagValue;
                    btn.BorderBrush = isSelected ? Brushes.Cyan : (Brush)new BrushConverter().ConvertFrom("#444444")!;
                    btn.BorderThickness = new Thickness(isSelected ? 2 : 1);
                    btn.Background = isSelected ? (Brush)new BrushConverter().ConvertFrom("#005A9E")! : (Brush)new BrushConverter().ConvertFrom("#2A2A2A")!;
                }
            }
        }

        private SolidColorBrush GetCurrentBrush()
        {
            if (_currentTool == ToolType.Highlighter)
            {
                var transparentColor = Color.FromArgb(100, _currentColor.R, _currentColor.G, _currentColor.B);
                return new SolidColorBrush(transparentColor);
            }
            return new SolidColorBrush(_currentColor);
        }

        private double GetEffectiveThickness()
        {
            return _currentTool == ToolType.Highlighter ? _currentThickness * 4 : _currentThickness;
        }

        private double GetCandleWidth()
        {
            return _currentThickness switch
            {
                2 => 12,
                8 => 40,
                _ => 24
            };
        }

        private void BoardModeButton_Click(object sender, RoutedEventArgs e)
        {
            _boardMode = _boardMode switch
            {
                BoardMode.Transparent => BoardMode.Whiteboard,
                BoardMode.Whiteboard => BoardMode.Blackboard,
                _ => BoardMode.Transparent
            };

            switch (_boardMode)
            {
                case BoardMode.Whiteboard:
                    BoardBackground.Fill = Brushes.White;
                    BoardModeIcon.Text = "⬜";
                    BoardModeLabel.Text = "WHITEBOARD";
                    break;
                case BoardMode.Blackboard:
                    BoardBackground.Fill = Brushes.Black;
                    BoardModeIcon.Text = "⬛";
                    BoardModeLabel.Text = "BLACKBOARD";
                    break;
                default:
                    BoardBackground.Fill = Brushes.Transparent;
                    BoardModeIcon.Text = "🖥️";
                    BoardModeLabel.Text = "TRANSP.";
                    break;
            }
        }

        // =========================================================
        //  SELEÇÃO (única, múltipla via Ctrl, e por área/marquee)
        // =========================================================

        private bool IsSelectionVisual(UIElement el)
        {
            return _selectionBoxes.Contains(el) || el == _marqueeBox;
        }

        private UIElement? FindDrawingElementAtPoint(UIElement? clickedSource)
        {
            if (clickedSource == null || clickedSource == DrawCanvas) return null;
            if (IsSelectionVisual(clickedSource)) return null;

            UIElement target = clickedSource;
            while (VisualTreeHelper.GetParent(target) is UIElement parent && parent != DrawCanvas)
            {
                target = parent;
            }

            if (IsSelectionVisual(target)) return null;
            return target;
        }

        private void EnsureTranslateTransform(UIElement element)
        {
            if (!(element.RenderTransform is TranslateTransform))
            {
                element.RenderTransform = new TranslateTransform();
            }
        }

        private void SelectSingle(UIElement element)
        {
            ClearSelection();
            EnsureTranslateTransform(element);
            _selectedElements.Add(element);
            UpdateSelectionBoxes();
        }

        private void ToggleSelection(UIElement element)
        {
            if (_selectedElements.Contains(element))
            {
                _selectedElements.Remove(element);
            }
            else
            {
                EnsureTranslateTransform(element);
                _selectedElements.Add(element);
            }
            UpdateSelectionBoxes();
        }

        // Calcula os limites (bounds) locais de um elemento, servindo tanto para
        // containers com filhos (Grid do FVG) quanto para formas "folha" que não
        // têm filhos visuais (Rectangle, Line, Ellipse, Path — candle, linha,
        // círculo, seta, caneta...), cujo desenho só aparece em GetContentBounds.
        // IMPORTANTE: esses bounds ainda são relativos à origem própria do
        // elemento (0,0) — NÃO incluem Canvas.Left/Canvas.Top. Para comparar
        // com a área do laço de seleção, use GetElementCanvasBounds abaixo.
        private Rect GetElementLocalBounds(UIElement element)
        {
            Rect contentBounds = VisualTreeHelper.GetContentBounds(element);
            Rect descendantBounds = VisualTreeHelper.GetDescendantBounds(element);

            Rect combined = Rect.Union(contentBounds, descendantBounds);

            if (!combined.IsEmpty) return combined;

            // Último recurso: usa o tamanho real do elemento (funciona bem pra
            // TextBlock, TextBox, Rectangle com Width/Height explícitos, etc.)
            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                return new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);
            }

            return Rect.Empty;
        }

        // Soma o Canvas.Left/Canvas.Top real à posição local do elemento.
        // Necessário porque GetContentBounds/GetDescendantBounds retornam
        // bounds relativos à origem própria do elemento (0,0), e não à
        // posição real dentro do DrawCanvas. Elementos como Rectangle
        // (retângulo, elipse, candle) e o Grid do FVG usam Canvas.Left/Top
        // pra se posicionar; Line/Polyline/Path já guardam coordenadas
        // absolutas do Canvas, então o offset ali é 0 e não afeta nada.
        private Rect GetElementCanvasBounds(UIElement element)
        {
            Rect localBounds = GetElementLocalBounds(element);
            if (localBounds.IsEmpty) return Rect.Empty;

            double left = Canvas.GetLeft(element);
            double top = Canvas.GetTop(element);

            double offsetX = double.IsNaN(left) ? 0 : left;
            double offsetY = double.IsNaN(top) ? 0 : top;

            localBounds.Offset(offsetX, offsetY);
            return localBounds;
        }

        private void SelectElementsInRect(Rect area, bool addToExisting)
        {
            if (!addToExisting)
            {
                ClearSelection();
            }

            foreach (UIElement child in DrawCanvas.Children.OfType<UIElement>().ToList())
            {
                if (IsSelectionVisual(child)) continue;
                if (child is TextBox) continue; // não seleciona caixa de texto ainda em edição

                Rect bounds = GetElementCanvasBounds(child);
                if (bounds.IsEmpty) continue;

                if (child.RenderTransform is Transform transform)
                {
                    bounds = transform.TransformBounds(bounds);
                }

                if (area.IntersectsWith(bounds) && !_selectedElements.Contains(child))
                {
                    EnsureTranslateTransform(child);
                    _selectedElements.Add(child);
                }
            }

            UpdateSelectionBoxes();
        }

        private void ClearSelection()
        {
            foreach (var box in _selectionBoxes)
            {
                DrawCanvas.Children.Remove(box);
            }
            _selectionBoxes.Clear();
            _selectedElements.Clear();
            _isDraggingElement = false;
        }

        private void UpdateSelectionBoxes()
        {
            foreach (var box in _selectionBoxes)
            {
                DrawCanvas.Children.Remove(box);
            }
            _selectionBoxes.Clear();

            foreach (var el in _selectedElements)
            {
                Rect bounds = GetElementCanvasBounds(el);

                if (el.RenderTransform is Transform transform)
                {
                    bounds = transform.TransformBounds(bounds);
                }

                var box = new Rectangle
                {
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(box, bounds.Left - 4);
                Canvas.SetTop(box, bounds.Top - 4);
                box.Width = Math.Max(8, bounds.Width + 8);
                box.Height = Math.Max(8, bounds.Height + 8);

                DrawCanvas.Children.Add(box);
                _selectionBoxes.Add(box);
            }
        }

        private void ApplyTranslationToElement(UIElement element, Vector delta)
        {
            if (element.RenderTransform is TranslateTransform translate)
            {
                translate.X += delta.X;
                translate.Y += delta.Y;
            }
            else
            {
                element.RenderTransform = new TranslateTransform(delta.X, delta.Y);
            }
        }

        // =========================================================

        private void DrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_drawingMode) return;

            var currentClick = e.GetPosition(DrawCanvas);

            // Se clicar fora de um TextBox ativo, confirma a edição dele
            var activeTextBox = DrawCanvas.Children.OfType<TextBox>().FirstOrDefault();
            if (activeTextBox != null && !activeTextBox.IsMouseOver)
            {
                CommitTextBox(activeTextBox);
            }

            if (_currentTool == ToolType.Select)
            {
                bool ctrlHeld = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                UIElement? hitTarget = FindDrawingElementAtPoint(e.OriginalSource as UIElement);

                if (hitTarget != null)
                {
                    if (ctrlHeld)
                    {
                        ToggleSelection(hitTarget);
                    }
                    else if (!_selectedElements.Contains(hitTarget))
                    {
                        // Clicou em algo fora da seleção atual (sem Ctrl): troca a seleção pra só esse item.
                        SelectSingle(hitTarget);
                    }
                    // Se já estava selecionado e sem Ctrl, mantém o grupo inteiro (pra poder arrastar todos juntos).

                    if (_selectedElements.Count > 0)
                    {
                        _isDraggingElement = true;
                        _dragStartPoint = currentClick;
                        _totalDragDisplacement = new Vector(0, 0);
                        DrawCanvas.CaptureMouse();
                    }
                }
                else
                {
                    if (!ctrlHeld)
                    {
                        ClearSelection();
                    }

                    // Começa a "caixa de laço" (marquee) pra selecionar por área.
                    _isMarqueeSelecting = true;
                    _marqueeStartPoint = currentClick;
                    _marqueeBox = new Rectangle
                    {
                        Stroke = Brushes.DeepSkyBlue,
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 3, 3 },
                        Fill = new SolidColorBrush(Color.FromArgb(40, 41, 182, 246)),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(_marqueeBox, _marqueeStartPoint.X);
                    Canvas.SetTop(_marqueeBox, _marqueeStartPoint.Y);
                    DrawCanvas.Children.Add(_marqueeBox);
                    DrawCanvas.CaptureMouse();
                }
                return;
            }

            if (_currentTool == ToolType.Eraser)
            {
                ApplyEraserAtPoint(currentClick);
                return;
            }

            if (_currentTool == ToolType.Path)
            {
                if (e.ClickCount == 2)
                {
                    FinalizePathDrawing();
                    return;
                }

                HandlePathClick(currentClick);
                return;
            }

            if (_pathPoints.Count > 0)
            {
                FinalizePathDrawing();
            }

            _startPoint = currentClick;

            if (_currentTool == ToolType.Text)
            {
                CreateTextBox(_startPoint);
                return;
            }

            var brush = GetCurrentBrush();
            double thickness = GetEffectiveThickness();

            switch (_currentTool)
            {
                case ToolType.Pen:
                case ToolType.Highlighter:
                    var polyline = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    polyline.Points.Add(_startPoint);
                    _currentShape = polyline;
                    break;

                case ToolType.Line:
                    _currentShape = new Line
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        X1 = _startPoint.X,
                        Y1 = _startPoint.Y,
                        X2 = _startPoint.X,
                        Y2 = _startPoint.Y,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    break;

                case ToolType.Arrow:
                    _currentShape = new Path
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        Data = ArrowHelper.CreateArrowGeometry(_startPoint, _startPoint)
                    };
                    break;

                case ToolType.Rectangle:
                    _currentShape = new Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        Fill = Brushes.Transparent
                    };
                    Canvas.SetLeft(_currentShape, _startPoint.X);
                    Canvas.SetTop(_currentShape, _startPoint.Y);
                    break;

                case ToolType.Ellipse:
                    _currentShape = new Ellipse
                    {
                        Stroke = brush,
                        StrokeThickness = thickness,
                        Fill = Brushes.Transparent
                    };
                    Canvas.SetLeft(_currentShape, _startPoint.X);
                    Canvas.SetTop(_currentShape, _startPoint.Y);
                    break;

                case ToolType.Candle:
                    var candleRect = new Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = 1,
                        Fill = brush,
                        Tag = "CANDLE_BODY"
                    };
                    double candleWidth = GetCandleWidth();
                    Canvas.SetLeft(candleRect, _startPoint.X - (candleWidth / 2));
                    Canvas.SetTop(candleRect, _startPoint.Y);
                    _currentShape = candleRect;
                    break;

                case ToolType.FVG:
                    _currentShape = CreateFVGGroup(_startPoint, _startPoint);
                    break;
            }

            if (_currentShape != null)
            {
                DrawCanvas.Children.Add(_currentShape);
                DrawCanvas.CaptureMouse();
            }
        }

        private void CreateTextBox(Point position)
        {
            var textBox = new TextBox
            {
                FontSize = 14 + (_currentThickness * 2),
                Foreground = GetCurrentBrush(),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Cyan,
                BorderThickness = new Thickness(1),
                MinWidth = 60,
                Padding = new Thickness(2),
                AcceptsReturn = true
            };

            Canvas.SetLeft(textBox, position.X);
            Canvas.SetTop(textBox, position.Y);

            textBox.LostFocus += (s, e) => CommitTextBox(textBox);
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0))
                {
                    CommitTextBox(textBox);
                    e.Handled = true;
                }
            };

            DrawCanvas.Children.Add(textBox);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                textBox.Focus();
                Keyboard.Focus(textBox);
            }));
        }

        private void CommitTextBox(TextBox textBox)
        {
            if (!DrawCanvas.Children.Contains(textBox)) return;

            string text = textBox.Text.Trim();
            double left = Canvas.GetLeft(textBox);
            double top = Canvas.GetTop(textBox);

            DrawCanvas.Children.Remove(textBox);

            if (string.IsNullOrWhiteSpace(text)) return;

            var textBlock = new TextBlock
            {
                Text = text,
                FontSize = textBox.FontSize,
                Foreground = textBox.Foreground,
                FontWeight = FontWeights.Bold,
                Background = Brushes.Transparent,
                IsHitTestVisible = true
            };

            Canvas.SetLeft(textBlock, left);
            Canvas.SetTop(textBlock, top);

            var cmd = new AddStrokeCommand(DrawCanvas, textBlock);
            _undoManager.ExecuteCommand(cmd);
        }

        private void ApplyEraserAtPoint(Point erasePoint)
        {
            double eraserRadius = Math.Max(12, GetEffectiveThickness() * 3);
            var elementsToRemove = new List<UIElement>();
            var elementsToAdd = new List<UIElement>();

            foreach (UIElement child in DrawCanvas.Children.OfType<UIElement>().ToList())
            {
                if (IsSelectionVisual(child)) continue;

                if (child is TextBox activeBox)
                {
                    CommitTextBox(activeBox);
                    continue;
                }

                if (child is Polyline polyline)
                {
                    var currentSegment = new PointCollection();
                    bool segmentSplit = false;

                    foreach (Point pt in polyline.Points)
                    {
                        if ((pt - erasePoint).Length < eraserRadius)
                        {
                            segmentSplit = true;
                            if (currentSegment.Count > 1)
                            {
                                elementsToAdd.Add(CreatePartialPolyline(polyline, currentSegment));
                            }
                            currentSegment = new PointCollection();
                        }
                        else
                        {
                            currentSegment.Add(pt);
                        }
                    }

                    if (segmentSplit)
                    {
                        elementsToRemove.Add(polyline);
                        if (currentSegment.Count > 1)
                        {
                            elementsToAdd.Add(CreatePartialPolyline(polyline, currentSegment));
                        }
                    }
                }
                else
                {
                    Rect bounds = GetElementCanvasBounds(child);
                    if (child.RenderTransform is Transform transform)
                    {
                        bounds = transform.TransformBounds(bounds);
                    }

                    bounds.Inflate(eraserRadius, eraserRadius);
                    Point ptRelativeToElement = DrawCanvas.TranslatePoint(erasePoint, child);

                    if (bounds.Contains(erasePoint) || child.InputHitTest(ptRelativeToElement) != null)
                    {
                        if (_selectedElements.Contains(child)) ClearSelection();
                        elementsToRemove.Add(child);
                    }
                }
            }

            foreach (var el in elementsToRemove)
            {
                var cmd = new RemoveStrokeCommand(DrawCanvas, el);
                _undoManager.ExecuteCommand(cmd);
            }

            foreach (var el in elementsToAdd)
            {
                var cmd = new AddStrokeCommand(DrawCanvas, el);
                _undoManager.ExecuteCommand(cmd);
            }
        }

        private Polyline CreatePartialPolyline(Polyline original, PointCollection points)
        {
            return new Polyline
            {
                Stroke = original.Stroke,
                StrokeThickness = original.StrokeThickness,
                StrokeLineJoin = original.StrokeLineJoin,
                StrokeStartLineCap = original.StrokeStartLineCap,
                StrokeEndLineCap = original.StrokeEndLineCap,
                Points = points,
                RenderTransform = original.RenderTransform != null ? original.RenderTransform.Clone() : new TranslateTransform()
            };
        }

        private void HandlePathClick(Point clickPoint)
        {
            var brush = GetCurrentBrush();
            double thickness = GetEffectiveThickness();

            if (_pathPoints.Count == 0)
            {
                _pathPoints.Add(clickPoint);
                _pathPoints.Add(clickPoint);

                _activePathGroup = new Grid();
                _activePathElement = new Path
                {
                    Stroke = brush,
                    StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };

                _activePathGroup.Children.Add(_activePathElement);
                DrawCanvas.Children.Add(_activePathGroup);
            }
            else
            {
                _pathPoints[_pathPoints.Count - 1] = clickPoint;
                _pathPoints.Add(clickPoint);
            }

            UpdatePathGeometry();
        }

        private void UpdatePathGeometry(Point? currentMousePos = null)
        {
            if (_activePathElement == null || _pathPoints.Count < 2) return;

            var pointsToRender = new List<Point>(_pathPoints);
            if (currentMousePos.HasValue)
            {
                pointsToRender[pointsToRender.Count - 1] = currentMousePos.Value;
            }

            var geometryGroup = new GeometryGroup();

            for (int i = 0; i < pointsToRender.Count - 1; i++)
            {
                Point p1 = pointsToRender[i];
                Point p2 = pointsToRender[i + 1];

                if (i == pointsToRender.Count - 2)
                {
                    var arrowGeo = ArrowHelper.CreateArrowGeometry(p1, p2);
                    geometryGroup.Children.Add(arrowGeo);
                }
                else
                {
                    var lineGeo = new LineGeometry(p1, p2);
                    geometryGroup.Children.Add(lineGeo);
                }
            }

            _activePathElement.Data = geometryGroup;
        }

        private void FinalizePathDrawing()
        {
            if (_activePathGroup == null || _pathPoints.Count < 2)
            {
                _pathPoints.Clear();
                _activePathGroup = null;
                _activePathElement = null;
                return;
            }

            _pathPoints.RemoveAt(_pathPoints.Count - 1);

            if (_pathPoints.Count >= 2)
            {
                UpdatePathGeometry();
                var cmd = new AddStrokeCommand(DrawCanvas, _activePathGroup);
                _undoManager.ExecuteCommand(cmd);
            }
            else
            {
                DrawCanvas.Children.Remove(_activePathGroup);
            }

            _pathPoints.Clear();
            _activePathGroup = null;
            _activePathElement = null;
        }

        private Grid CreateFVGGroup(Point start, Point end)
        {
            var grid = new Grid();

            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double width = Math.Max(1, Math.Abs(end.X - start.X));
            double height = Math.Max(1, Math.Abs(end.Y - start.Y));

            var fillBrush = new SolidColorBrush(Color.FromArgb(35, _currentColor.R, _currentColor.G, _currentColor.B));
            var borderBrush = new SolidColorBrush(_currentColor);

            var rect = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = fillBrush,
                Stroke = borderBrush,
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            var midLine = new Line
            {
                X1 = 0,
                Y1 = height / 2,
                X2 = width,
                Y2 = height / 2,
                Stroke = borderBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            var label = new TextBlock
            {
                Text = "FVG",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = borderBrush,
                Margin = new Thickness(Math.Max(0, width - 32), (height / 2) - 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            grid.Children.Add(rect);
            grid.Children.Add(midLine);
            grid.Children.Add(label);

            Canvas.SetLeft(grid, x);
            Canvas.SetTop(grid, y);

            return grid;
        }

        private void UpdateFVGGroup(Grid grid, Point start, Point current)
        {
            double x = Math.Min(start.X, current.X);
            double y = Math.Min(start.Y, current.Y);
            double width = Math.Max(1, Math.Abs(current.X - start.X));
            double height = Math.Max(1, Math.Abs(current.Y - start.Y));

            Canvas.SetLeft(grid, x);
            Canvas.SetTop(grid, y);

            if (grid.Children[0] is Rectangle rect)
            {
                rect.Width = width;
                rect.Height = height;
            }

            if (grid.Children[1] is Line midLine)
            {
                midLine.X2 = width;
                midLine.Y1 = height / 2;
                midLine.Y2 = height / 2;
            }

            if (grid.Children[2] is TextBlock label)
            {
                label.Margin = new Thickness(Math.Max(0, width - 32), (height / 2) - 8, 0, 0);
            }
        }

        private void DrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_drawingMode) return;

            if (_pathPoints.Count > 0)
            {
                FinalizePathDrawing();
                e.Handled = true;
                return;
            }

            if (_currentTool != ToolType.Candle) return;

            var clickPos = e.GetPosition(DrawCanvas);
            double targetX = clickPos.X;
            bool candleFound = false;

            double minDistance = double.MaxValue;

            foreach (var child in DrawCanvas.Children.OfType<Rectangle>())
            {
                if (Equals(child.Tag, "CANDLE_BODY"))
                {
                    double candleLeft = Canvas.GetLeft(child);
                    double candleWidth = child.Width > 0 ? child.Width : GetCandleWidth();

                    double offsetX = 0;
                    if (child.RenderTransform is TranslateTransform candleTransform)
                    {
                        offsetX = candleTransform.X;
                    }

                    double candleCenterX = candleLeft + offsetX + (candleWidth / 2);

                    double dist = Math.Abs(clickPos.X - candleCenterX);

                    if (dist < 30 && dist < minDistance)
                    {
                        minDistance = dist;
                        targetX = candleCenterX;
                        candleFound = true;
                    }
                }
            }

            if (!candleFound)
            {
                targetX = clickPos.X;
            }

            var brush = GetCurrentBrush();
            _startPoint = new Point(targetX, clickPos.Y);

            _currentShape = new Line
            {
                Stroke = brush,
                StrokeThickness = 2,
                X1 = targetX,
                Y1 = clickPos.Y,
                X2 = targetX,
                Y2 = clickPos.Y,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            DrawCanvas.Children.Add(_currentShape);
            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void DrawCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_drawingMode) return;

            var currentPoint = e.GetPosition(DrawCanvas);

            CrosshairV.X1 = currentPoint.X;
            CrosshairV.X2 = currentPoint.X;
            CrosshairV.Y1 = 0;
            CrosshairV.Y2 = DrawCanvas.ActualHeight;

            CrosshairH.Y1 = currentPoint.Y;
            CrosshairH.Y2 = currentPoint.Y;
            CrosshairH.X1 = 0;
            CrosshairH.X2 = DrawCanvas.ActualWidth;

            if (e.LeftButton == MouseButtonState.Pressed && _currentTool == ToolType.Eraser)
            {
                ApplyEraserAtPoint(currentPoint);
                return;
            }

            if (_isMarqueeSelecting && _marqueeBox != null)
            {
                double x = Math.Min(_marqueeStartPoint.X, currentPoint.X);
                double y = Math.Min(_marqueeStartPoint.Y, currentPoint.Y);
                double w = Math.Abs(currentPoint.X - _marqueeStartPoint.X);
                double h = Math.Abs(currentPoint.Y - _marqueeStartPoint.Y);

                Canvas.SetLeft(_marqueeBox, x);
                Canvas.SetTop(_marqueeBox, y);
                _marqueeBox.Width = w;
                _marqueeBox.Height = h;
                return;
            }

            if (_isDraggingElement && _selectedElements.Count > 0)
            {
                Vector delta = currentPoint - _dragStartPoint;
                _dragStartPoint = currentPoint;
                _totalDragDisplacement += delta;

                foreach (var el in _selectedElements)
                {
                    ApplyTranslationToElement(el, delta);
                }
                UpdateSelectionBoxes();
                return;
            }

            if (_currentTool == ToolType.Path && _pathPoints.Count > 0)
            {
                UpdatePathGeometry(currentPoint);
                return;
            }

            if (_currentShape == null) return;

            if (e.RightButton == MouseButtonState.Pressed && _currentShape is Line wickLine)
            {
                wickLine.Y2 = currentPoint.Y;
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed) return;

            if (_currentShape is Polyline polyline)
            {
                polyline.Points.Add(currentPoint);
            }
            else if (_currentShape is Line line)
            {
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    line.X2 = currentPoint.X;
                    line.Y2 = _startPoint.Y;
                }
                else
                {
                    line.X2 = currentPoint.X;
                    line.Y2 = currentPoint.Y;
                }
            }
            else if (_currentShape is Path path)
            {
                Point arrowEnd = currentPoint;
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    arrowEnd = new Point(currentPoint.X, _startPoint.Y);
                }
                path.Data = ArrowHelper.CreateArrowGeometry(_startPoint, arrowEnd);
            }
            else if (_currentShape is Grid fvgGrid && _currentTool == ToolType.FVG)
            {
                UpdateFVGGroup(fvgGrid, _startPoint, currentPoint);
            }
            else if (_currentShape is Ellipse ellipse)
            {
                double ex = currentPoint.X;
                double ey = currentPoint.Y;

                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    double side = Math.Max(Math.Abs(currentPoint.X - _startPoint.X), Math.Abs(currentPoint.Y - _startPoint.Y));
                    ex = _startPoint.X + (currentPoint.X < _startPoint.X ? -side : side);
                    ey = _startPoint.Y + (currentPoint.Y < _startPoint.Y ? -side : side);
                }

                var x = Math.Min(_startPoint.X, ex);
                var y = Math.Min(_startPoint.Y, ey);
                var width = Math.Abs(ex - _startPoint.X);
                var height = Math.Abs(ey - _startPoint.Y);

                Canvas.SetLeft(ellipse, x);
                Canvas.SetTop(ellipse, y);
                ellipse.Width = width;
                ellipse.Height = height;
            }
            else if (_currentShape is Rectangle rect)
            {
                var y = Math.Min(_startPoint.Y, currentPoint.Y);
                var height = Math.Abs(currentPoint.Y - _startPoint.Y);

                if (_currentTool == ToolType.Candle)
                {
                    double candleWidth = GetCandleWidth();
                    Canvas.SetLeft(rect, _startPoint.X - (candleWidth / 2));
                    Canvas.SetTop(rect, y);
                    rect.Width = candleWidth;
                    rect.Height = Math.Max(1, height);
                }
                else
                {
                    var x = Math.Min(_startPoint.X, currentPoint.X);
                    var width = Math.Abs(currentPoint.X - _startPoint.X);

                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, y);
                    rect.Width = width;
                    rect.Height = height;
                }
            }
        }

        private void DrawCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawingMode) return;
            if (e.ChangedButton != MouseButton.Left) return;

            if (_isMarqueeSelecting)
            {
                _isMarqueeSelecting = false;
                DrawCanvas.ReleaseMouseCapture();

                if (_marqueeBox != null)
                {
                    double x = Canvas.GetLeft(_marqueeBox);
                    double y = Canvas.GetTop(_marqueeBox);
                    var marqueeRect = new Rect(x, y, _marqueeBox.Width, _marqueeBox.Height);

                    DrawCanvas.Children.Remove(_marqueeBox);
                    _marqueeBox = null;

                    bool ctrlHeld = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
                    SelectElementsInRect(marqueeRect, ctrlHeld);
                }
                return;
            }

            if (_isDraggingElement && _selectedElements.Count > 0)
            {
                _isDraggingElement = false;
                DrawCanvas.ReleaseMouseCapture();

                UpdateSelectionBoxes();

                if (_totalDragDisplacement.Length > 0.5)
                {
                    var moveCmd = new MoveMultipleStrokesCommand(_selectedElements.ToList(), _totalDragDisplacement);
                    _undoManager.ExecuteCommand(moveCmd);
                }
                return;
            }

            if (_currentShape == null) return;

            var command = new AddStrokeCommand(DrawCanvas, _currentShape);
            _undoManager.ExecuteCommand(command);

            _currentShape = null;
            DrawCanvas.ReleaseMouseCapture();
        }

        private void DrawCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawingMode || _currentShape == null) return;
            if (e.ChangedButton != MouseButton.Right) return;

            var command = new AddStrokeCommand(DrawCanvas, _currentShape);
            _undoManager.ExecuteCommand(command);

            _currentShape = null;
            DrawCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingToolbar = true;
            _toolbarDragStart = e.GetPosition(ToolbarBorder);
            ToolbarBorder.CaptureMouse();
            e.Handled = true;
        }

        private void Toolbar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingToolbar)
            {
                var currentPos = e.GetPosition(UIOverlayCanvas);
                Canvas.SetLeft(ToolbarBorder, currentPos.X - _toolbarDragStart.X);
                Canvas.SetTop(ToolbarBorder, currentPos.Y - _toolbarDragStart.Y);
            }
        }

        private void Toolbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingToolbar)
            {
                _isDraggingToolbar = false;
                ToolbarBorder.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        // =========================================================
        //  LEGENDA DE ATALHOS
        // =========================================================

        private void ShortcutsButton_Click(object sender, RoutedEventArgs e)
        {
            ShortcutsPopup.IsOpen = !ShortcutsPopup.IsOpen;
        }

        // =========================================================
        //  MINIMIZAR / RESTAURAR TOOLBAR (bolinha flutuante)
        // =========================================================

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            double left = Canvas.GetLeft(ToolbarBorder);
            double top = Canvas.GetTop(ToolbarBorder);

            Canvas.SetLeft(MinimizedBubble, left);
            Canvas.SetTop(MinimizedBubble, top);

            ToolbarBorder.Visibility = Visibility.Collapsed;
            MinimizedBubble.Visibility = Visibility.Visible;
            _toolbarMinimized = true;
        }

        private void RestoreToolbar()
        {
            double left = Canvas.GetLeft(MinimizedBubble);
            double top = Canvas.GetTop(MinimizedBubble);

            Canvas.SetLeft(ToolbarBorder, left);
            Canvas.SetTop(ToolbarBorder, top);

            MinimizedBubble.Visibility = Visibility.Collapsed;
            ToolbarBorder.Visibility = Visibility.Visible;
            _toolbarMinimized = false;
        }

        private void MinimizedBubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingBubble = true;
            _bubbleDragged = false;
            _bubbleDragStart = e.GetPosition(MinimizedBubble);
            MinimizedBubble.CaptureMouse();
            e.Handled = true;
        }

        private void MinimizedBubble_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingBubble) return;

            var currentPos = e.GetPosition(UIOverlayCanvas);
            double newLeft = currentPos.X - _bubbleDragStart.X;
            double newTop = currentPos.Y - _bubbleDragStart.Y;

            // Só conta como "arrastou de verdade" se moveu mais que alguns pixels
            // (evita que um clique com a mão trêmula seja confundido com drag).
            if (Math.Abs(newLeft - Canvas.GetLeft(MinimizedBubble)) > 2 ||
                Math.Abs(newTop - Canvas.GetTop(MinimizedBubble)) > 2)
            {
                _bubbleDragged = true;
            }

            Canvas.SetLeft(MinimizedBubble, newLeft);
            Canvas.SetTop(MinimizedBubble, newTop);
        }

        private void MinimizedBubble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingBubble)
            {
                _isDraggingBubble = false;
                MinimizedBubble.ReleaseMouseCapture();

                // Se não arrastou (só clicou), reabre a barra completa.
                if (!_bubbleDragged)
                {
                    RestoreToolbar();
                }
            }
            e.Handled = true;
        }

        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && Enum.TryParse<ToolType>(btn.Tag?.ToString(), out var tool))
            {
                SwitchTool(tool);
            }
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                string tagStr = btn.Tag.ToString() ?? "";
                string hexColor = tagStr switch
                {
                    "Green" => "#00C853",
                    "Yellow" => "#FFD600",
                    "White" => "#FFFFFF",
                    "Blue" => "#29B6F6",
                    "Black" => "#000000",
                    _ => "#FF4444"
                };
                ChangeColor(hexColor, tagStr);
            }
        }

        private void ChangeColor(string hexColor, string tagStr = "")
        {
            _currentColor = (Color)ColorConverter.ConvertFromString(hexColor);
            if (!string.IsNullOrEmpty(tagStr))
            {
                HighlightColorButton(tagStr);
            }
        }

        private void ThicknessButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && double.TryParse(btn.Tag?.ToString(), out double thickness))
            {
                _currentThickness = thickness;
                HighlightThicknessButton(btn.Tag.ToString() ?? "4");
            }
        }

        private void ToggleCanvasVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (DrawCanvas.Visibility == Visibility.Visible)
            {
                DrawCanvas.Visibility = Visibility.Collapsed;
                ToggleCanvasVisibilityButton.Content = "🙈";
                ToggleCanvasVisibilityButton.ToolTip = "Mostrar Desenhos";
            }
            else
            {
                DrawCanvas.Visibility = Visibility.Visible;
                ToggleCanvasVisibilityButton.Content = "👁️";
                ToggleCanvasVisibilityButton.ToolTip = "Ocultar Desenhos";
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
            _undoManager.Undo();
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
            _undoManager.Redo();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
            var cmd = new ClearAllCommand(DrawCanvas);
            _undoManager.ExecuteCommand(cmd);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _hotkeys?.Dispose();
            base.OnClosed(e);
        }
    }
}