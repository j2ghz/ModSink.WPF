using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reactive;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autofac;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using ModSink.Common.Client;
using ModSink.Core;
using ModSink.WPF.Helpers;
using ReactiveUI;
using Serilog;
using Serilog.Debugging;
using Serilog.Sinks.Sentry;
using Squirrel;

namespace ModSink.WPF
{
    public partial class App : Application
    {
        private ILogger log;

        private static string FullVersion => typeof(App).GetTypeInfo().Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        private IContainer BuildContainer()
        {
            //TODO: FIX:
            ServicePointManager.DefaultConnectionLimit = 10;

            var builder = new ContainerBuilder();

            builder.RegisterType<BinaryFormatter>().As<IFormatter>().SingleInstance();
            builder.Register(_ =>
                    new LocalStorageService(new Uri(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ModSink_Data"))))
                .AsImplementedInterfaces()
                .SingleInstance();
            builder.RegisterAssemblyTypes(typeof(IModSink).Assembly, typeof(Common.ModSink).Assembly)
                .Where(t => t.Name != "LocalStorageService").AsImplementedInterfaces().SingleInstance();

            builder.RegisterAssemblyTypes(typeof(App).Assembly).Where(t => t.Name.EndsWith("Model"))
                .AsImplementedInterfaces().AsSelf().SingleInstance();
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
            Analytics.TrackEvent(nameof(CheckUpdates));
            var updateLog = Log.ForContext<UpdateManager>();
            Task.Factory.StartNew(async () =>
            {
                log.Information("Looking for updates");
                try
                {
                    using (var mgr =
                        await UpdateManager.GitHubUpdateManager("https://github.com/j2ghz/ModSink", prerelease: true))
                    {
                        var release = await mgr.UpdateApp(i =>
                            updateLog.Debug("Downloading file, progress: {progress}", i));
                        log.Information("Latest version: {version}", release.Version);
                    }
                }
                catch (Exception e)
                {
                    updateLog.Error(e, "Exception during update checking");
                }
                finally
                {
                    updateLog.Debug("Update check finished");
                }
            });
        }

        private void FatalException(Exception e, Type source)
        {
            ConsoleManager.Show();
            log.ForContext(source).Fatal(e, "{exceptionText}", e.Demystify().ToString());
            if (Debugger.IsAttached == false)
            {
                Console.WriteLine(WPF.Properties.Resources.FatalExceptionPressAnyKeyToContinue);
                Console.ReadKey();
                Current.Shutdown(1);
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!Debugger.IsAttached)
                ConsoleManager.Show();
            SelfLog.Enable(Console.Error);
            SetupLogging();
            log.Information("Starting ModSink ({version})", FullVersion);
#if !DEBUG
            if (!Debugger.IsAttached)
                CheckUpdates();
#endif

            base.OnStartup(e);

            var container = BuildContainer();

            log.Information("Starting UI");
            var mw = container.Resolve<MainWindow>();
            mw.Show();
        }

        private void SetupLogging()
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.LiterateConsole(
                    outputTemplate:
                    "{Timestamp:HH:mm:ss} {Level:u3} {ThreadId} [{SourceContext}] {Properties} {Message:lj}{NewLine}{Exception}")
                .WriteTo.RollingFile(
                    "../Logs/{Date}.log",
                    outputTemplate:
                    "{Timestamp:o} [{Level:u3}] ({SourceContext}) {Properties} {Message}{NewLine}{Exception}")
                .WriteTo.Sentry(
                    "https://ed0faccfadff441ebe18267965502bf8:85d11ebd26374716b0f40cb3e046269b@sentry.j2ghz.com/2",
                    FullVersion?.Substring(0, 64))
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .Enrich.With<ExceptionEnricher>()
                .MinimumLevel.Verbose()
                .CreateLogger();
            log = Log.ForContext<App>();
            log.Information("Log initialized");
            if (!Debugger.IsAttached)
            {
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    FatalException(args.ExceptionObject as Exception,
                        sender.GetType());
                };
                Current.DispatcherUnhandledException += (sender, args) =>
                {
                    args.Handled = true;
                    FatalException(args.Exception, sender.GetType());
                };

                RxApp.DefaultExceptionHandler = Observer.Create<Exception>(
                    ex =>
                    {
                        if (Debugger.IsAttached) Debugger.Break();
                        log.ForContext(typeof(RxApp))
                            .Error(ex, "An uncaught exception in ReactiveUI (probably binding)");
                        Dispatcher.Invoke(() => throw ex);
                    },
                    ex =>
                    {
                        if (Debugger.IsAttached) Debugger.Break();
                        Dispatcher.Invoke(() => throw new NotImplementedException());
                    },
                    () =>
                    {
                        if (Debugger.IsAttached) Debugger.Break();
                        Dispatcher.Invoke(() => throw new NotImplementedException());
                    }
                );
            }

            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new RelayTraceListener(m =>
            {
                log.ForContext(typeof(PresentationTraceSources)).Warning(m);
            }));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
            AppCenter.Start("5f28a034-bd8f-4f69-9eaa-7e5c228ed328", typeof(Analytics), typeof(Crashes));
            
        }
    }
}