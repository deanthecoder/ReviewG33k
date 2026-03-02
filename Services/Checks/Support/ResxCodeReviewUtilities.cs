// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ReviewG33k.Services.Checks.Support;

internal static class ResxCodeReviewUtilities
{
    public static bool TryGetLocaleMetadata(
        CodeReviewChangedFile file,
        out string baseRelativePath,
        out string baseFullPath,
        out string localeName)
    {
        baseRelativePath = null;
        baseFullPath = null;
        localeName = null;

        if (file == null || !CodeReviewFileClassification.IsAnalyzableResxPath(file.Path))
            return false;

        var relativePath = NormalizePath(file.Path);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(relativePath);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            return false;

        var separatorIndex = fileNameWithoutExtension.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= fileNameWithoutExtension.Length - 1)
            return false;

        var cultureToken = fileNameWithoutExtension[(separatorIndex + 1)..];
        if (!LooksLikeCultureName(cultureToken))
            return false;

        localeName = cultureToken;
        var baseName = fileNameWithoutExtension[..separatorIndex] + ".resx";
        var relativeDirectory = Path.GetDirectoryName(relativePath);
        baseRelativePath = NormalizePath(string.IsNullOrWhiteSpace(relativeDirectory)
            ? baseName
            : Path.Combine(relativeDirectory, baseName));

        var fullPath = file.FullPath;
        var fullDirectory = string.IsNullOrWhiteSpace(fullPath) ? null : Path.GetDirectoryName(fullPath);
        baseFullPath = string.IsNullOrWhiteSpace(fullDirectory)
            ? baseName
            : Path.Combine(fullDirectory, baseName);
        return true;
    }

    public static bool TryGetResxEntries(CodeReviewChangedFile file, out IReadOnlyDictionary<string, ResxEntry> entries)
    {
        entries = null;
        if (file == null || string.IsNullOrWhiteSpace(file.Text))
            return false;

        return TryParseResxEntries(file.Text, out entries);
    }

    public static bool TryGetBaseResxEntries(
        CodeReviewAnalysisContext context,
        string baseRelativePath,
        string baseFullPath,
        out IReadOnlyDictionary<string, ResxEntry> entries)
    {
        entries = null;
        if (string.IsNullOrWhiteSpace(baseRelativePath))
            return false;

        var normalizedBasePath = NormalizePath(baseRelativePath);
        var changedBase = context?.ResxFiles?
            .FirstOrDefault(file => string.Equals(NormalizePath(file.Path), normalizedBasePath, StringComparison.OrdinalIgnoreCase));
        if (changedBase != null)
            return TryGetResxEntries(changedBase, out entries);

        if (string.IsNullOrWhiteSpace(baseFullPath) || !File.Exists(baseFullPath))
            return false;

        string baseText;
        try
        {
            baseText = File.ReadAllText(baseFullPath);
        }
        catch
        {
            return false;
        }

        return TryParseResxEntries(baseText, out entries);
    }

    public static string NormalizePath(string path) =>
        (path ?? string.Empty).Replace('\\', '/');

    private static bool TryParseResxEntries(string text, out IReadOnlyDictionary<string, ResxEntry> entries)
    {
        entries = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(text),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });

            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            var parsedEntries = new Dictionary<string, ResxEntry>(StringComparer.Ordinal);
            foreach (var dataElement in document.Descendants().Where(element => element.Name.LocalName == "data"))
            {
                var name = dataElement.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var valueElement = dataElement.Elements().FirstOrDefault(element => element.Name.LocalName == "value");
                var value = valueElement?.Value ?? string.Empty;
                var lineNumber = GetLineNumber(valueElement) ?? GetLineNumber(dataElement) ?? 1;
                parsedEntries[name] = new ResxEntry(name, value, lineNumber);
            }

            entries = parsedEntries;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeCultureName(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            _ = CultureInfo.GetCultureInfo(token);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static int? GetLineNumber(XElement element)
    {
        if (element is not IXmlLineInfo lineInfo || !lineInfo.HasLineInfo())
            return null;

        return lineInfo.LineNumber > 0 ? lineInfo.LineNumber : null;
    }
}

internal sealed record ResxEntry(string Key, string Value, int LineNumber);
