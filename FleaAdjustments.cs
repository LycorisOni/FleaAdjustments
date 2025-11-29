using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Models.Common;

namespace LycorisFleaAdjustments;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.lycorisoni.fleaadjustments";
    public override string Name { get; init; } = "LycorisFleaAdjustments";
    public override string Author { get; init; } = "LycorisOni";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string? License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 50)]
public class FleaAdjustmentLoader(
    DatabaseService databaseService,
    ISptLogger<FleaAdjustmentLoader> logger) : IOnLoad
{
    private FleaPriceAdjustments? fleaconfig;
    
    public Task OnLoad()
    {
        fleaconfig = LoadConfig();
        ApplyCustomPricing();
        
        if (fleaconfig?.Enabled == true)
        {
            logger.Success("🦊💕 Beep boop! Your flea prices are set, cutie patootie! ✨(｡•̀ᴗ-)✧");
        }
        
        return Task.CompletedTask;
    }
    // Checks the config file and reads it. Everything is documented in the config file.
    private FleaPriceAdjustments LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"user\mods\FleaAdjustment\Config\FleaAdjustmentConfig.json");
            var jsonContent = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<FleaPriceAdjustments>(jsonContent, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            return config ?? new FleaPriceAdjustments { Enabled = false };
        }
        catch
        {
            return new FleaPriceAdjustments { Enabled = false };
        }
    }

    private void ApplyCustomPricing()
    {
        if (fleaconfig == null || !fleaconfig.Enabled)
            return;

        var prices = databaseService.GetPrices();
        var handbook = databaseService.GetHandbook();
        
        // Should be working just fine to check handbook first.
        foreach (var handbookItem in handbook.Items)
        {
            if (!handbookItem.Price.HasValue || handbookItem.Price <= 0)
                continue;
            
            var itemId = new MongoId(handbookItem.Id);
            
            // If it is in handbook and not in Prices should add it here safely.
            if (!prices.ContainsKey(itemId))
            {
                double multiplier;
                
                if (fleaconfig.SpecificItemOverrides.TryGetValue(handbookItem.Id, out var specificMultiplier))
                {
                    multiplier = specificMultiplier;
                }
                else if (fleaconfig.UseRangeBasedPricing)
                {
                    multiplier = GetMultiplierForPriceRange(handbookItem.Price.Value);
                }
                else
                {
                    multiplier = fleaconfig.PriceMultiplier;
                }
                
                var newPrice = handbookItem.Price.Value * multiplier;
                
                if (newPrice < fleaconfig.MinimumPrice)
                    newPrice = fleaconfig.MinimumPrice;
                if (newPrice > fleaconfig.MaximumPrice)
                    newPrice = fleaconfig.MaximumPrice;
                
                prices[itemId] = newPrice;
            }
        }

        // This will now modify all the shit in Prices.json hopefully including everything in handbook. Maybe modded items too
        foreach (var keyValuePair in prices.ToList())
        {
            var itemId = keyValuePair.Key;
            var originalPrice = keyValuePair.Value;
            double multiplier;

            if (fleaconfig.SpecificItemOverrides.TryGetValue(itemId.ToString(), out var specificMultiplier))
            {
                multiplier = specificMultiplier;
            }
            else if (fleaconfig.UseRangeBasedPricing)
            {
                multiplier = GetMultiplierForPriceRange(originalPrice);
            }
            else
            {
                multiplier = fleaconfig.PriceMultiplier;
            }

            var newPrice = originalPrice * multiplier;
            
            if (newPrice < fleaconfig.MinimumPrice)
                newPrice = fleaconfig.MinimumPrice;
            if (newPrice > fleaconfig.MaximumPrice)
                newPrice = fleaconfig.MaximumPrice;

            if (Math.Abs(newPrice - originalPrice) > 0.01)
            {
                prices[itemId] = newPrice;
            }
        }
    }

    private double GetMultiplierForPriceRange(double price)
    {
        if (fleaconfig?.PriceRanges == null)
            return 1.0;

        var range = fleaconfig.PriceRanges
            .OrderBy(r => r.MaxPrice)
            .FirstOrDefault(r => price <= r.MaxPrice);

        return range?.Multiplier ?? 1.0;
    }
}

public class FleaPriceAdjustments
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("priceMultiplier")]
    public double PriceMultiplier { get; set; } = 1.5;

    [JsonPropertyName("useRangeBasedPricing")]
    public bool UseRangeBasedPricing { get; set; } = false;

    [JsonPropertyName("priceRanges")]
    public List<PriceRange> PriceRanges { get; set; } = new();

    [JsonPropertyName("specificItemOverrides")]
    public Dictionary<string, double> SpecificItemOverrides { get; set; } = new();

    [JsonPropertyName("minimumPrice")]
    public double MinimumPrice { get; set; } = 1;

    [JsonPropertyName("maximumPrice")]
    public double MaximumPrice { get; set; } = 500000000;
}

public class PriceRange
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("maxPrice")]
    public double MaxPrice { get; set; }

    [JsonPropertyName("multiplier")]
    public double Multiplier { get; set; } = 1.0;
}
