using System.Globalization;
using SerialLog.App.Converters;

namespace SerialLog.Tests;

public class WorkspaceScaleConverterTests
{
    [Fact]
    public void Convert_returns_one_when_window_is_at_least_design_size()
    {
        var converter = new WorkspaceScaleConverter();

        var scale = converter.Convert([2048d, 1120d], typeof(double), null!, CultureInfo.InvariantCulture);

        Assert.Equal(1d, Assert.IsType<double>(scale));
    }

    [Fact]
    public void Convert_scales_down_by_smaller_axis()
    {
        var converter = new WorkspaceScaleConverter();

        var scale = converter.Convert([1600d, 700d], typeof(double), null!, CultureInfo.InvariantCulture);

        Assert.Equal(0.66, Assert.IsType<double>(scale), precision: 2);
    }

    [Fact]
    public void Convert_height_expands_logical_workspace_to_fill_available_height()
    {
        var converter = new WorkspaceScaleConverter();

        var height = converter.Convert([1875d, 1185d], typeof(double), "Height", CultureInfo.InvariantCulture);

        Assert.Equal(1219.8, Assert.IsType<double>(height), precision: 1);
    }

    [Fact]
    public void Convert_width_uses_available_width_when_scale_is_one()
    {
        var converter = new WorkspaceScaleConverter();

        var width = converter.Convert([2048d, 1120d], typeof(double), "Width", CultureInfo.InvariantCulture);

        Assert.Equal(2048d, Assert.IsType<double>(width));
    }

    [Fact]
    public void Convert_width_expands_logical_workspace_to_fill_scaled_viewport()
    {
        var converter = new WorkspaceScaleConverter();

        var width = converter.Convert([1600d, 700d], typeof(double), "Width", CultureInfo.InvariantCulture);

        Assert.Equal(2422.9, Assert.IsType<double>(width), precision: 1);
    }
}
