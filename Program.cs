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
using Avalonia;
using ReviewG33k.Services;
using ReviewG33k.Views;

namespace ReviewG33k;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CommandLineReviewOptions.IsCommandLineReview(args))
        {
            return new CommandLineReviewService(
                    MainWindowCompositionRoot.CreateDependencies().ReviewWorkflowService)
                .RunAsync(args)
                .GetAwaiter()
                .GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
