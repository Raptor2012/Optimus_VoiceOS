namespace Optimus.Architecture.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

public class DotNetDependencyRuleTests
{
    private static readonly HashSet<string> ApprovedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.runner.visualstudio",
        "Microsoft.Extensions.Hosting"
    };

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Optimus.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Repository root containing Optimus.sln was not found walking up from '{AppContext.BaseDirectory}'.");
    }

    private static Dictionary<string, HashSet<string>> GetProductProjectReferences()
    {
        var root = GetRepositoryRoot();
        var srcDir = Path.Combine(root, "src");
        var csprojFiles = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories);
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in csprojFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(file);
            var doc = XDocument.Load(file);
            var refs = doc.Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(inc => !string.IsNullOrWhiteSpace(inc))
                .Select(inc => Path.GetFileNameWithoutExtension(inc!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            map[projectName] = refs;
        }

        return map;
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Exact name mandated by task contract")]
    public void Contracts_HasNoProjectReferences()
    {
        var refs = GetProductProjectReferences();
        Assert.True(refs.ContainsKey("Optimus.Contracts"), "Project 'Optimus.Contracts' was not found in src/.");

        var contractRefs = refs["Optimus.Contracts"];
        Assert.True(
            contractRefs.Count == 0,
            $"Rule 'Contracts_HasNoProjectReferences' violated: 'Optimus.Contracts' has project references: [{string.Join(", ", contractRefs)}].");
    }

    [Fact]
    public void NothingReferencesShellOrService()
    {
        var refs = GetProductProjectReferences();
        var forbidden = new[] { "Optimus.Shell", "Optimus.Service" };
        var violations = new List<string>();

        foreach (var (project, projectRefs) in refs)
        {
            foreach (var target in forbidden)
            {
                if (projectRefs.Contains(target))
                {
                    violations.Add($"Project '{project}' references forbidden target '{target}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Rule 'NothingReferencesShellOrService' violated: {string.Join("; ", violations)}.");
    }

    [Fact]
    public void InferenceAndProvidersAreIndependentOfEachOtherAndOfCore()
    {
        var refs = GetProductProjectReferences();
        var violations = new List<string>();

        if (refs.TryGetValue("Optimus.Inference", out var infRefs))
        {
            if (infRefs.Contains("Optimus.Providers"))
            {
                violations.Add("Project 'Optimus.Inference' references 'Optimus.Providers'");
            }
            if (infRefs.Contains("Optimus.Core"))
            {
                violations.Add("Project 'Optimus.Inference' references 'Optimus.Core'");
            }
        }
        else
        {
            violations.Add("Project 'Optimus.Inference' was not found");
        }

        if (refs.TryGetValue("Optimus.Providers", out var provRefs))
        {
            if (provRefs.Contains("Optimus.Inference"))
            {
                violations.Add("Project 'Optimus.Providers' references 'Optimus.Inference'");
            }
            if (provRefs.Contains("Optimus.Core"))
            {
                violations.Add("Project 'Optimus.Providers' references 'Optimus.Core'");
            }
        }
        else
        {
            violations.Add("Project 'Optimus.Providers' was not found");
        }

        Assert.True(
            violations.Count == 0,
            $"Rule 'InferenceAndProvidersAreIndependentOfEachOtherAndOfCore' violated: {string.Join("; ", violations)}.");
    }

    [Fact]
    public void ShellDoesNotReferenceCoreInferenceOrProviders()
    {
        var refs = GetProductProjectReferences();
        Assert.True(refs.ContainsKey("Optimus.Shell"), "Project 'Optimus.Shell' was not found in src/.");

        var shellRefs = refs["Optimus.Shell"];
        var forbidden = new[] { "Optimus.Core", "Optimus.Inference", "Optimus.Providers" };
        var violations = forbidden.Where(f => shellRefs.Contains(f)).ToList();

        Assert.True(
            violations.Count == 0,
            $"Rule 'ShellDoesNotReferenceCoreInferenceOrProviders' violated: Project 'Optimus.Shell' references [{string.Join(", ", violations)}].");
    }

    [Fact]
    public void ClientReferencesOnlyContracts()
    {
        var refs = GetProductProjectReferences();
        Assert.True(refs.ContainsKey("Optimus.Client"), "Project 'Optimus.Client' was not found in src/.");

        var clientRefs = refs["Optimus.Client"];
        var nonContractRefs = clientRefs.Where(r => !string.Equals(r, "Optimus.Contracts", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(
            nonContractRefs.Count == 0,
            $"Rule 'ClientReferencesOnlyContracts' violated: Project 'Optimus.Client' references forbidden projects: [{string.Join(", ", nonContractRefs)}].");
    }

    [Fact]
    public void AllProductProjectsAreInTheSolution()
    {
        var root = GetRepositoryRoot();
        var slnPath = Path.Combine(root, "Optimus.sln");
        var slnContent = File.ReadAllText(slnPath);

        var srcCsproj = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        var testsCsproj = Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories);
        var allProjects = srcCsproj.Concat(testsCsproj).ToList();

        var missing = new List<string>();
        foreach (var projectPath in allProjects)
        {
            var fileName = Path.GetFileName(projectPath);
            if (!slnContent.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(Path.GetFileNameWithoutExtension(projectPath));
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Rule 'AllProductProjectsAreInTheSolution' violated: Projects missing from Optimus.sln: [{string.Join(", ", missing)}].");
    }

    [Fact]
    public void NoProjectDeclaresAnInlinePackageVersion()
    {
        var root = GetRepositoryRoot();
        var srcCsproj = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        var testsCsproj = Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories);
        var allProjects = srcCsproj.Concat(testsCsproj).ToList();

        var violations = new List<string>();
        foreach (var projectPath in allProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var doc = XDocument.Load(projectPath);
            var pkgRefs = doc.Descendants("PackageReference");
            foreach (var pr in pkgRefs)
            {
                var pkgName = (string?)pr.Attribute("Include") ?? "Unknown";
                if (pr.Attribute("Version") != null || pr.Element("Version") != null)
                {
                    violations.Add($"Project '{projectName}' specifies inline version on package '{pkgName}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Rule 'NoProjectDeclaresAnInlinePackageVersion' violated: {string.Join("; ", violations)}.");
    }

    [Fact]
    public void OnlyApprovedPackagesAreDeclared()
    {
        var root = GetRepositoryRoot();
        var packagesProps = Path.Combine(root, "Directory.Packages.props");
        Assert.True(File.Exists(packagesProps), "Directory.Packages.props was not found in repository root.");

        var doc = XDocument.Load(packagesProps);
        var declaredPackages = doc.Descendants("PackageVersion")
            .Select(pv => (string?)pv.Attribute("Include"))
            .Where(inc => !string.IsNullOrWhiteSpace(inc))
            .Select(inc => inc!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unapproved = declaredPackages.Except(ApprovedPackages, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = ApprovedPackages.Except(declaredPackages, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(
            unapproved.Count == 0 && missing.Count == 0,
            $"Rule 'OnlyApprovedPackagesAreDeclared' violated in Directory.Packages.props: Unapproved packages: [{string.Join(", ", unapproved)}], Missing required packages: [{string.Join(", ", missing)}].");
    }
}
