using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Xml.Linq;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace Hostix.UI.Services
{
    public class TechIconService
    {
        private static readonly string IconsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icons", "Frameworks");
        private static readonly Dictionary<string, DrawingImage> _cache = new Dictionary<string, DrawingImage>(StringComparer.OrdinalIgnoreCase);

        static TechIconService()
        {
            try
            {
                if (!Directory.Exists(IconsDirectory))
                {
                    Directory.CreateDirectory(IconsDirectory);
                }
            }
            catch { }
        }

        public static DrawingImage? GetIcon(string techName)
        {
            if (string.IsNullOrEmpty(techName)) return null;

            if (_cache.TryGetValue(techName, out var cachedImage))
            {
                return cachedImage;
            }

            var image = LoadIconFromSvgFile(techName);
            if (image != null)
            {
                _cache[techName] = image;
            }
            return image;
        }

        private static DrawingImage? LoadIconFromSvgFile(string name)
        {
            string cleanName = name.ToLowerInvariant()
                                   .Replace(" ", "")
                                   .Replace(".", "")
                                   .Replace("/", "");

            if (cleanName == "plainphp") cleanName = "corephp";
            
            string fileName = cleanName + ".svg";
            string filePath = Path.Combine(IconsDirectory, fileName);

            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                string xmlContent = File.ReadAllText(filePath);
                var doc = XDocument.Parse(xmlContent);
                var root = doc.Root;
                if (root == null) return null;

                var drawingGroup = new DrawingGroup();

                foreach (var el in root.Descendants())
                {
                    string localName = el.Name.LocalName.ToLowerInvariant();
                    if (localName == "path")
                    {
                        var dAttr = el.Attribute("d");
                        if (dAttr == null) continue;

                        var geometry = Geometry.Parse(dAttr.Value);
                        
                        var fillAttr = el.Attribute("fill");
                        Brush fillBrush = Brushes.Transparent;
                        if (fillAttr != null && fillAttr.Value != "none")
                        {
                            fillBrush = (Brush)new BrushConverter().ConvertFromString(fillAttr.Value)!;
                        }

                        var strokeAttr = el.Attribute("stroke");
                        Pen? strokePen = null;
                        if (strokeAttr != null && strokeAttr.Value != "none")
                        {
                            var strokeBrush = (Brush)new BrushConverter().ConvertFromString(strokeAttr.Value)!;
                            double strokeWidth = 1.0;
                            var widthAttr = el.Attribute("stroke-width");
                            if (widthAttr != null && double.TryParse(widthAttr.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var w))
                            {
                                strokeWidth = w;
                            }
                            strokePen = new Pen(strokeBrush, strokeWidth);
                        }

                        var geometryDrawing = new GeometryDrawing(fillBrush, strokePen, geometry);
                        drawingGroup.Children.Add(geometryDrawing);
                    }
                    else if (localName == "circle")
                    {
                        var cxAttr = el.Attribute("cx");
                        var cyAttr = el.Attribute("cy");
                        var rAttr = el.Attribute("r");
                        if (cxAttr == null || cyAttr == null || rAttr == null) continue;

                        double cx = double.Parse(cxAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                        double cy = double.Parse(cyAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                        double r = double.Parse(rAttr.Value, System.Globalization.CultureInfo.InvariantCulture);

                        var geometry = new EllipseGeometry(new System.Windows.Point(cx, cy), r, r);

                        var fillAttr = el.Attribute("fill");
                        Brush fillBrush = Brushes.Transparent;
                        if (fillAttr != null && fillAttr.Value != "none")
                        {
                            fillBrush = (Brush)new BrushConverter().ConvertFromString(fillAttr.Value)!;
                        }

                        var strokeAttr = el.Attribute("stroke");
                        Pen? strokePen = null;
                        if (strokeAttr != null && strokeAttr.Value != "none")
                        {
                            var strokeBrush = (Brush)new BrushConverter().ConvertFromString(strokeAttr.Value)!;
                            double strokeWidth = 1.0;
                            var widthAttr = el.Attribute("stroke-width");
                            if (widthAttr != null && double.TryParse(widthAttr.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var w))
                            {
                                strokeWidth = w;
                            }
                            strokePen = new Pen(strokeBrush, strokeWidth);
                        }

                        var geometryDrawing = new GeometryDrawing(fillBrush, strokePen, geometry);
                        drawingGroup.Children.Add(geometryDrawing);
                    }
                    else if (localName == "ellipse")
                    {
                        var cxAttr = el.Attribute("cx");
                        var cyAttr = el.Attribute("cy");
                        var rxAttr = el.Attribute("rx");
                        var ryAttr = el.Attribute("ry");
                        if (cxAttr == null || cyAttr == null || rxAttr == null || ryAttr == null) continue;

                        double cx = double.Parse(cxAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                        double cy = double.Parse(cyAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                        double rx = double.Parse(rxAttr.Value, System.Globalization.CultureInfo.InvariantCulture);
                        double ry = double.Parse(ryAttr.Value, System.Globalization.CultureInfo.InvariantCulture);

                        var geometry = new EllipseGeometry(new System.Windows.Point(cx, cy), rx, ry);

                        var fillAttr = el.Attribute("fill");
                        Brush fillBrush = Brushes.Transparent;
                        if (fillAttr != null && fillAttr.Value != "none")
                        {
                            fillBrush = (Brush)new BrushConverter().ConvertFromString(fillAttr.Value)!;
                        }

                        var strokeAttr = el.Attribute("stroke");
                        Pen? strokePen = null;
                        if (strokeAttr != null && strokeAttr.Value != "none")
                        {
                            var strokeBrush = (Brush)new BrushConverter().ConvertFromString(strokeAttr.Value)!;
                            double strokeWidth = 1.0;
                            var widthAttr = el.Attribute("stroke-width");
                            if (widthAttr != null && double.TryParse(widthAttr.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var w))
                            {
                                strokeWidth = w;
                            }
                            strokePen = new Pen(strokeBrush, strokeWidth);
                        }

                        var geometryDrawing = new GeometryDrawing(fillBrush, strokePen, geometry);
                        drawingGroup.Children.Add(geometryDrawing);
                    }
                }

                return new DrawingImage(drawingGroup);
            }
            catch
            {
                return null;
            }
        }
    }
}
