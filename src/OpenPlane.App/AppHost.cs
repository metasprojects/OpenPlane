using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using OpenPlane.App.Api;
using OpenPlane.App.Services;

namespace OpenPlane.App;

/// <summary>Everything the API uses, built once per app run.</summary>
public sealed class OpenPlaneServices
{
    public OpenPlaneServices(AppOptions options, INativeDialogs dialogs)
    {
        Options = options;
        DataDir = options.DataDir ?? AppPaths.DefaultDataDir();
        Directory.CreateDirectory(DataDir);
        Settings = new SettingsStore(DataDir);
        Steam = new SteamLocator();
        Paths = new AppPaths(options, Settings, Steam, DataDir);
        Flyout = new FlyoutLibrary(Paths);
        Planes = new PlaneLibrary(Paths);
        Meshes = new MeshStore();
        Drafts = new DraftStore(Paths);
        References = new ReferenceStore(Path.Combine(Paths.CacheDir, "References"));
        Jobs = new JobManager();
        Shell = new SystemShell(options.NoExternal);
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenPlane/" + Version);
        Installer = new ModInstaller(Paths, Http, Settings);
        Opened = new OpenedFiles();
        Updates = new UpdateCheck(Http);
        Dialogs = dialogs;
    }

    public static string Version =>
        typeof(OpenPlaneServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? OpenPlane.Core.BuildInfo.Version;

    public AppOptions Options { get; }
    public string DataDir { get; }
    public SettingsStore Settings { get; }
    public SteamLocator Steam { get; }
    public AppPaths Paths { get; }
    public FlyoutLibrary Flyout { get; }
    public PlaneLibrary Planes { get; }
    public MeshStore Meshes { get; }
    public DraftStore Drafts { get; }
    public ReferenceStore References { get; }
    public JobManager Jobs { get; }
    public SystemShell Shell { get; }
    public HttpClient Http { get; }
    public ModInstaller Installer { get; }
    public OpenedFiles Opened { get; }
    public UpdateCheck Updates { get; }
    public INativeDialogs Dialogs { get; set; }
}

/// <summary>The local web server: the page (wwwroot) and its JSON API, on 127.0.0.1 only.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(WebApplication app, OpenPlaneServices services, string url) { App = app; Services = services; Url = url; }

    public WebApplication App { get; }
    public OpenPlaneServices Services { get; }
    /// <summary>The address the page is served at, e.g. http://127.0.0.1:5173/.</summary>
    public string Url { get; }

    public static async Task<AppHost> StartAsync(AppOptions options, INativeDialogs? dialogs = null, bool logToConsole = false)
    {
        var services = new OpenPlaneServices(options, dialogs ?? new NoDialogs());
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Logging.ClearProviders();
        if (logToConsole) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, options.Port);
            k.Limits.MaxRequestBodySize = Endpoints.RequestLimit; // the import route raises its own limit (Endpoints.ImportLimit)
        });

        var app = builder.Build();
        app.Use(Guard);
        app.Use(ErrorHandling);
        var web = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = web });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = web,
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });
        Endpoints.Map(app, services);

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address).Port;
        return new AppHost(app, services, $"http://127.0.0.1:{port}/");
    }

    /// <summary>
    /// Only this computer's own pages may use the API: the Host must be 127.0.0.1/localhost (stops DNS rebinding), and
    /// requests that change things must carry the X-OpenPlane header (other websites can't add it without CORS, which
    /// this server never allows).
    /// </summary>
    private static async Task Guard(HttpContext ctx, Func<Task> next)
    {
        var host = ctx.Request.Host.Host;
        if (host != "127.0.0.1" && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            bool sameOrigin = string.IsNullOrEmpty(origin) || origin.Equals($"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase);
            bool changes = !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method);
            if (!sameOrigin || (changes && ctx.Request.Headers["X-OpenPlane"] != "1"))
            {
                await ApiJson.Error("This request didn't come from the OpenPlane app.", 403).ExecuteAsync(ctx);
                return;
            }
        }
        await next();
    }

    private static async Task ErrorHandling(HttpContext ctx, Func<Task> next)
    {
        try { await next(); }
        catch (UserFacingException e) when (!ctx.Response.HasStarted)
        {
            await ApiJson.Error(e.Message, e.Status).ExecuteAsync(ctx);
        }
        catch (OpenPlane.Core.Builder.MeshOpException e) when (!ctx.Response.HasStarted)
        {
            await ApiJson.Error(e.Message, 400).ExecuteAsync(ctx);
        }
        catch (Exception e) when (!ctx.Response.HasStarted && e is not OperationCanceledException)
        {
            var msg = e switch
            {
                UnauthorizedAccessException => "OpenPlane wasn't allowed to use a file or folder: " + e.Message,
                IOException => "A file problem stopped this: " + e.Message,
                InvalidDataException => e.Message,
                _ => "Something went wrong: " + e.Message,
            };
            await ApiJson.Error(msg, 500).ExecuteAsync(ctx);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Services.Http.Dispose();
    }
}
