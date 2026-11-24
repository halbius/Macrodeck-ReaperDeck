using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReaperControl;
using ReaperControl.Reaper;

var builder = MacroDeckPlugin.CreatePlugin(args)
    .UseMacroDeckLogging()
    .RegisterIntegration<ReaperIntegration>();

// When Macro Deck stops the plugin, end within a few seconds even if a REAPER request still hangs:
// the supervisor expects a stopped managed plugin to be gone (conformance check MDC0604, 15 s).
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));

// One shared connection: the integration fills it from the stored config, the actions use it.
builder.Services.AddSingleton<ReaperConnection>();

// Starts the background Lua scripts in REAPER when their values are needed and keeps them alive.
builder.Services.AddSingleton<ScriptKeeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScriptKeeper>());

// Polls the on/off state of the REAPER actions shown on buttons (one instance, also a hosted service).
builder.Services.AddSingleton<CommandStateMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommandStateMonitor>());

// Marker list + cursor position for the marker buttons, and the shared marker filter.
builder.Services.AddSingleton<MarkerMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MarkerMonitor>());
builder.Services.AddSingleton<MarkerFilterState>();

// Cursor position for the timecode widget (polls only while a timecode tile is open).
builder.Services.AddSingleton<TimecodeMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TimecodeMonitor>());

// Event "REAPER context changed" (tracks / items / envelopes) - polls only while a trigger uses it.
builder.Services.AddSingleton<ContextMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ContextMonitor>());

var plugin = builder.Build();
await plugin.RunAsync();
