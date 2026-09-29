# Run after dotnet build src/Querywright.Ssms, with Windows PowerShell -STA.
# Offscreen WPF checks: no SSMS install, database connection, or user-history changes.
param([string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms
$bin = Join-Path $PSScriptRoot '../src/Querywright.Ssms/bin/Debug/net472'
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Ssms.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $bin 'Querywright.Core.dll'))
function Dialog($name, [object[]]$arguments) {
    [Activator]::CreateInstance($assembly.GetType("Querywright.Ssms.$name", $true), [Reflection.BindingFlags]'Instance,NonPublic', $null, $arguments, $null)
}
function Nodes($element) {
    if ($element -isnot [Windows.DependencyObject]) { return }
    Write-Output -NoEnumerate $element
    foreach ($child in [Windows.LogicalTreeHelper]::GetChildren($element)) { Nodes $child }
}
function Check($condition, $message) { if (!$condition) { throw $message } }
function Layout($window, $name) {
    $root = $window.Content
    $size = [Windows.Size]::new($window.MinWidth - 32, $window.MinHeight - 48)
    $root.Measure($size); $root.Arrange([Windows.Rect]::new($size)); $root.UpdateLayout()
    foreach ($button in (Nodes $root | Where-Object { $_ -is [Windows.Controls.Button] })) {
        $point = $button.TranslatePoint([Windows.Point]::new(0, 0), $root)
        Check ($button.ActualHeight -ge 32) "$name button too short: $($button.Content)"
        Check ($point.X -ge -1 -and $point.Y -ge -1 -and $point.X + $button.ActualWidth -le $size.Width + 1 -and $point.Y + $button.ActualHeight -le $size.Height + 1) "$name button clipped: $($button.Content)"
    }
    if ($Out) {
        [void][IO.Directory]::CreateDirectory($Out)
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$size.Width, [int]$size.Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $background = [Windows.Media.DrawingVisual]::new()
        $drawing = $background.RenderOpen()
        $drawing.DrawRectangle($window.Background, $null, [Windows.Rect]::new($size)); $drawing.Close()
        $bitmap.Render($background)
        $bitmap.Render($root)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $Out "$name.png"))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    }
}
$previous = [Collections.Generic.HashSet[string]]::new()
[void]$previous.Add('master')
$db = Dialog 'DatabasePickerDialog' @('test-server', [string[]]@('master', 'sales_archive', 'Sales'), $previous, $false, $null)
try {
    $nodes = @(Nodes $db.Content)
    $filter = $nodes | Where-Object { $_ -is [Windows.Controls.TextBox] } | Select-Object -First 1
    $list = $nodes | Where-Object { $_ -is [Windows.Controls.ListBox] } | Select-Object -First 1
    $count = $nodes | Where-Object { $_ -is [Windows.Controls.TextBlock] -and $_.Text -like '*database selected*' } | Select-Object -First 1
    Check ($list.Items.Count -eq 2) 'System databases should be hidden without blank rows'
    Check ($count.Text -eq '1 database selected (1 hidden by filters)') 'Hidden selections must be explicit'
    $filter.Text = 'archive'
    Check ($list.Items.Count -eq 1 -and $list.Items[0].Content.Text -eq 'sales_archive') 'Filter must preserve underscores in database names'
    $all = $nodes | Where-Object { $_ -is [Windows.Controls.Button] -and $_.Content -eq 'Select _all shown' }
    $all.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    Check ($count.Text -eq '2 databases selected (1 hidden by filters)') 'Select all must preserve hidden selections'
    $filter.Text = 'no-match'
    Check ($list.Items.Count -eq 0 -and !$all.IsEnabled) 'Empty search should disable selection actions'
    $filter.Text = ''
    Layout $db 'database-picker'
} finally { $db.Close() }
$items = [Collections.Generic.List[Querywright.Core.CompletionItem]]::new()
$picker = Dialog 'CompletionPicker' @(,$items)
try {
    $insert = Nodes $picker.Content | Where-Object { $_ -is [Windows.Controls.Button] -and $_.Content -eq '_Insert' }
    Check (!$insert.IsEnabled) 'Empty suggestions must disable Insert'
    Layout $picker 'suggestions-empty'
} finally { $picker.Close() }
$items.Add([Activator]::CreateInstance([Querywright.Core.CompletionItem], [Reflection.BindingFlags]'Instance,NonPublic', $null, @('Customer_Id', '[Customer_Id]', 'int'), $null))
$items.Add([Activator]::CreateInstance([Querywright.Core.CompletionItem], [Reflection.BindingFlags]'Instance,NonPublic', $null, @('Orders', '[Orders]', 'table'), $null))
$picker = Dialog 'CompletionPicker' @(,$items)
try {
    $nodes = @(Nodes $picker.Content)
    $search = $nodes | Where-Object { $_ -is [Windows.Controls.TextBox] } | Select-Object -First 1
    $list = $nodes | Where-Object { $_ -is [Windows.Controls.ListBox] } | Select-Object -First 1
    $insert = $nodes | Where-Object { $_ -is [Windows.Controls.Button] -and $_.Content -eq '_Insert' }
    $search.Text = 'customer'
    Check ($list.Items.Count -eq 1 -and $list.SelectedItem.Name -eq 'Customer_Id' -and $insert.IsEnabled) 'Suggestions must filter case-insensitively and select matching item'
    $search.Text = 'missing'
    Check ($list.SelectedItem -eq $null -and !$insert.IsEnabled) 'No-match search must clear stale selection'
    $search.Text = ''
    Check ($list.Items.Count -eq 2 -and $insert.IsEnabled) 'Clearing search must restore suggestions'
    Layout $picker 'suggestions'
} finally { $picker.Close() }
$prompt = Dialog 'PromptDialog' @('Querywright: name', '_Name:', '', $null)
try {
    $ok = Nodes $prompt.Content | Where-Object { $_ -is [Windows.Controls.Button] -and $_.IsDefault }
    $ok.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    Check (@(Nodes $prompt.Content | Where-Object { $_ -is [Windows.Controls.TextBlock] -and $_.Text -eq 'Enter a name.' }).Count -eq 1) 'Blank input must show validation'
} finally { $prompt.Close() }
$rename = Dialog 'RenameVariableDialog' @('SELECT @old;', 'local variable', '@old', $null)
try { Layout $rename 'rename' } finally { $rename.Close() }
$style = Dialog 'FormattingStyleDialog' @([Querywright.Core.FormattingStyle]::new(), 'test-settings.xml')
try {
    $style.Size = $style.MinimumSize; $style.PerformLayout()
    Check ($style.AcceptButton.Enabled) 'Valid formatting preview must allow Save'
    Check ($style.Controls[0] -is [Windows.Forms.SplitContainer]) 'Formatting panes should be resizable'
} finally { $style.Dispose() }
$history = Dialog 'TabHistoryDialog' @([string](Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString())))
try {
    $actions = @(Nodes $history.Content | Where-Object { $_ -is [Windows.Controls.Button] -and !$_.IsCancel })
    Check ($actions.Count -eq 4 -and @($actions | Where-Object IsEnabled).Count -eq 0) 'Empty history must disable tab actions'
    Layout $history 'history-empty'
} finally { $history.Close() }
$columns = Dialog 'ColumnPickerDialog' @([string[]]@('Customer_Id', 'Name', 'Status'), $true)
try { Layout $columns 'columns' } finally { $columns.Close() }
Write-Host 'Dialog checks passed: filtering, hidden selections, empty actions, minimum-size button layouts.'
