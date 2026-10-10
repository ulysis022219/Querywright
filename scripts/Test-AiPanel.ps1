# Offscreen panel checks using installed SSMS SDK assemblies. No SSMS process, SQL, API calls or history writes.
param([string]$HostIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE', [string]$Out)
$ErrorActionPreference = 'Stop'
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
    foreach ($button in $buttons) { Check (![string]::IsNullOrWhiteSpace([Windows.Automation.AutomationProperties]::GetName($button))) 'All panel buttons need accessible names' }
    $boxes = @($nodes | Where-Object { $_ -is [Windows.Controls.RichTextBox] })
    Check ($boxes.Count -eq 4 -and @($boxes | Where-Object { !$_.IsReadOnly -or !$_.IsDocumentEnabled }).Count -eq 0) 'Output must be selectable, read-only and allow embedded SQL actions'
    $prompt = $type.GetField('prompt', $flags).GetValue($panel)
    $type.GetField('context', $flags).GetValue($panel).Text = 'DEV-SQL · Sales | Schema: 2 tables (fixture)'
    Check ($prompt.MinLines -eq 2 -and $prompt.MaxLines -eq 6 -and $prompt.AcceptsReturn) 'Composer must grow to six lines'
    Check (@($nodes | Where-Object { $_.GetType().FullName -eq 'System.Windows.Controls.Primitives.ToggleButton' }).Count -eq 5) 'Five quick-action chips required'
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
            $type.GetField('scroll', $flags).GetValue($panel).ScrollToHome(); $panel.UpdateLayout()
            Check ($panel.Background -eq $panel.Resources[$colors::ToolWindowBackgroundBrushKey]) 'Panel background must follow changed theme resources'
            Check ($boxes[0].Foreground -eq $panel.Resources[$colors::ToolWindowTextBrushKey]) 'Reply foreground must follow changed theme resources'
            foreach ($button in $buttons) { Check ($button.ActualWidth -gt 0 -and $button.ActualHeight -ge 30) 'Panel buttons must retain usable size' }
            if ($Out) {
                [void][IO.Directory]::CreateDirectory($Out)
                $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$size.Width, [int]$size.Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
                $bitmap.Render($panel)
                $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                $file = [IO.File]::Create((Join-Path $Out "ai-panel-$theme-$scale.png"))
                try { $encoder.Save($file) } finally { $file.Dispose() }
            }
        }
    }
    $fresh = $buttons | Where-Object Content -eq 'New conversation'
    $fresh.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    $transcript = $type.GetField('transcript', $flags).GetValue($panel)
    Check ($transcript.Children.Count -eq 1 -and $transcript.Children[0].Tag -eq 'welcome') 'New conversation must restore welcome state'
} finally { [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver) }
Write-Host 'PASS: AI transcript, per-block actions, selectable output, accessibility, theme resource updates and 100/125/150/200% layouts. Offscreen fixtures; hands-on SSMS not tested.'
