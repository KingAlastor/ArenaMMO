using GameServer;
using GameServer.DataLayer;
using Microsoft.Extensions.Configuration;

Console.WriteLine("=== ARENA MMO — GAME SERVER ===");

// ── Configuration ─────────────────────────────────────────────────────────────
// appsettings.json provides non-secret defaults.
// Environment variables override any value from the file (useful for containerised deploys).
// ARENA_TICKET_SECRET must always be supplied via environment variable — never stored in files.
IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

string? ticketSecret = config["ARENA_TICKET_SECRET"]
    ?? Environment.GetEnvironmentVariable("ARENA_TICKET_SECRET");
if (string.IsNullOrWhiteSpace(ticketSecret))
    throw new InvalidOperationException(
        "Missing ARENA_TICKET_SECRET. Set it as an environment variable before starting the server.");

int  port          = config.GetValue<int>("Arena:Port", defaultValue: 9050);
string redisConn   = config.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Redis in appsettings.json.");
string postgresConn = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres in appsettings.json.");

// ── Services ──────────────────────────────────────────────────────────────────
using var dataService = new MatchDataService(redisConn, postgresConn);

// ── Arena ─────────────────────────────────────────────────────────────────────
Console.WriteLine($"Initialising arena instance on port {port}...");
Console.WriteLine("Press Ctrl+C to shut down.\n");

var arena = new ArenaInstance(ticketSecret, new ZoneDescriptor(), dataService);
arena.Start(port);   // Blocks on the 30 Hz game loop until process is killed
