using LobbyServer;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

Console.WriteLine("=== ARENA MMO — LOBBY SERVER ===");

// ── Configuration ─────────────────────────────────────────────────────────────
// appsettings.json provides non-secret defaults.
// Environment variables override any value (useful for containerised deploys).
// ARENA_TICKET_SECRET must always come from an environment variable — never store it in files.
IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

string? ticketSecret = config["ARENA_TICKET_SECRET"]
    ?? Environment.GetEnvironmentVariable("ARENA_TICKET_SECRET");
if (string.IsNullOrWhiteSpace(ticketSecret))
    throw new InvalidOperationException(
        "Missing ARENA_TICKET_SECRET. Set it as an environment variable before starting the lobby.");

int    lobbyPort           = config.GetValue<int>("Lobby:Port",                  9040);
int    matchSize           = config.GetValue<int>("Lobby:MatchSize",             2);
int    ticketLifetimeMs    = config.GetValue<int>("Lobby:TicketLifetimeMs",      30000);
int    queueStatusInterval = config.GetValue<int>("Lobby:QueueStatusIntervalMs", 2000);

string arenaIp   = config["Arena:Ip"]                   ?? "127.0.0.1";
int    arenaPort = config.GetValue<int>("Arena:Port",     9050);

string redisConn    = config.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Redis in appsettings.json.");
string postgresConn = config.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres in appsettings.json.");

// ── Services ──────────────────────────────────────────────────────────────────
using var redis      = ConnectionMultiplexer.Connect(redisConn);
var       subscriber = redis.GetSubscriber();

using var authService  = new PlayerAuthService(postgresConn);
var       queue        = new MatchmakingQueue(matchSize);
var       ticketIssuer = new TicketIssuer(ticketSecret, ticketLifetimeMs);

// ── Lobby ─────────────────────────────────────────────────────────────────────
Console.WriteLine($"Starting lobby on port {lobbyPort}  (match size={matchSize}, arena={arenaIp}:{arenaPort})");
Console.WriteLine("Press Ctrl+C to shut down.\n");

using var lobbyNetwork = new LobbyNetworkManager(
    authService,
    queue,
    ticketIssuer,
    subscriber,
    arenaIp,
    arenaPort,
    queueStatusInterval);

lobbyNetwork.Start(lobbyPort);  // Blocks on the coordination loop until process is killed
