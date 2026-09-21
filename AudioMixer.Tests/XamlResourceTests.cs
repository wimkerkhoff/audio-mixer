using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AudioMixer.Tests;

/// <summary>
/// Static check over the app's markup: every {StaticResource K} must be defined in the same file.
///
/// WPF resolves StaticResource when a template is *applied* during layout, not at compile time, so a
/// clean build proves nothing — a missing key throws XamlParseException mid-Measure and kills the
/// process with no binding error and nothing in the log. That shipped once: two leveler help texts in
/// MainWindow.xaml referenced 'Cap', a style that only exists in the Views/ windows, and the Advanced
/// window crashed on open from ed756fa until it was found live.
///
/// Per-file is the right scope *because* App.xaml carries no resources and every style lives in a
/// Window.Resources — a key defined in another window is unreachable. DefinitionsAreNotShared guards
/// that assumption, so this test starts failing honestly if merged dictionaries are ever introduced.
/// </summary>
public class XamlResourceTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Both markup-extension spellings: {StaticResource Key} and {StaticResource ResourceKey=Key}.
    private static readonly Regex Reference =
        new(@"\{StaticResource\s+(?:ResourceKey\s*=\s*)?([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    public static TheoryData<string> XamlFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in AppXamlFiles()) data.Add(Path.GetRelativePath(RepoRoot(), f));
        return data;
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EveryStaticResourceIsDefinedInTheSameFile(string relativePath)
    {
        string path = Path.Combine(RepoRoot(), relativePath);
        string text = File.ReadAllText(path);
        var doc = XDocument.Parse(text);

        var defined = doc.Descendants()
            .Select(e => e.Attribute(X + "Key")?.Value)
            .Where(k => k != null)
            .ToHashSet()!;

        var missing = Reference.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(k => !defined.Contains(k))
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        Assert.True(missing.Count == 0,
            $"{relativePath} references undefined StaticResource key(s): {string.Join(", ", missing)}. "
            + "A key defined in another window is not in scope and throws XamlParseException during layout.");
    }

    /// <summary>
    /// A StaticResource must be DEFINED BEFORE IT IS USED in the same dictionary — WPF resolves them
    /// in document order, so a style that BasedOn's one declared further down throws exactly like a
    /// missing key. Existence alone is not enough, which this test learned the hard way.
    /// </summary>
    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EveryStaticResourceIsDefinedBeforeItIsUsed(string relativePath)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        var definedAt = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(text, @"x:Key=""([A-Za-z0-9_.]+)"""))
            definedAt.TryAdd(m.Groups[1].Value, m.Index);

        var tooEarly = new List<string>();
        foreach (Match m in Reference.Matches(text))
        {
            var key = m.Groups[1].Value;
            // Only same-dictionary ordering matters, and only for keys this file defines. A forward
            // reference inside a DataTemplate applied later is fine; a BasedOn is not.
            if (!definedAt.TryGetValue(key, out int at)) continue;
            if (m.Index < at && text.LastIndexOf("BasedOn", m.Index, StringComparison.Ordinal)
                > text.LastIndexOf('<', m.Index))
            {
                tooEarly.Add(key);
            }
        }

        Assert.True(tooEarly.Count == 0,
            $"{relativePath} uses {string.Join(", ", tooEarly.Distinct())} in a BasedOn before defining it.");
    }

    /// <summary>The glob must never silently match nothing — a vacuous pass is worse than no test.</summary>
    [Fact]
    public void TheMarkupIsActuallyBeingChecked()
    {
        var names = AppXamlFiles().Select(Path.GetFileName).ToList();

        Assert.Contains("MainWindow.xaml", names);
        Assert.Contains("SimpleWindow.xaml", names);
        Assert.Contains("DiagnosticsWindow.xaml", names);
        Assert.Contains("SettingsWindow.xaml", names);
        Assert.True(names.Count >= 5, $"Only found {names.Count} XAML files — the search is broken.");
    }

    /// <summary>
    /// Pins the scoping assumption this whole test rests on: resources are per-window, App.xaml is
    /// empty, and nothing is pulled in via a merged dictionary.
    /// </summary>
    [Fact]
    public void DefinitionsAreNotShared()
    {
        foreach (var f in AppXamlFiles())
        {
            Assert.DoesNotContain("MergedDictionaries", File.ReadAllText(f));
        }

        var app = XDocument.Load(Path.Combine(RepoRoot(), "AudioMixer", "App.xaml"));
        Assert.Empty(app.Descendants().Where(e => e.Attribute(X + "Key") != null));
    }

    private static IEnumerable<string> AppXamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "AudioMixer"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f);

    // Walk up from the test assembly to the solution, so the test reads live source rather than a
    // copy that can go stale in the output directory.
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AudioMixer.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
