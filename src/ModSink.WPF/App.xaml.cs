using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autofac;
using AutofacSerilogIntegration;
using ModSink.Common.Client;
using ModSink.Core;
using ModSink.WPF.Helpers;
using Serilog;
using Serilog.Debugging;
using Serilog.Sinks.Sentry;
using SharpRaven;
using SharpRaven.Data;
using Squirrel;

namespace ModSink.WPF
{
    public partial class App : Application
    {
        private ILogger log;

        private string FullVersion => typeof(App).GetTypeInfo().Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        private event EventHandler<Exception> UpdateFailed;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!Debugger.IsAttached)
                ConsoleManager.Show();
            SelfLog.Enable(Console.Error);
            SetupLogging();
            log = Log.ForContext<App>();
            log.Information("Starting ModSink ({version})", FullVersion);
#if !DEBUG
            if (!Debugger.IsAttached)
                CheckUpdates();
#endif

            base.OnStartup(e);

            var container = BuildContainer();

            log.Information("Starting UI");
            var mw = container.Resolve<MainWindow>();
            mw.ShowDialog();
        }

        private IContainer BuildContainer()
        {
            var builder = new ContainerBuilder();

            builder.RegisterLogger();

            builder.RegisterType<BinaryFormatter>().As<IFormatter>().SingleInstance();
            builder.Register(_ => new LocalStorageService(new Uri(@"D:\modsink"))).AsImplementedInterfaces()
                .SingleInstance();
            builder.RegisterAssemblyTypes(typeof(IModSink).Assembly, typeof(Common.ModSink).Assembly)
                .Where(t => t.Name != "LocalStorageService").AsImplementedInterfaces().SingleInstance();

            builder.RegisterAssemblyTypes(typeof(App).Assembly).Where(t => t.Name.EndsWith("ViewModel"))
                .AsImplementedInterfaces().AsSelf().SingleInstance();
            builder.RegisterAssemblyTypes(typeof(App).Assembly).Where(t => t.IsAssignableTo<TabItem>()).AsSelf()
                .As<TabItem>().SingleInstance();
            builder.RegisterType<MainWindow>().AsSelf().SingleInstance();

            //TODO: Load plugins, waiting on https://stackoverflow.com/questions/46351411

            return builder.Build();
        }

        private void CheckUpdates()
        {
            var updateLog = Log.ForContext<UpdateManager>();
            Task.Factory.StartNew(async () =>
            {
                log.Information("Looking for updates");
                try
                {
                    using (var mgr =
                        await UpdateManager.GitHubUpdateManager("https://github.com/j2ghz/ModSink", prerelease: true))
                    {
                        var release = await mgr.UpdateApp(i => updateLog.Debug("Download progress: {progress}", i));
                        log.Debug("Latest version: {version}", release.Version);
                    }
                }
                catch (Exception e)
                {
                    updateLog.Error(e, "Exception during update checking");
                    UpdateFailed?.Invoke(null, e);
                }
                finally
                {
                    updateLog.Debug("Update check finished");
                }
            });
        }

        private void SetupLogging()
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.LiterateConsole(
                    outputTemplate:
                    "{Timestamp:HH:mm:ss} {Level:u3} [{SourceContext}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.RollingFile(
                    "../Logs/{Date}.log",
                    outputTemplate: "{Timestamp:o} [{Level:u3}] ({SourceContext}) {Message}{NewLine}{Exception}")
                .WriteTo.Sentry(
                    "https://6e3a1e08759944bb932434095137f63b:ab9ff9be2ec74518bcb0c1d860d98cbe@sentry.j2ghz.com/2")
                .Enrich.FromLogContext()
                .Enrich.WithProcessId()
                .Enrich.WithProcessName()
                .Enrich.WithThreadId()
                .MinimumLevel.Verbose()
                .CreateLogger();
            Log.Information("Log initialized");
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                ConsoleManager.Show();
                Log.Fatal(args.ExceptionObject as Exception, nameof(AppDomain.CurrentDomain.UnhandledException));
            };
            Current.DispatcherUnhandledException += (sender, args) =>
            {
                ConsoleManager.Show();
                Log.Fatal(args.Exception, nameof(DispatcherUnhandledException));
            };
        }

        [Obsolete]
        private void SetupSentry()
        {
            log.Information("Setting up exception reporting");
            var ravenClient =
                new RavenClient(
                    "https://6e3a1e08759944bb932434095137f63b:ab9ff9be2ec74518bcb0c1d860d98cbe@sentry.j2ghz.com/2");
            ravenClient.Release = FullVersion?.Split('+').First();
            ravenClient.ErrorOnCapture = exception =>
            {
                Log.ForContext<RavenClient>().Error(exception, "Sentry error reporting encountered an exception");
            };
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                ravenClient.Capture(new SentryEvent(args.ExceptionObject as Exception));
            };
            UpdateFailed += (sender, e) => { ravenClient.Capture(new SentryEvent(e)); };
        }
    }
}