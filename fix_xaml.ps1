$content = Get-Content "src\AcqShell.UI\MainWindow.axaml" -Raw

$content = $content -replace '<StackPanel x:Name="ResultViewsStackPanel" />', '<ItemsControl x:Name="ResultViewsGridControl">
                <ItemsControl.ItemsPanel>
                  <ItemsPanelTemplate>
                    <StackPanel Orientation="Vertical" Spacing="1" Background="#333" />
                  </ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                  <DataTemplate>
                    <Border Background="#1e1e1e" BorderBrush="#444" BorderThickness="0,0,0,1" Height="200" Padding="10">
                      <Grid ColumnDefinitions="50, *, 80">
                         <StackPanel Grid.Column="0" VerticalAlignment="Center">
                           <TextBlock Text="{Binding Title}" Foreground="White" FontWeight="Bold"/>
                         </StackPanel>
                         
                         <ContentControl Content="{Binding Canvas}" Grid.Column="1" Margin="10,0" ClipToBounds="True" />

                         <StackPanel Grid.Column="2" VerticalAlignment="Center" HorizontalAlignment="Right">
                           <TextBlock Text="Vpp: --" Foreground="#aaa" FontSize="11" />
                           <TextBlock Text="Avg: --" Foreground="#aaa" FontSize="11" />
                         </StackPanel>
                      </Grid>
                    </Border>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>'
Set-Content "src\AcqShell.UI\MainWindow.axaml" $content -Encoding UTF8
