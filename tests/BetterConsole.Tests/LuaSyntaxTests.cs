using MoonSharp.Interpreter;
using Xunit;

namespace BetterConsole.Tests;

/// <summary>
/// Every Lua file of the companion addon and the samples must at least compile: a syntax error
/// would stop the addon from loading on every server. MoonSharp only parses here, nothing runs.
/// </summary>
public class LuaSyntaxTests
{
    public static IEnumerable<object[]> LuaFiles()
    {
        var root = FindRepoRoot();
        foreach (var dir in new[] { "addon", Path.Combine("samples", "lua") })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(root, dir), "*.lua", SearchOption.AllDirectories))
                yield return [Path.GetRelativePath(root, f)];
    }

    [Theory]
    [MemberData(nameof(LuaFiles))]
    public void Compiles(string relativePath)
    {
        var code = File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath));
        var script = new Script(CoreModules.None);
        var ex = Record.Exception(() => script.LoadString(code, null, relativePath));
        Assert.True(ex == null, $"{relativePath}: {(ex as InterpreterException)?.DecoratedMessage ?? ex?.Message}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BetterConsole.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
