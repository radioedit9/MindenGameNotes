using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MindenGameNotes;

public partial class MainWindow : Window
{
    private readonly ProjectStore store = new();
    private readonly ImportService importer = new();
    private readonly GameInformationWorkflow gameWorkflow = new();
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
        StagedGameReportsGrid.ItemsSource = null; StagedGameReportsGrid.ItemsSource = project.StagedGameReports;
        ReadinessText.Text = project.IsReady ? "READY" : "NOT READY";
        ReadinessText.Foreground = project.IsReady ? Brushes.DarkGreen : Brushes.DarkRed;
        ReadinessIssues.ItemsSource = project.ReadinessIssues;
        UpdateGameInformationView();
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

    private async void ImportSingleGame_Click(object sender, RoutedEventArgs e)
    {
        CommitGrids();
        if (ExpectedDocumentsGrid.SelectedItem is not ExpectedSourceDocument document) { MessageBox.Show("Select the expected single-game document on the Expected sources tab first."); return; }
        var family = workspace.SourceFamilies.FirstOrDefault(x => x.Id == document.SourceFamilyId); if (family is null) { MessageBox.Show("The selected document has no valid source family."); return; }
        var path = document.ResolvePath(family);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { var dialog = new OpenFileDialog { Filter = "Game Stats PDF (*.pdf)|*.pdf" }; if (dialog.ShowDialog() != true) return; path = dialog.FileName; document.ResolvedPath = path; Refresh(document); }
        try { var staged = await importer.ImportSingleGameAsync(path, project, document, family); await store.SaveAsync(workspace); SetProject(); StagedGameReportsGrid.SelectedItem = staged; StatusText.Text = "Single-game report parsed and pending review; source remains unverified"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void AcceptGameReport_Click(object sender, RoutedEventArgs e) => await ReviewGameReport(false);
    private async void ReplaceGameReport_Click(object sender, RoutedEventArgs e) => await ReviewGameReport(true);
    private async Task ReviewGameReport(bool replace)
    {
        if (StagedGameReportsGrid.SelectedItem is not StagedSingleGameReport staged) { MessageBox.Show("Select a staged report first."); return; }
        var source = project.ExpectedDocuments.FirstOrDefault(x => x.Id == staged.ExpectedDocumentId); if (source is null) { MessageBox.Show("The staged report's expected source is unavailable."); return; }
        var family = workspace.SourceFamilies.FirstOrDefault(x => x.Id == source.SourceFamilyId); if (family is null) { MessageBox.Show("The staged report's source family is unavailable."); return; }
        try { gameWorkflow.Accept(project, staged, source, family, ReviewNote.Text, replace); await store.SaveAsync(workspace); SetProject(); StatusText.Text = replace ? "Accepted report explicitly replaced" : "Report accepted atomically"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void RejectGameReport_Click(object sender, RoutedEventArgs e)
    {
        if (StagedGameReportsGrid.SelectedItem is not StagedSingleGameReport staged) { MessageBox.Show("Select a staged report first."); return; }
        try { gameWorkflow.Reject(staged, ReviewNote.Text); await store.SaveAsync(workspace); SetProject(); StatusText.Text = "Staged report rejected; no authoritative information created"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void CorrectGameReport_Click(object sender, RoutedEventArgs e)
    {
        if (StagedGameReportsGrid.SelectedItem is not StagedSingleGameReport staged || string.IsNullOrWhiteSpace(CorrectionField.Text)) { MessageBox.Show("Select a pending report and correction field."); return; }
        try { gameWorkflow.Correct(staged, CorrectionField.Text, CorrectionValue.Text, ReviewNote.Text); await store.SaveAsync(workspace); UpdateGameInformationView(); StatusText.Text = "Staged correction recorded with original value and note"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void StagedGameReportsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateGameInformationView();

    private void UpdateGameInformationView()
    {
        if (AcceptedSupplyText is null || StagedReportDetails is null) return;
        var supply = PageOneInformationSupply.Build(project); AcceptedSupplyText.Text = $"PAGE 1 FACTUAL SUPPLY: {supply.Status}" + (supply.Provenance is null ? "" : $" • {supply.Provenance.FileName} • imported {supply.Provenance.ImportedUtc:u}");
        if (StagedGameReportsGrid.SelectedItem is not StagedSingleGameReport staged) { AcceptGameReportButton.IsEnabled = ReplaceGameReportButton.IsEnabled = false; LinkedSourceStateText.Text = "Linked source: select a staged report"; StagedReportDetails.Text = "Select a staged report to inspect its review sections and provenance."; return; }
        var source = project.ExpectedDocuments.FirstOrDefault(x => x.Id == staged.ExpectedDocumentId); LinkedSourceStateText.Text = source is null ? "Linked source unavailable" : $"Linked source: {source.Name} • health {source.Status} • verification {source.Verification}"; AcceptGameReportButton.IsEnabled = gameWorkflow.CanAccept(project, staged, source, false); ReplaceGameReportButton.IsEnabled = gameWorkflow.CanAccept(project, staged, source, true);
        StagedReportDetails.Text = $"{staged.State}: {staged.AwayTeam} vs {staged.HomeTeam} • {staged.GameDate:d} • {staged.Site}\nFinal: Minden {staged.MindenScore}, {staged.Opponent} {staged.OpponentScore}\n\nPERIOD SCORING\n{string.Join("\n", staged.PeriodScores.Select(x => $"{x.Label}: Minden {x.MindenPoints}, opponent {x.OpponentPoints}"))}\n\nSCORING PLAYS\n{string.Join("\n", staged.ScoringPlays.Select(x => $"{x.Period} {x.Clock} {x.Description} {x.ScoreAfterPlay}"))}\n\nTEAM STATISTICS\n{string.Join("\n", staged.TeamStatistics.Select(x => $"{x.Label}: Minden {x.Minden.Reported}; opponent {x.Opponent.Reported}"))}\n\nRUSHING\n{string.Join("\n", staged.Rushing.Select(x => x.Reported))}\n\nPASSING\n{string.Join("\n", staged.Passing.Select(x => x.Reported))}\n\nRECEIVING\n{string.Join("\n", staged.Receiving.Select(x => x.Reported))}\n\nProvenance: import {staged.ImportRecordId}; expected document {staged.ExpectedDocumentId}; source family {staged.SourceFamilyId}\nIssues:\n{string.Join("\n", staged.Issues.Select(x => $"[{x.Severity}] {x.Message}"))}\nCorrections:\n{string.Join("\n", staged.Corrections.Select(x => $"{x.FieldKey}: '{x.OriginalValue}' → '{x.CorrectedValue}' ({x.Note})"))}";
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
