using System.Reflection;
using WeaveCopilot.Acp;
using WeaveCopilot.Sdk;

string? cliArg = null;
var hydra = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--copilot-cli" when i + 1 < args.Length: cliArg = args[++i]; break;
        case "--hydrafusion": hydra = true; break;
        case "--version": Console.WriteLine("weave-copilot " + Version()); return 0;
        default: Console.Error.WriteLine($"weave-copilot: unknown argument '{args[i]}'\nusage: weave-copilot [--copilot-cli <path>] [--hydrafusion]"); return 2; }
}

static string Version() => typeof(AcpAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

SdkHost host;
try { host = new SdkHost(SdkHost.ResolveCli(cliArg), hydra); }
catch (Exception e) { Console.Error.WriteLine("weave-copilot: " + e.Message); return 1; }

// stdout carries only protocol lines; everything else goes to stderr.
var rpc = new JsonRpcConnection(new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)), new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { NewLine = "\n" });
using var login = new CopilotLogin(host.CliPath, ExperimentalGuard.DefaultHome());
new AcpAgent(rpc, host, hydra, Version(), msg => { Console.Error.WriteLine("weave-copilot: " + msg); host.RestoreSettings(); Environment.Exit(1); }, login).Attach();
// Never leave the Copilot CLI child behind: stop it on SIGTERM/SIGINT as well as on stdin EOF.
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; login.Dispose(); host.DisposeAsync().AsTask().Wait(3000); Environment.Exit(0); });
using var intr = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGINT, ctx => { ctx.Cancel = true; login.Dispose(); host.DisposeAsync().AsTask().Wait(3000); Environment.Exit(0); });
await rpc.RunAsync();
await host.DisposeAsync();
return 0;
