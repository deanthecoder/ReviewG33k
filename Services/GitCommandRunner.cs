using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace ReviewG33k.Services;

public sealed class GitCommandRunner
{
    public async Task<GitCommandResult> RunAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        outputBuilder.Append(await outputTask);
        errorBuilder.Append(await errorTask);

        return new GitCommandResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString(), string.Join(" ", arguments));
    }
}

public sealed class GitCommandResult
{
    public GitCommandResult(int exitCode, string standardOutput, string standardError, string commandText)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? string.Empty;
        StandardError = standardError ?? string.Empty;
        CommandText = commandText ?? string.Empty;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public string CommandText { get; }

    public bool IsSuccess => ExitCode == 0;

    public string GetCombinedOutput()
    {
        if (string.IsNullOrWhiteSpace(StandardError))
            return StandardOutput.Trim();

        if (string.IsNullOrWhiteSpace(StandardOutput))
            return StandardError.Trim();

        return $"{StandardOutput.Trim()}{Environment.NewLine}{StandardError.Trim()}";
    }
}
