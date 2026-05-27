using GameServer;

Console.WriteLine("=== ARENA MMO — GAME SERVER ===");
Console.WriteLine("Initialising arena instance on port 9050...");
Console.WriteLine("Press Ctrl+C to shut down.\n");

var arena = new ArenaInstance();
arena.Start(9050);   // Blocks on the 30 Hz game loop until process is killed