using GameServer;

Console.WriteLine("=== ARENA MMO — GAME SERVER ===");
Console.WriteLine("Initialising arena instance on port 9050...");
Console.WriteLine("Press Ctrl+C to shut down.\n");

string? ticketSecret = Environment.GetEnvironmentVariable("ARENA_TICKET_SECRET");
if (string.IsNullOrWhiteSpace(ticketSecret))
	throw new InvalidOperationException("Missing ARENA_TICKET_SECRET environment variable.");

var arena = new ArenaInstance(ticketSecret);
arena.Start(9050);   // Blocks on the 30 Hz game loop until process is killed