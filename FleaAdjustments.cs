using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Spt.Mod;

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

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class FleaAdjustmentLoader(DatabaseService databaseService) : IOnLoad
{
    private FleaPriceChanging? _fleaconfiguration;
    
    public Task OnLoad()
    {
        _fleaconfiguration = LoadConfig();
        ApplyCustomPricing();
        return Task.CompletedTask;
    }

    private FleaPriceChanging LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user", "mods", "FleaAdjustment", "Config", "FleaAdjustmentConfig.json");
            var jsonContent = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<FleaPriceChanging>(jsonContent, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            return config ?? new FleaPriceChanging { Enabled = false };
        }
        catch
        {
            return new FleaPriceChanging { Enabled = false };
        }
    }

    private void ApplyCustomPricing()
    {
        try
        {
            if (_fleaconfiguration == null || !_fleaconfiguration.Enabled)
                return;

            // Modify prices.json
            var prices = databaseService.GetPrices();

            foreach (var kvp in prices)
            {
                var itemId = kvp.Key;
                var originalPrice = kvp.Value;
                double multiplier;

                if (_fleaconfiguration.SpecificItemOverrides.TryGetValue(itemId.ToString(), out var specificMultiplier))
                {
                    multiplier = specificMultiplier;
                }
                else if (_fleaconfiguration.UseRangeBasedPricing)
                {
                    multiplier = GetMultiplierForPriceRange(originalPrice);
                }
                else
                {
                    multiplier = _fleaconfiguration.PriceMultiplier;
                }

                var newPrice = originalPrice * multiplier;
                
                if (newPrice < _fleaconfiguration.MinimumPrice)
                    newPrice = _fleaconfiguration.MinimumPrice;
                if (newPrice > _fleaconfiguration.MaximumPrice)
                    newPrice = _fleaconfiguration.MaximumPrice;

                if (Math.Abs(newPrice - originalPrice) > 0.01)
                {
                    prices[itemId] = newPrice;
                }
            }

            // Modify handbook prices
            var handbook = databaseService.GetHandbook();
            
            foreach (var item in handbook.Items)
            {
                if (item.Price.HasValue && item.Price > 0)
                {
                    var oldHandbookPrice = item.Price.Value;
                    double multiplier;
                    
                    if (_fleaconfiguration.SpecificItemOverrides.TryGetValue(item.Id, out var specificMultiplier))
                    {
                        multiplier = specificMultiplier;
                    }
                    else if (_fleaconfiguration.UseRangeBasedPricing)
                    {
                        multiplier = GetMultiplierForPriceRange(oldHandbookPrice);
                    }
                    else
                    {
                        multiplier = _fleaconfiguration.PriceMultiplier;
                    }
                    
                    var newPrice = (int)(oldHandbookPrice * multiplier);
                    
                    if (newPrice < _fleaconfiguration.MinimumPrice)
                        newPrice = (int)_fleaconfiguration.MinimumPrice;
                    if (newPrice > _fleaconfiguration.MaximumPrice)
                        newPrice = (int)_fleaconfiguration.MaximumPrice;
                    
                    if (Math.Abs(newPrice - oldHandbookPrice) > 0.01)
                    {
                        item.Price = newPrice;
                    }
                }
            }
        }
        catch
        {
            // Silently fail
        }
    }

    private double GetMultiplierForPriceRange(double price)
    {
        if (_fleaconfiguration?.PriceRanges == null)
            return 1.0;

        var range = _fleaconfiguration.PriceRanges
            .OrderBy(r => r.MaxPrice)
            .FirstOrDefault(r => price <= r.MaxPrice);

        return range?.Multiplier ?? 1.0;
    }
}

public class FleaPriceChanging
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