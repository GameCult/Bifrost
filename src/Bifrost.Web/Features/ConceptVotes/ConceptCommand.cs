using Microsoft.Extensions.DependencyInjection;

namespace Bifrost.Web.Features.ConceptVotes;

// Host command: Bifrost.Web.dll concepts plan|apply <collection> <manifest.json> [--retire k1,k2]
public static class ConceptCommand
{
    public const int Ok = 0;
    public const int Refused = 1;
    public const int Usage = 2;

    // args are the words after "concepts".
    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output)
    {
        var known = args.Length > 0 && args[0] is "plan" or "apply";
        var hasRetire = args.Length == 5 && args[3] == "--retire";
        if (!known || !(args.Length == 3 || hasRetire))
        {
            await output.WriteLineAsync("usage: concepts plan|apply <collection> <manifest.json> [--retire k1,k2]");
            return Usage;
        }

        string[] retire = hasRetire ? args[4].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        try
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(args[2]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ManifestRefusedException("manifest", "Unreadable");
            }

            var ships = ConceptSubjectRegistry.ParseManifest(json);
            await using var scope = services.CreateAsyncScope();
            var registry = scope.ServiceProvider.GetRequiredService<ConceptSubjectRegistry>();
            var plan = args[0] == "apply"
                ? await registry.ApplyAsync(args[1], ships, retire, default)
                : await registry.PlanAsync(args[1], ships, retire, default);

            foreach (var entry in plan.Entries)
            {
                await output.WriteLineAsync($"{entry.Change.ToString().ToLowerInvariant()} {entry.Key}");
            }

            return Ok;
        }
        catch (ManifestRefusedException ex)
        {
            await output.WriteLineAsync($"refused {ex.Field} {ex.Code}");
            return Refused;
        }
    }
}
