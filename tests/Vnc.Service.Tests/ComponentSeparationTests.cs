using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace Vnc.Service.Tests;

public sealed class ComponentSeparationTests
{
    [Fact]
    public void ManagerProjectDoesNotReferenceServerComponents()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Vnc.App", "Vnc.App.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value is not null)
            .ToArray();

        Assert.DoesNotContain(references, value =>
            value!.Contains("Vnc.Service", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Vnc.Agent", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Vnc.Server", StringComparison.OrdinalIgnoreCase));

        var publishedDeps = Path.Combine(
            root,
            "artifacts",
            "publish",
            "manager",
            "LucyVNC.Manager.deps.json");
        if (File.Exists(publishedDeps))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(publishedDeps));
            var libraries = document.RootElement.GetProperty("targets")
                .EnumerateObject()
                .SelectMany(target => target.Value.EnumerateObject())
                .Select(library => library.Name);
            Assert.DoesNotContain(libraries, name =>
                name.StartsWith("Vnc.Service/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Vnc.Agent/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Vnc.Server/", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ServerConfigurationProjectDoesNotReferenceTheManager()
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "Vnc.Tray", "Vnc.Tray.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value is not null)
            .ToArray();

        Assert.DoesNotContain(references, value =>
            value!.Contains("Vnc.App", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            project.Descendants("AssemblyName").Select(element => element.Value),
            value => value == "LucyVNC.Server");
    }

    [Fact]
    public void SetupFilesAreIsolatedBySelectedProductComponent()
    {
        var root = FindRepositoryRoot();
        var setup = File.ReadAllText(Path.Combine(root, "installer", "LucyVNC.iss"));
        var fileEntries = setup.Split(Environment.NewLine)
            .Where(line => line.StartsWith("Source:", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains(fileEntries, line =>
            line.Contains("publish\\manager\\*", StringComparison.Ordinal) &&
            line.Contains("Components: manager", StringComparison.Ordinal));
        Assert.Contains(fileEntries, line =>
            (line.Contains("publish\\server-worker\\*", StringComparison.Ordinal) ||
             line.Contains("publish\\agent\\*", StringComparison.Ordinal) ||
             line.Contains("publish\\server-ui\\*", StringComparison.Ordinal) ||
             line.Contains("scripts\\*.ps1", StringComparison.Ordinal)) &&
            line.Contains("Components: server", StringComparison.Ordinal));
        Assert.All(fileEntries, line =>
            Assert.True(line.Contains("Components: manager", StringComparison.Ordinal) ||
                line.Contains("Components: server", StringComparison.Ordinal)));
        Assert.Contains("WizardIsComponentSelected('server')", setup, StringComparison.Ordinal);
        Assert.Contains("not InstallServiceCheck.Checked", setup, StringComparison.Ordinal);
        Assert.Contains("RemoveAgentStartupRegistryValues", setup, StringComparison.Ordinal);
        Assert.Contains("DelTree(ExpandConstant('{app}\\Server')", setup, StringComparison.Ordinal);
        Assert.Contains("Name: \"server\"; Description: \"LucyVNC Server\"", setup, StringComparison.Ordinal);
        Assert.Contains("Name: \"manager\"; Description: \"LucyVNC Manager / Viewer\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("Types: manager both; Flags: fixed", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("Types: server both; Flags: fixed", setup, StringComparison.Ordinal);
        Assert.Contains("PasswordEnvironmentVariable", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("LucyVNC.Server.Worker.exe\"; DestDir: \"{app}\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain(Regex.Matches(setup, @"(?m)^Filename: .*LucyVNC\.Server\.exe.*")
            .Select(match => match.Value), line => line.Contains("Components: manager", StringComparison.Ordinal));
    }

    [Fact]
    public void SetupDefaultsToWildcardButRequiresConfirmationAndUsesProvisionerValidation()
    {
        var root = FindRepositoryRoot();
        var setup = File.ReadAllText(Path.Combine(root, "installer", "LucyVNC.iss"));

        Assert.Contains("ServerConfigurationPage.Values[0] := '0.0.0.0';", setup, StringComparison.Ordinal);
        Assert.Contains("NetworkModeCombo.ItemIndex := 2;", setup, StringComparison.Ordinal);
        Assert.Contains("AllInterfacesCheck.Checked := False;", setup, StringComparison.Ordinal);
        Assert.Contains("function GetEffectiveBindAddress", setup, StringComparison.Ordinal);
        Assert.Contains("AddQuotes(GetEffectiveBindAddress)", setup, StringComparison.Ordinal);
        Assert.Contains("WildcardWarningConfirmed", setup, StringComparison.Ordinal);
        Assert.Contains("RunServerSetupCommand('--validate-setup')", setup, StringComparison.Ordinal);
        Assert.Contains("A autenticação VNC clássica não criptografa o tráfego. Utilize redes confiáveis ou VPN e não exponha a porta diretamente à internet.", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerConfigurationPage.Values[0] := '127.0.0.1';", setup, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LucyVNC.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the LucyVNC repository root.");
    }
}
