// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System.Reflection;
using ReviewG33k.Services.Checks;

namespace ReviewG33k.Tests;

[TestFixture]
public sealed class CodeReviewFileClassificationTests
{
    [Test]
    public void IsAnalyzableChangedCSharpPathWhenPathIsAxamlCodeBehindReturnsTrue()
    {
        var result = InvokeIsAnalyzableChangedCSharpPath("Views/ReviewResultsWindow.axaml.cs");

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsAnalyzableChangedCSharpPathWhenPathIsXamlCodeBehindReturnsTrue()
    {
        var result = InvokeIsAnalyzableChangedCSharpPath("Views/MainWindow.xaml.cs");

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsAnalyzableChangedCSharpPathWhenPathIsPlainCSharpReturnsTrue()
    {
        var result = InvokeIsAnalyzableChangedCSharpPath("Services/Worker.cs");

        Assert.That(result, Is.True);
    }

    [Test]
    public void IsAnalyzableChangedCSharpPathWhenPathIsMarkupReturnsTrue()
    {
        var axamlResult = InvokeIsAnalyzableChangedCSharpPath("Views/ReviewResultsWindow.axaml");
        var xamlResult = InvokeIsAnalyzableChangedCSharpPath("Views/MainWindow.xaml");

        Assert.Multiple(() =>
        {
            Assert.That(axamlResult, Is.True);
            Assert.That(xamlResult, Is.True);
        });
    }

    [Test]
    public void IsCodeBehindFilePathWhenPathIsAxamlOrXamlCodeBehindReturnsTrue()
    {
        var axamlResult = InvokeIsCodeBehindFilePath("Views/ReviewResultsWindow.axaml.cs");
        var xamlResult = InvokeIsCodeBehindFilePath("Views/MainWindow.xaml.cs");

        Assert.Multiple(() =>
        {
            Assert.That(axamlResult, Is.True);
            Assert.That(xamlResult, Is.True);
        });
    }

    [Test]
    public void IsMarkupFilePathWhenPathIsAxamlOrXamlReturnsTrue()
    {
        var axamlResult = InvokeIsMarkupFilePath("Views/ReviewResultsWindow.axaml");
        var xamlResult = InvokeIsMarkupFilePath("Views/MainWindow.xaml");

        Assert.Multiple(() =>
        {
            Assert.That(axamlResult, Is.True);
            Assert.That(xamlResult, Is.True);
        });
    }

    private static bool InvokeIsAnalyzableChangedCSharpPath(string path)
    {
        var method = ResolveClassificationMethod(
            "IsAnalyzableChangedCSharpPath",
            BindingFlags.Public | BindingFlags.Static);

        Assert.That(method, Is.Not.Null, "Could not find IsAnalyzableChangedCSharpPath via reflection.");

        return (bool)method.Invoke(null, [path]);
    }

    private static bool InvokeIsCodeBehindFilePath(string path)
    {
        var method = ResolveClassificationMethod(
            "IsCodeBehindFilePath",
            BindingFlags.Public | BindingFlags.Static);

        Assert.That(method, Is.Not.Null, "Could not find IsCodeBehindFilePath via reflection.");

        return (bool)method.Invoke(null, [path]);
    }

    private static bool InvokeIsMarkupFilePath(string path)
    {
        var method = ResolveClassificationMethod(
            "IsMarkupFilePath",
            BindingFlags.Public | BindingFlags.Static);

        Assert.That(method, Is.Not.Null, "Could not find IsMarkupFilePath via reflection.");

        return (bool)method.Invoke(null, [path]);
    }

    private static MethodInfo ResolveClassificationMethod(string methodName, BindingFlags bindingFlags)
    {
        var classificationType = typeof(MissingUnitTestsCodeReviewCheck).Assembly.GetType(
            "ReviewG33k.Services.Checks.CodeReviewFileClassification",
            throwOnError: true);
        return classificationType.GetMethod(methodName, bindingFlags);
    }
}
