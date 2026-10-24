using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private static ControlTemplate CreateCoCreationSelectorTemplate()
    {
        // DefaultToggleButtonStyle uses ObjectAnimation storyboards plus an 83ms
        // BrushTransition on Background. Rapid pointer changes interrupt/restart it.
        // The segmented selector needs stable fills; its native states use setters.
        var states = string.Join("", new[] { "Normal", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed" }
            .Select(state =>
            {
                var suffix = state == "Normal" ? "" : state;
                return $"<VisualState x:Name=\"{state}\"><VisualState.Setters>"
                    + $"<Setter Target=\"ContentPresenter.Background\" Value=\"{{ThemeResource ToggleButtonBackground{suffix}}}\"/>"
                    + $"<Setter Target=\"ContentPresenter.Foreground\" Value=\"{{ThemeResource ToggleButtonForeground{suffix}}}\"/>"
                    + "</VisualState.Setters></VisualState>";
            }));
        return (ControlTemplate)XamlReader.Load($$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
                <ContentPresenter x:Name="ContentPresenter" Content="{TemplateBinding Content}"
                    Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}"
                    CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}"
                    HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                    VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}">
                    <VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="CommonStates">{{states}}</VisualStateGroup></VisualStateManager.VisualStateGroups>
                </ContentPresenter>
            </ControlTemplate>
            """);
    }
}
