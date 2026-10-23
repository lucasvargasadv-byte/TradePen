using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DrawingPath = System.Windows.Shapes.Path;

namespace TraderPen.History
{
    public sealed class ProjectDocument
    {
        public string BoardMode { get; set; } = "Transparent";
        public int? ActiveTabNumber { get; set; }
        public List<ProjectTab> Tabs { get; set; } = new();
    }

    public sealed class ProjectTab
    {
        public int Number { get; set; }
        public List<UIElement> Elements { get; set; } = new();
    }

    public static class ProjectFileService
    {
        private const int CurrentFormatVersion = 1;
        private const long MaximumFileSize = 100 * 1024 * 1024;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        public static string Serialize(ProjectDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            return JsonSerializer.Serialize(ToFile(document), JsonOptions);
        }

        public static void Save(string path, ProjectDocument document)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            File.WriteAllText(path, Serialize(document));
        }

        public static ProjectDocument Load(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException("O arquivo do projeto não foi encontrado.", path);
            }

            if (file.Length > MaximumFileSize)
            {
                throw new InvalidDataException("O arquivo do projeto excede o limite de 100 MB.");
            }

            var fileData = JsonSerializer.Deserialize<ProjectFileData>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("O arquivo do projeto está vazio ou inválido.");

            if (fileData.FormatVersion != CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"A versão do documento ({fileData.FormatVersion}) não é compatível com esta versão do TraderPen.");
            }

            if (fileData.BoardMode is not ("Transparent" or "Whiteboard" or "Blackboard"))
            {
                throw new InvalidDataException("O documento contém um modo de fundo desconhecido.");
            }

            if (fileData.Tabs == null || fileData.Tabs.Any(tab => tab == null || tab.Number <= 0) ||
                fileData.Tabs.Select(tab => tab.Number).Distinct().Count() != fileData.Tabs.Count)
            {
                throw new InvalidDataException("O documento contém abas inválidas ou números de aba duplicados.");
            }

            if (fileData.ActiveTabNumber.HasValue &&
                !fileData.Tabs.Any(tab => tab.Number == fileData.ActiveTabNumber.Value))
            {
                throw new InvalidDataException("A aba ativa indicada no documento não existe.");
            }

            var document = new ProjectDocument
            {
                BoardMode = fileData.BoardMode ?? "Transparent",
                ActiveTabNumber = fileData.ActiveTabNumber
            };

            foreach (var tabData in fileData.Tabs)
            {
                if (tabData.Elements == null)
                {
                    throw new InvalidDataException($"A aba {tabData.Number} não contém uma lista de objetos válida.");
                }

                var elements = tabData.Elements.Select(CreateElement).ToList();
                var elementsByIndex = elements
                    .Select((element, index) => (element, index))
                    .ToDictionary(item => item.index, item => item.element);

                for (int i = 0; i < elements.Count; i++)
                {
                    var elementData = tabData.Elements[i];
                    if (elementData.TagReferenceIndex.HasValue)
                    {
                        if (!elementsByIndex.TryGetValue(elementData.TagReferenceIndex.Value, out var referencedElement))
                        {
                            throw new InvalidDataException("O documento contém uma referência inválida entre objetos.");
                        }

                        if (elements[i] is FrameworkElement frameworkElement)
                        {
                            frameworkElement.Tag = referencedElement;
                        }
                        else
                        {
                            throw new InvalidDataException("O objeto referenciado não aceita metadados.");
                        }
                    }
                    else if (elementData.Tag != null && elements[i] is FrameworkElement frameworkElement)
                    {
                        frameworkElement.Tag = elementData.Tag;
                    }
                }

                document.Tabs.Add(new ProjectTab { Number = tabData.Number, Elements = elements });
            }

            return document;
        }

        private static ProjectFileData ToFile(ProjectDocument document)
        {
            if (string.IsNullOrWhiteSpace(document.BoardMode))
            {
                throw new InvalidDataException("O modo de fundo do documento não é válido.");
            }

            if (document.ActiveTabNumber.HasValue &&
                !document.Tabs.Any(tab => tab.Number == document.ActiveTabNumber.Value))
            {
                throw new InvalidDataException("A aba ativa do documento não existe.");
            }

            if (document.Tabs.Any(tab => tab.Number <= 0) ||
                document.Tabs.Select(tab => tab.Number).Distinct().Count() != document.Tabs.Count)
            {
                throw new InvalidDataException("O documento contém abas inválidas ou números de aba duplicados.");
            }

            var result = new ProjectFileData
            {
                FormatVersion = CurrentFormatVersion,
                BoardMode = document.BoardMode,
                ActiveTabNumber = document.ActiveTabNumber
            };

            foreach (var tab in document.Tabs)
            {
                var referenceIndexes = tab.Elements
                    .Select((element, index) => (element, index))
                    .ToDictionary(item => item.element, item => item.index);

                result.Tabs.Add(new ProjectTabData
                {
                    Number = tab.Number,
                    Elements = tab.Elements.Select(element => CreateElementData(element, referenceIndexes)).ToList()
                });
            }

            return result;
        }

        private static ElementData CreateElementData(
            UIElement element,
            IReadOnlyDictionary<UIElement, int> referenceIndexes)
        {
            var data = new ElementData();
            switch (element)
            {
                case Rectangle rectangle:
                    data.Kind = "Rectangle";
                    AddShapeData(data, rectangle);
                    break;
                case Ellipse ellipse:
                    data.Kind = "Ellipse";
                    AddShapeData(data, ellipse);
                    break;
                case Line line:
                    data.Kind = "Line";
                    AddShapeData(data, line);
                    data.LinePoints = new[] { line.X1, line.Y1, line.X2, line.Y2 };
                    break;
                case Polyline polyline:
                    data.Kind = "Polyline";
                    AddShapeData(data, polyline);
                    data.Points = polyline.Points.Select(point => new[] { point.X, point.Y }).ToList();
                    break;
                case DrawingPath path:
                    data.Kind = "Path";
                    AddShapeData(data, path);
                    data.Geometry = path.Data == null
                        ? null
                        : PathGeometry.CreateFromGeometry(path.Data).ToString(CultureInfo.InvariantCulture);
                    break;
                case TextBlock textBlock:
                    data.Kind = "TextBlock";
                    data.Text = textBlock.Text;
                    data.Foreground = BrushToString(textBlock.Foreground);
                    data.Background = BrushToString(textBlock.Background);
                    data.FontSize = textBlock.FontSize;
                    data.FontWeight = textBlock.FontWeight.ToString();
                    data.TextWrapping = textBlock.TextWrapping.ToString();
                    data.TextAlignment = textBlock.TextAlignment.ToString();
                    data.Margin = ThicknessToArray(textBlock.Margin);
                    data.IsHitTestVisible = textBlock.IsHitTestVisible;
                    AddFrameworkElementData(data, textBlock);
                    break;
                case Canvas canvas:
                    data.Kind = "Canvas";
                    data.Background = BrushToString(canvas.Background);
                    data.Children = canvas.Children.OfType<UIElement>()
                        .Select(child => CreateElementData(child, referenceIndexes)).ToList();
                    AddFrameworkElementData(data, canvas);
                    break;
                case Grid grid:
                    data.Kind = "Grid";
                    data.Children = grid.Children.OfType<UIElement>()
                        .Select(child => CreateElementData(child, referenceIndexes)).ToList();
                    AddFrameworkElementData(data, grid);
                    break;
                default:
                    throw new NotSupportedException(
                        $"O tipo de objeto '{element.GetType().Name}' não pode ser salvo neste documento.");
            }

            if (element is FrameworkElement frameworkElement && frameworkElement.Tag != null)
            {
                if (frameworkElement.Tag is string tag)
                {
                    data.Tag = tag;
                }
                else if (frameworkElement.Tag is UIElement referencedElement &&
                         referenceIndexes.TryGetValue(referencedElement, out int referenceIndex))
                {
                    data.TagReferenceIndex = referenceIndex;
                }
                else if (frameworkElement.Tag is UIElement)
                {
                    data.Tag = null;
                }
                else
                {
                    throw new NotSupportedException("Um objeto contém metadados que não podem ser salvos.");
                }
            }

            return data;
        }

        private static void AddShapeData(ElementData data, Shape shape)
        {
            data.Stroke = BrushToString(shape.Stroke);
            data.Fill = BrushToString(shape.Fill);
            data.StrokeThickness = shape.StrokeThickness;
            data.StrokeDashArray = shape.StrokeDashArray?.ToArray();
            data.StrokeStartLineCap = shape.StrokeStartLineCap.ToString();
            data.StrokeEndLineCap = shape.StrokeEndLineCap.ToString();
            data.StrokeLineJoin = shape.StrokeLineJoin.ToString();
            data.Opacity = shape.Opacity;
            if (shape is Rectangle rectangle)
            {
                data.RadiusX = rectangle.RadiusX;
                data.RadiusY = rectangle.RadiusY;
            }
            AddFrameworkElementData(data, shape);

            if (shape is FrameworkElement frameworkElement)
            {
                data.IsHitTestVisible = frameworkElement.IsHitTestVisible;
            }
        }

        private static void AddFrameworkElementData(ElementData data, FrameworkElement element)
        {
            double left = Canvas.GetLeft(element);
            double top = Canvas.GetTop(element);
            data.Left = double.IsFinite(left) ? left : null;
            data.Top = double.IsFinite(top) ? top : null;
            data.Width = double.IsFinite(element.Width) ? element.Width : null;
            data.Height = double.IsFinite(element.Height) ? element.Height : null;
            data.RenderTransform = element.RenderTransform == Transform.Identity
                ? null
                : MatrixToArray(element.RenderTransform.Value);
            data.RenderTransformOrigin = new[] { element.RenderTransformOrigin.X, element.RenderTransformOrigin.Y };
        }

        private static UIElement CreateElement(ElementData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (string.IsNullOrWhiteSpace(data.Kind))
            {
                throw new InvalidDataException("O documento contém um objeto sem tipo.");
            }

            UIElement element = data.Kind switch
            {
                "Rectangle" => CreateShape(new Rectangle(), data),
                "Ellipse" => CreateShape(new Ellipse(), data),
                "Line" => CreateLine(data),
                "Polyline" => CreatePolyline(data),
                "Path" => CreatePath(data),
                "TextBlock" => CreateTextBlock(data),
                "Canvas" => CreateCanvas(data),
                "Grid" => CreateGrid(data),
                _ => throw new InvalidDataException($"O tipo de objeto '{data.Kind}' não é reconhecido.")
            };

            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Tag = data.Tag;
            }

            return element;
        }

        private static T CreateShape<T>(T shape, ElementData data) where T : Shape
        {
            ApplyShapeData(shape, data);
            if (shape is Rectangle rectangle)
            {
                rectangle.RadiusX = data.RadiusX ?? 0;
                rectangle.RadiusY = data.RadiusY ?? 0;
            }
            return shape;
        }

        private static Line CreateLine(ElementData data)
        {
            if (data.LinePoints is not { Length: 4 } points)
            {
                throw new InvalidDataException("Uma linha no documento contém coordenadas inválidas.");
            }

            var line = new Line { X1 = points[0], Y1 = points[1], X2 = points[2], Y2 = points[3] };
            ApplyShapeData(line, data);
            return line;
        }

        private static Polyline CreatePolyline(ElementData data)
        {
            var polyline = new Polyline();
            ApplyShapeData(polyline, data);
            if (data.Points != null)
            {
                foreach (var point in data.Points)
                {
                    if (point is not { Length: 2 })
                    {
                        throw new InvalidDataException("Um traço no documento contém coordenadas inválidas.");
                    }
                    polyline.Points.Add(new Point(point[0], point[1]));
                }
            }
            return polyline;
        }

        private static DrawingPath CreatePath(ElementData data)
        {
            var path = new DrawingPath();
            ApplyShapeData(path, data);
            if (data.Geometry != null)
            {
                path.Data = Geometry.Parse(data.Geometry);
            }
            return path;
        }

        private static TextBlock CreateTextBlock(ElementData data)
        {
            var textBlock = new TextBlock
            {
                Text = data.Text ?? string.Empty,
                Foreground = StringToBrush(data.Foreground) ?? Brushes.Black,
                Background = StringToBrush(data.Background) ?? Brushes.Transparent,
                FontSize = data.FontSize ?? 12,
                FontWeight = ParseFontWeight(data.FontWeight),
                TextWrapping = ParseEnum(data.TextWrapping, TextWrapping.NoWrap),
                TextAlignment = ParseEnum(data.TextAlignment, TextAlignment.Left),
                Margin = ArrayToThickness(data.Margin),
                IsHitTestVisible = data.IsHitTestVisible ?? true
            };
            ApplyFrameworkElementData(textBlock, data);
            return textBlock;
        }

        private static Canvas CreateCanvas(ElementData data)
        {
            var canvas = new Canvas
            {
                Background = StringToBrush(data.Background),
                IsHitTestVisible = data.IsHitTestVisible ?? true
            };
            ApplyFrameworkElementData(canvas, data);
            foreach (var childData in data.Children ?? new List<ElementData>())
            {
                canvas.Children.Add(CreateElement(childData));
            }
            return canvas;
        }

        private static Grid CreateGrid(ElementData data)
        {
            var grid = new Grid { IsHitTestVisible = data.IsHitTestVisible ?? true };
            ApplyFrameworkElementData(grid, data);
            foreach (var childData in data.Children ?? new List<ElementData>())
            {
                grid.Children.Add(CreateElement(childData));
            }
            return grid;
        }

        private static void ApplyShapeData(Shape shape, ElementData data)
        {
            shape.Stroke = StringToBrush(data.Stroke);
            shape.Fill = StringToBrush(data.Fill);
            shape.StrokeThickness = data.StrokeThickness ?? 1;
            shape.Opacity = data.Opacity ?? 1;
            shape.StrokeStartLineCap = ParseEnum(data.StrokeStartLineCap, PenLineCap.Flat);
            shape.StrokeEndLineCap = ParseEnum(data.StrokeEndLineCap, PenLineCap.Flat);
            shape.StrokeLineJoin = ParseEnum(data.StrokeLineJoin, PenLineJoin.Miter);
            if (data.StrokeDashArray != null)
            {
                shape.StrokeDashArray = new DoubleCollection(data.StrokeDashArray);
            }

            ApplyFrameworkElementData(shape, data);
            shape.IsHitTestVisible = data.IsHitTestVisible ?? true;
        }

        private static void ApplyFrameworkElementData(FrameworkElement element, ElementData data)
        {
            if (data.Left.HasValue) Canvas.SetLeft(element, data.Left.Value);
            if (data.Top.HasValue) Canvas.SetTop(element, data.Top.Value);
            if (data.Width.HasValue) element.Width = data.Width.Value;
            if (data.Height.HasValue) element.Height = data.Height.Value;

            if (data.RenderTransform is { Length: 6 } matrixValues)
            {
                var group = new TransformGroup();
                group.Children.Add(new MatrixTransform(ArrayToMatrix(matrixValues)));
                group.Children.Add(new TranslateTransform());
                element.RenderTransform = group;
            }

            if (data.RenderTransformOrigin is { Length: 2 } origin)
            {
                element.RenderTransformOrigin = new Point(origin[0], origin[1]);
            }
        }

        private static string? BrushToString(Brush? brush)
        {
            if (brush == null) return null;
            if (brush is not SolidColorBrush solidColorBrush)
            {
                throw new NotSupportedException("Este documento suporta apenas cores sólidas nos desenhos.");
            }
            return solidColorBrush.Color.ToString();
        }

        private static Brush? StringToBrush(string? value)
        {
            if (value == null) return null;
            var brush = new BrushConverter().ConvertFromString(null, CultureInfo.InvariantCulture, value) as Brush;
            if (brush is not SolidColorBrush solidColorBrush)
            {
                throw new InvalidDataException("O documento contém uma cor de desenho inválida.");
            }
            solidColorBrush.Freeze();
            return solidColorBrush;
        }

        private static double[]? ThicknessToArray(Thickness thickness) =>
            new[] { thickness.Left, thickness.Top, thickness.Right, thickness.Bottom };

        private static Thickness ArrayToThickness(double[]? values)
        {
            if (values == null) return new Thickness();
            if (values.Length != 4) throw new InvalidDataException("O documento contém uma margem inválida.");
            return new Thickness(values[0], values[1], values[2], values[3]);
        }

        private static double[] MatrixToArray(Matrix matrix) =>
            new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY };

        private static Matrix ArrayToMatrix(double[] values) =>
            new(values[0], values[1], values[2], values[3], values[4], values[5]);

        private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
            Enum.TryParse(value, out T parsed) ? parsed : fallback;

        private static FontWeight ParseFontWeight(string? value)
        {
            if (value == null) return FontWeights.Normal;
            var converter = new System.Windows.FontWeightConverter();
            return converter.ConvertFromString(null, CultureInfo.InvariantCulture, value) is FontWeight weight
                ? weight
                : FontWeights.Normal;
        }

        private sealed class ProjectFileData
        {
            public int FormatVersion { get; set; }
            public string? BoardMode { get; set; }
            public int? ActiveTabNumber { get; set; }
            public List<ProjectTabData> Tabs { get; set; } = new();
        }

        private sealed class ProjectTabData
        {
            public int Number { get; set; }
            public List<ElementData> Elements { get; set; } = new();
        }

        private sealed class ElementData
        {
            public string? Kind { get; set; }
            public string? Tag { get; set; }
            public int? TagReferenceIndex { get; set; }
            public double? Left { get; set; }
            public double? Top { get; set; }
            public double? Width { get; set; }
            public double? Height { get; set; }
            public double? Opacity { get; set; }
            public bool? IsHitTestVisible { get; set; }
            public double[]? RenderTransform { get; set; }
            public double[]? RenderTransformOrigin { get; set; }
            public string? Stroke { get; set; }
            public string? Fill { get; set; }
            public double? StrokeThickness { get; set; }
            public double[]? StrokeDashArray { get; set; }
            public string? StrokeStartLineCap { get; set; }
            public string? StrokeEndLineCap { get; set; }
            public string? StrokeLineJoin { get; set; }
            public double? RadiusX { get; set; }
            public double? RadiusY { get; set; }
            public double[]? LinePoints { get; set; }
            public List<double[]>? Points { get; set; }
            public string? Geometry { get; set; }
            public string? Text { get; set; }
            public string? Foreground { get; set; }
            public string? Background { get; set; }
            public double? FontSize { get; set; }
            public string? FontWeight { get; set; }
            public string? TextWrapping { get; set; }
            public string? TextAlignment { get; set; }
            public double[]? Margin { get; set; }
            public List<ElementData>? Children { get; set; }
        }
    }
}
