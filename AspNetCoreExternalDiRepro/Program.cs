using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autofac;
using Autofac.Core;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace throwawayx
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Figure out where webroot and contentRoot are for HostingEnvironment
            string webRoot;
            string contentRoot;
            var testPath = Directory.GetCurrentDirectory();
            while (true)
            {
                if (Directory.Exists(Path.Combine(testPath, "wwwroot")))
                {
                    contentRoot = testPath;
                    webRoot = Path.Combine(testPath, "wwwroot");
                    break;
                }
                else
                {
                    var parent = Directory.GetParent(testPath);
                    if (parent == null) throw new InvalidOperationException("Unable to discover WebRoot");
                    testPath = parent.FullName;
                }
            }

            contentRoot = Directory.GetCurrentDirectory();
            
            // Create a hosting environment
            var env = new HostingEnvironment
            {
                ContentRootPath = contentRoot,
                ApplicationName = "throwawayx",
                EnvironmentName = "Development",
                WebRootPath = webRoot,
                ContentRootFileProvider = new PhysicalFileProvider(contentRoot),
                WebRootFileProvider = new PhysicalFileProvider(webRoot),
            };

            // Build configuration
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();
            var configuration = builder.Build();

            // Now imagine the lifetime exists outside of ASP.NET Core.  In other words,
            // pretend that we're really inside of a nested context w/other things running
            // adjacent to ASP.NET Core.
            var cb = new ContainerBuilder();

            // Just make logging work w/o an IServiceCollection.  In my actual project, 
            // just like the outer Autofac scope, logging is configured externally.
            var loggerFactory = new LoggerFactory().AddConsole();
            cb.RegisterInstance(loggerFactory).SingleInstance();
            cb.RegisterGeneric(typeof(Logger<>)).As(typeof(ILogger<>));
            
            // Throw something random in the container that should hopefully turn up in 
            // ASP.NET Core's child scope.
            cb.RegisterType<RootObject>().SingleInstance();

            // Build the container
            var container = cb.Build();
            
            // The point of this object is to flow some stuff into the Startup class's
            // constructor via DI.  Trying to avoid using statics
            var startupContainer = new StartupHelper(container, env, configuration);

            var hostBuilder = new WebHostBuilder()
                .UseKestrel()
            
                // .UseContentRoot(Directory.GetCurrentDirectory())
                
                .UseLoggerFactory(loggerFactory) // <-- Please don't deprecate me

                .ConfigureServices(svc =>
                {
                    // Because how else can I parameterize startup? 
                    svc.AddSingleton(startupContainer);
                })

                .UseStartup<Startup>();

            var host = hostBuilder.Build();

            host.Run();
        }
    }

    public class StartupHelper
    {
        public IContainer Container { get; }
        public IHostingEnvironment HostingEnvironment { get; }
        public IConfigurationRoot ConfigurationRoot { get; }
        
        public StartupHelper(IContainer container, IHostingEnvironment hostingEnvironment, IConfigurationRoot configurationRoot)
        {
            Container = container ?? throw new ArgumentNullException(nameof(container));
            HostingEnvironment = hostingEnvironment ?? throw new ArgumentNullException(nameof(hostingEnvironment));
            ConfigurationRoot = configurationRoot;
        }
    }

    public class Startup : StartupBase
    {
        public StartupHelper Helper { get; }
        public IConfigurationRoot Configuration => Helper.ConfigurationRoot;

        public Startup(StartupHelper container)
        {
            Helper = container;
        }

        public override IServiceProvider ConfigureServices(IServiceCollection services)
        {
            // I take my existing Autofac container, spawn a child scpoe, and 
            // register the services being added to that child container.
            // then I'll create an AutofacServiceProvider out of it.
            
            var scope = Helper.Container.BeginLifetimeScope(cb =>
            {
                services.AddMvc();
                
                cb.Populate(services);
            });
            
            return new AutofacServiceProvider(scope);
            
            // I strugged with StartupBase<ContainerBuilder> and the IServiceProviderFactory
            // but threw in the towel
        }

        public override void Configure(IApplicationBuilder app)
        {
            var env = app.ApplicationServices.GetService<IHostingEnvironment>();
            var loggerFactory = app.ApplicationServices.GetService<ILoggerFactory>();

            loggerFactory.AddConsole(Configuration.GetSection("Logging"));
            loggerFactory.AddDebug();

            app.Use(async (ctx, next) =>
            {
                // Add ?DEBUG to the end of any request to see the ctx.RequestServices instead
                if (ctx.Request.Query.ContainsKey("DEBUG"))
                {
                    var sb = new StringBuilder();

                    var services = (AutofacServiceProvider)ctx.RequestServices;

                    var componentContext = (IComponentContext)typeof(AutofacServiceProvider).GetField("_componentContext", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(services);
                    var componentRegistry = componentContext.ComponentRegistry;

                    sb.AppendLine(componentRegistry.Registrations.Count() + " registrations:");

                    foreach (var entry in componentRegistry.Registrations.SelectMany(x => x.Services).OfType<IServiceWithType>().Select(x => x.ServiceType))
                    {
                        sb.AppendLine(" - " + entry.ToString());
                    }

                    await ctx.Response.WriteAsync(sb.ToString());
                }
                else
                {
                    await next();
                }
            });

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseBrowserLink();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            app.UseStaticFiles();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });

        }
    }

    public class RootObject
    {
    }
}
