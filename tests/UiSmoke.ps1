param([Parameter(Mandatory=$true)][string]$Exe, [ValidateSet('Accept','Decline','Expire')][string]$Decision = 'Accept', [string]$Screenshot)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PickerNative {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, string text);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
}
'@
$scope = [System.Windows.Automation.TreeScope]::Descendants
function Find-Control($root, $property, $value) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($property, $value)
    return $root.FindFirst($scope, $condition)
}
function Find-Id($root, $id) { Find-Control $root ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $id }
function Find-Name($root, $name) { Find-Control $root ([System.Windows.Automation.AutomationElement]::NameProperty) $name }
function Invoke-Control($control) {
    if ($null -eq $control) { throw 'Required UI control was not found.' }
    $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Wait-Until([scriptblock]$check, [int]$seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        $result = & $check
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'UI test timed out.'
}
function Window-For($process) {
    $process.Refresh()
    if ($process.HasExited) { throw 'Application exited unexpectedly.' }
    if ($process.MainWindowHandle -ne 0) { return [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle) }
}
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('openshare-ui-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testDirectory) | Out-Null
$fileName = 'OpenShare-UI-' + [Guid]::NewGuid().ToString('N') + '.txt'
$source = Join-Path $testDirectory $fileName
$destination = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('OpenShare\' + $fileName)
[IO.File]::WriteAllText($source, 'OpenShare automated UI transfer fixture.')
$receiver = $null
$sender = $null
try {
    $receiver = Start-Process -FilePath $Exe -PassThru -WindowStyle Hidden
    $sender = Start-Process -FilePath $Exe -PassThru -WindowStyle Hidden
    $receiveWindow = Wait-Until { Window-For $receiver }
    $sendWindow = Wait-Until { Window-For $sender }
    if ($Screenshot) {
        Start-Sleep -Milliseconds 500
        $bounds = $receiveWindow.Current.BoundingRectangle
        $bitmap = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        try { $null = [PickerNative]::PrintWindow($receiver.MainWindowHandle, $dc, 2) }
        finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        try { $bitmap.Save($Screenshot) } finally { $bitmap.Dispose() }
    }
    Invoke-Control (Find-Id $receiveWindow 'ListenButton')
    $pairing = Wait-Until {
        $value = (Find-Id $receiveWindow 'PairCode').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value.StartsWith('openshare1|')) { return ($value -split "`n")[0].Trim() }
    }
    Invoke-Control (Find-Name $sendWindow 'Choose file')
    $dialog = Wait-Until { Find-Name $sendWindow 'Choose a file to send' }
    $filenameControl = Find-Id $dialog '1148'
    if (-not $filenameControl.Current.IsEnabled) { throw 'File picker is not enabled.' }
    $editable = Find-Control $filenameControl ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Edit)
    if ($null -ne $editable) { $filenameControl = $editable }
    $combo = [PickerNative]::GetDlgItem([IntPtr]$dialog.Current.NativeWindowHandle, 1148)
    $inner = [PickerNative]::FindWindowEx($combo, [IntPtr]::Zero, 'ComboBox', $null)
    $edit = [PickerNative]::FindWindowEx($inner, [IntPtr]::Zero, 'Edit', $null)
    if ($edit -eq [IntPtr]::Zero) { throw 'Native file-name edit handle was not found.' }
    $null = [PickerNative]::SendMessage($edit, 0x000C, [IntPtr]::Zero, $source)
    $null = [PickerNative]::PostMessage([IntPtr]$dialog.Current.NativeWindowHandle, 0x0111, [IntPtr]1, [IntPtr]::Zero)
    Wait-Until { (Find-Id $sendWindow 'FileText').Current.Name -eq $source } | Out-Null
    (Find-Id $sendWindow 'CodeBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($pairing)
    Invoke-Control (Find-Name $sendWindow 'Send file')
    $approval = Wait-Until { Find-Name $receiveWindow 'Incoming file' }
    if (Test-Path -LiteralPath $destination) { throw 'File was saved before UI approval.' }
    if ($Decision -ne 'Expire') {
        $command = if ($Decision -eq 'Accept') { 6 } else { 7 }
        $null = [PickerNative]::PostMessage([IntPtr]$approval.Current.NativeWindowHandle, 0x0111, [IntPtr]$command, [IntPtr]::Zero)
    }
    if ($Decision -eq 'Accept') {
        Wait-Until { (Find-Id $sendWindow 'StatusText').Current.Name -eq 'Receiver confirmed: file saved and verified.' } | Out-Null
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw 'UI transfer content mismatch.' }
    } else {
        Wait-Until { (Find-Id $sendWindow 'StatusText').Current.Name.StartsWith('Transfer not confirmed:') } 35 | Out-Null
        Wait-Until { -not (Find-Name $receiveWindow 'Incoming file') } | Out-Null
        if (Test-Path -LiteralPath $destination) { throw 'Declined or expired transfer saved a file.' }
    }
    Write-Output "PASS: packaged app UI workflow ($Decision), file picker, pairing, and destination verification."
} catch {
    Write-Output $_.ScriptStackTrace
    if ($sendWindow) {
        foreach ($item in $sendWindow.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition)) {
            if ($item.Current.AutomationId -eq 'FileText' -or $item.Current.AutomationId -eq 'StatusText' -or $item.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window -or $item.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
                Write-Output ($item.Current.AutomationId + ' / ' + $item.Current.Name)
            }
        }
        Write-Output ('Receiver status: ' + (Find-Id $receiveWindow 'StatusText').Current.Name)
    }
    throw
} finally {
    foreach ($process in @($sender, $receiver)) {
        if ($null -ne $process -and -not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id }
        }
    }
    # Only these uniquely named fixtures belong to this test. Never remove the user's receive folder.
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
    Remove-Item -LiteralPath $source
    Remove-Item -LiteralPath $testDirectory
}
