using System;
using System.IO;
using System.Management.Automation.Language;
using Xunit;

namespace Auth.UnitTests;

public class PowerShellScriptSyntaxTests
{
    private static readonly string[] ScriptRelativePaths =
    [
        "script/local-bootstrap/bootstrap_local_2027.ps1",
        "script/local-bootstrap/adopt_2027_clone.ps1",
        "script/local-bootstrap/cleanup_azure_sync_tables_local_2027.ps1",
        "script/local-bootstrap/verify_bootstrap_integrity.ps1",
        "script/local-bootstrap/smoke_test_local_2026.ps1"
    ];

    [Theory]
    [InlineData("script/local-bootstrap/bootstrap_local_2027.ps1")]
    [InlineData("script/local-bootstrap/adopt_2027_clone.ps1")]
    [InlineData("script/local-bootstrap/cleanup_azure_sync_tables_local_2027.ps1")]
    [InlineData("script/local-bootstrap/verify_bootstrap_integrity.ps1")]
    [InlineData("script/local-bootstrap/smoke_test_local_2026.ps1")]
    public void BootstrapScript_MustHaveCleanPowerShellSyntax_WithoutErrors(string relativePath)
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        Assert.NotNull(repoRoot);

        var fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Script file not found at: {fullPath}");

        var scriptContent = File.ReadAllText(fullPath);
        Token[] tokens;
        ParseError[] errors;
        var ast = Parser.ParseInput(scriptContent, out tokens, out errors);

        Assert.NotNull(ast);
        Assert.True(errors.Length == 0,
            $"Syntax error(s) in {relativePath}:\n" +
            string.Join("\n", Array.ConvertAll(errors, e => $"  Line {e.Extent.StartLineNumber}: {e.Message}")));
        Assert.NotEmpty(tokens);
    }

    private static string? FindRepoRoot(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "IProgram.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}
