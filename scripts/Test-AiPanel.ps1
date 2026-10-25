# Offscreen panel checks using installed SSMS SDK assemblies. No SSMS process, SQL, API calls or history writes.
param([string]$HostIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE', [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)
    $name = [Reflection.AssemblyName]::new($eventArgs.Name).Name + '.dll'
    foreach ($folder in 'PublicAssemblies', 'PrivateAssemblies', '') {
        $path = Join-Path (Join-Path $HostIde $folder) $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
function Check($condition, $message) { if (!$condition) { throw $message } }
function Nodes($element) {
    if ($element -isnot [Windows.DependencyObject]) { return }
    Write-Output -NoEnumerate $element
    foreach ($child in [Windows.LogicalTreeHelper]::GetChildren($element)) { Nodes $child }
}
# IsVisible is always false offscreen, so walk the logical parents for a collapsed ancestor instead.
function Shown($element) {
    for ($node = $element; $null -ne $node; $node = [Windows.LogicalTreeHelper]::GetParent($node)) {
        if ($node -is [Windows.UIElement] -and $node.Visibility -ne [Windows.Visibility]::Visible) { return $false }
    }
    return $true
}
function Named($items, $name) { @($items | Where-Object { [Windows.Automation.AutomationProperties]::GetName($_) -eq $name }) }
function Snap($element, $size, $name) {
    if (!$Out) { return }
    [void][IO.Directory]::CreateDirectory($Out)
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$size.Width, [int]$size.Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($element)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file = [IO.File]::Create((Join-Path $Out "$name.png"))
    try { $encoder.Save($file) } finally { $file.Dispose() }
}
try {
    $bin = Join-Path $PSScriptRoot '../src/Querywright.Ssms/bin/Debug/net472'
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Ssms.dll'))
    [void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Core.dll'))
    $type = $assembly.GetType('Querywright.Ssms.AiPanel', $true)
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $panel = [Activator]::CreateInstance($type, $flags, $null, @(), $null)
    $message = $type.GetMethod('Message', $flags)
    [void]$message.Invoke($panel, @('You', 'Find duplicate customers and explain the query.'))
    $reply = @'
## Find duplicate customers

Group rows by **email address**, then keep groups with *more than one row*. `HAVING` filters the groups.

```sql
SELECT Email, COUNT(*) AS DuplicateCount
FROM dbo.Customers
GROUP BY Email
HAVING COUNT(*) > 1; -- review duplicates
```

- Check how NULL email addresses should be handled.
- Review results before changing data.

| Column | Meaning |
| --- | --- |
| `Email` | The address shared by duplicates |
| `DuplicateCount` | Rows sharing that address |

### Inspect one address

```sql
SELECT CustomerId, Email
FROM dbo.Customers
WHERE Email = N'example@example.com';
```

```text
This block is explanatory text, not executable SQL.
```
'@
    $body = $message.Invoke($panel, @('Assistant', $reply))
    $type.GetMethod('Finish', $flags).Invoke($panel, @($body, $reply))
    $nodes = @(Nodes $panel)
    $buttons = @($nodes | Where-Object { $_ -is [Windows.Controls.Button] })
    foreach ($action in 'Insert', 'Replace', 'New window', 'Run') {
        Check (@($buttons | Where-Object Content -eq $action).Count -eq 2) "Each of two SQL blocks needs its own $action action"
    }
    Check (@($buttons | Where-Object Content -eq 'Copy').Count -eq 3) 'Every code block must be copyable'
    Check (@($buttons | Where-Object Content -eq 'Copy answer').Count -eq 1) 'Whole Markdown answer must be copyable'
    Check ((Named $buttons 'Logs').Count -eq 1) 'Panel must expose diagnostic logs without requiring a provider request'
    $boxes = @($nodes | Where-Object { $_ -is [Windows.Controls.RichTextBox] })
    Check (@($boxes | Where-Object { @($_.Document.Blocks | Where-Object { $_ -is [Windows.Documents.Table] }).Count -eq 1 }).Count -eq 1) 'Markdown tables must render as document tables'
    foreach ($button in $buttons) { Check (![string]::IsNullOrWhiteSpace([Windows.Automation.AutomationProperties]::GetName($button))) 'All panel buttons need accessible names' }
    Check ($boxes.Count -eq 4 -and @($boxes | Where-Object { !$_.IsReadOnly -or !$_.IsDocumentEnabled }).Count -eq 0) 'Output must be selectable, read-only and allow embedded SQL actions'
    $model = $type.GetField('model', $flags).GetValue($panel)
    $model.ItemsSource = @('fixture-model'); $model.Text = 'fixture-model'
    Check ($model.IsEditable -and ![string]::IsNullOrWhiteSpace([Windows.Automation.AutomationProperties]::GetName($model))) 'Model picker needs typed fallback and accessible name'
    $reasoning = $type.GetField('reasoning', $flags).GetValue($panel)
    Check ($reasoning.Items.Count -eq 4 -and $reasoning.SelectedIndex -eq 0) 'Reasoning picker needs Off/Low/Med/High and defaults to Off'
    $type.GetMethod('AddReasoning', $flags).Invoke($panel, @($body, 'Thinking includes ```sql SELECT 99;``` but must never offer SQL actions.', 2.5))
    $thought = $body.Children[0]
    Check ($thought -is [Windows.Controls.Expander] -and !$thought.IsExpanded) 'Reasoning must appear above answer and start collapsed'
    Check ($thought.Content -is [Windows.Controls.RichTextBox] -and $thought.Content.IsReadOnly) 'Reasoning must be plain, selectable and read-only'
    Check ($thought.Content.VerticalScrollBarVisibility -eq [Windows.Controls.ScrollBarVisibility]::Auto) 'Long reasoning must remain scrollable within its height limit'
    Check (@(Nodes $thought | Where-Object { $_ -is [Windows.Controls.Button] }).Count -eq 0) 'Reasoning must never expose insert/replace/run/copy-answer actions'
    $logType = $assembly.GetType('Querywright.Ssms.DiagnosticsDialog', $true)
    $logDialog = [Activator]::CreateInstance($logType, $flags, $null, @(), $null)
    $logView = $logType.GetField('view', $flags).GetValue($logDialog)
    Check ($logView.IsReadOnly -and $logView.VerticalScrollBarVisibility -eq [Windows.Controls.ScrollBarVisibility]::Auto -and $logView.HorizontalScrollBarVisibility -eq [Windows.Controls.ScrollBarVisibility]::Auto) 'Imported diagnostic text must be read-only and scrollable'
    $logButtons = @(Nodes $logDialog | Where-Object { $_ -is [Windows.Controls.Button] })
    foreach ($action in '_Refresh', '_Import logs', '_Export logs', '_Close') { Check (@($logButtons | Where-Object Content -eq $action).Count -eq 1) "Diagnostics dialog needs $action" }
    $logDialog.Content.Measure([Windows.Size]::new(460, 320)); $logDialog.Content.Arrange([Windows.Rect]::new(0, 0, 460, 320)); $logDialog.Content.UpdateLayout()
    Check ($logView.ActualHeight -gt 0) 'Diagnostic text must remain visible at minimum dialog size'
    $logDialog.Close()
    # Model persistence runs against a temporary file, never user settings.
    $models = $assembly.GetType('Querywright.Ssms.AiModels', $true)
    $staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
    $modelFile = $models.GetField('FilePath', $staticFlags); $modelCache = $models.GetField('saved', $staticFlags)
    $originalFile = $modelFile.GetValue($null); $originalCache = $modelCache.GetValue($null)
    $temporaryFile = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.models.json')
    try {
        $modelFile.SetValue($null, $temporaryFile); $modelCache.SetValue($null, $null)
        $remember = $models.GetMethod('Remember', $staticFlags)
        1..12 | ForEach-Object { $remember.Invoke($null, @('https://fixture.invalid/v1', "fixture-$_")) }
        $remember.Invoke($null, @('https://other.invalid', 'other-fixture'))
        $recent = $models.GetMethod('Recent', $staticFlags).Invoke($null, @('https://fixture.invalid/v1/'))
        Check ($recent.Count -eq 10 -and $recent[0] -eq 'fixture-12') 'Recent model history must keep last ten per base URL'
        $modelCache.SetValue($null, $null)
        Check ($models.GetMethod('Selected', $staticFlags).Invoke($null, @('https://fixture.invalid/v1')) -eq 'fixture-12') 'Selected model must survive a fresh persistence read'
        Check ($models.GetMethod('Selected', $staticFlags).Invoke($null, @('https://other.invalid')) -eq 'other-fixture') 'Provider choices must stay separate'
        Check (![IO.File]::ReadAllText($temporaryFile).Contains('https://')) 'Model history must hash provider URL keys'
    } finally {
        $modelFile.SetValue($null, $originalFile); $modelCache.SetValue($null, $originalCache)
        [IO.File]::Delete($temporaryFile); [IO.File]::Delete($temporaryFile + '.tmp')
    }
    $prompt = $type.GetField('prompt', $flags).GetValue($panel)
    $type.GetField('context', $flags).GetValue($panel).Text = 'Sales on DEV-SQL, 2 tables'
    Check ($prompt.MinLines -eq 2 -and $prompt.MaxLines -eq 6 -and $prompt.AcceptsReturn) 'Composer must grow to six lines'
    Check (@($nodes | Where-Object { $_.GetType().FullName -eq 'System.Windows.Controls.Primitives.ToggleButton' }).Count -eq 8) 'Eight task chips required'
    $colors = [Microsoft.VisualStudio.PlatformUI.EnvironmentColors]
    foreach ($theme in 'light', 'dark') {
        $background = $(if ($theme -eq 'dark') { '#1E1E1E' } else { '#FFFFFF' })
        $foreground = $(if ($theme -eq 'dark') { '#E4E4E4' } else { '#202020' })
        $surface = $(if ($theme -eq 'dark') { '#292929' } else { '#F5F5F5' })
        $border = $(if ($theme -eq 'dark') { '#454545' } else { '#D8D8D8' })
        # Fixture colors stand in for the host's theme dictionary; production UI uses dynamic resource keys.
        $panel.Resources[$colors::ToolWindowBackgroundBrushKey] = [Windows.Media.BrushConverter]::new().ConvertFromString($background)
        $panel.Resources[$colors::ToolWindowTextBrushKey] = [Windows.Media.BrushConverter]::new().ConvertFromString($foreground)
        $panel.Resources[$colors::ComboBoxBackgroundBrushKey] = [Windows.Media.BrushConverter]::new().ConvertFromString($surface)
        $panel.Resources[$colors::ToolWindowBorderBrushKey] = [Windows.Media.BrushConverter]::new().ConvertFromString($border)
        $panel.Resources[$colors::PanelHyperlinkBrushKey] = [Windows.Media.BrushConverter]::new().ConvertFromString($(if ($theme -eq 'dark') { '#8AB4F8' } else { '#245AA8' }))
        foreach ($scale in 100, 125, 150, 200) {
            $factor = $scale / 100.0
            $panel.LayoutTransform = [Windows.Media.ScaleTransform]::new($factor, $factor)
            $size = [Windows.Size]::new(440 * $factor, 820 * $factor)
            $panel.Measure($size); $panel.Arrange([Windows.Rect]::new($size)); $panel.UpdateLayout()
            foreach ($picker in $model, $reasoning) { $type.GetMethod('ThemePicker', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @($picker)) }
            $panel.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
            $type.GetField('scroll', $flags).GetValue($panel).ScrollToHome(); $panel.UpdateLayout()
            Check ($panel.Background -eq $panel.Resources[$colors::ToolWindowBackgroundBrushKey]) 'Panel background must follow changed theme resources'
            Check ($boxes[0].Foreground -eq $panel.Resources[$colors::ToolWindowTextBrushKey]) 'Reply foreground must follow changed theme resources'
            Check ($thought.Foreground -eq $panel.Resources[$colors::ToolWindowTextBrushKey]) 'Reasoning heading must follow theme foreground'
            Check ($thought.Header.Foreground -eq $panel.Resources[$colors::ToolWindowTextBrushKey]) 'Reasoning header text must follow theme foreground'
            foreach ($button in @($buttons | Where-Object { Shown $_ })) { Check ($button.ActualWidth -gt 0 -and $button.ActualHeight -ge 30) "Panel button '$([Windows.Automation.AutomationProperties]::GetName($button))' must retain usable size" }
            Snap $panel $size "ai-panel-$theme-$scale"
        }
    }
    $layout = {
        $panel.LayoutTransform = $null
        $size = [Windows.Size]::new(440, 820)
        $panel.Measure($size); $panel.Arrange([Windows.Rect]::new($size)); $panel.UpdateLayout()
        $panel.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
        $size
    }
    $drawer = $type.GetField('drawer', $flags).GetValue($panel)
    $type.GetMethod('ToggleDrawer', $flags).Invoke($panel, @())
    Check ($drawer.Visibility -eq [Windows.Visibility]::Visible) 'Model button must open the model drawer'
    $surfaces = { param($node) if ($node -is [Windows.Controls.Border]) { $node }; for ($i = 0; $i -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($node); $i++) { & $surfaces ([Windows.Media.VisualTreeHelper]::GetChild($node, $i)) } }
    $pickerBorders = @(& $surfaces $reasoning)
    Check ($pickerBorders.Count -gt 0 -and @($pickerBorders | Where-Object { $_.Background -ne $panel.Resources[$colors::ComboBoxBackgroundBrushKey] }).Count -eq 0) 'Reasoning picker must be themed after the drawer opens'
    Snap $panel (& $layout) 'ai-panel-dark-drawer'
    $type.GetMethod('ToggleDrawer', $flags).Invoke($panel, @())
    Check ($drawer.Visibility -eq [Windows.Visibility]::Collapsed) 'Model button must close the model drawer'
    $fresh = Named $buttons 'New conversation'
    Check ($fresh.Count -eq 1) 'Panel needs one New conversation button'
    $fresh[0].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    $transcript = $type.GetField('transcript', $flags).GetValue($panel)
    Check ($transcript.Children.Count -eq 1 -and $transcript.Children[0].Tag -eq 'welcome') 'New conversation must restore welcome state'
    $welcomeButtons = @(Nodes $transcript | Where-Object { $_ -is [Windows.Controls.Button] })
    Check ($welcomeButtons.Count -eq 5 -and @($welcomeButtons | Where-Object { [string]::IsNullOrWhiteSpace([Windows.Automation.AutomationProperties]::GetName($_)) }).Count -eq 0) 'Welcome state needs five named starters'
    $welcomeButtons[0].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    Check ($prompt.Text -eq '/write ') 'Welcome starter must seed the prompt'
    $prompt.Clear()
    Snap $panel (& $layout) 'ai-panel-dark-welcome'
} finally { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
Write-Host 'PASS: AI transcript, per-block actions, separate reasoning, model persistence, accessibility, theme resource updates and 100/125/150/200% layouts. Offscreen fixtures; hands-on SSMS not tested.'
