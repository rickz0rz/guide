using Guide.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder
        .AddFilter("Microsoft", LogLevel.Warning)
        .AddFilter("System", LogLevel.Warning)
        .AddFilter("LoggingConsoleApp.Program", LogLevel.Debug)
        .AddConsole()
        .Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ContainedLineLoggerProvider>());
});

var logger = loggerFactory.CreateLogger<Program>();

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception exception)
    {
        logger.LogCritical("Unhandled exception encountered: {message} @ {stackTrace}",
            exception.Message, exception.StackTrace);
    }
    else
    {
        logger.LogCritical("Unhandled exception encountered: {exceptionObject}", eventArgs.ExceptionObject);
    }
};

logger.LogInformation("Current process ID: {processId}", System.Diagnostics.Process.GetCurrentProcess().Id);

Guide.NativeLibraryResolver.Register();

var options = Guide.RunnerOptions.Parse(args);

// The fonts and images load from relative paths, so run from the folder of the build output.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

using var guide = new Guide.ScrollingGuideRunner(logger, options);
guide.Run();
