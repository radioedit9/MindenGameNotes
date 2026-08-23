using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MindenGameNotes;

public partial class MainWindow : Window
{
    private readonly ProjectStore store = new();
    private readonly ImportService importer = new();
    private GameNotesProject project = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => { project = await store.LoadAsync(); SetProject(); };
    }

    private void SetProject()
    {
        DataContext = null; DataContext = project; RenderPreview();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await store.SaveAsync(project); RenderPreview(); StatusText.Text = $"Saved {DateTime.Now:t}"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Supported files (*.pdf;*.xlsx)|*.pdf;*.xlsx|PDF files (*.pdf)|*.pdf|Excel workbooks (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            StatusText.Text = "Importing…";
            var count = await importer.ImportAsync(dialog.FileName, project);
            await store.SaveAsync(project); SetProject(); StatusText.Text = $"Imported {count} row(s); review required";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void VerifyAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in project.Players) p.Verified = true;
        SetProject(); StatusText.Text = "All player rows marked verified (save to commit)";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var unverified = project.Players.Count(x => !x.Verified);
        if (unverified > 0 && MessageBox.Show($"{unverified} player row(s) are not verified. Export anyway?", "Verification warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new SaveFileDialog { Filter = "PDF document (*.pdf)|*.pdf", FileName = $"Minden-Game-Notes-{project.GameDate:yyyy-MM-dd}.pdf" };
        if (dialog.ShowDialog() != true) return;
        try { await store.SaveAsync(project); PdfExporter.Export(dialog.FileName, project); StatusText.Text = $"Exported {Path.GetFileName(dialog.FileName)}"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderPreview()
    {
        PreviewPages.Items.Clear();
        var native = PageRasterizer.CreatePreview(project);
        PreviewPages.Items.Add(new Border { Child = native, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0,0,0,18), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, Opacity = .2, ShadowDepth = 2 } });
        foreach (var page in PageComposer.Compose(project).Skip(1))
        {
            var canvas = new Canvas { Width = 510, Height = 660, Background = Brushes.White, Margin = new Thickness(0, 0, 0, 18) };
            canvas.Children.Add(new Border { Width = 510, Height = 58, Background = (Brush)FindResource("Crimson") });
            foreach (var line in page.Lines)
            {
                var text = new TextBlock { Text = line.Text, FontFamily = new FontFamily("Segoe UI"), FontSize = Math.Max(7, line.Size * .82), Foreground = line.Y > 720 || line.Y < 30 ? Brushes.White : (Brush)FindResource("Navy"), Width = 450, TextTrimming = TextTrimming.CharacterEllipsis };
                Canvas.SetLeft(text, line.X * 510.0 / 612); Canvas.SetTop(text, (792 - line.Y - line.Size) * 660.0 / 792); canvas.Children.Add(text);
            }
            PreviewPages.Items.Add(new Border { Child = canvas, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, Opacity = .2, ShadowDepth = 2 } });
        }
    }

    private static void ShowError(Exception ex) => MessageBox.Show(ex.Message, "Minden Game Notes Builder", MessageBoxButton.OK, MessageBoxImage.Error);
}
