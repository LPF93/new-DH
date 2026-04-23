$content = Get-Content "src\AcqShell.UI\MainWindow.axaml" -Raw
$content = $content -replace '<ItemsControl x:Name="ResultViewsGridControl">[\s\S]*?</ItemsControl>', '<UniformGrid x:Name="ResultViewsGridControl" />'
Set-Content "src\AcqShell.UI\MainWindow.axaml" $content -Encoding UTF8
