using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TraderPen.History;
using TraderPen.Input;
using TraderPen.Overlay;
using TraderPen.Tools;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using IOException = System.IO.IOException;
using InvalidDataException = System.IO.InvalidDataException;

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
        private double _currentThickness = 3; // Padrão inicial
        private Point _startPoint;
        private int _handwritingSmoothingLevel;
        private int _smoothCurrentPenStroke;

        private UIElement? _currentShape;

        private BoardMode _boardMode = BoardMode.Transparent;

        // ---- Seleção múltipla / Arraste em grupo ----
        private readonly List<UIElement> _selectedElements = new();
        private readonly List<UIElement> _copiedElements = new();
        private double _nextPasteOffset = 20;
        private readonly List<Rectangle> _selectionBoxes = new();
        private readonly List<Rectangle> _resizeHandles = new();
        private readonly List<Rectangle> _lineEndpointHandles = new();
        private Point _dragStartPoint;
        private Vector _totalDragDisplacement;
        private bool _isDraggingElement = false;
        private bool _isResizingElement = false;
        private bool _isEditingLineEndpoint = false;
        private Line? _lineEndpointElement;
        private TextBlock? _lineEndpointLabel;
        private bool _editingLineStartEndpoint;
        private Point _lineEndpointStartPoint;
        private Point _lineEndpointBeforePoint;
        private Point _lineEndpointBeforeLabelPoint;
        private string? _resizeHandleDirection;
        private UIElement? _resizeElement;
        private Point _resizeStartPoint;
        private Rect _resizeOriginalBounds;
        private Rect _resizeOriginalLocalBounds;
        private Transform? _resizeOriginalTransform;
        private readonly Dictionary<Shape, double> _resizeOriginalStrokeThickness = new();
        private readonly Dictionary<TextBlock, double> _resizeOriginalFontSizes = new();
        private readonly Dictionary<TextBlock, Transform?> _resizeOriginalTextTransforms = new();
        private readonly Dictionary<Shape, double> _strokeThicknessBaselines = new();
        private readonly Dictionary<TextBlock, double> _fontSizeBaselines = new();
        private readonly Dictionary<TextBlock, Transform?> _textTransformBaselines = new();

        // ---- Marquee (caixa de seleção por área) ----
        private bool _isMarqueeSelecting = false;
        private Point _marqueeStartPoint;
        private Rectangle? _marqueeBox;

        private readonly List<Point> _pathPoints = new();
        private Grid? _activePathGroup;
        private Path? _activePathElement;

        private UndoManager _undoManager = new();

        // ---- Abas (histórico de telas da aula) ----
        private readonly List<DrawingTab> _savedTabs = new();
        private int _nextTabNumber = 1;
        private DrawingTab? _activeTab = null; // null = a tela atual ainda não foi salva como aba
        private string? _currentProjectPath;
        private string? _savedProjectSnapshot;

        private bool _isDraggingToolbar = false;
        private bool _toolbarDragged = false;
        private Point _toolbarDragStart;
        private bool _toolbarVertical;
        public string? StartupProjectPath { get; set; }

        // ---- Auto-esconder o indicador de modo (DRAWING/MOUSE MODE) ----
        private readonly DispatcherTimer _modeIndicatorTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };

        // ---- Modo da borracha: apaga o traço inteiro ou só o pedaço por onde passa ----
        private bool _eraserWholeMode = true; // true = inteiro (padrão atual); false = pedaços (estilo borracha normal)

        // ---- Modo do retângulo: vazado (contorno) ou preenchido ----
        private bool _rectangleFilledMode = false; // false = vazado (padrão atual); true = preenchido

        // ---- Rótulo opcional para linhas de estrutura ----
        private string? _lineLabel;

        // ---- Tipo de zona desenhada pelo botão FVG ----
        private string _zoneMode = "FVG";

        // Sessão de borracha em andamento (do MouseDown até o MouseUp). Enquanto
        // não-nula, ApplyEraserAtPoint aplica os cortes direto na tela, sem gerar
        // um Undo por frame — tudo vira UM único passo de Undo ao soltar o mouse.
        private EraserSessionCommand? _eraserSession;

        // Guarda o último ponto onde a borracha realmente processou um corte,
        // para "espaçar" as chamadas (throttling) — sem isso, o WPF dispara
        // MouseMove dezenas de vezes por segundo e cada uma delas recalcularia
        // geometria, mesmo movendo o mouse 1 pixel. Cortar a cada ~6px de
        // distância já é suave visualmente e reduz muito o custo por gesto.
        private Point? _lastEraserPoint;
        private const double EraserStepDistance = 6.0;

        // ---- Minimizar toolbar (bolinha flutuante estilo Epic Pen) ----
        private bool _toolbarMinimized = false;
        private bool _isDraggingBubble = false;
        private bool _bubbleDragged = false;
        private Point _bubbleDragStart;

        // ---- Modo apresentação ----
        private bool _presentationMode = false;
        private Visibility _toolbarVisibilityBeforePresentation;
        private Visibility _bubbleVisibilityBeforePresentation;
        private readonly DispatcherTimer _mouseModeInteractionTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };

        private sealed class ActionCommand : IUndoableCommand
        {
            private readonly Action _execute;
            private readonly Action _undo;

            public ActionCommand(Action execute, Action undo)
            {
                _execute = execute;
                _undo = undo;
            }

            public void Execute() => _execute();

            public void Undo() => _undo();
        }

        public MainWindow()
        {
            InitializeComponent();

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Loaded += MainWindow_Loaded;

            SourceInitialized += MainWindow_SourceInitialized;
            KeyDown += MainWindow_KeyDown;
            PreviewMouseWheel += MainWindow_PreviewMouseWheel;

            DrawCanvas.MouseRightButtonDown += DrawCanvas_MouseRightButtonDown;
            DrawCanvas.MouseRightButtonUp += DrawCanvas_MouseRightButtonUp;

            _modeIndicatorTimer.Tick += (_, _) =>
            {
                _modeIndicatorTimer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
                ModeIndicator.BeginAnimation(OpacityProperty, fadeOut);
            };

            _mouseModeInteractionTimer.Tick += (_, _) => UpdateMouseModeInteraction();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _toolbarVertical = WindowsIntegration.IsToolbarVertical();
            ApplyToolbarOrientation();
            ToolbarBorder.Visibility = Visibility.Visible;
            UpdateLayout();
            ConfigureToolbarScale();
            UpdateLayout();
            UpdateModeButtons();
            Canvas.SetLeft(ToolbarBorder, 20);
            Canvas.SetTop(ToolbarBorder, Math.Max(20, ActualHeight - ToolbarBorder.ActualHeight - 30));
            UpdateMouseModeInteraction();
            _savedProjectSnapshot = ProjectFileService.Serialize(CaptureProjectDocument());

            if (!string.IsNullOrWhiteSpace(StartupProjectPath))
            {
                OpenProject(StartupProjectPath);
                StartupProjectPath = null;
            }
        }

        private void ConfigureToolbarScale()
        {
            double availableSpace = _toolbarVertical
                ? SystemParameters.WorkArea.Height
                : SystemParameters.WorkArea.Width;
            double contentSize = _toolbarVertical ? ToolbarBorder.ActualHeight : ToolbarBorder.ActualWidth;
            if (availableSpace <= 0 || contentSize <= 0) return;

            double fitScale = (availableSpace - 40) / contentSize;
            if (fitScale >= 1)
            {
                ToolbarBorder.ClearValue(LayoutTransformProperty);
                return;
            }

            double scale = Math.Clamp(fitScale, 0.55, 1.0);
            ToolbarBorder.LayoutTransform = new ScaleTransform(scale, scale);
        }

        private void ApplyToolbarOrientation()
        {
            ToolbarLayoutPanel.Orientation = _toolbarVertical ? Orientation.Horizontal : Orientation.Vertical;
            ToolbarToolsRow.Orientation = _toolbarVertical ? Orientation.Vertical : Orientation.Horizontal;
            ToolContainer.Orientation = _toolbarVertical ? Orientation.Vertical : Orientation.Horizontal;
            ToolbarContextRow.Orientation = _toolbarVertical ? Orientation.Vertical : Orientation.Horizontal;
            ToolbarDocumentsRow.Orientation = _toolbarVertical ? Orientation.Vertical : Orientation.Horizontal;
            ToolbarActionsRow.Orientation = _toolbarVertical ? Orientation.Vertical : Orientation.Horizontal;

            ToolbarLayoutPanel.Children.Remove(ToolbarToolsRow);
            ToolbarLayoutPanel.Children.Remove(ToolbarContextRow);
            if (_toolbarVertical)
            {
                ToolbarLayoutPanel.Children.Add(ToolbarContextRow);
                ToolbarLayoutPanel.Children.Add(ToolbarToolsRow);
            }
            else
            {
                ToolbarLayoutPanel.Children.Add(ToolbarToolsRow);
                ToolbarLayoutPanel.Children.Add(ToolbarContextRow);
            }

            ApplyToolbarDensity();
            ToolbarOrientationLabel.Text = _toolbarVertical ? "HORIZONTAL" : "VERTICAL";
            ToolbarOrientationIcon.Text = _toolbarVertical ? "↔" : "↕";
            ToolbarOrientationButton.ToolTip = _toolbarVertical
                ? "Mudar a barra para orientação horizontal"
                : "Mudar a barra para orientação vertical";
        }

        private void ApplyToolbarDensity()
        {
            if (_toolbarVertical)
            {
                ToolbarBorder.Padding = new Thickness(5);
                ToolbarDragHandle.Orientation = Orientation.Horizontal;
                ToolbarDragHandle.Margin = new Thickness(0);
                ToolbarBrandLabel.Visibility = Visibility.Visible;
                ToolbarLogoBorder.Width = 24;
                ToolbarLogoBorder.Height = 24;
                ToolbarLogoBorder.Margin = new Thickness(0);
                ToolbarToolsDivider.Width = 30;
                ToolbarToolsDivider.Height = 1;
                ToolbarToolsDivider.Margin = new Thickness(2);
                ToolbarContextDivider.Width = 30;
                ToolbarContextDivider.Height = 1;
                ToolbarContextDivider.Margin = new Thickness(2);
                ToolbarActionsDivider.Width = 30;
                ToolbarActionsDivider.Height = 1;
                ToolbarActionsDivider.Margin = new Thickness(2);
                ToolbarContextRow.Margin = new Thickness(0);
                ThicknessContainer.Width = 76;
                ThicknessContainer.Margin = new Thickness(2);
                ThicknessSlider.Width = 72;
                ColorContainer.Margin = new Thickness(0, 0, 0, 4);
                ModeButtonsContainer.Margin = new Thickness(0, 0, 0, 7);
                foreach (var separator in ToolbarContextRow.Children.OfType<Rectangle>())
                {
                    separator.Width = 30;
                    separator.Height = 1;
                    separator.Margin = new Thickness(2);
                }
                foreach (var separator in ToolContainer.Children.OfType<Rectangle>())
                {
                    separator.Width = 30;
                    separator.Height = 1;
                    separator.Margin = new Thickness(2);
                }
                foreach (var button in ToolContainer.Children.OfType<Button>())
                {
                    button.Width = 76;
                    button.Margin = new Thickness(1);
                }
                ColorContainer.Width = 72;

                foreach (var button in ToolbarDocumentsRow.Children.OfType<Button>())
                {
                    button.Width = 76;
                    button.Margin = new Thickness(1);
                }

                foreach (var tabButton in TabsContainer.Children.OfType<Button>())
                {
                    tabButton.Width = 76;
                    tabButton.Height = 46;
                    tabButton.Margin = new Thickness(1);
                }

                foreach (var button in ToolbarActionsRow.Children.OfType<Button>())
                {
                    button.Width = 76;
                    button.Margin = new Thickness(1, 2, 1, 2);
                }
                MouseModeButton.Width = 76;
                DrawingModeButton.Width = 76;
            }
            else
            {
                ToolbarBorder.Padding = new Thickness(10, 4, 10, 4);
                ToolbarDragHandle.Orientation = Orientation.Horizontal;
                ToolbarDragHandle.Margin = new Thickness(2, 0, 10, 0);
                ToolbarBrandLabel.Visibility = Visibility.Visible;
                ToolbarLogoBorder.Width = 28;
                ToolbarLogoBorder.Height = 28;
                ToolbarLogoBorder.Margin = new Thickness(0, 0, 7, 0);
                ToolbarToolsDivider.Width = 1;
                ToolbarToolsDivider.Height = 30;
                ToolbarToolsDivider.Margin = new Thickness(4, 0, 8, 0);
                ToolbarContextDivider.Width = 1;
                ToolbarContextDivider.Height = 30;
                ToolbarContextDivider.Margin = new Thickness(8, 0, 8, 0);
                ToolbarActionsDivider.Width = 1;
                ToolbarActionsDivider.Height = 30;
                ToolbarActionsDivider.Margin = new Thickness(8, 0, 8, 0);
                ToolbarContextRow.Margin = new Thickness(0);
                foreach (var separator in ToolbarContextRow.Children.OfType<Rectangle>())
                {
                    separator.Width = 1;
                    separator.Height = 30;
                    separator.Margin = new Thickness(8, 0, 8, 0);
                }
                foreach (var separator in ToolContainer.Children.OfType<Rectangle>())
                {
                    separator.Width = 1;
                    separator.Height = 30;
                    separator.Margin = new Thickness(6, 0, 6, 0);
                }
                foreach (var button in ToolContainer.Children.OfType<Button>())
                {
                    button.ClearValue(WidthProperty);
                    button.ClearValue(MarginProperty);
                }
                ThicknessContainer.Width = 126;
                ThicknessContainer.Margin = new Thickness(4, 0, 6, 0);
                ThicknessSlider.Width = 118;
                ColorContainer.Margin = new Thickness(0);
                ModeButtonsContainer.Margin = new Thickness(0);
                ColorContainer.ClearValue(WidthProperty);

                foreach (var button in ToolbarDocumentsRow.Children.OfType<Button>())
                {
                    button.ClearValue(WidthProperty);
                    button.ClearValue(MarginProperty);
                }

                foreach (var tabButton in TabsContainer.Children.OfType<Button>())
                {
                    tabButton.ClearValue(WidthProperty);
                    tabButton.ClearValue(HeightProperty);
                    tabButton.ClearValue(MarginProperty);
                }

                foreach (var button in ToolbarActionsRow.Children.OfType<Button>())
                {
                    button.ClearValue(WidthProperty);
                    button.ClearValue(MarginProperty);
                }
                MouseModeButton.Width = 62;
                DrawingModeButton.Width = 62;
            }
        }

        private void ToolbarOrientationButton_Click(object sender, RoutedEventArgs e)
        {
            _toolbarVertical = !_toolbarVertical;
            ApplyToolbarOrientation();
            UpdateLayout();
            ConfigureToolbarScale();
            UpdateLayout();
            KeepToolbarOnScreen();

            try
            {
                WindowsIntegration.SetToolbarVertical(_toolbarVertical);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                _toolbarVertical = !_toolbarVertical;
                ApplyToolbarOrientation();
                UpdateLayout();
                ConfigureToolbarScale();
                KeepToolbarOnScreen();
                MessageBox.Show($"Não foi possível salvar a preferência da barra:\n{ex.Message}",
                    "Erro ao alterar orientação", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void KeepToolbarOnScreen()
        {
            double left = Canvas.GetLeft(ToolbarBorder);
            double top = Canvas.GetTop(ToolbarBorder);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            Canvas.SetLeft(ToolbarBorder, Math.Clamp(left, 0, Math.Max(0, ActualWidth - ToolbarBorder.ActualWidth)));
            Canvas.SetTop(ToolbarBorder, Math.Clamp(top, 0, Math.Max(0, ActualHeight - ToolbarBorder.ActualHeight)));
        }

        /// <summary>
        /// Mostra o indicador de modo/ferramenta por alguns segundos e some sozinho,
        /// em vez de ficar fixo no canto da tela o tempo todo.
        /// </summary>
        private void ShowModeIndicatorTemporarily()
        {
            ModeIndicator.BeginAnimation(OpacityProperty, null); // cancela qualquer fade em andamento
            ModeIndicator.Opacity = 1;
            _modeIndicatorTimer.Stop();
            _modeIndicatorTimer.Start();
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.EnableClickThrough(hwnd);
            _mouseModeInteractionTimer.Start();

            _hotkeys = new HotkeyManager(this);
            _hotkeys.ToggleModeRequested += ToggleMode;

            HighlightToolButton(_currentTool.ToString());
            HighlightColorButton("Red");
            UpdateModeButtons();
        }

        private static bool IsPointInside(FrameworkElement element, Point point)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return false;
            }

            double left = Canvas.GetLeft(element);
            double top = Canvas.GetTop(element);
            return point.X >= left && point.X <= left + element.ActualWidth &&
                   point.Y >= top && point.Y <= top + element.ActualHeight;
        }

        private void UpdateMouseModeInteraction()
        {
            if (_drawingMode || _presentationMode) return;

            var cursorPosition = PointFromScreen(NativeMethods.GetCursorPosition());
            bool overToolbar = IsPointInside(ToolbarBorder, cursorPosition) || IsPointInside(MinimizedBubble, cursorPosition);
            var hwnd = new WindowInteropHelper(this).Handle;

            if (overToolbar || _isDraggingToolbar || _isDraggingBubble)
            {
                NativeMethods.DisableClickThrough(hwnd);
            }
            else
            {
                NativeMethods.EnableClickThrough(hwnd);
            }
        }

        private void ToggleMode()
        {
            if (_presentationMode) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            _drawingMode = !_drawingMode;

            if (_drawingMode)
            {
                NativeMethods.DisableClickThrough(hwnd);
                this.Activate();
                this.Focus();

                ModeIndicator.Text = $"DRAWING MODE | Ferramenta: {GetToolDisplayName(_currentTool)} (Pressione F9 para soltar o mouse)";
                ShowModeIndicatorTemporarily();

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
                CloseEraserSession();
                NativeMethods.EnableClickThrough(hwnd);
                ModeIndicator.Text = "MOUSE MODE (Pressione F9 para desenhar)";
                ShowModeIndicatorTemporarily();
            }

            UpdateModeButtons();
            UpdateCrosshairVisibility();
        }

        private void UpdateModeButtons()
        {
            var accent = (Brush)FindResource("ToolbarAccentBrush");
            var buttonBackground = (Brush)FindResource("ToolbarButtonBrush");
            var activeBackground = (Brush)FindResource("ToolbarHoverBrush");
            var text = (Brush)FindResource("ToolbarTextBrush");
            var secondaryText = (Brush)FindResource("ToolbarSecondaryTextBrush");
            MouseModeButton.Content = _drawingMode ? "MOUSE" : "● MOUSE";
            MouseModeButton.BorderBrush = _drawingMode ? (Brush)FindResource("ToolbarBorderBrush") : accent;
            MouseModeButton.BorderThickness = new Thickness(_drawingMode ? 1 : 2);
            MouseModeButton.Background = _drawingMode ? buttonBackground : activeBackground;
            MouseModeButton.Foreground = _drawingMode ? secondaryText : text;
            DrawingModeButton.Content = _drawingMode ? "● DESENHAR" : "DESENHAR";
            DrawingModeButton.BorderBrush = _drawingMode ? accent : (Brush)FindResource("ToolbarBorderBrush");
            DrawingModeButton.BorderThickness = new Thickness(_drawingMode ? 2 : 1);
            DrawingModeButton.Background = _drawingMode ? activeBackground : buttonBackground;
            DrawingModeButton.Foreground = _drawingMode ? text : secondaryText;
        }

        // Fecha uma sessão de borracha em andamento (se houver), registrando no
        // Undo o que já foi apagado até agora. Usado como "trava de segurança"
        // caso o usuário troque de ferramenta ou saia do Drawing Mode no meio
        // do arraste, sem soltar o botão do mouse primeiro.
        private void CloseEraserSession()
        {
            if (_eraserSession == null) return;

            if (DrawCanvas.IsMouseCaptured)
            {
                DrawCanvas.ReleaseMouseCapture();
            }

            if (_eraserSession.HasChanges)
            {
                _undoManager.RegisterCompletedCommand(_eraserSession);
            }

            _eraserSession = null;
            _lastEraserPoint = null;
        }

        private void UpdateCrosshairVisibility()
        {
            bool showCrosshair = DrawCanvas.Visibility == Visibility.Visible &&
                (_presentationMode || (_drawingMode && _currentTool != ToolType.Select));
            CrosshairV.Visibility = showCrosshair ? Visibility.Visible : Visibility.Collapsed;
            CrosshairH.Visibility = showCrosshair ? Visibility.Visible : Visibility.Collapsed;
            DrawCanvas.Cursor = showCrosshair ? Cursors.None : Cursors.Arrow;
            CandleWidthPreview.Visibility = Visibility.Collapsed; // reaparece no próximo movimento do mouse, se fizer sentido
            EraserPreview.Visibility = (_drawingMode && _currentTool == ToolType.Eraser) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (e.Key == Key.S || e.Key == Key.O))
            {
                if (e.Key == Key.S)
                {
                    SaveProject(saveAs: false);
                }
                else
                {
                    OpenProject();
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space)
            {
                if (_drawingMode || _presentationMode)
                {
                    TogglePresentationMode();
                    e.Handled = true;
                }
                return;
            }

            if (_presentationMode)
            {
                if (e.Key == Key.Escape)
                {
                    TogglePresentationMode();
                    e.Handled = true;
                }
                return;
            }

            if (!_drawingMode) return;

            if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                if (_pathPoints.Count > 0)
                {
                    FinalizePathDrawing();
                    return;
                }
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (e.Key == Key.C || e.Key == Key.V))
            {
                if (e.Key == Key.C)
                {
                    CopySelectedElements();
                }
                else
                {
                    PasteCopiedElements();
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
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
            else if (e.Key == Key.D1 || e.Key == Key.NumPad1) SwitchTool(ToolType.Candle);
            else if (e.Key == Key.D2 || e.Key == Key.NumPad2) SwitchTool(ToolType.FVG);
            else if (e.Key == Key.D3 || e.Key == Key.NumPad3) SwitchTool(ToolType.Line);
            else if (e.Key == Key.D4 || e.Key == Key.NumPad4) SwitchTool(ToolType.Path);
            else if (e.Key == Key.D5 || e.Key == Key.NumPad5) SwitchTool(ToolType.Arrow);
            else if (e.Key == Key.D6 || e.Key == Key.NumPad6) SwitchTool(ToolType.Pen);
            else if (e.Key == Key.D7 || e.Key == Key.NumPad7) SwitchTool(ToolType.Highlighter);
            else if (e.Key == Key.D8 || e.Key == Key.NumPad8) SwitchTool(ToolType.Rectangle);
            else if (e.Key == Key.D9 || e.Key == Key.NumPad9) SwitchTool(ToolType.Ellipse);
            else if (e.Key == Key.T) SwitchTool(ToolType.Text);
            else if (e.Key == Key.E) SwitchTool(ToolType.Eraser);
            else if (e.Key == Key.M) SwitchTool(ToolType.Select);
            else if (e.Key == Key.R) ChangeColor("#FF4444", "Red");
            else if (e.Key == Key.G) ChangeColor("#00C853", "Green");
        }

        private void CopySelectedElements()
        {
            if (_selectedElements.Count == 0) return;

            try
            {
                var copies = CloneDrawingElements(_selectedElements);
                _copiedElements.Clear();
                _copiedElements.AddRange(copies);
                _nextPasteOffset = 20;
            }
            catch (System.Xaml.XamlException ex)
            {
                MessageBox.Show($"Não foi possível copiar a seleção: {ex.Message}", "Erro ao copiar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show($"Não foi possível copiar a seleção: {ex.Message}", "Erro ao copiar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PasteCopiedElements()
        {
            if (_copiedElements.Count == 0) return;

            var pastedElements = new List<UIElement>(_copiedElements.Count);
            try
            {
                pastedElements.AddRange(CloneDrawingElements(_copiedElements));
                foreach (var copy in pastedElements)
                {
                    ApplyTranslationToElement(copy, new Vector(_nextPasteOffset, _nextPasteOffset));
                }
            }
            catch (System.Xaml.XamlException ex)
            {
                MessageBox.Show($"Não foi possível colar os objetos: {ex.Message}", "Erro ao colar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show($"Não foi possível colar os objetos: {ex.Message}", "Erro ao colar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _undoManager.ExecuteCommand(new AddMultipleStrokesCommand(DrawCanvas, pastedElements));
            _nextPasteOffset += 20;

            ClearSelection();
            foreach (var element in pastedElements)
            {
                EnsureTranslateTransform(element);
                _selectedElements.Add(element);
            }
            UpdateSelectionBoxes();
        }

        private static UIElement CloneDrawingElement(UIElement element)
        {
            return XamlReader.Parse(XamlWriter.Save(element)) as UIElement
                ?? throw new InvalidOperationException("O objeto copiado não é um elemento de desenho válido.");
        }

        private static List<UIElement> CloneDrawingElements(IReadOnlyList<UIElement> elements)
        {
            var copies = elements.Select(CloneDrawingElement).ToList();
            var copiesBySource = elements
                .Select((element, index) => new KeyValuePair<UIElement, UIElement>(element, copies[index]))
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i] is FrameworkElement source &&
                    copies[i] is FrameworkElement copy &&
                    source.Tag is UIElement referencedElement)
                {
                    copy.Tag = copiesBySource.TryGetValue(referencedElement, out var referencedCopy)
                        ? referencedCopy
                        : null;
                }
            }

            return copies;
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

            if (_currentTool == ToolType.Eraser && tool != ToolType.Eraser)
            {
                CloseEraserSession();
            }

            _currentTool = tool;
            ModeIndicator.Text = $"DRAWING MODE | Ferramenta: {GetToolDisplayName(_currentTool)} (Pressione F9 para soltar o mouse)";
            ShowModeIndicatorTemporarily();
            HighlightToolButton(tool.ToString());
            UpdateCrosshairVisibility();
        }

        private static string GetToolDisplayName(ToolType tool)
        {
            return tool == ToolType.Path ? "Caminho" : tool.ToString();
        }

        private void TogglePresentationMode()
        {
            if (_presentationMode)
            {
                _presentationMode = false;
                ToolbarBorder.Visibility = _toolbarVisibilityBeforePresentation;
                MinimizedBubble.Visibility = _bubbleVisibilityBeforePresentation;
                ModeIndicator.Visibility = Visibility.Visible;
                DrawCanvas.IsHitTestVisible = true;
                UIOverlayCanvas.IsHitTestVisible = true;
                UpdateCrosshairVisibility();
                return;
            }

            _presentationMode = true;
            _toolbarVisibilityBeforePresentation = ToolbarBorder.Visibility;
            _bubbleVisibilityBeforePresentation = MinimizedBubble.Visibility;

            ShortcutsPopup.IsOpen = false;
            ToolbarBorder.Visibility = Visibility.Collapsed;
            MinimizedBubble.Visibility = Visibility.Collapsed;
            ModeIndicator.Visibility = Visibility.Collapsed;
            CandleWidthPreview.Visibility = Visibility.Collapsed;
            EraserPreview.Visibility = Visibility.Collapsed;
            DrawCanvas.IsHitTestVisible = true;
            UIOverlayCanvas.IsHitTestVisible = false;
            UpdateCrosshairVisibility();
        }

        private void HighlightToolButton(string tagValue)
        {
            var accent = (Brush)FindResource("ToolbarAccentBrush");
            var buttonBackground = (Brush)FindResource("ToolbarButtonBrush");
            var activeBackground = (Brush)FindResource("ToolbarHoverBrush");
            foreach (var btn in GetToolButtons(ToolContainer).Where(button => !ColorContainer.IsAncestorOf(button)))
            {
                if (btn == MouseModeButton || btn == DrawingModeButton)
                {
                    continue;
                }

                bool isSelected = btn.Tag?.ToString() == tagValue;
                btn.BorderBrush = isSelected ? accent : (Brush)FindResource("ToolbarBorderBrush");
                btn.BorderThickness = new Thickness(isSelected ? 1.5 : 1);
                btn.Background = isSelected ? activeBackground : buttonBackground;
            }
        }

        private static IEnumerable<Button> GetToolButtons(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button button)
                {
                    yield return button;
                }

                foreach (Button descendant in GetToolButtons(child))
                {
                    yield return descendant;
                }
            }
        }

        private void HighlightColorButton(string tagValue)
        {
            var accent = (Brush)FindResource("ToolbarAccentBrush");
            foreach (var child in ColorContainer.Children)
            {
                if (child is Button btn)
                {
                    bool isSelected = btn.Tag?.ToString() == tagValue;
                    btn.BorderBrush = isSelected ? accent : (Brush)FindResource("ToolbarBorderBrush");
                    btn.BorderThickness = new Thickness(isSelected ? 2 : 1);
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

        private static double GetTextFontSize(double thickness)
        {
            return 14 + (thickness * 2);
        }

        private double GetCandleWidth(double? thickness = null)
        {
            double effectiveThickness = thickness ?? _currentThickness;
            return effectiveThickness switch
            {
                2 => 12,
                4 => 24,
                8 => 40,
                <= 4 => 12 + ((effectiveThickness - 2) * 6),
                _ => 24 + ((effectiveThickness - 4) * 4)
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

            UpdateBoardModeAppearance();
        }

        private void UpdateBoardModeAppearance()
        {
            switch (_boardMode)
            {
                case BoardMode.Whiteboard:
                    BoardBackground.Fill = Brushes.White;
                    BoardModeIcon.Text = "⬜";
                    BoardModeLabel.Text = "FUNDO";
                    break;
                case BoardMode.Blackboard:
                    BoardBackground.Fill = Brushes.Black;
                    BoardModeIcon.Text = "⬛";
                    BoardModeLabel.Text = "FUNDO";
                    break;
                default:
                    BoardBackground.Fill = Brushes.Transparent;
                    BoardModeIcon.Text = "🖥️";
                    BoardModeLabel.Text = "FUNDO";
                    break;
            }
        }

        // =========================================================
        //  SELEÇÃO (única, múltipla via Ctrl, e por área/marquee)
        // =========================================================

        private bool IsSelectionVisual(UIElement el)
        {
            return _selectionBoxes.Contains(el) || _resizeHandles.Contains(el) ||
                   _lineEndpointHandles.Contains(el) || el == _marqueeBox;
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
            if (element.RenderTransform is null)
            {
                element.RenderTransform = new TranslateTransform();
            }
            else if (element.RenderTransform is not TranslateTransform && element.RenderTransform is not TransformGroup)
            {
                var group = new TransformGroup();
                group.Children.Add(element.RenderTransform);
                group.Children.Add(new TranslateTransform());
                element.RenderTransform = group;
            }
            else if (element.RenderTransform is TransformGroup group &&
                     !group.Children.OfType<TranslateTransform>().Any())
            {
                group.Children.Add(new TranslateTransform());
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

            if (IsFiniteBounds(combined)) return combined;

            // Último recurso: usa o tamanho real do elemento (funciona bem pra
            // TextBlock, TextBox, Rectangle com Width/Height explícitos, etc.)
            if (element is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                return new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);
            }

            if (element is FrameworkElement sizedElement &&
                double.IsFinite(sizedElement.Width) && sizedElement.Width > 0 &&
                double.IsFinite(sizedElement.Height) && sizedElement.Height > 0)
            {
                return new Rect(0, 0, sizedElement.Width, sizedElement.Height);
            }

            return Rect.Empty;
        }

        private static bool IsFiniteBounds(Rect bounds)
        {
            return !bounds.IsEmpty &&
                   double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                   double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
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

        private Rect GetTransformedElementCanvasBounds(UIElement element)
        {
            Rect localBounds = GetElementLocalBounds(element);
            if (localBounds.IsEmpty) return Rect.Empty;

            if (element.RenderTransform is Transform transform)
            {
                localBounds = transform.TransformBounds(localBounds);
            }

            double left = Canvas.GetLeft(element);
            double top = Canvas.GetTop(element);
            localBounds.Offset(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top);
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

                Rect bounds = GetTransformedElementCanvasBounds(child);
                if (bounds.IsEmpty) continue;

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
            if (_isEditingLineEndpoint && _lineEndpointElement != null)
            {
                SetLineEndpoint(_lineEndpointElement, _editingLineStartEndpoint, _lineEndpointBeforePoint);
                if (_lineEndpointLabel != null)
                {
                    Canvas.SetLeft(_lineEndpointLabel, _lineEndpointBeforeLabelPoint.X);
                    Canvas.SetTop(_lineEndpointLabel, _lineEndpointBeforeLabelPoint.Y);
                }
                _isEditingLineEndpoint = false;
                _lineEndpointElement = null;
                _lineEndpointLabel = null;
                DrawCanvas.ReleaseMouseCapture();
            }

            if (_isResizingElement)
            {
                _isResizingElement = false;
                _resizeElement = null;
                DrawCanvas.ReleaseMouseCapture();
            }

            foreach (var box in _selectionBoxes)
            {
                UIOverlayCanvas.Children.Remove(box);
            }
            _selectionBoxes.Clear();
            foreach (var handle in _resizeHandles)
            {
                UIOverlayCanvas.Children.Remove(handle);
            }
            _resizeHandles.Clear();
            foreach (var handle in _lineEndpointHandles)
            {
                UIOverlayCanvas.Children.Remove(handle);
            }
            _lineEndpointHandles.Clear();
            _selectedElements.Clear();
            _isDraggingElement = false;
        }

        private void UpdateSelectionBoxes()
        {
            foreach (var box in _selectionBoxes)
            {
                UIOverlayCanvas.Children.Remove(box);
            }
            _selectionBoxes.Clear();

            foreach (var handle in _resizeHandles)
            {
                UIOverlayCanvas.Children.Remove(handle);
            }
            _resizeHandles.Clear();
            foreach (var handle in _lineEndpointHandles)
            {
                UIOverlayCanvas.Children.Remove(handle);
            }
            _lineEndpointHandles.Clear();

            foreach (var el in _selectedElements)
            {
                Rect bounds = GetTransformedElementCanvasBounds(el);
                if (!IsFiniteBounds(bounds)) continue;

                var box = new Rectangle
                {
                    Stroke = Brushes.Yellow,
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false,
                    Visibility = DrawCanvas.Visibility
                };

                Canvas.SetLeft(box, bounds.Left - 4);
                Canvas.SetTop(box, bounds.Top - 4);
                box.Width = Math.Max(8, bounds.Width + 8);
                box.Height = Math.Max(8, bounds.Height + 8);

                UIOverlayCanvas.Children.Add(box);
                _selectionBoxes.Add(box);
            }

            if (_selectedElements.Count == 1 &&
                IsFiniteBounds(GetTransformedElementCanvasBounds(_selectedElements[0])))
            {
                if (TryGetEditableLine(_selectedElements[0], out Line line))
                {
                    AddLineEndpointHandles(line);
                }
                else
                {
                    Rect selectedBounds = GetTransformedElementCanvasBounds(_selectedElements[0]);
                    AddResizeHandles(selectedBounds);
                }
            }
        }

        private void AddLineEndpointHandles(Line line)
        {
            AddLineEndpointHandle(line, isStart: true);
            AddLineEndpointHandle(line, isStart: false);
            UpdateLineEndpointHandlePositions(line);
        }

        private void AddLineEndpointHandle(Line line, bool isStart)
        {
            var handle = new Rectangle
            {
                Width = 14,
                Height = 14,
                Fill = Brushes.LimeGreen,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Visibility = DrawCanvas.Visibility,
                Tag = isStart ? "LINE_START" : "LINE_END",
                Cursor = Cursors.Cross,
                ToolTip = "Arraste para ajustar a ponta da linha"
            };
            handle.MouseLeftButtonDown += LineEndpointHandle_MouseLeftButtonDown;
            UIOverlayCanvas.Children.Add(handle);
            _lineEndpointHandles.Add(handle);
        }

        private void UpdateLineEndpointHandlePositions(Line line)
        {
            foreach (var handle in _lineEndpointHandles)
            {
                bool isStart = handle.Tag?.ToString() == "LINE_START";
                Point endpoint = isStart ? new Point(line.X1, line.Y1) : new Point(line.X2, line.Y2);
                Point canvasPoint = line.TransformToVisual(UIOverlayCanvas).Transform(endpoint);
                Canvas.SetLeft(handle, canvasPoint.X - handle.Width / 2);
                Canvas.SetTop(handle, canvasPoint.Y - handle.Height / 2);
            }
        }

        private void LineEndpointHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Rectangle handle || _selectedElements.Count != 1 ||
                !TryGetEditableLine(_selectedElements[0], out Line line))
            {
                return;
            }

            _lineEndpointElement = line;
            _lineEndpointLabel = GetLabeledLineTextBlock(_selectedElements[0]);
            _editingLineStartEndpoint = handle.Tag?.ToString() == "LINE_START";
            _lineEndpointBeforePoint = _editingLineStartEndpoint
                ? new Point(line.X1, line.Y1)
                : new Point(line.X2, line.Y2);
            _lineEndpointStartPoint = e.GetPosition(DrawCanvas);
            _lineEndpointBeforeLabelPoint = _lineEndpointLabel == null
                ? default
                : new Point(Canvas.GetLeft(_lineEndpointLabel), Canvas.GetTop(_lineEndpointLabel));
            _isEditingLineEndpoint = true;
            _isDraggingElement = false;
            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private static void SetLineEndpoint(Line line, bool isStart, Point point)
        {
            if (isStart)
            {
                line.X1 = point.X;
                line.Y1 = point.Y;
            }
            else
            {
                line.X2 = point.X;
                line.Y2 = point.Y;
            }
        }

        private static bool TryGetEditableLine(UIElement element, out Line line)
        {
            if (element is Line directLine)
            {
                line = directLine;
                return true;
            }

            if (element is Canvas lineGroup &&
                lineGroup.Children.OfType<Line>().SingleOrDefault() is Line groupedLine &&
                lineGroup.Children.OfType<TextBlock>().Any())
            {
                line = groupedLine;
                return true;
            }

            line = null!;
            return false;
        }

        private static TextBlock? GetLabeledLineTextBlock(UIElement element)
        {
            return element is Canvas lineGroup
                ? lineGroup.Children.OfType<TextBlock>().FirstOrDefault()
                : null;
        }

        private static void UpdateLabeledLineTextPosition(Line line, TextBlock? label)
        {
            if (label == null) return;

            Canvas.SetLeft(label, Math.Max(line.X1, line.X2) + 6);
            Canvas.SetTop(label, ((line.Y1 + line.Y2) / 2) - 9);
        }

        private void UpdateLineEndpoint(Point canvasPoint)
        {
            if (_lineEndpointElement == null) return;

            GeneralTransform? inverse = _lineEndpointElement.TransformToVisual(DrawCanvas).Inverse;
            if (inverse == null || !inverse.TryTransform(canvasPoint, out Point localPoint)) return;

            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                localPoint.Y = _editingLineStartEndpoint
                    ? _lineEndpointElement.Y2
                    : _lineEndpointElement.Y1;
            }

            SetLineEndpoint(_lineEndpointElement, _editingLineStartEndpoint, localPoint);
            UpdateLabeledLineTextPosition(_lineEndpointElement, _lineEndpointLabel);
            UpdateSelectionVisuals();
        }

        private void AddResizeHandles(Rect bounds)
        {
            var positions = GetResizeHandlePositions(bounds);

            foreach (var position in positions)
            {
                var handle = new Rectangle
                {
                    Width = 14,
                    Height = 14,
                    Fill = Brushes.Yellow,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Visibility = DrawCanvas.Visibility,
                    Tag = position.Key,
                    Cursor = position.Key switch
                    {
                        "N" or "S" => Cursors.SizeNS,
                        "E" or "W" => Cursors.SizeWE,
                        "NE" or "SW" => Cursors.SizeNESW,
                        _ => Cursors.SizeNWSE
                    }
                };
                Canvas.SetLeft(handle, position.Value.X);
                Canvas.SetTop(handle, position.Value.Y);
                handle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
                UIOverlayCanvas.Children.Add(handle);
                _resizeHandles.Add(handle);
            }
        }

        private static Dictionary<string, Point> GetResizeHandlePositions(Rect bounds)
        {
            double left = bounds.Left - 7;
            double top = bounds.Top - 7;
            double right = bounds.Right - 7;
            double bottom = bounds.Bottom - 7;
            double centerX = bounds.Left + (bounds.Width / 2) - 7;
            double centerY = bounds.Top + (bounds.Height / 2) - 7;

            return new Dictionary<string, Point>
            {
                ["NW"] = new Point(left, top),
                ["N"] = new Point(centerX, top),
                ["NE"] = new Point(right, top),
                ["E"] = new Point(right, centerY),
                ["SE"] = new Point(right, bottom),
                ["S"] = new Point(centerX, bottom),
                ["SW"] = new Point(left, bottom),
                ["W"] = new Point(left, centerY)
            };
        }

        private void UpdateSelectionVisuals()
        {
            if (_selectedElements.Count != 1 || _selectionBoxes.Count != 1) return;

            Rect bounds = GetTransformedElementCanvasBounds(_selectedElements[0]);
            var box = _selectionBoxes[0];
            Canvas.SetLeft(box, bounds.Left - 4);
            Canvas.SetTop(box, bounds.Top - 4);
            box.Width = Math.Max(8, bounds.Width + 8);
            box.Height = Math.Max(8, bounds.Height + 8);

            var positions = GetResizeHandlePositions(bounds);
            foreach (var handle in _resizeHandles)
            {
                if (handle.Tag is string direction && positions.TryGetValue(direction, out Point position))
                {
                    Canvas.SetLeft(handle, position.X);
                    Canvas.SetTop(handle, position.Y);
                }
            }

            if (TryGetEditableLine(_selectedElements[0], out Line line))
            {
                UpdateLineEndpointHandlePositions(line);
            }
        }

        private void SetSelectionVisualsVisibility(Visibility visibility)
        {
            foreach (var box in _selectionBoxes)
            {
                box.Visibility = visibility;
            }

            foreach (var handle in _resizeHandles)
            {
                handle.Visibility = visibility;
            }

            foreach (var handle in _lineEndpointHandles)
            {
                handle.Visibility = visibility;
            }
        }

        private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Rectangle handle || _selectedElements.Count != 1) return;

            _resizeElement = _selectedElements[0];
            _resizeHandleDirection = handle.Tag?.ToString();
            _resizeStartPoint = e.GetPosition(DrawCanvas);
            _resizeOriginalLocalBounds = GetElementLocalBounds(_resizeElement);
            _resizeOriginalBounds = GetTransformedElementCanvasBounds(_resizeElement);
            _resizeOriginalTransform = _resizeElement.RenderTransform?.Clone();
            _resizeOriginalStrokeThickness.Clear();
            _resizeOriginalFontSizes.Clear();
            _resizeOriginalTextTransforms.Clear();
            foreach (var shape in GetDescendantShapes(_resizeElement))
            {
                if (!_strokeThicknessBaselines.ContainsKey(shape))
                {
                    _strokeThicknessBaselines[shape] = shape.StrokeThickness;
                }
                _resizeOriginalStrokeThickness[shape] = shape.StrokeThickness;
            }
            foreach (var textBlock in GetDescendantTextBlocks(_resizeElement))
            {
                if (!_fontSizeBaselines.ContainsKey(textBlock))
                {
                    _fontSizeBaselines[textBlock] = textBlock.FontSize;
                    _textTransformBaselines[textBlock] = textBlock.RenderTransform?.Clone();
                }
                _resizeOriginalFontSizes[textBlock] = textBlock.FontSize;
                _resizeOriginalTextTransforms[textBlock] = textBlock.RenderTransform?.Clone();
            }
            _isResizingElement = true;
            _isDraggingElement = false;
            DrawCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void ResizeSelectedElement(Point currentPoint)
        {
            if (_resizeElement == null || _resizeHandleDirection == null) return;

            Vector delta = currentPoint - _resizeStartPoint;
            Rect bounds = _resizeOriginalBounds;
            const double minimumSize = 8;

            if (_resizeHandleDirection.Contains("W"))
            {
                bounds.X = Math.Min(bounds.Right - minimumSize, bounds.Left + delta.X);
                bounds.Width = _resizeOriginalBounds.Right - bounds.Left;
            }
            else if (_resizeHandleDirection.Contains("E"))
            {
                bounds.Width = Math.Max(minimumSize, _resizeOriginalBounds.Width + delta.X);
            }

            if (_resizeHandleDirection.Contains("N"))
            {
                bounds.Y = Math.Min(bounds.Bottom - minimumSize, bounds.Top + delta.Y);
                bounds.Height = _resizeOriginalBounds.Bottom - bounds.Top;
            }
            else if (_resizeHandleDirection.Contains("S"))
            {
                bounds.Height = Math.Max(minimumSize, _resizeOriginalBounds.Height + delta.Y);
            }

            if (_resizeHandleDirection.Length == 2 &&
                (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
            {
                double aspectRatio = _resizeOriginalBounds.Width / _resizeOriginalBounds.Height;
                if (bounds.Width / bounds.Height > aspectRatio)
                {
                    bounds.Width = bounds.Height * aspectRatio;
                }
                else
                {
                    bounds.Height = bounds.Width / aspectRatio;
                }

                if (_resizeHandleDirection.Contains("W")) bounds.X = _resizeOriginalBounds.Right - bounds.Width;
                if (_resizeHandleDirection.Contains("N")) bounds.Y = _resizeOriginalBounds.Bottom - bounds.Height;
            }

            Rect localBounds = _resizeOriginalLocalBounds;
            if (localBounds.IsEmpty || localBounds.Width <= 0 || localBounds.Height <= 0) return;

            double canvasLeft = Canvas.GetLeft(_resizeElement);
            double canvasTop = Canvas.GetTop(_resizeElement);
            if (double.IsNaN(canvasLeft)) canvasLeft = 0;
            if (double.IsNaN(canvasTop)) canvasTop = 0;

            double scaleX = bounds.Width / localBounds.Width;
            double scaleY = bounds.Height / localBounds.Height;
            var transformGroup = new TransformGroup();
            transformGroup.Children.Add(new ScaleTransform(scaleX, scaleY));
            transformGroup.Children.Add(new TranslateTransform(
                bounds.Left - canvasLeft - (localBounds.Left * scaleX),
                bounds.Top - canvasTop - (localBounds.Top * scaleY)));
            _resizeElement.RenderTransform = transformGroup;
            // A maior escala impede que um esticamento em qualquer direção
            // aumente visualmente a espessura do traço.
            double strokeScale = Math.Max(scaleX, scaleY);
            foreach (var pair in _resizeOriginalStrokeThickness)
            {
                pair.Key.StrokeThickness = _strokeThicknessBaselines[pair.Key] / Math.Max(0.01, strokeScale);
            }
            double textScale = Math.Max(0.01, scaleY);
            foreach (var pair in _resizeOriginalFontSizes)
            {
                pair.Key.FontSize = _fontSizeBaselines[pair.Key];
                pair.Key.RenderTransform = new ScaleTransform(
                    1 / Math.Max(0.01, scaleX),
                    1 / textScale);
                pair.Key.RenderTransformOrigin = new Point(0, 0);
            }
            UpdateSelectionVisuals();
        }

        private static IEnumerable<Shape> GetDescendantShapes(UIElement element)
        {
            if (element is Shape shape)
            {
                yield return shape;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is UIElement child)
                {
                    foreach (var descendant in GetDescendantShapes(child))
                    {
                        yield return descendant;
                    }
                }
            }
        }

        private static IEnumerable<TextBlock> GetDescendantTextBlocks(UIElement element)
        {
            if (element is TextBlock textBlock)
            {
                yield return textBlock;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                if (VisualTreeHelper.GetChild(element, i) is UIElement child)
                {
                    foreach (var descendant in GetDescendantTextBlocks(child))
                    {
                        yield return descendant;
                    }
                }
            }
        }

        private void ApplyTranslationToElement(UIElement element, Vector delta)
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
            else
            {
                element.RenderTransform = new TranslateTransform(delta.X, delta.Y);
            }
        }

        // =========================================================

        private void DrawCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_presentationMode) return;
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
                _eraserSession = new EraserSessionCommand(DrawCanvas);
                _lastEraserPoint = null; // garante que o primeiro corte do gesto sempre acontece
                ApplyEraserAtPoint(currentClick);
                DrawCanvas.CaptureMouse();
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
                    _smoothCurrentPenStroke = _currentTool == ToolType.Pen ? _handwritingSmoothingLevel : 0;
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
                    var line = new Line
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
                    if (_lineLabel == null)
                    {
                        _currentShape = line;
                    }
                    else
                    {
                        line.X1 = 0;
                        line.Y1 = 0;
                        line.X2 = 0;
                        line.Y2 = 0;
                        var lineGroup = new Canvas
                        {
                            Background = Brushes.Transparent,
                            IsHitTestVisible = true
                        };
                        lineGroup.Children.Add(line);
                        var label = new TextBlock
                        {
                            Text = _lineLabel,
                            Foreground = brush,
                            FontSize = 13,
                            FontWeight = FontWeights.Bold,
                            Background = Brushes.Transparent,
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(label, 6);
                        Canvas.SetTop(label, -20);
                        lineGroup.Children.Add(label);
                        Canvas.SetLeft(lineGroup, _startPoint.X);
                        Canvas.SetTop(lineGroup, _startPoint.Y);
                        lineGroup.Width = 1;
                        lineGroup.Height = 1;
                        _currentShape = lineGroup;
                    }
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
                        Fill = _rectangleFilledMode ? brush : Brushes.Transparent
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
                        StrokeThickness = thickness,
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
                FontSize = GetTextFontSize(_currentThickness),
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

        // Aplica a borracha em um ponto DENTRO de uma sessão já aberta (ver
        // _eraserSession). Não mexe no UndoManager diretamente — cada
        // remoção/adição só é registrada dentro da sessão, e um único
        // comando é empilhado no Undo quando o mouse é solto.
        private void ApplyEraserAtPoint(Point erasePoint)
        {
            if (_eraserSession == null) return;

            // Throttling por distância: só reprocessa o corte se o mouse já se
            // moveu o suficiente desde a última vez. Isso evita recalcular
            // geometria a cada pixel — o maior vilão do travamento.
            if (_lastEraserPoint.HasValue)
            {
                double movedDistance = (erasePoint - _lastEraserPoint.Value).Length;
                if (movedDistance < EraserStepDistance)
                {
                    return;
                }
            }
            _lastEraserPoint = erasePoint;

            double eraserRadius = Math.Max(12, GetEffectiveThickness() * 3);

            foreach (UIElement child in DrawCanvas.Children.OfType<UIElement>().ToList())
            {
                if (IsSelectionVisual(child)) continue;

                if (child is TextBox activeBox)
                {
                    CommitTextBox(activeBox);
                    continue;
                }

                // Modo "Em Pedaços": traços de caneta/marca-texto são cortados ponto a ponto
                if (!_eraserWholeMode && child is Polyline polyline)
                {
                    var currentSegment = new PointCollection();
                    bool segmentSplit = false;
                    Vector offset = GetCanvasTranslationOffset(polyline);

                    foreach (Point pt in polyline.Points)
                    {
                        Point canvasPoint = pt + offset;
                        if ((canvasPoint - erasePoint).Length < eraserRadius)
                        {
                            segmentSplit = true;
                            if (currentSegment.Count > 1)
                            {
                                _eraserSession.Add(CreatePartialPolyline(polyline, currentSegment));
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
                        _eraserSession.Remove(polyline);
                        if (currentSegment.Count > 1)
                        {
                            _eraserSession.Add(CreatePartialPolyline(polyline, currentSegment));
                        }
                    }
                    continue;
                }

                // Modo "Em Pedaços": Retângulo, Elipse, Linha e Seta ganham um recorte
                // de verdade (buraco na geometria), estilo borracha do Paint.
                if (!_eraserWholeMode && (child is Rectangle || child is Ellipse || child is Line || child is Path))
                {
                    var (touched, replacement) = TryCutShape(child, erasePoint, eraserRadius);
                    if (touched)
                    {
                        if (_selectedElements.Contains(child)) ClearSelection();
                        _eraserSession.Remove(child);
                        if (replacement != null)
                        {
                            _eraserSession.Add(replacement);
                        }
                    }
                    continue;
                }

                // Modo "Inteiro" (ou formas sem suporte a corte parcial: FVG, Texto, Path multi-segmento)
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
                    _eraserSession.Remove(child);
                }
            }
        }

        // Tenta "morder" um pedaço real da forma no ponto da borracha, usando
        // subtração de geometria (CombinedGeometry/Exclude) — o mesmo truque
        // usado em editores vetoriais pra simular a borracha do Paint sem
        // precisar converter nada pra bitmap.
        //
        // IMPORTANTE sobre performance: se "child" já é o RESULTADO de um corte
        // anterior (um Path cuja tag é "ERASED_SHAPE"), a geometria dele já
        // representa uma ÁREA (não um traço fino) — então aplicamos o Exclude
        // DIRETO nela, sem chamar GetWidenedPathGeometry de novo. Recalcular o
        // contorno "largo" (widen) em cima de uma geometria já complexa, a cada
        // pixel de movimento do mouse, é o que travava a tela: o custo cresce
        // muito rápido conforme a forma vai sendo mordida várias vezes.
        //
        // Retorna (false, null) se a borracha nem tocou a forma.
        // Retorna (true, null) se a borracha cobriu a forma inteira (remover sem substituir).
        // Retorna (true, Path) com a forma já "mordida" no ponto certo.

        // Diz se um Brush representa um preenchimento visível de verdade (cor
        // sólida com opacidade real), em vez de vazio/transparente. Usado para
        // decidir se a borracha deve morder a ÁREA inteira (Candle, Retângulo
        // Preenchido) ou só o CONTORNO fino (Retângulo Vazado, Círculo, etc.).
        private static bool IsVisuallyFilled(Brush? brush)
        {
            if (brush == null) return false;
            if (brush == Brushes.Transparent) return false;
            if (brush is SolidColorBrush solid) return solid.Color.A > 0;
            return true; // outros tipos de Brush (gradiente etc.) — assume preenchido
        }

        private (bool touched, Path? replacement) TryCutShape(UIElement child, Point erasePoint, double eraserRadius)
        {
            Vector offset = GetCanvasTranslationOffset(child);
            double offsetX = offset.X;
            double offsetY = offset.Y;

            // Caso especial: "child" já é uma forma que resultou de um corte
            // anterior. A geometria dela já é uma ÁREA fechada — pula direto
            // pro Exclude, sem recalcular widen (ver comentário acima do método).
            if (child is Path alreadyCutPath && Equals(alreadyCutPath.Tag, "ERASED_SHAPE") && alreadyCutPath.Data != null)
            {
                var translatedData = TranslateGeometry(alreadyCutPath.Data, offsetX, offsetY);
                return CutAreaGeometry(translatedData, alreadyCutPath.Fill, alreadyCutPath.Stroke, alreadyCutPath.StrokeThickness, erasePoint, eraserRadius);
            }

            Geometry? sourceGeometry = null;
            Brush? fillBrush = null;
            Brush? strokeBrush = null;
            double strokeThickness = 0;

            switch (child)
            {
                case Rectangle rect:
                    if (rect.Width <= 0 || rect.Height <= 0) return (false, null);
                    if (IsVisuallyFilled(rect.Fill))
                    {
                        // Preenchido de verdade (Candle, ou Retângulo no modo
                        // "Preenchido") — usa a área inteira mesmo.
                        sourceGeometry = new RectangleGeometry(new Rect(offsetX, offsetY, rect.Width, rect.Height));
                        fillBrush = rect.Fill;
                        strokeBrush = rect.Stroke;
                        strokeThickness = rect.StrokeThickness;
                    }
                    else
                    {
                        // Retângulo vazado (Fill=Transparent/nulo) — usa o
                        // CONTORNO (não a área preenchida), igual já acontece
                        // com Linha/Seta/Caneta. Sem isso, a borracha "comia"
                        // um buraco na área inteira, incluindo o miolo vazio,
                        // em vez de apagar só o traço.
                        var rectGeo = new RectangleGeometry(new Rect(offsetX, offsetY, rect.Width, rect.Height));
                        var rectPen = new Pen(rect.Stroke, Math.Max(1, rect.StrokeThickness))
                        {
                            LineJoin = PenLineJoin.Round
                        };
                        sourceGeometry = rectGeo.GetWidenedPathGeometry(rectPen);
                        fillBrush = rect.Stroke;
                        strokeBrush = null;
                        strokeThickness = 0;
                    }
                    break;

                case Ellipse ellipse:
                    if (ellipse.Width <= 0 || ellipse.Height <= 0) return (false, null);
                    {
                        // Mesmo raciocínio do Retângulo acima: contorno, não área cheia.
                        var ellipseGeo = new EllipseGeometry(new Rect(offsetX, offsetY, ellipse.Width, ellipse.Height));
                        var ellipsePen = new Pen(ellipse.Stroke, Math.Max(1, ellipse.StrokeThickness))
                        {
                            LineJoin = PenLineJoin.Round
                        };
                        sourceGeometry = ellipseGeo.GetWidenedPathGeometry(ellipsePen);
                        fillBrush = ellipse.Stroke;
                        strokeBrush = null;
                        strokeThickness = 0;
                    }
                    break;

                case Line line:
                    {
                        var lineGeo = new LineGeometry(
                            new Point(line.X1 + offsetX, line.Y1 + offsetY),
                            new Point(line.X2 + offsetX, line.Y2 + offsetY));
                        var pen = new Pen(line.Stroke, Math.Max(1, line.StrokeThickness))
                        {
                            StartLineCap = PenLineCap.Round,
                            EndLineCap = PenLineCap.Round
                        };
                        // GetWidenedPathGeometry só é caro se a geometria de origem
                        // já for complexa. Aqui a Line é sempre um traço simples
                        // (2 pontos), então o custo é baixo — problema só existia
                        // pro caso "Path" abaixo, que agora tem tratamento especial.
                        sourceGeometry = lineGeo.GetWidenedPathGeometry(pen);
                        fillBrush = line.Stroke;
                    }
                    break;

                case Path p when p.Data != null:
                    {
                        var pen = new Pen(p.Stroke, Math.Max(1, p.StrokeThickness))
                        {
                            LineJoin = PenLineJoin.Round,
                            StartLineCap = PenLineCap.Round,
                            EndLineCap = PenLineCap.Round
                        };
                        sourceGeometry = TranslateGeometry(p.Data, offsetX, offsetY).GetWidenedPathGeometry(pen);
                        fillBrush = p.Stroke;
                    }
                    break;

                default:
                    return (false, null);
            }

            if (sourceGeometry == null) return (false, null);

            return CutAreaGeometry(sourceGeometry, fillBrush, strokeBrush, strokeThickness, erasePoint, eraserRadius);
        }

        private static Vector GetCanvasTranslationOffset(UIElement element)
        {
            double offsetX = 0;
            double offsetY = 0;
            double left = Canvas.GetLeft(element);
            double top = Canvas.GetTop(element);
            if (!double.IsNaN(left)) offsetX += left;
            if (!double.IsNaN(top)) offsetY += top;

            if (element.RenderTransform is TranslateTransform translate)
            {
                offsetX += translate.X;
                offsetY += translate.Y;
            }
            else if (element.RenderTransform is TransformGroup transformGroup)
            {
                Point transformOffset = transformGroup.Value.Transform(new Point());
                offsetX += transformOffset.X;
                offsetY += transformOffset.Y;
            }

            return new Vector(offsetX, offsetY);
        }

        private static Geometry TranslateGeometry(Geometry geometry, double offsetX, double offsetY)
        {
            var translatedGeometry = geometry.Clone();
            if (offsetX == 0 && offsetY == 0) return translatedGeometry;

            var transform = new TransformGroup();
            if (!translatedGeometry.Transform.Value.IsIdentity)
            {
                transform.Children.Add(translatedGeometry.Transform);
            }
            transform.Children.Add(new TranslateTransform(offsetX, offsetY));
            translatedGeometry.Transform = transform;
            return translatedGeometry;
        }

        // Recebe uma geometria que já representa uma ÁREA fechada (não um traço
        // fino) e faz o corte (Exclude) do círculo da borracha nela. Usado tanto
        // para a primeira mordida (geometria vinda de GetWidenedPathGeometry)
        // quanto para mordidas seguintes em cima de um Path já cortado antes.
        private (bool touched, Path? replacement) CutAreaGeometry(Geometry sourceGeometry, Brush? fillBrush, Brush? strokeBrush, double strokeThickness, Point erasePoint, double eraserRadius)
        {
            var eraserGeometry = new EllipseGeometry(erasePoint, eraserRadius, eraserRadius);

            if (!sourceGeometry.Bounds.IntersectsWith(eraserGeometry.Bounds))
            {
                return (false, null); // borracha nem chegou perto — não mexe na forma
            }

            // Se o círculo da borracha cobre toda a área da forma, é mais simples remover
            // por completo do que gerar uma geometria "vazia".
            if (eraserGeometry.Bounds.Contains(sourceGeometry.Bounds))
            {
                return (true, null);
            }

            var combined = new CombinedGeometry(GeometryCombineMode.Exclude, sourceGeometry, eraserGeometry);

            // "Achata" o resultado (converte a árvore de CombinedGeometry em um
            // único PathGeometry de segmentos retos) ANTES de guardar. Isso é o
            // que impede a geometria de crescer indefinidamente em profundidade
            // a cada mordida — sem isso, a 10ª mordida estaria recalculando uma
            // árvore com 10 níveis de CombinedGeometry aninhados a cada frame.
            PathGeometry resultGeometry = combined.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
            resultGeometry.Freeze();

            var replacement = new Path
            {
                Data = resultGeometry,
                Fill = fillBrush,
                Stroke = strokeBrush,
                StrokeThickness = strokeThickness,
                Tag = "ERASED_SHAPE"
            };

            return (true, replacement);
        }

        private Polyline CreatePartialPolyline(Polyline original, PointCollection points)
        {
            var replacement = new Polyline
            {
                Stroke = original.Stroke,
                StrokeThickness = original.StrokeThickness,
                StrokeLineJoin = original.StrokeLineJoin,
                StrokeStartLineCap = original.StrokeStartLineCap,
                StrokeEndLineCap = original.StrokeEndLineCap,
                Points = points,
                RenderTransform = original.RenderTransform != null ? original.RenderTransform.Clone() : new TranslateTransform()
            };
            Canvas.SetLeft(replacement, Canvas.GetLeft(original));
            Canvas.SetTop(replacement, Canvas.GetTop(original));
            return replacement;
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

            Color zoneColor = _zoneMode == "BPR"
                ? Color.FromRgb(255, 152, 0)
                : _currentColor;
            byte fillOpacity = _zoneMode == "OB" ? (byte)15 : (byte)35;
            var fillBrush = new SolidColorBrush(Color.FromArgb(fillOpacity, zoneColor.R, zoneColor.G, zoneColor.B));
            var borderBrush = new SolidColorBrush(zoneColor);

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
                Text = _zoneMode,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = borderBrush,
                Margin = new Thickness(Math.Max(0, width - 32), Math.Max(2, (height / 2) - 18), 0, 0),
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

        private void ZoneModeArrow_Click(object sender, RoutedEventArgs e)
        {
            ZoneModePopup.IsOpen = !ZoneModePopup.IsOpen;
        }

        private void ZoneModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            _zoneMode = button.Tag?.ToString() switch
            {
                "BPR" => "BPR",
                "OB" => "OB",
                _ => "FVG"
            };

            ZoneModeIcon.Text = _zoneMode switch
            {
                "BPR" => "🟧",
                "OB" => "⬜",
                _ => "🟦"
            };
            ZoneModeLabel.Text = $"{_zoneMode} (9)";
            ZoneModePopup.IsOpen = false;
            SwitchTool(ToolType.FVG);
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
                label.Margin = new Thickness(Math.Max(0, width - 32), Math.Max(2, (height / 2) - 18), 0, 0);
            }
        }

        private void UpdateLabeledLineGroup(Canvas lineGroup, Point start, Point current)
        {
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                current = new Point(current.X, start.Y);
            }

            double x = Math.Min(start.X, current.X);
            double y = Math.Min(start.Y, current.Y);
            double width = Math.Max(1, Math.Abs(current.X - start.X));
            double height = Math.Max(1, Math.Abs(current.Y - start.Y));

            Canvas.SetLeft(lineGroup, x);
            Canvas.SetTop(lineGroup, y);
            lineGroup.Width = width;
            lineGroup.Height = height;

            if (lineGroup.Children[0] is Line line)
            {
                line.X1 = start.X - x;
                line.Y1 = start.Y - y;
                line.X2 = current.X - x;
                line.Y2 = current.Y - y;
            }

            if (lineGroup.Children[1] is TextBlock label)
            {
                Canvas.SetLeft(label, width + 6);
                Canvas.SetTop(label, Math.Max(0, height / 2 - 9));
            }
        }

        private void DrawCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_presentationMode) return;
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
            Rectangle? targetCandle = null;

            double minDistance = double.MaxValue;

            foreach (var child in DrawCanvas.Children.OfType<Rectangle>())
            {
                if (Equals(child.Tag, "CANDLE_BODY"))
                {
                    double candleLeft = Canvas.GetLeft(child);
                    double candleWidth = child.Width > 0 ? child.Width : GetCandleWidth();
                    if (double.IsNaN(candleLeft)) candleLeft = 0;
                    double localCenterX = child.RenderTransform?.Transform(new Point(candleWidth / 2, 0)).X
                        ?? candleWidth / 2;
                    double candleCenterX = candleLeft + localCenterX;

                    double dist = Math.Abs(clickPos.X - candleCenterX);

                    if (dist < 30 && dist < minDistance)
                    {
                        minDistance = dist;
                        targetX = candleCenterX;
                        candleFound = true;
                        targetCandle = child;
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
                StrokeThickness = GetEffectiveThickness(),
                X1 = targetX,
                Y1 = clickPos.Y,
                X2 = targetX,
                Y2 = clickPos.Y,
                Tag = targetCandle,
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

            // Guia de largura do candle: mostra antes de clicar, some assim que começa a arrastar
            if (!_presentationMode && _currentTool == ToolType.Candle && _currentShape == null)
            {
                double previewWidth = GetCandleWidth();
                CandleWidthPreview.Width = previewWidth;
                Canvas.SetLeft(CandleWidthPreview, currentPoint.X - (previewWidth / 2));
                Canvas.SetTop(CandleWidthPreview, currentPoint.Y - (CandleWidthPreview.Height / 2));
                CandleWidthPreview.Visibility = Visibility.Visible;
            }
            else
            {
                CandleWidthPreview.Visibility = Visibility.Collapsed;
            }

            // Prévia da borracha: ao contrário do candle, continua visível mesmo
            // enquanto o botão está pressionado (apagando de verdade).
            if (!_presentationMode && _currentTool == ToolType.Eraser)
            {
                double eraserPreviewRadius = Math.Max(12, GetEffectiveThickness() * 3);
                double eraserPreviewDiameter = eraserPreviewRadius * 2;
                EraserPreview.Width = eraserPreviewDiameter;
                EraserPreview.Height = eraserPreviewDiameter;
                Canvas.SetLeft(EraserPreview, currentPoint.X - eraserPreviewRadius);
                Canvas.SetTop(EraserPreview, currentPoint.Y - eraserPreviewRadius);
                EraserPreview.Visibility = Visibility.Visible;
            }
            else
            {
                EraserPreview.Visibility = Visibility.Collapsed;
            }

            if (_isEditingLineEndpoint && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateLineEndpoint(currentPoint);
                return;
            }

            if (_isResizingElement && e.LeftButton == MouseButtonState.Pressed)
            {
                ResizeSelectedElement(currentPoint);
                return;
            }

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
            else if (_currentShape is Canvas lineGroup && _currentTool == ToolType.Line)
            {
                UpdateLabeledLineGroup(lineGroup, _startPoint, currentPoint);
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
            if (_presentationMode) return;
            if (!_drawingMode) return;
            if (e.ChangedButton != MouseButton.Left) return;

            if (_isEditingLineEndpoint)
            {
                _isEditingLineEndpoint = false;
                DrawCanvas.ReleaseMouseCapture();

                if (_lineEndpointElement != null &&
                    (_lineEndpointStartPoint - e.GetPosition(DrawCanvas)).Length > 0.5)
                {
                    Point afterPoint = _editingLineStartEndpoint
                        ? new Point(_lineEndpointElement.X1, _lineEndpointElement.Y1)
                        : new Point(_lineEndpointElement.X2, _lineEndpointElement.Y2);
                    _undoManager.RegisterCompletedCommand(new EditLineEndpointCommand(
                        _lineEndpointElement,
                        _editingLineStartEndpoint,
                        _lineEndpointBeforePoint,
                        afterPoint,
                        _lineEndpointLabel,
                        _lineEndpointBeforeLabelPoint,
                        _lineEndpointLabel == null
                            ? default
                            : new Point(Canvas.GetLeft(_lineEndpointLabel), Canvas.GetTop(_lineEndpointLabel))));
                }

                _lineEndpointElement = null;
                _lineEndpointLabel = null;
                UpdateSelectionBoxes();
                return;
            }

            if (_isResizingElement)
            {
                _isResizingElement = false;
                DrawCanvas.ReleaseMouseCapture();

                if (_resizeElement != null &&
                    _resizeElement.RenderTransform is Transform resizedTransform &&
                    (_resizeStartPoint - e.GetPosition(DrawCanvas)).Length > 0.5)
                {
                    _undoManager.ExecuteCommand(new ResizeStrokeCommand(
                        _resizeElement,
                        _resizeOriginalTransform,
                        resizedTransform,
                        new Dictionary<Shape, double>(_resizeOriginalStrokeThickness),
                        _resizeOriginalStrokeThickness.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Key.StrokeThickness),
                        new Dictionary<TextBlock, double>(_resizeOriginalFontSizes),
                        _resizeOriginalFontSizes.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Key.FontSize),
                        new Dictionary<TextBlock, Transform?>(_resizeOriginalTextTransforms),
                        _resizeOriginalTextTransforms.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Key.RenderTransform?.Clone())));
                }

                _resizeElement = null;
                _resizeHandleDirection = null;
                UpdateSelectionBoxes();
                return;
            }

            if (_eraserSession != null)
            {
                DrawCanvas.ReleaseMouseCapture();

                // Só registra no Undo se a borracha realmente apagou algo nesse
                // arraste (evita empilhar um passo "vazio" quando você só
                // encostou e soltou sem tocar em nenhum desenho).
                if (_eraserSession.HasChanges)
                {
                    _undoManager.RegisterCompletedCommand(_eraserSession);
                }

                _eraserSession = null;
                _lastEraserPoint = null;
                return;
            }

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

            CommitCurrentShape();
        }

        private void DrawCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_presentationMode) return;
            if (!_drawingMode || _currentShape == null) return;
            if (e.ChangedButton != MouseButton.Right) return;

            CommitCurrentShape();
            e.Handled = true;
        }

        private void CommitCurrentShape()
        {
            if (_currentShape == null) return;

            if (_smoothCurrentPenStroke > 0 && _currentShape is Polyline penStroke)
            {
                penStroke.Points = StrokeSmoother.Smooth(penStroke.Points, _smoothCurrentPenStroke);
            }

            _undoManager.ExecuteCommand(new AddStrokeCommand(DrawCanvas, _currentShape));
            _currentShape = null;
            _smoothCurrentPenStroke = 0;
            DrawCanvas.ReleaseMouseCapture();
        }

        private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingToolbar = true;
            _toolbarDragged = false;
            Point cursorPosition = e.GetPosition(UIOverlayCanvas);
            _toolbarDragStart = new Point(
                cursorPosition.X - Canvas.GetLeft(ToolbarBorder),
                cursorPosition.Y - Canvas.GetTop(ToolbarBorder));
            ToolbarBorder.CaptureMouse();
            e.Handled = true;
        }

        private void Toolbar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingToolbar)
            {
                var currentPos = e.GetPosition(UIOverlayCanvas);
                double newLeft = currentPos.X - _toolbarDragStart.X;
                double newTop = currentPos.Y - _toolbarDragStart.Y;

                // Só conta como "arrastou de verdade" se moveu mais que alguns pixels
                // (evita que um clique com a mão trêmula seja confundido com drag).
                if (Math.Abs(newLeft - Canvas.GetLeft(ToolbarBorder)) > 2 ||
                    Math.Abs(newTop - Canvas.GetTop(ToolbarBorder)) > 2)
                {
                    _toolbarDragged = true;
                }

                Canvas.SetLeft(ToolbarBorder, newLeft);
                Canvas.SetTop(ToolbarBorder, newTop);
            }
        }

        private void Toolbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingToolbar)
            {
                _isDraggingToolbar = false;
                ToolbarBorder.ReleaseMouseCapture();

                // Um clique simples só minimiza quando ocorre no logo/nome do app.
                if (!_toolbarDragged && ToolbarDragHandle.IsMouseOver)
                {
                    MinimizeToolbar();
                }

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
            MinimizeToolbar();
        }

        private void MinimizeToolbar()
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

        private void EraserModeArrow_Click(object sender, RoutedEventArgs e)
        {
            EraserModePopup.IsOpen = !EraserModePopup.IsOpen;
        }

        private void EraserModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _eraserWholeMode = (btn.Tag as string) == "Whole";
                EraserModeIcon.Text = _eraserWholeMode ? "🧹" : "✂️";
                SwitchTool(ToolType.Eraser);
                EraserModePopup.IsOpen = false;
            }
        }

        private void RectangleModeArrow_Click(object sender, RoutedEventArgs e)
        {
            RectangleModePopup.IsOpen = !RectangleModePopup.IsOpen;
        }

        private void RectangleModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _rectangleFilledMode = (btn.Tag as string) == "Filled";
                RectangleModeIcon.Text = _rectangleFilledMode ? "⬛" : "🔲";
                SwitchTool(ToolType.Rectangle);
                RectangleModePopup.IsOpen = false;
            }
        }

        private void LineModeArrow_Click(object sender, RoutedEventArgs e)
        {
            LineModePopup.IsOpen = !LineModePopup.IsOpen;
        }

        private void LineModeOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;

            _lineLabel = button.Tag?.ToString() switch
            {
                "CISD" => "CISD",
                "PX" => "PX",
                "BoS" => "BoS",
                "ChoCh" => "ChoCh",
                "MSS" => "MSS",
                "Liquidez" => "Liquidez",
                _ => null
            };

            LineModeIcon.Text = _lineLabel == null ? "📏" : $"{_lineLabel}";
            LineModePopup.IsOpen = false;
            SwitchTool(ToolType.Line);
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
                    "Orange" => "#FF6D00",
                    "Purple" => "#AA00FF",
                    "Pink" => "#F50057",
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

        private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            double previousThickness = _currentThickness;
            double newThickness = e.NewValue;
            _currentThickness = e.NewValue;
            if (ThicknessValueLabel != null)
            {
                ThicknessValueLabel.Text = $"{e.NewValue:0} px";
            }

            if (_selectedElements.Count == 0 || previousThickness <= 0 || previousThickness == newThickness)
            {
                return;
            }

            var beforeStrokeThickness = new Dictionary<Shape, double>();
            var beforeFontSizes = new Dictionary<TextBlock, double>();
            var beforeCandleWidths = new Dictionary<Rectangle, double>();
            var beforeCandleLefts = new Dictionary<Rectangle, double>();
            var beforeWickPoints = new Dictionary<Line, (double X1, double Y1, double X2, double Y2)>();

            foreach (var element in _selectedElements)
            {
                var shapes = GetDescendantShapes(element).ToList();
                foreach (var shape in shapes)
                {
                    if (shape.StrokeThickness == newThickness) continue;

                    beforeStrokeThickness.TryAdd(shape, shape.StrokeThickness);
                    shape.StrokeThickness = newThickness;
                }

                foreach (var textBlock in GetDescendantTextBlocks(element))
                {
                    double newFontSize = GetTextFontSize(newThickness);
                    if (textBlock.FontSize == newFontSize) continue;

                    beforeFontSizes.TryAdd(textBlock, textBlock.FontSize);
                    textBlock.FontSize = newFontSize;
                }

            }

            var selectedCandles = _selectedElements
                .SelectMany(GetDescendantShapes)
                .OfType<Rectangle>()
                .Where(shape => Equals(shape.Tag, "CANDLE_BODY") && shape.Width > 0)
                .Distinct()
                .ToList();

            double newCandleWidth = GetCandleWidth(newThickness);
            var candleBounds = selectedCandles.ToDictionary(candle => candle, GetTransformedElementCanvasBounds);
            double averageCurrentCandleWidth = selectedCandles.Count == 0
                ? 0
                : selectedCandles.Average(candle => candle.Width);
            double candleSpacingScale = averageCurrentCandleWidth > 0
                ? newCandleWidth / averageCurrentCandleWidth
                : 1;
            double candleCenter = selectedCandles.Count == 0
                ? 0
                : selectedCandles.Average(candle => candleBounds[candle].X + candleBounds[candle].Width / 2);

            foreach (var candle in selectedCandles)
            {
                Rect bounds = candleBounds[candle];
                double currentCenter = bounds.X + bounds.Width / 2;
                double newCenter = candleCenter + ((currentCenter - candleCenter) * candleSpacingScale);
                double centerDelta = newCenter - currentCenter;
                double widthDelta = newCandleWidth - candle.Width;
                if (Math.Abs(centerDelta) < 0.001 && Math.Abs(widthDelta) < 0.001) continue;

                double left = Canvas.GetLeft(candle);
                if (double.IsNaN(left)) left = 0;

                beforeCandleWidths.TryAdd(candle, candle.Width);
                beforeCandleLefts.TryAdd(candle, left);
                Canvas.SetLeft(candle, left + centerDelta - (widthDelta / 2));
                candle.Width = newCandleWidth;

                foreach (var wick in DrawCanvas.Children.OfType<Line>()
                             .Where(line => ReferenceEquals(line.Tag, candle)))
                {
                    if (Math.Abs(centerDelta) < 0.001) continue;

                    beforeWickPoints.TryAdd(wick, (wick.X1, wick.Y1, wick.X2, wick.Y2));
                    wick.X1 += centerDelta;
                    wick.X2 += centerDelta;
                }
            }

            if (beforeStrokeThickness.Count == 0 && beforeFontSizes.Count == 0 &&
                beforeCandleWidths.Count == 0 && beforeWickPoints.Count == 0)
            {
                return;
            }

            _undoManager.RegisterCompletedCommand(new ChangeSelectedSizeCommand(
                beforeStrokeThickness,
                beforeStrokeThickness.ToDictionary(pair => pair.Key, pair => pair.Key.StrokeThickness),
                beforeFontSizes,
                beforeFontSizes.ToDictionary(pair => pair.Key, pair => pair.Key.FontSize),
                beforeCandleWidths,
                beforeCandleWidths.ToDictionary(pair => pair.Key, pair => pair.Key.Width),
                beforeCandleLefts,
                beforeCandleLefts.ToDictionary(pair => pair.Key, pair => Canvas.GetLeft(pair.Key)),
                beforeWickPoints,
                beforeWickPoints.ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Key.X1, pair.Key.Y1, pair.Key.X2, pair.Key.Y2))));
            UpdateSelectionBoxes();
        }

        private void MainWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_drawingMode) return;

            ThicknessSlider.Value = Math.Clamp(
                ThicknessSlider.Value + Math.Sign(e.Delta),
                ThicknessSlider.Minimum,
                ThicknessSlider.Maximum);
            e.Handled = true;
        }

        private void SmoothWritingButton_Click(object sender, RoutedEventArgs e)
        {
            _handwritingSmoothingLevel = (_handwritingSmoothingLevel + 1) % 3;
            bool isEnabled = _handwritingSmoothingLevel > 0;
            SmoothWritingButton.Content = _handwritingSmoothingLevel == 2 ? "≋" : "≈";
            SmoothWritingButton.BorderBrush = isEnabled
                ? (Brush)FindResource("ToolbarAccentBrush")
                : (Brush)FindResource("ToolbarBorderBrush");
            SmoothWritingButton.Foreground = (Brush)FindResource("ToolbarTextBrush");
            SmoothWritingButton.Background = isEnabled
                ? (Brush)FindResource("ToolbarHoverBrush")
                : (Brush)FindResource("ToolbarButtonBrush");
            SmoothWritingButton.ToolTip = _handwritingSmoothingLevel switch
            {
                1 => "Suavização suave ativada (somente Caneta); clique para aumentar",
                2 => "Suavização forte ativada (somente Caneta); clique para desligar",
                _ => "Suavização desativada; clique para ativar (somente Caneta)"
            };
        }

        private void ToggleCanvasVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (DrawCanvas.Visibility == Visibility.Visible)
            {
                DrawCanvas.Visibility = Visibility.Collapsed;
                SetSelectionVisualsVisibility(Visibility.Collapsed);
                ToggleCanvasVisibilityIcon.Text = "🙈";
                ToggleCanvasVisibilityLabel.Text = "MOSTRAR";
                ToggleCanvasVisibilityButton.ToolTip = "Mostrar Desenhos";

                if (_drawingMode)
                {
                    ToggleMode();
                }
            }
            else
            {
                DrawCanvas.Visibility = Visibility.Visible;
                SetSelectionVisualsVisibility(Visibility.Visible);
                ToggleCanvasVisibilityIcon.Text = "👁️";
                ToggleCanvasVisibilityLabel.Text = "OCULTAR";
                ToggleCanvasVisibilityButton.ToolTip = "Ocultar Desenhos";
            }

            UpdateCrosshairVisibility();
        }

        private void MouseModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_drawingMode)
            {
                ToggleMode();
            }
        }

        private void DrawingModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_drawingMode)
            {
                ToggleMode();
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

        // ================== Abas (histórico de telas da aula) ==================

        /// <summary>
        /// Guarda o que está no canvas agora dentro da aba ativa (se ela já existia)
        /// ou, se for trabalho novo ainda não numerado, transforma em uma aba nova.
        /// Não mexe no que está visível na tela — só atualiza o "arquivo" interno.
        /// </summary>
        private void SaveCurrentIntoActiveTabSlot(bool saveEmptyCanvas = false)
        {
            var currentElements = DrawCanvas.Children.OfType<UIElement>().ToList();

            if (_activeTab != null)
            {
                // Já era uma aba salva (reaberta pra editar) - atualiza o conteúdo dela
                _activeTab.Elements = currentElements;
                _activeTab.UndoManager = _undoManager;
            }
            else if (currentElements.Count > 0 || saveEmptyCanvas)
            {
                // Trabalho novo, ainda sem número - vira a próxima aba
                var tab = new DrawingTab
                {
                    Number = _nextTabNumber++,
                    Elements = currentElements,
                    UndoManager = _undoManager
                };
                _savedTabs.Add(tab);
                AddTabButton(tab);
            }
        }

        private void NovaAbaButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
            SaveCurrentIntoActiveTabSlot(saveEmptyCanvas: true);

            DrawCanvas.Children.Clear();
            _undoManager = new UndoManager();
            _activeTab = null;
            HighlightTabButton(null);
        }

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int tabNumber) return;

            var tab = _savedTabs.FirstOrDefault(t => t.Number == tabNumber);
            if (tab == null || tab == _activeTab) return;

            ClearSelection();
            SaveCurrentIntoActiveTabSlot(); // preserva o que está na tela antes de trocar

            DrawCanvas.Children.Clear();
            foreach (var el in tab.Elements)
                DrawCanvas.Children.Add(el);

            _undoManager = tab.UndoManager;
            _activeTab = tab;
            HighlightTabButton(tabNumber);
        }

        private void AddTabButton(DrawingTab tab, int? index = null)
        {
            var closeButton = new Button
            {
                Content = "×",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Width = 16,
                Height = 16,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.IndianRed,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = tab.Number,
                ToolTip = "Fechar esta aba"
            };
            closeButton.Click += CloseTabButton_Click;

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = $"Aba {tab.Number}", VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(closeButton);

            var btn = new Button
            {
                Content = content,
                Tag = tab.Number,
                Style = (Style)FindResource("ThicknessButtonStyle")
            };
            btn.Click += TabButton_Click;
            if (index.HasValue)
            {
                TabsContainer.Children.Insert(Math.Min(index.Value, TabsContainer.Children.Count), btn);
            }
            else
            {
                TabsContainer.Children.Add(btn);
            }
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button closeBtn || closeBtn.Tag is not int tabNumber) return;

            var tab = _savedTabs.FirstOrDefault(t => t.Number == tabNumber);
            if (tab == null) return;

            int tabIndex = _savedTabs.IndexOf(tab);
            bool wasActive = _activeTab == tab;

            void RemoveTab()
            {
                var tabButton = TabsContainer.Children.OfType<Button>().FirstOrDefault(b => b.Tag is int n && n == tabNumber);
                if (tabButton != null) TabsContainer.Children.Remove(tabButton);
                _savedTabs.Remove(tab);

                if (wasActive)
                {
                    DrawCanvas.Children.Clear();
                    _activeTab = null;
                    HighlightTabButton(null);
                }
            }

            void RestoreTab()
            {
                _savedTabs.Insert(Math.Min(tabIndex, _savedTabs.Count), tab);
                AddTabButton(tab, tabIndex);

                if (wasActive)
                {
                    DrawCanvas.Children.Clear();
                    foreach (var element in tab.Elements)
                        DrawCanvas.Children.Add(element);
                    _undoManager = tab.UndoManager;
                    _activeTab = tab;
                    HighlightTabButton(tab.Number);
                }
            }

            RemoveTab();
            _undoManager.RegisterCompletedCommand(new ActionCommand(RemoveTab, RestoreTab));
        }

        private void HighlightTabButton(int? activeNumber)
        {
            var accent = (Brush)FindResource("ToolbarAccentBrush");
            var border = (Brush)FindResource("ToolbarBorderBrush");
            foreach (var child in TabsContainer.Children)
            {
                if (child is Button btn && btn.Tag is int tagNumber)
                {
                    bool isSelected = tagNumber == activeNumber;
                    btn.BorderBrush = isSelected ? accent : border;
                    btn.BorderThickness = new Thickness(isSelected ? 2 : 1);
                }
            }
        }

        private List<UIElement> GetCurrentProjectElements()
        {
            return DrawCanvas.Children.OfType<UIElement>()
                .Where(element => !IsSelectionVisual(element) &&
                                  element != _currentShape &&
                                  element is not TextBox)
                .ToList();
        }

        private ProjectDocument CaptureProjectDocument()
        {
            var currentElements = GetCurrentProjectElements();
            var document = new ProjectDocument
            {
                BoardMode = _boardMode.ToString(),
                ActiveTabNumber = _activeTab?.Number
            };

            foreach (var tab in _savedTabs)
            {
                document.Tabs.Add(new ProjectTab
                {
                    Number = tab.Number,
                    Elements = tab == _activeTab ? currentElements : tab.Elements.ToList()
                });
            }

            if (_activeTab == null && currentElements.Count > 0)
            {
                int number = Math.Max(_nextTabNumber, document.Tabs.Select(tab => tab.Number).DefaultIfEmpty(0).Max() + 1);
                document.Tabs.Add(new ProjectTab { Number = number, Elements = currentElements });
                document.ActiveTabNumber = number;
            }

            return document;
        }

        private void PrepareCurrentDrawingForProjectSave()
        {
            if (_pathPoints.Count > 0) FinalizePathDrawing();
            foreach (var textBox in DrawCanvas.Children.OfType<TextBox>().ToList())
            {
                CommitTextBox(textBox);
            }
            CommitCurrentShape();
            CloseEraserSession();

            if (_marqueeBox != null)
            {
                DrawCanvas.Children.Remove(_marqueeBox);
                _marqueeBox = null;
                _isMarqueeSelecting = false;
                if (DrawCanvas.IsMouseCaptured) DrawCanvas.ReleaseMouseCapture();
            }
        }

        private void SaveProjectButton_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);

        private void SaveProjectAsButton_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

        private void OpenProjectButton_Click(object sender, RoutedEventArgs e) => OpenProject();

        private bool SaveProject(bool saveAs)
        {
            string? path = _currentProjectPath;
            if (saveAs || string.IsNullOrWhiteSpace(path))
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Salvar documento TraderPen",
                    Filter = "Documento TraderPen (*.traderpen)|*.traderpen",
                    DefaultExt = ".traderpen",
                    AddExtension = true,
                    FileName = string.IsNullOrWhiteSpace(path)
                        ? "Meu desenho.traderpen"
                        : System.IO.Path.GetFileName(path),
                    OverwritePrompt = true
                };
                if (dialog.ShowDialog(this) != true) return false;
                path = dialog.FileName;
            }

            try
            {
                PrepareCurrentDrawingForProjectSave();
                var document = CaptureProjectDocument();
                ProjectFileService.Save(path!, document);
                _currentProjectPath = path;
                _savedProjectSnapshot = ProjectFileService.Serialize(document);
                UpdateWindowTitle();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       NotSupportedException or
                                       InvalidOperationException or ArgumentException or FormatException)
            {
                MessageBox.Show($"Não foi possível salvar o documento:\n{ex.Message}", "Erro ao salvar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void OpenProject(string? projectPath = null)
        {
            string? path = projectPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Abrir documento TraderPen",
                    Filter = "Documento TraderPen (*.traderpen)|*.traderpen",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (dialog.ShowDialog(this) != true) return;
                path = dialog.FileName;
            }

            ProjectDocument document;
            try
            {
                document = ProjectFileService.Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       NotSupportedException or
                                       InvalidOperationException or ArgumentException or FormatException or
                                       System.Text.Json.JsonException)
            {
                MessageBox.Show($"Não foi possível abrir o documento:\n{ex.Message}", "Erro ao abrir",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!ConfirmSaveChangesBeforeLeaving()) return;

            ClearSelection();
            CloseEraserSession();
            _currentShape = null;
            _pathPoints.Clear();
            _activePathGroup = null;
            _activePathElement = null;
            _savedTabs.Clear();
            TabsContainer.Children.Clear();

            foreach (var loadedTab in document.Tabs.OrderBy(tab => tab.Number))
            {
                var tab = new DrawingTab
                {
                    Number = loadedTab.Number,
                    Elements = loadedTab.Elements,
                    UndoManager = new UndoManager()
                };
                _savedTabs.Add(tab);
                AddTabButton(tab);
            }

            DrawCanvas.Children.Clear();
            _activeTab = document.ActiveTabNumber.HasValue
                ? _savedTabs.First(tab => tab.Number == document.ActiveTabNumber.Value)
                : null;
            if (_activeTab != null)
            {
                foreach (var element in _activeTab.Elements)
                {
                    DrawCanvas.Children.Add(element);
                }
                _undoManager = _activeTab.UndoManager;
            }
            else
            {
                _undoManager = new UndoManager();
            }

            _nextTabNumber = _savedTabs.Select(tab => tab.Number).DefaultIfEmpty(0).Max() + 1;
            _boardMode = Enum.TryParse(document.BoardMode, out BoardMode boardMode) &&
                         Enum.IsDefined(boardMode)
                ? boardMode
                : BoardMode.Transparent;
            UpdateBoardModeAppearance();
            HighlightTabButton(_activeTab?.Number);
            _currentProjectPath = path;
            _savedProjectSnapshot = ProjectFileService.Serialize(CaptureProjectDocument());
            UpdateWindowTitle();
        }

        private bool ConfirmSaveChangesBeforeLeaving()
        {
            bool hasChanges;
            try
            {
                PrepareCurrentDrawingForProjectSave();
                hasChanges = _savedProjectSnapshot != ProjectFileService.Serialize(CaptureProjectDocument());
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or InvalidOperationException)
            {
                MessageBox.Show($"Não foi possível verificar as alterações do documento:\n{ex.Message}",
                    "Erro no documento", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (!hasChanges) return true;

            string documentName = string.IsNullOrWhiteSpace(_currentProjectPath)
                ? "documento atual"
                : System.IO.Path.GetFileName(_currentProjectPath);
            var result = MessageBox.Show(
                $"Deseja salvar as alterações em {documentName} antes de continuar?",
                "Alterações não salvas",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            return result switch
            {
                MessageBoxResult.Yes => SaveProject(saveAs: false),
                MessageBoxResult.No => true,
                _ => false
            };
        }

        private void UpdateWindowTitle()
        {
            Title = string.IsNullOrWhiteSpace(_currentProjectPath)
                ? "TraderPen Overlay"
                : $"TraderPen - {System.IO.Path.GetFileName(_currentProjectPath)}";
        }

        private void CapturarComposicaoButton_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentIntoActiveTabSlot();

            if (_savedTabs.Count == 0)
            {
                MessageBox.Show("Ainda não tem nenhuma aba salva pra capturar.", "Capturar composição",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new NameInputWindow { Owner = this };
            if (dialog.ShowDialog() != true) return;

            string pastaBase = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "TraderPen", $"{dialog.EnteredName} - Capturas - {DateTime.Now:yyyy-MM-dd_HH-mm-ss}");

            var elementosAtivosOriginais = DrawCanvas.Children.OfType<UIElement>().ToList();
            var fundoOriginal = DrawCanvas.Background;
            BitmapSource? fundoDaTela = null;

            try
            {
                System.IO.Directory.CreateDirectory(pastaBase);

                if (_boardMode == BoardMode.Transparent)
                {
                    fundoDaTela = CaptureScreenBehindOverlay();
                }

                DrawCanvas.Background = Brushes.Transparent;

                foreach (var tab in _savedTabs.OrderBy(t => t.Number))
                {
                    DrawCanvas.Children.Clear();
                    foreach (var el in tab.Elements)
                        DrawCanvas.Children.Add(el);

                    DrawCanvas.UpdateLayout();
                    var desenhos = RenderTargetBitmapFromCanvas();
                    var composicao = new DrawingVisual();

                    using (var drawingContext = composicao.RenderOpen())
                    {
                        var area = new Rect(0, 0, DrawCanvas.ActualWidth, DrawCanvas.ActualHeight);
                        if (fundoDaTela != null)
                        {
                            drawingContext.DrawImage(fundoDaTela, area);
                        }
                        else
                        {
                            drawingContext.DrawRectangle(BoardBackground.Fill, null, area);
                        }

                        drawingContext.DrawImage(desenhos, area);
                    }

                    var bitmap = new RenderTargetBitmap(
                        (int)DrawCanvas.ActualWidth, (int)DrawCanvas.ActualHeight,
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(composicao);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    string caminhoArquivo = System.IO.Path.Combine(pastaBase, $"Aba {tab.Number}.png");
                    using var stream = new System.IO.FileStream(caminhoArquivo, System.IO.FileMode.Create);
                    encoder.Save(stream);
                }

                MessageBox.Show($"Capturas exportadas em:\n{pastaBase}", "Capturar composição",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não consegui capturar: {ex.Message}", "Erro ao capturar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DrawCanvas.Children.Clear();
                foreach (var el in elementosAtivosOriginais)
                    DrawCanvas.Children.Add(el);
                DrawCanvas.Background = fundoOriginal;
            }
        }

        private BitmapSource CaptureScreenBehindOverlay()
        {
            int width = Math.Max(1, (int)Math.Round(ActualWidth));
            int height = Math.Max(1, (int)Math.Round(ActualHeight));
            int left = (int)Math.Round(Left);
            int top = (int)Math.Round(Top);

            using var screenshot = new DrawingBitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            Hide();
            try
            {
                Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                using var graphics = DrawingGraphics.FromImage(screenshot);
                graphics.CopyFromScreen(left, top, 0, 0, screenshot.Size);
            }
            finally
            {
                Show();
                Activate();
            }

            using var stream = new System.IO.MemoryStream();
            screenshot.Save(stream, DrawingImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        private RenderTargetBitmap RenderTargetBitmapFromCanvas()
        {
            var bitmap = new RenderTargetBitmap(
                (int)DrawCanvas.ActualWidth, (int)DrawCanvas.ActualHeight,
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(DrawCanvas);
            return bitmap;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!ConfirmSaveChangesBeforeLeaving())
            {
                e.Cancel = true;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _hotkeys?.Dispose();
            base.OnClosed(e);
        }
    }
}