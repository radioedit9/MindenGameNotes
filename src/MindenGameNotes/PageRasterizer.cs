using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MindenGameNotes;

public static class PageRasterizer
{
    public static void SavePageOnePng(string path, GameNotesProject project, int dpi = 150, TypographyVariant? typography = null)
    {
        typography ??= TypographyVariant.Candidates[0];
        PageOneBoundsValidator.Validate(project);
        // The display list is in PDF points. Scale once from 72-point inches to target pixels.
        // Render at 96 DPI internally so RenderTargetBitmap does not apply a second DPI transform.
        var scale = dpi / 72d; var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, PageOneRenderer.Compose(project), scale, typography);
        var bitmap = new RenderTargetBitmap((int)(PageGeometry.WidthPoints * scale), (int)(PageGeometry.HeightPoints * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    public static Canvas CreatePreview(GameNotesProject project, double scale = .82, TypographyVariant? typography = null)
    {
        typography ??= TypographyVariant.Candidates[0];
        var canvas = new Canvas { Width = 612 * scale, Height = 792 * scale, Background = Brushes.White };
        foreach (var item in PageOneRenderer.Compose(project))
        {
            switch (item)
            {
                case Box b:
                    var rect = new System.Windows.Shapes.Rectangle { Width=b.Width*scale, Height=b.Height*scale, Fill=BrushFor(b.FillBlack?PublicationFill.Black:b.Fill), Stroke=b.Stroke>0?Brushes.Black:null, StrokeThickness=b.Stroke*scale };
                    Canvas.SetLeft(rect,b.X*scale);Canvas.SetTop(rect,b.Y*scale);canvas.Children.Add(rect); break;
                case Rule r:
                    canvas.Children.Add(new System.Windows.Shapes.Line { X1=r.X1*scale,Y1=r.Y1*scale,X2=r.X2*scale,Y2=r.Y2*scale,Stroke=Brushes.Black,StrokeThickness=r.Width*scale }); break;
                case Label l:
                    var family=new FontFamily(l.Bold?typography.DisplayName:typography.BodyName);var sx=FitScale(l,family,scale,l.BodyBold);var tb=new TextBlock{Text=l.Value,Width=l.Width*scale/sx,FontFamily=family,FontSize=l.Size*scale,FontWeight=l.BodyBold?FontWeights.Bold:FontWeights.Normal,Foreground=l.White?Brushes.White:Brushes.Black,TextAlignment=l.Align==TextAlign.Center?TextAlignment.Center:l.Align==TextAlign.Right?TextAlignment.Right:TextAlignment.Left,LineHeight=(l.Size+2)*scale,RenderTransform=new ScaleTransform(sx,1),RenderTransformOrigin=new Point(0,0)};
                    Canvas.SetLeft(tb,l.X*scale);Canvas.SetTop(tb,l.Y*scale);canvas.Children.Add(tb);break;
                case ImageMark m:
                    var img=new Image{Source=LoadCropped(m.Path),Width=m.Width*scale,Height=m.Height*scale,Stretch=Stretch.Uniform};Canvas.SetLeft(img,m.X*scale);Canvas.SetTop(img,m.Y*scale);canvas.Children.Add(img);break;
            }
        }
        return canvas;
    }

    private static void Draw(DrawingContext dc, List<PageElement> elements, double s, TypographyVariant typography)
    {
        dc.DrawRectangle(Brushes.White,null,new Rect(0,0,612*s,792*s));
        foreach(var item in elements) switch(item)
        {
            case Box b: dc.DrawRectangle(BrushFor(b.FillBlack?PublicationFill.Black:b.Fill),b.Stroke>0?new Pen(Brushes.Black,b.Stroke*s):null,new Rect(b.X*s,b.Y*s,b.Width*s,b.Height*s));break;
            case Rule r:dc.DrawLine(new Pen(Brushes.Black,r.Width*s),new Point(r.X1*s,r.Y1*s),new Point(r.X2*s,r.Y2*s));break;
            case Label l:
                var typeface=new Typeface(new FontFamily(l.Bold?typography.DisplayName:typography.BodyName),FontStyles.Normal,l.BodyBold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal);
                var sx=FitScale(l,typeface.FontFamily,s,l.BodyBold);var ft=new FormattedText(l.Value,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,l.Size*s,l.White?Brushes.White:Brushes.Black,1.0){MaxTextWidth=l.Width*s/sx,TextAlignment=l.Align==TextAlign.Center?TextAlignment.Center:l.Align==TextAlign.Right?TextAlignment.Right:TextAlignment.Right,LineHeight=(l.Size+2)*s};
                ft.TextAlignment=l.Align==TextAlign.Center?TextAlignment.Center:l.Align==TextAlign.Right?TextAlignment.Right:TextAlignment.Left;
                dc.PushTransform(new ScaleTransform(sx,1,l.X*s,l.Y*s));dc.DrawText(ft,new Point(l.X*s,l.Y*s));dc.Pop();break;
            case ImageMark m:dc.DrawImage(LoadCropped(m.Path),new Rect(m.X*s,m.Y*s,m.Width*s,m.Height*s));break;
        }
    }

    public static BitmapSource LoadCropped(string path)
    {
        var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.UriSource=new Uri(path);b.EndInit();
        var converted=new FormatConvertedBitmap(b,PixelFormats.Bgra32,null,0);var stride=converted.PixelWidth*4;var pixels=new byte[stride*converted.PixelHeight];converted.CopyPixels(pixels,stride,0);
        var minX=converted.PixelWidth;var minY=converted.PixelHeight;var maxX=0;var maxY=0;
        for(var y=0;y<converted.PixelHeight;y++)for(var x=0;x<converted.PixelWidth;x++)if(pixels[y*stride+x*4+3]>8){minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}
        return minX<=maxX?new CroppedBitmap(converted,new Int32Rect(minX,minY,maxX-minX+1,maxY-minY+1)):converted;
    }

    public static (byte[] Bytes,int Width,int Height) LogoJpeg(string path)
    {
        var source=LoadCropped(path);var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Brushes.White,null,new Rect(0,0,source.PixelWidth,source.PixelHeight));dc.DrawImage(source,new Rect(0,0,source.PixelWidth,source.PixelHeight));}
        var bmp=new RenderTargetBitmap(source.PixelWidth,source.PixelHeight,96,96,PixelFormats.Pbgra32);bmp.Render(visual);var enc=new JpegBitmapEncoder{QualityLevel=95};enc.Frames.Add(BitmapFrame.Create(bmp));using var ms=new MemoryStream();enc.Save(ms);return(ms.ToArray(),bmp.PixelWidth,bmp.PixelHeight);
    }

    private static double FitScale(Label l,FontFamily family,double renderScale,bool bodyBold)
    {
        var face=new Typeface(family,FontStyles.Normal,bodyBold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal);var widest=l.Value.Split('\n').Select(line=>new FormattedText(line,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,l.Size*renderScale,Brushes.Black,1).WidthIncludingTrailingWhitespace).DefaultIfEmpty(1).Max();
        return Math.Max(.55,Math.Min(l.Condense/100,(l.Width*renderScale)/Math.Max(1,widest)));
    }
    private static Brush BrushFor(PublicationFill fill){var v=(byte)Math.Round(PublicationStyles.Gray(fill)*255);return new SolidColorBrush(Color.FromRgb(v,v,v));}
}
