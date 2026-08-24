using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MindenGameNotes;

public partial class MainWindow : Window
{
    private readonly ProjectStore store = new();
    private readonly ImportService importer = new();
    private BuilderWorkspace workspace = new();
    private GameNotesProject project = new();
    private bool settingProject;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { workspace = await store.LoadAsync(); SetWorkspace(); }
            catch (Exception ex) { ShowError(ex); Close(); }
        };
    }

    private void SetWorkspace()
    {
        workspace.Normalize();
        project = workspace.ActiveProject ?? AddNewProject();
        settingProject = true;
        ProjectSelector.ItemsSource = null; ProjectSelector.ItemsSource = workspace.Projects; ProjectSelector.SelectedItem = project;
        SourceFamiliesGrid.ItemsSource = null; SourceFamiliesGrid.ItemsSource = workspace.SourceFamilies;
        SourceFamilyColumn.ItemsSource = workspace.SourceFamilies;
        settingProject = false;
        SetProject();
    }

    private void SetProject()
    {
        workspace.ActiveProjectId = project.Id;
        workspace.Normalize();
        DataContext = null; DataContext = project;
        ExpectedDocumentsGrid.ItemsSource = null; ExpectedDocumentsGrid.ItemsSource = project.ExpectedDocuments;
        ReadinessText.Text = project.IsReady ? "READY" : "NOT READY";
        ReadinessText.Foreground = project.IsReady ? Brushes.DarkGreen : Brushes.DarkRed;
        ReadinessIssues.ItemsSource = project.ReadinessIssues;
        RenderPreview();
    }

    private GameNotesProject AddNewProject()
    {
        var created = new GameNotesProject();
        workspace.Projects.Add(created); workspace.ActiveProjectId = created.Id; workspace.Normalize();
        return created;
    }

    private void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (settingProject || ProjectSelector.SelectedItem is not GameNotesProject selected) return;
        project = selected; SetProject();
    }

    private void NewProject_Click(object sender, RoutedEventArgs e) { project = AddNewProject(); SetWorkspace(); StatusText.Text = "New weekly project created"; }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { CommitGrids(); await store.SaveAsync(workspace); SetWorkspace(); StatusText.Text = $"Saved {DateTime.Now:t}"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void AddFamily_Click(object sender, RoutedEventArgs e)
    {
        CommitGrids(); var family = new SourceFamilyConfiguration { Name = "New source family" }; workspace.SourceFamilies.Add(family); SetWorkspace(); SourceFamiliesGrid.SelectedItem = family;
    }

    private void BrowseFamily_Click(object sender, RoutedEventArgs e)
    {
        if (SourceFamiliesGrid.SelectedItem is not SourceFamilyConfiguration family) { MessageBox.Show("Select a source family first."); return; }
        var dialog = new OpenFolderDialog { Title = "Select locally synchronized source folder", InitialDirectory = Directory.Exists(family.RootPath) ? family.RootPath : null };
        if (dialog.ShowDialog() == true) { family.RootPath = dialog.FolderName; SetWorkspace(); }
    }

    private void AddDocument_Click(object sender, RoutedEventArgs e)
    {
        CommitGrids(); var family = workspace.SourceFamilies.FirstOrDefault(x => x.Enabled);
        if (family is null) { MessageBox.Show("Configure an enabled source family first."); return; }
        var document = new ExpectedSourceDocument { Name = "Expected document", SourceFamilyId = family.Id };
        project.ExpectedDocuments.Add(document); SetProject(); ExpectedDocumentsGrid.SelectedItem = document;
    }

    private void ResolveDocument_Click(object sender, RoutedEventArgs e)
    {
        if (ExpectedDocumentsGrid.SelectedItem is not ExpectedSourceDocument document) { MessageBox.Show("Select an expected document first."); return; }
        var dialog = new OpenFileDialog { Title = "Resolve expected weekly source" };
        if (dialog.ShowDialog() == true) { document.ResolvedPath = dialog.FileName; Refresh(document); SetProject(); }
    }

    private void RefreshDocument_Click(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        if (ExpectedDocumentsGrid.SelectedItem is ExpectedSourceDocument document) Refresh(document);
        else foreach (var item in project.ExpectedDocuments) Refresh(item);
        SetProject(); StatusText.Text = "Source status refreshed";
    }

    private void VerifyDocument_Click(object sender, RoutedEventArgs e)
    {
        if (ExpectedDocumentsGrid.SelectedItem is not ExpectedSourceDocument document) { MessageBox.Show("Select an expected document first."); return; }
        document.SetVerified(document.Verification != DocumentVerificationState.Verified); SetProject();
        StatusText.Text = document.Verification == DocumentVerificationState.Verified ? "Document explicitly verified" : "Document marked unverified";
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        if (ExpectedDocumentsGrid.SelectedItem is not ExpectedSourceDocument document) { MessageBox.Show("Select an expected document first."); return; }
        var family = workspace.SourceFamilies.FirstOrDefault(x => x.Id == document.SourceFamilyId);
        if (family is null) { MessageBox.Show("The selected document has no valid source family."); return; }
        var path = document.ResolvePath(family);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var dialog = new OpenFileDialog { Filter = "Supported files (*.pdf;*.xlsx)|*.pdf;*.xlsx|PDF files (*.pdf)|*.pdf|Excel workbooks (*.xlsx)|*.xlsx" };
            if (dialog.ShowDialog() != true) return; path = dialog.FileName; document.ResolvedPath = path; Refresh(document);
        }
        try
        {
            StatusText.Text = "Importing…";
            var count = await importer.ImportAsync(path, project, document, family);
            await store.SaveAsync(workspace); SetWorkspace(); StatusText.Text = $"Imported {count} row(s); document remains unverified";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void VerifyAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var player in project.Players) player.Verified = true;
        SetProject(); StatusText.Text = "All player rows marked verified; project readiness is unchanged";
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "PDF document (*.pdf)|*.pdf", FileName = $"Minden-Game-Notes-{project.GameDate?.ToString("yyyy-MM-dd") ?? "undated"}.pdf" };
        if (dialog.ShowDialog() != true) return;
        try { await store.SaveAsync(workspace); PdfExporter.Export(dialog.FileName, project); StatusText.Text = $"Exported legacy diagnostic {Path.GetFileName(dialog.FileName)}"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Refresh(ExpectedSourceDocument document)
    {
        var family = workspace.SourceFamilies.FirstOrDefault(x => x.Id == document.SourceFamilyId);
        document.RefreshStatus(family);
    }

    private void CommitGrids()
    {
        SourceFamiliesGrid.CommitEdit(DataGridEditingUnit.Cell, true); SourceFamiliesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ExpectedDocumentsGrid.CommitEdit(DataGridEditingUnit.Cell, true); ExpectedDocumentsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        workspace.Normalize();
    }

    private void RenderPreview()
    {
        PreviewPages.Items.Clear();
        try
        {
            var native = PageRasterizer.CreatePreview(project);
            PreviewPages.Items.Add(new Border { Child = native, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 18) });
            foreach (var page in PageComposer.Compose(project).Skip(1))
            {
                var canvas = new Canvas { Width = 510, Height = 660, Background = Brushes.White, Margin = new Thickness(0, 0, 0, 18) };
                foreach (var line in page.Lines) { var text = new TextBlock { Text = line.Text, FontSize = Math.Max(7, line.Size * .82), Width = 450, TextTrimming = TextTrimming.CharacterEllipsis }; Canvas.SetLeft(text, line.X * 510.0 / 612); Canvas.SetTop(text, (792 - line.Y - line.Size) * 660.0 / 792); canvas.Children.Add(text); }
                PreviewPages.Items.Add(new Border { Child = canvas, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1) });
            }
        }
        catch (Exception ex) { PreviewPages.Items.Add(new TextBlock { Text = $"Diagnostic preview unavailable: {ex.Message}", TextWrapping = TextWrapping.Wrap }); }
    }

    private static void ShowError(Exception ex) => MessageBox.Show(ex.Message, "Minden Game Notes Information Builder", MessageBoxButton.OK, MessageBoxImage.Error);
}
