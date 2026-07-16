using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mountainizer.Core;
using Mountainizer.Export;
using Mountainizer.Formats;
using Mountainizer.Iso;
using Mountainizer.Rendering;

namespace Mountainizer.Editor;

public partial class MainWindow : Window
{
    private sealed record LevelSelection(string Code, string DisplayName, Ssx3CourseDefinition? Course, Ssx3LevelArea? Area);
    private sealed record AssetEntry(string DisplayName, ISceneItem Item);
    private sealed record PropCategoryFilterEntry(string DisplayName, PropRenderCategory? Category);
    private readonly SceneRenderer _renderer = new();
    private MountainizerProject? _project;
    private Ssx3Sdb? _sdb;
    private Ssx3LevelParseResult? _parseResult;
    private MountainizerScene? _scene;
    private MountainizerScene? _textureResolverScene;
    private SceneTextureResolver? _textureResolver;
    private Point _lastMouse;
    private MouseButton? _dragButton;
    private bool _loadingLevel;
    private ISceneItem? _selectedItem;
    private readonly ObservableCollection<ParseDiagnostic> _diagnostics = [];

    public MainWindow()
    {
        InitializeComponent(); DiagnosticsGrid.ItemsSource = _diagnostics;
        HierarchyCategoryFilter.ItemsSource = new[] { new PropCategoryFilterEntry("All types", null) }
            .Concat(Enum.GetValues<PropRenderCategory>().Select(x => new PropCategoryFilterEntry(CategoryLabel(x), x))).ToArray();
        HierarchyCategoryFilter.DisplayMemberPath = nameof(PropCategoryFilterEntry.DisplayName); HierarchyCategoryFilter.SelectedIndex = 0;
        GlViewport.Start(); Log("Information", "Mountainizer started in read-only mode.");
        Loaded += async (_, _) =>
        {
            var requested = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            var recent = ReadRecentProject();
            var projectPath = requested ?? (recent is not null && File.Exists(recent) ? recent : null);
            if (projectPath is null) return;
            try { SetBusy(true, "Opening recent project…"); await OpenProjectAsync(ProjectService.Open(projectPath)); }
            catch (Exception ex) { ShowError("Recent project could not be opened", ex); }
            finally { SetBusy(false, "Ready"); }
        };
    }

    private async void ImportIso_Click(object sender, RoutedEventArgs e)
    {
        var isoDialog = new OpenFileDialog { Title = "Select a legally obtained SSX 3 PS2 ISO", Filter = "PlayStation 2 ISO (*.iso;*.bin)|*.iso;*.bin|All files|*.*" };
        if (isoDialog.ShowDialog(this) != true) return;
        var folderDialog = new OpenFolderDialog { Title = "Choose a parent folder for the Mountainizer project" };
        if (folderDialog.ShowDialog(this) != true) return;
        var projectName = System.IO.Path.GetFileNameWithoutExtension(isoDialog.FileName);
        var projectDirectory = System.IO.Path.Combine(folderDialog.FolderName, Sanitize(projectName));
        try
        {
            SetBusy(true, "Importing ISO…"); var diagnostics = new DiagnosticBag();
            var progress = new Progress<(long Current, long Total, string Stage)>(p => UpdateProgress(p.Stage, p.Total == 0 ? 0 : p.Current * 100d / p.Total));
            _project = await ProjectService.ImportAsync(isoDialog.FileName, projectDirectory, projectName, diagnostics, progress);
            AddDiagnostics(diagnostics); await OpenProjectAsync(_project); Log("Information", $"Created project {_project.ProjectDirectory}");
        }
        catch (Exception ex) { ShowError("ISO import failed", ex); }
        finally { SetBusy(false, "Ready"); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open Mountainizer project", Filter = "Mountainizer project (project.json)|project.json|JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { SetBusy(true, "Opening project…"); await OpenProjectAsync(ProjectService.Open(dialog.FileName)); }
        catch (Exception ex) { ShowError("Project could not be opened", ex); }
        finally { SetBusy(false, "Ready"); }
    }

    private async Task OpenProjectAsync(MountainizerProject project)
    {
        _project = project; WriteRecentProject(Path.Combine(project.ProjectDirectory, "project.json")); var diagnostics = new DiagnosticBag(); var sdbPath = ProjectService.WorldFile(project, ".sdb");
        _sdb = await Task.Run(() => Ssx3Sdb.Parse(File.ReadAllBytes(sdbPath), sdbPath, diagnostics)); AddDiagnostics(diagnostics);
        var areasByName = _sdb.Areas.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var courseCodes = Ssx3CourseCatalog.Courses.Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selections = Ssx3CourseCatalog.Courses
            .Where(x => areasByName.ContainsKey(x.Code))
            .Select(x => new LevelSelection(x.Code, x.DisplayName, x, areasByName[x.Code]))
            .Concat(_sdb.Areas.Where(x => !courseCodes.Contains(x.Name) && !x.Name.Equals("TRANSP", StringComparison.OrdinalIgnoreCase))
                .Select(x => new LevelSelection(x.Name, $"Technical mountain area  •  {x.Name}", null, x)))
            .ToArray();
        LevelPicker.ItemsSource = selections; LevelPicker.DisplayMemberPath = nameof(LevelSelection.DisplayName);
        StatusText.Text = $"{project.ProjectName} · {project.DetectedRevision}";
        if (selections.Length > 0)
        {
            var savedCourse = project.SelectedLevel is not null && Ssx3CourseCatalog.Find(project.SelectedLevel) is not null
                ? selections.FirstOrDefault(x => x.Code.Equals(project.SelectedLevel, StringComparison.OrdinalIgnoreCase)) : null;
            var initial = savedCourse ?? selections.FirstOrDefault(x => x.Code.Equals("ABC1", StringComparison.OrdinalIgnoreCase))
                ?? selections[0];
            LevelPicker.SelectedItem = initial;
        }
    }

    private async void LevelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLevel || _project is null || _sdb is null || LevelPicker.SelectedItem is not LevelSelection { Area: not null } selection) return;
        var area = selection.Area;
        _loadingLevel = true;
        try
        {
            SetBusy(true, $"Parsing {area.Name}…");
            var progress = new Progress<(int Current, int Total, string Stage)>(p => UpdateProgress(p.Stage, p.Total == 0 ? 0 : p.Current * 100d / p.Total));
            var ssbPath = ProjectService.WorldFile(_project, ".ssb");
            _parseResult = await Task.Run(() => selection.Course is not null
                ? Ssx3SsbReader.ParseCourse(ssbPath, _sdb, selection.Course, 8, progress)
                : Ssx3SsbReader.ParseLevel(ssbPath, area, 8, progress)); _scene = _parseResult.Scene;
            _renderer.SetScene(_scene); _renderer.FrameCourseStart(_scene, selection.Code); _project.SelectedLevel = area.Name; ProjectService.Save(_project); AddDiagnostics(_parseResult.Diagnostics);
            BuildHierarchy(); StatusText.Text = $"{_project.ProjectName} · {_scene.Name} [{selection.Code}] · {_scene.Terrain.Count} patches · {_scene.Props.Count} props · {_scene.Splines.Count} splines · {_scene.NavigationPaths.Count} navigation paths";
            Log("Information", $"Loaded {_scene.Name} [{selection.Code}]: {_scene.Terrain.Count} terrain patches, {_scene.Props.Count} prop instances, {_scene.Splines.Count} splines, {_scene.NavigationPaths.Count} navigation paths, {_scene.NavigationMarkers.Count} navigation markers, {_scene.PlanarRoutes.Count} radar routes, {_scene.Collisions.Count} collision meshes, {_scene.SphereTrees.Count} collision sphere trees, {_scene.SoundTriggerTables.Count} sound-trigger tables, {_scene.StructuredTables.Count} structured tables, {_scene.AudioBanks.Count} BNKl banks, {_scene.AvalancheAnimations.Count} avalanche streams, {_scene.ParticleModels.Count} particle models, {_scene.ParticleEmitters.Count} particle emitters, {_scene.Lights.Count} lights, {_scene.Halos.Count} halos, {_scene.NisReferenceTables.Count} NIS script-object tables, {_scene.CameraTriggerTables.Count} camera-trigger tables, {_scene.Triggers.Count} triggers, {_scene.VisibilityCurtains.Count} visibility curtains, and {_scene.Textures.Count} textures.");
        }
        catch (Exception ex) { ShowError($"Level {area.Name} could not be parsed", ex); }
        finally { _loadingLevel = false; SetBusy(false, "Ready"); }
    }

    private void BuildHierarchy()
    {
        SceneTree.Items.Clear(); if (_scene is null) return; var filter = SearchBox.Text.Trim();
        if (!ReferenceEquals(_textureResolverScene, _scene))
        {
            _textureResolverScene = _scene; _textureResolver = new SceneTextureResolver(_scene);
            TexturePreview.Source = null; AssetDetails.Text = "Select an object or asset to preview its texture.";
        }
        var root = Item($"Level — {_scene.Name}", null);
        AddCategory(root, "Terrain", _scene.Terrain.Cast<ISceneItem>(), filter);
        var categoryFilter = (HierarchyCategoryFilter.SelectedItem as PropCategoryFilterEntry)?.Category;
        foreach (var category in Enum.GetValues<PropRenderCategory>())
        {
            if (categoryFilter is not null && category != categoryFilter) continue;
            AddCategory(root, $"Props - {CategoryLabel(category)}", _scene.Props.Where(x => x.Classification.Category == category).Cast<ISceneItem>(), filter);
        }
        AddCategory(root, "Splines", _scene.Splines.Cast<ISceneItem>(), filter);
        AddCategory(root, "Navigation tables and paths", _scene.NavigationTables.Cast<ISceneItem>()
            .Concat(_scene.NavigationPaths)
            .Concat(_scene.NavigationMarkers).Concat(_scene.UnknownSections.Where(x => x.ResourceType == 14)), filter);
        AddCategory(root, "Collision meshes", _scene.Collisions.Cast<ISceneItem>()
            .Concat(_scene.SphereTrees).Concat(_scene.UnknownSections.Where(x => x.ResourceType == 12)), filter);
        AddCategory(root, "Sound trigger tables", _scene.SoundTriggerTables.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 13)), filter);
        AddCategory(root, "Radar routes", _scene.PlanarRoutes.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 21)), filter);
        AddCategory(root, "Structured Type-15/16 tables", _scene.StructuredTables.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType is 15 or 16)), filter);
        AddCategory(root, "BNKl audio banks", _scene.AudioBanks.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 20)), filter);
        AddCategory(root, "Camera Trigger Tables", _scene.CameraTriggerTables.Cast<ISceneItem>(), filter);
        AddCategory(root, "Triggers", _scene.Triggers.Cast<ISceneItem>(), filter); AddCategory(root, "Visibility", _scene.VisibilityCurtains.Cast<ISceneItem>(), filter);
        AddCategory(root, "Models", _scene.Models.Cast<ISceneItem>(), filter); AddCategory(root, "Materials", _scene.Materials.Cast<ISceneItem>(), filter); AddCategory(root, "Textures", _scene.Textures.Cast<ISceneItem>(), filter);
        AddCategory(root, "Particle models", _scene.ParticleModels.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 4)), filter);
        AddCategory(root, "Particle emitters", _scene.ParticleEmitters.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 5)), filter);
        AddCategory(root, "Lights", _scene.Lights.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 6)), filter);
        AddCategory(root, "Halos", _scene.Halos.Cast<ISceneItem>()
            .Concat(_scene.UnknownSections.Where(x => x.ResourceType == 7)), filter);
        AddCategory(root, "NIS script objects / avalanche", _scene.AvalancheAnimations.Cast<ISceneItem>()
            .Concat(_scene.NisReferenceTables).Concat(_scene.UnknownSections.Where(x => x.ResourceType is 18 or 22)), filter);
        AddCategory(root, "Unknown Sections", _scene.UnknownSections.Where(x => x.ResourceType is not (4 or 5 or 6 or 7 or 12 or 13 or 14 or 15 or 16 or 18 or 20 or 21 or 22)).Cast<ISceneItem>(), filter, 500);
        root.IsExpanded = true; SceneTree.Items.Add(root);
        AssetList.ItemsSource = _scene.Models.Select(x => new AssetEntry($"Model  •  {x.Name}", x))
            .Concat(_scene.Materials.Select(x => new AssetEntry($"Material  •  {x.Name}", x)))
            .Concat(_scene.Textures.Select(x => new AssetEntry($"Texture  •  {x.Name}  ({x.Width}×{x.Height})", x))).ToArray();
    }
    private static TreeViewItem Item(string header, object? tag) => new() { Header = header, Tag = tag };
    private static void AddCategory(TreeViewItem root, string name, IEnumerable<ISceneItem> items, string filter, int displayLimit = 5000)
    {
        var all = items.ToList(); var category = Item($"{name} ({all.Count})", null); var shown = 0;
        foreach (var item in all.Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))) { if (shown++ >= displayLimit) break; category.Items.Add(Item(item.Name, item)); }
        if (shown < all.Count && shown >= displayLimit) category.Items.Add(Item($"… {all.Count - displayLimit} more (use search)", null));
        root.Items.Add(category);
    }
    private void SceneTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        SelectItem(e.NewValue is TreeViewItem { Tag: ISceneItem item } ? item : null);
    }
    private void SelectItem(ISceneItem? item)
    {
        _selectedItem = item;
        HideSelectedPropMenu.IsEnabled = item is PropInstance; ShowOnlySelectedTypeMenu.IsEnabled = item is PropInstance;
        if (item is null) { PropertyGrid.ItemsSource = null; _renderer.ClearSelection(); return; }
        var values = new List<KeyValuePair<string, string>> { new("Name", item.Name), new("Source file", item.Source.SourceFile), new("Source offset", $"0x{item.Source.SourceOffset:X}"),
            new("Source length", item.Source.SourceLength.ToString()), new("Section", item.Source.SectionName), new("Original index", item.Source.OriginalIndex.ToString()), new("Confidence", item.Source.Confidence.ToString()) };
        if (item is PropInstance classifiedProp)
        {
            values.Add(new("Render category", classifiedProp.Classification.Category.ToString()));
            values.Add(new("Classification reason", classifiedProp.Classification.Reason));
            values.Add(new("Category visible", _renderer.IsPropCategoryVisible(classifiedProp.Classification.Category).ToString()));
            values.Add(new("Manually hidden", _renderer.IsPropHidden(classifiedProp).ToString()));
        }
        values.AddRange(item.Properties.Select(x => new KeyValuePair<string, string>(x.Key, x.Value?.ToString() ?? "null"))); PropertyGrid.ItemsSource = values;
        if (_scene is not null) _renderer.SelectItem(_scene, item);
        ShowAssetPreview(item);
    }
    private void AssetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AssetList.SelectedItem is AssetEntry asset) ShowAssetPreview(asset.Item);
    }
    private void ShowAssetPreview(ISceneItem item)
    {
        var textures = _textureResolver?.Resolve(item);
        if (textures is null || textures.Count == 0)
        {
            TexturePreview.Source = null; AssetDetails.Text = $"{item.Name}: no decoded texture is linked to this asset."; return;
        }
        ShowTexture(textures[0], item, textures.Count);
    }
    private void ShowTexture(TextureAsset texture, ISceneItem context, int textureCount)
    {
        if (!texture.Decoded) { TexturePreview.Source = null; AssetDetails.Text = $"{texture.Name}: texture data is not decoded."; return; }
        var bgra = new byte[texture.RgbaPixels.Length];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = texture.RgbaPixels[i + 2]; bgra[i + 1] = texture.RgbaPixels[i + 1]; bgra[i + 2] = texture.RgbaPixels[i]; bgra[i + 3] = texture.RgbaPixels[i + 3]; }
        var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, bgra, texture.Width * 4);
        bitmap.Freeze(); TexturePreview.Source = bitmap;
        var linked = ReferenceEquals(context, texture) ? string.Empty : $"  •  linked from {context.Name}";
        var count = textureCount > 1 ? $"  •  first of {textureCount} textures" : string.Empty;
        AssetDetails.Text = $"{texture.Name}  •  {texture.Width}×{texture.Height}  •  RID {texture.ResourceId}{linked}{count}";
    }
    private void SceneTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_scene is not null && SceneTree.SelectedItem is TreeViewItem { Tag: PropInstance prop } && _renderer.FrameProp(_scene, prop)) e.Handled = true;
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => BuildHierarchy();
    private void HierarchyCategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => BuildHierarchy();
    private void GlViewport_Render(TimeSpan delta)
    {
        UpdateNavigation(delta);
        _renderer.Render((int)GlViewport.ActualWidth, (int)GlViewport.ActualHeight);
    }
    private void UpdateNavigation(TimeSpan delta)
    {
        var keyboardRotating = Keyboard.IsKeyDown(Key.Left) || Keyboard.IsKeyDown(Key.Right)
            || Keyboard.IsKeyDown(Key.Up) || Keyboard.IsKeyDown(Key.Down);
        if (!GlViewport.IsKeyboardFocusWithin && _dragButton is null && !keyboardRotating) return;
        var right = (Keyboard.IsKeyDown(Key.D) ? 1f : 0f) - (Keyboard.IsKeyDown(Key.A) ? 1f : 0f);
        var up = (Keyboard.IsKeyDown(Key.E) ? 1f : 0f) - (Keyboard.IsKeyDown(Key.Q) ? 1f : 0f);
        var forward = (Keyboard.IsKeyDown(Key.W) ? 1f : 0f) - (Keyboard.IsKeyDown(Key.S) ? 1f : 0f);
        var yaw = (Keyboard.IsKeyDown(Key.Right) ? 1f : 0f) - (Keyboard.IsKeyDown(Key.Left) ? 1f : 0f);
        var pitch = (Keyboard.IsKeyDown(Key.Up) ? 1f : 0f) - (Keyboard.IsKeyDown(Key.Down) ? 1f : 0f);
        var elapsedSeconds = (float)Math.Clamp(delta.TotalSeconds, 0, 0.1);
        var fast = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 4f : 1f;
        if (right != 0 || up != 0 || forward != 0) _renderer.Camera.Fly(right, up, forward, elapsedSeconds, fast);
        if (yaw != 0 || pitch != 0) _renderer.Camera.LookRadians(yaw * elapsedSeconds, pitch * elapsedSeconds);
    }
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(GlViewport); GlViewport.Focus();
        if (e.ChangedButton == MouseButton.Left)
        {
            var item = _renderer.Pick((float)position.X, (float)position.Y, (int)GlViewport.ActualWidth, (int)GlViewport.ActualHeight);
            SelectItem(item); if (item is not null) SelectTreeItem(item); e.Handled = true; return;
        }
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;
        _dragButton = e.ChangedButton; _lastMouse = position; GlViewport.CaptureMouse();
        if (_dragButton == MouseButton.Right) GlViewport.Cursor = Cursors.None;
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != _dragButton) return;
        _dragButton = null; GlViewport.Cursor = Cursors.Arrow; GlViewport.ReleaseMouseCapture();
    }
    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragButton = null; GlViewport.Cursor = Cursors.Arrow;
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e) { if (_dragButton is null) return; var now = e.GetPosition(GlViewport); var dx = (float)(now.X - _lastMouse.X); var dy = (float)(now.Y - _lastMouse.Y); if (_dragButton == MouseButton.Right) _renderer.Camera.Look(dx, dy); else if (_dragButton == MouseButton.Middle) _renderer.Camera.Pan(dx, dy, (float)GlViewport.ActualHeight); _lastMouse = now; }
    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.3f : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 2f : 1f;
        _renderer.Camera.Dolly(e.Delta / 120f, speed);
        e.Handled = true;
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        switch (e.Key) { case Key.F: if (_renderer.IsIsolated || _selectedItem is null || !_renderer.FrameItem(_selectedItem)) FrameScene(); e.Handled = true; break; case Key.Escape: ClearSelection(); e.Handled = true; break; }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) e.Handled = true;
    }
    private void ClearSelection()
    {
        if (SceneTree.SelectedItem is TreeViewItem selected) selected.IsSelected = false;
        SelectItem(null);
    }
    private void SelectTreeItem(ISceneItem item)
    {
        foreach (var root in SceneTree.Items.OfType<TreeViewItem>())
            if (Select(root)) return;
        return;

        bool Select(TreeViewItem node)
        {
            if (ReferenceEquals(node.Tag, item)) { node.IsSelected = true; node.BringIntoView(); return true; }
            foreach (var child in node.Items.OfType<TreeViewItem>())
                if (Select(child)) { node.IsExpanded = true; return true; }
            return false;
        }
    }
    private void FrameScene() { if (_scene is not null) { _renderer.ClearIsolation(); _renderer.Camera.Frame(_scene.Bounds); } }
    private void Frame_Click(object sender, RoutedEventArgs e) => FrameScene();
    private void Wireframe_Click(object sender, RoutedEventArgs e) => _renderer.Wireframe = ((MenuItem)sender).IsChecked;
    private void Culling_Click(object sender, RoutedEventArgs e) => _renderer.BackfaceCulling = ((MenuItem)sender).IsChecked;
    private void Grid_Click(object sender, RoutedEventArgs e) => _renderer.ShowGrid = ((MenuItem)sender).IsChecked;
    private void Terrain_Click(object sender, RoutedEventArgs e) => _renderer.ShowTerrain = ((MenuItem)sender).IsChecked;
    private void PropCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } item || !Enum.TryParse<PropRenderCategory>(value, out var category)) return;
        _renderer.SetPropCategoryVisible(category, item.IsChecked); RefreshSelectedProperties();
    }
    private void HideSelectedProp_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not PropInstance prop || !_renderer.HideProp(prop)) return;
        Log("Information", $"Hidden prop {prop.Name}. Use View > Unhide all props to restore it."); RefreshSelectedProperties();
    }
    private void ShowOnlySelectedType_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItem is not PropInstance prop) return;
        _renderer.ShowOnlyPropCategory(prop.Classification.Category); SyncPropCategoryMenus(); RefreshSelectedProperties();
        Log("Information", $"Showing only {CategoryLabel(prop.Classification.Category)} props.");
    }
    private void ShowAllPropTypes_Click(object sender, RoutedEventArgs e) { _renderer.ShowAllPropCategories(); SyncPropCategoryMenus(); RefreshSelectedProperties(); }
    private void ResetPropTypes_Click(object sender, RoutedEventArgs e) { _renderer.ShowOnlyPropCategory(PropRenderCategory.Visual); SyncPropCategoryMenus(); RefreshSelectedProperties(); }
    private void UnhideAllProps_Click(object sender, RoutedEventArgs e) { _renderer.ShowAllHiddenProps(); RefreshSelectedProperties(); }
    private void Splines_Click(object sender, RoutedEventArgs e) => _renderer.ShowSplines = ((MenuItem)sender).IsChecked;
    private void Triggers_Click(object sender, RoutedEventArgs e) => _renderer.ShowTriggers = ((MenuItem)sender).IsChecked;
    private void Visibility_Click(object sender, RoutedEventArgs e) => _renderer.ShowVisibilityCurtains = ((MenuItem)sender).IsChecked;
    private void ExportObj_Click(object sender, RoutedEventArgs e) { if (_scene is null) return; var dialog = new SaveFileDialog { Filter = "Wavefront OBJ (*.obj)|*.obj", FileName = _scene.Name + ".obj" }; if (dialog.ShowDialog(this) == true) { var result = ObjExporter.ExportScene(_scene, dialog.FileName); Log("Information", $"Exported textured scene to {result.ObjPath}, {result.MaterialPath}, and {result.TextureCount} PNG texture(s) in {result.TextureDirectory}"); } }
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e) { var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "mountainizer-diagnostics.json" }; if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(_diagnostics, DiagnosticBag.JsonOptions)); }
    private void Controls_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "Left mouse: select visible terrain or prop\nRight mouse: free look\nMiddle mouse: pan\nWheel: dolly forward or backward\nCtrl + wheel: precision dolly\nShift + wheel: fast dolly\nWASD + Q/E: move in camera space\nArrow keys: rotate the camera\nShift + movement: 4x faster\nF: frame selected item (or leave prop isolation)\nEscape: clear selection", "Viewport controls");
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void AddDiagnostics(DiagnosticBag bag) { foreach (var item in bag.Items) _diagnostics.Add(item); }
    private void SetBusy(bool busy, string text) { ProgressText.Text = text; LevelPicker.IsEnabled = !busy; }
    private void UpdateProgress(string stage, double percent) { ProgressText.Text = stage; ProgressBar.Value = Math.Clamp(percent, 0, 100); }
    private void Log(string level, string message) => LogBox.AppendText($"{DateTime.Now:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
    private void ShowError(string title, Exception ex) { Log("Error", ex.ToString()); MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    private MenuItem[] PropCategoryMenus => [PropVisualMenu, PropCollisionMenu, PropResetPlaneMenu, PropGameplayVolumeMenu, PropTriggerMenu,
        PropRideStateMenu, PropStreamingMarkerMenu, PropEffectMarkerMenu, PropProxyMenu];
    private void SyncPropCategoryMenus()
    {
        foreach (var item in PropCategoryMenus)
            if (item.Tag is string value && Enum.TryParse<PropRenderCategory>(value, out var category)) item.IsChecked = _renderer.IsPropCategoryVisible(category);
    }
    private void RefreshSelectedProperties() { var selected = _selectedItem; if (selected is not null) SelectItem(selected); }
    private static string CategoryLabel(PropRenderCategory category) => category switch
    {
        PropRenderCategory.ResetPlane => "Reset planes", PropRenderCategory.GameplayVolume => "Gameplay volumes",
        PropRenderCategory.RideState => "Ride-state markers", PropRenderCategory.StreamingMarker => "Streaming markers",
        PropRenderCategory.EffectMarker => "Effect markers", PropRenderCategory.Proxy => "Other proxies", _ => category.ToString()
    };
    private static string Sanitize(string value) => string.Concat(value.Select(x => System.IO.Path.GetInvalidFileNameChars().Contains(x) ? '_' : x));
    private static string RecentProjectFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mountainizer", "recent-project.txt");
    private static string? ReadRecentProject() { try { return File.Exists(RecentProjectFile) ? File.ReadAllText(RecentProjectFile).Trim() : null; } catch { return null; } }
    private static void WriteRecentProject(string path) { try { Directory.CreateDirectory(Path.GetDirectoryName(RecentProjectFile)!); File.WriteAllText(RecentProjectFile, path); } catch { /* Convenience only. */ } }
}
