using System.Buffers.Binary;
using System.Xml.Linq;

namespace Vnc.Infrastructure.Tests;

public sealed class UiVisualAssetTests
{
    private static readonly string AssetsDirectory = Path.Combine(AppContext.BaseDirectory, "UiAssets");
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void SharedThemeDefinesRequiredPaletteAndCustomControlStates()
    {
        var theme = XDocument.Load(Path.Combine(AssetsDirectory, "LucyTheme.xaml"));
        var colors = theme.Root!
            .Elements(Presentation + "SolidColorBrush")
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => (string)element.Attribute("Color")!,
                StringComparer.Ordinal);

        Assert.Equal("#100D18", colors["Canvas"]);
        Assert.Equal("#191323", colors["Surface"]);
        Assert.Equal("#21192F", colors["SurfaceElevated"]);
        Assert.Equal("#8B35E8", colors["Primary"]);
        Assert.Equal("#A855F7", colors["PrimaryHover"]);
        Assert.Equal("#C084FC", colors["Accent"]);
        Assert.Equal("#F5F3FF", colors["TextPrimary"]);
        Assert.Equal("#A9A0BC", colors["TextSecondary"]);
        Assert.Equal("#382B4B", colors["Line"]);
        Assert.Equal("#4ADE80", colors["Success"]);
        Assert.Equal("#FB7185", colors["Error"]);

        var customTemplates = theme.Root.Elements(Presentation + "Style")
            .SelectMany(style => style.Descendants(Presentation + "ControlTemplate"))
            .Select(template => (string?)template.Attribute("TargetType"))
            .ToArray();
        Assert.Contains("Button", customTemplates);
        Assert.Contains("ComboBox", customTemplates);
        Assert.Contains("TabControl", customTemplates);
        Assert.Contains("TabItem", customTemplates);
        Assert.Contains("ScrollBar", customTemplates);
    }

    [Fact]
    public void ManagerAndWorkspaceUseTheSharedThemeAndOfficialApplicationIcon()
    {
        var manager = File.ReadAllText(Path.Combine(AssetsDirectory, "MainWindow.xaml"));
        var workspace = File.ReadAllText(Path.Combine(AssetsDirectory, "RemoteWorkspaceWindow.xaml"));

        Assert.Contains("Icon=\"Assets/LucyVNC.ico\"", manager, StringComparison.Ordinal);
        Assert.Contains("Icon=\"Assets/LucyVNC.ico\"", workspace, StringComparison.Ordinal);
        Assert.Contains("MouseDoubleClick=\"ConnectSelected_Click\"", manager, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"True\"", workspace, StringComparison.Ordinal);
        Assert.Contains("SessionTabs", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialIconContainsPngArtworkAndSevenRequestedIcoSizes()
    {
        var png = File.ReadAllBytes(Path.Combine(AssetsDirectory, "LucyVNC-Icon.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(768, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(768, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));

        var icon = File.ReadAllBytes(Path.Combine(AssetsDirectory, "LucyVNC.ico"));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(0, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(2, 2)));
        var imageCount = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4, 2));
        Assert.Equal((ushort)7, imageCount);

        var dimensions = Enumerable.Range(0, imageCount)
            .Select(index =>
            {
                var width = icon[6 + index * 16];
                return width == 0 ? 256 : width;
            })
            .ToArray();
        Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, dimensions);

        for (var index = 0; index < imageCount; index++)
        {
            var entry = icon.AsSpan(6 + index * 16, 16);
            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Assert.True(dataLength >= 8);
            Assert.True(dataOffset + dataLength <= icon.Length);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, icon.AsSpan((int)dataOffset, 8).ToArray());
        }
    }
}
