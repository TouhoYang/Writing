using System;
using System.Windows;
using System.Windows.Markup;

namespace Writing
{
    // 极简浅色主题:按钮 / 开关 / 色块 / 输入框 / 下拉框 / 目录树 / 滚动条 / 右键菜单的控件模板
    public static class UiTheme
    {
        public static ResourceDictionary Create()
        {
            string xaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">

  <SolidColorBrush x:Key=""Fg"" Color=""#23272E""/>
  <SolidColorBrush x:Key=""FgSoft"" Color=""#79818C""/>
  <SolidColorBrush x:Key=""FgFaint"" Color=""#A8AEB8""/>
  <SolidColorBrush x:Key=""Accent"" Color=""#2F6FEB""/>
  <SolidColorBrush x:Key=""AccentText"" Color=""#2F6FEB""/>
  <SolidColorBrush x:Key=""AccentSoft"" Color=""#EBF1FD""/>
  <SolidColorBrush x:Key=""BtnBg"" Color=""#2F6FEB""/>
  <SolidColorBrush x:Key=""BtnBgHover"" Color=""#2456BC""/>
  <SolidColorBrush x:Key=""Btn2Bg"" Color=""#F2F4F7""/>
  <SolidColorBrush x:Key=""Btn2Hover"" Color=""#E4E8EE""/>
  <SolidColorBrush x:Key=""Line"" Color=""#E9EBEF""/>
  <SolidColorBrush x:Key=""Hover"" Color=""#F2F4F7""/>
  <SolidColorBrush x:Key=""Press"" Color=""#E4E8EE""/>
  <SolidColorBrush x:Key=""PageBg"" Color=""#F6F7F9""/>
  <SolidColorBrush x:Key=""PanelBg"" Color=""#FFFFFF""/>
  <SolidColorBrush x:Key=""ToolbarBg"" Color=""#FFFFFF""/>
  <SolidColorBrush x:Key=""StatusBg"" Color=""#FFFFFF""/>
  <SolidColorBrush x:Key=""InputBg"" Color=""#FFFFFF""/>
  <SolidColorBrush x:Key=""BadgeBg"" Color=""#E9EBEF""/>
  <SolidColorBrush x:Key=""White"" Color=""#FFFFFF""/>
  <SolidColorBrush x:Key=""Sel"" Color=""#552F6FEB""/>
  <SolidColorBrush x:Key=""ThumbBg"" Color=""#4D000000""/>
  <SolidColorBrush x:Key=""ThumbHover"" Color=""#66000000""/>
  <SolidColorBrush x:Key=""ThumbDrag"" Color=""#80000000""/>
  <SolidColorBrush x:Key=""GitM"" Color=""#895503""/>
  <SolidColorBrush x:Key=""GitA"" Color=""#587C0C""/>
  <SolidColorBrush x:Key=""GitD"" Color=""#AD0707""/>
  <SolidColorBrush x:Key=""GitDot"" Color=""#A8AEB8""/>

  <!-- 幽灵按钮 -->
  <Style TargetType=""Button"">
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Padding"" Value=""9,5""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Button"">
          <Border x:Name=""bd"" Background=""{TemplateBinding Background}"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource Hover}""/>
            </Trigger>
            <Trigger Property=""IsPressed"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource Press}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Foreground"" Value=""{StaticResource FgFaint}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 主按钮 -->
  <Style x:Key=""PrimaryButton"" TargetType=""Button"">
    <Setter Property=""Background"" Value=""{StaticResource BtnBg}""/>
    <Setter Property=""Foreground"" Value=""#FFFFFF""/>
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Padding"" Value=""12,6""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Button"">
          <Border x:Name=""bd"" Background=""{TemplateBinding Background}"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource BtnBgHover}""/>
            </Trigger>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource Press}""/>
              <Setter Property=""Foreground"" Value=""{StaticResource FgFaint}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 分段开关 -->
  <Style TargetType=""ToggleButton"">
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Padding"" Value=""9,5""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ToggleButton"">
          <Border x:Name=""bd"" Background=""{TemplateBinding Background}"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource Hover}""/>
            </Trigger>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource AccentSoft}""/>
              <Setter Property=""Foreground"" Value=""{StaticResource Accent}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 背景色块 -->
  <Style x:Key=""Swatch"" TargetType=""ToggleButton"">
    <Setter Property=""Width"" Value=""26""/>
    <Setter Property=""Height"" Value=""26""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ToggleButton"">
          <Border x:Name=""ring"" CornerRadius=""9"" BorderThickness=""1.5"" BorderBrush=""Transparent"" Background=""Transparent"">
            <Border x:Name=""sw"" CornerRadius=""6"" Margin=""2.5"" Background=""{TemplateBinding Background}"" BorderBrush=""{StaticResource Line}"" BorderThickness=""1""/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""ring"" Property=""BorderBrush"" Value=""{StaticResource FgFaint}""/>
            </Trigger>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""ring"" Property=""BorderBrush"" Value=""{StaticResource Accent}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 输入框 -->
  <Style TargetType=""TextBox"">
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
    <Setter Property=""CaretBrush"" Value=""{StaticResource Accent}""/>
    <Setter Property=""SelectionBrush"" Value=""{StaticResource Sel}""/>
  </Style>

  <!-- 下拉框(浅色,含下拉列表) -->
  <Style TargetType=""ComboBox"">
    <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
    <Setter Property=""Background"" Value=""{StaticResource InputBg}""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""SnapsToDevicePixels"" Value=""True""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ComboBox"">
          <Grid>
            <ToggleButton x:Name=""Tgl"" Focusable=""False"" ClickMode=""Press""
                          IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}"">
              <ToggleButton.Template>
                <ControlTemplate TargetType=""ToggleButton"">
                  <Border x:Name=""bdb"" Background=""{StaticResource InputBg}"" BorderBrush=""{StaticResource Line}"" BorderThickness=""1"" CornerRadius=""3"">
                    <Path Data=""M 0,0 L 4,4 L 8,0"" Stroke=""{StaticResource FgSoft}"" StrokeThickness=""1.4""
                          HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,8,0""/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property=""IsMouseOver"" Value=""True"">
                      <Setter TargetName=""bdb"" Property=""BorderBrush"" Value=""{StaticResource FgFaint}""/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible=""False"" Content=""{TemplateBinding SelectionBoxItem}""
                              ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""
                              Margin=""8,4,24,4"" VerticalAlignment=""Center"" HorizontalAlignment=""Left""/>
            <Popup x:Name=""PART_Popup"" Placement=""Bottom"" IsOpen=""{TemplateBinding IsDropDownOpen}"" Focusable=""False"" AllowsTransparency=""True"">
              <Border Background=""{StaticResource PanelBg}"" BorderBrush=""{StaticResource Line}"" BorderThickness=""1""
                      MinWidth=""{TemplateBinding ActualWidth}"" MaxHeight=""{TemplateBinding MaxDropDownHeight}"">
                <ScrollViewer>
                  <StackPanel IsItemsHost=""True"" KeyboardNavigation.DirectionalNavigation=""Contained""/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsEnabled"" Value=""False"">
              <Setter Property=""Foreground"" Value=""{StaticResource FgFaint}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Setter Property=""ItemContainerStyle"">
      <Setter.Value>
        <Style TargetType=""ComboBoxItem"">
          <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
          <Setter Property=""Padding"" Value=""8,4""/>
          <Setter Property=""Template"">
            <Setter.Value>
              <ControlTemplate TargetType=""ComboBoxItem"">
                <Border x:Name=""itb"" Background=""Transparent"" Padding=""{TemplateBinding Padding}"">
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property=""IsHighlighted"" Value=""True"">
                    <Setter TargetName=""itb"" Property=""Background"" Value=""{StaticResource Hover}""/>
                  </Trigger>
                  <Trigger Property=""IsSelected"" Value=""True"">
                    <Setter TargetName=""itb"" Property=""Background"" Value=""{StaticResource AccentSoft}""/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 目录树 -->
  <Style TargetType=""TreeView"">
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Padding"" Value=""0""/>
    <Setter Property=""ScrollViewer.HorizontalScrollBarVisibility"" Value=""Disabled""/>
  </Style>

  <Style x:Key=""Chevron"" TargetType=""ToggleButton"">
    <Setter Property=""Focusable"" Value=""False""/>
    <Setter Property=""Width"" Value=""16""/>
    <Setter Property=""Height"" Value=""16""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ToggleButton"">
          <Border Background=""Transparent"">
            <Path x:Name=""arw"" Data=""M 1.5,2 L 5.5,6 L 1.5,10"" Stroke=""{StaticResource FgFaint}"" StrokeThickness=""1.6""
                  StrokeStartLineCap=""Round"" StrokeEndLineCap=""Round""
                  HorizontalAlignment=""Center"" VerticalAlignment=""Center"" RenderTransformOrigin=""0.5,0.5"">
              <Path.RenderTransform>
                <RotateTransform Angle=""0""/>
              </Path.RenderTransform>
            </Path>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsChecked"" Value=""True"">
              <Setter TargetName=""arw"" Property=""Stroke"" Value=""{StaticResource FgSoft}""/>
              <Setter TargetName=""arw"" Property=""RenderTransform"">
                <Setter.Value>
                  <RotateTransform Angle=""90""/>
                </Setter.Value>
              </Setter>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType=""TreeViewItem"">
    <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""Padding"" Value=""0""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""TreeViewItem"">
          <StackPanel>
            <Border x:Name=""bd"" CornerRadius=""6"" Padding=""4,3"" Margin=""0,0,6,1"" Background=""Transparent"">
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width=""18""/>
                  <ColumnDefinition Width=""*""/>
                </Grid.ColumnDefinitions>
                <ToggleButton x:Name=""Exp"" Style=""{StaticResource Chevron}"" ClickMode=""Press""
                              IsChecked=""{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}""/>
                <ContentPresenter Grid.Column=""1"" ContentSource=""Header"" VerticalAlignment=""Center"" HorizontalAlignment=""Stretch"" Margin=""2,0,0,0""/>
              </Grid>
            </Border>
            <ItemsPresenter x:Name=""Host"" Margin=""14,0,0,0""/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsExpanded"" Value=""False"">
              <Setter TargetName=""Host"" Property=""Visibility"" Value=""Collapsed""/>
            </Trigger>
            <Trigger Property=""HasItems"" Value=""False"">
              <Setter TargetName=""Exp"" Property=""Visibility"" Value=""Hidden""/>
            </Trigger>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource Hover}""/>
            </Trigger>
            <Trigger Property=""IsSelected"" Value=""True"">
              <Setter TargetName=""bd"" Property=""Background"" Value=""{StaticResource AccentSoft}""/>
              <Setter Property=""Foreground"" Value=""{StaticResource Accent}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- 细滚动条 -->
  <Style x:Key=""SlimThumb"" TargetType=""Thumb"">
    <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
    <Setter Property=""IsTabStop"" Value=""False""/>
    <Setter Property=""MinHeight"" Value=""32""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""Thumb"">
          <Border x:Name=""tb"" CornerRadius=""3"" Background=""{StaticResource ThumbBg}"" Margin=""3,1,3,1""/>
          <ControlTemplate.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
              <Setter TargetName=""tb"" Property=""Background"" Value=""{StaticResource ThumbHover}""/>
            </Trigger>
            <Trigger Property=""IsDragging"" Value=""True"">
              <Setter TargetName=""tb"" Property=""Background"" Value=""{StaticResource ThumbDrag}""/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType=""ScrollBar"">
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""Width"" Value=""12""/>
    <Setter Property=""Template"">
      <Setter.Value>
        <ControlTemplate TargetType=""ScrollBar"">
          <Grid Background=""Transparent"" Margin=""0,5,0,5"">
            <Track x:Name=""PART_Track"" Orientation=""{TemplateBinding Orientation}"" IsDirectionReversed=""True"">
              <Track.Thumb>
                <Thumb Style=""{StaticResource SlimThumb}""/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageDownCommand"" Opacity=""0"" Focusable=""False""/>
              </Track.IncreaseRepeatButton>
              <Track.DecreaseRepeatButton>
                <RepeatButton Command=""ScrollBar.PageUpCommand"" Opacity=""0"" Focusable=""False""/>
              </Track.DecreaseRepeatButton>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property=""Orientation"" Value=""Horizontal"">
        <Setter Property=""Width"" Value=""Auto""/>
        <Setter Property=""Height"" Value=""12""/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- 右键菜单 -->
  <Style TargetType=""ContextMenu"">
    <Setter Property=""Background"" Value=""{StaticResource PanelBg}""/>
    <Setter Property=""BorderBrush"" Value=""{StaticResource Line}""/>
    <Setter Property=""BorderThickness"" Value=""1""/>
    <Setter Property=""FontSize"" Value=""13""/>
  </Style>
  <Style TargetType=""MenuItem"">
    <Setter Property=""FontSize"" Value=""13""/>
    <Setter Property=""Foreground"" Value=""{StaticResource Fg}""/>
  </Style>

</ResourceDictionary>";
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }
    }
}
