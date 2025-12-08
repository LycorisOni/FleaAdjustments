using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Servers;

namespace LycorisFleaAdjustments;

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.lycorisoni.fleaadjustments";
    public override string Name { get; init; } = "LycorisFleaAdjustments";
    public override string Author { get; init; } = "LycorisOni";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.0");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; }
    public override string? License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 50)]
public class FleaAdjustmentLoader(
    ConfigServer configServer,
    DatabaseService databaseService,
    ISptLogger<FleaAdjustmentLoader> logger) : IOnLoad
{
    private FleaPriceAdjustments? _fleaconfig;
    
    public Task OnLoad()
    {
        _fleaconfig = LoadConfig();
        ApplyCustomPricing();
        
        if (_fleaconfig?.Enabled == true)
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
        if (_fleaconfig == null || !_fleaconfig.Enabled)
            return;

        var prices = databaseService.GetPrices();
        var handbook = databaseService.GetHandbook();


        if (_fleaconfig.RemoveFleaListingLimits)
        {
            RemoveFleaRestrictions();
        }

        if (_fleaconfig.FleaBlacklistAddition.Any())
        {
            AddItemsToFleaBlacklist();
        }

        if (_fleaconfig.FleaCategoryBlacklistAddition.Any())
        {
            AddCategoriesToFleaBlacklist();
        }
        // Automatically adjust fees if price multiplier > 1.0
        ApplyDynamicFeeAdjustment();
        foreach (var handbookItem in handbook.Items)
        {
            if (!handbookItem.Price.HasValue || handbookItem.Price <= 0)
                continue;
            
            var itemId = new MongoId(handbookItem.Id);
            
            // If it is in handbook and not in Prices should add it here safely.
            if (!prices.ContainsKey(itemId))
            {
                double multiplier;
                
                if (_fleaconfig.SpecificItemOverrides.TryGetValue(handbookItem.Id, out var specificMultiplier))
                {
                    multiplier = specificMultiplier;
                }
                else if (_fleaconfig.UseRangeBasedPricing)
                {
                    multiplier = GetMultiplierForPriceRange(handbookItem.Price.Value);
                }
                else
                {
                    multiplier = _fleaconfig.PriceMultiplier;
                }
                
                var newPrice = handbookItem.Price.Value * multiplier;
                
                if (newPrice < _fleaconfig.MinimumPrice)
                    newPrice = _fleaconfig.MinimumPrice;
                if (newPrice > _fleaconfig.MaximumPrice)
                    newPrice = _fleaconfig.MaximumPrice;
                
                prices[itemId] = newPrice;
            }
        }
        
        // This will now modify all the shit in Prices.json hopefully including everything in handbook. Maybe modded items too
        foreach (var keyValuePair in prices.ToList())
        {
            var itemId = keyValuePair.Key;
            var originalPrice = keyValuePair.Value;
            double multiplier;

            if (_fleaconfig.SpecificItemOverrides.TryGetValue(itemId.ToString(), out var specificMultiplier))
            {
                multiplier = specificMultiplier;
            }
            else if (_fleaconfig.UseRangeBasedPricing)
            {
                multiplier = GetMultiplierForPriceRange(originalPrice);
            }
            else
            {
                multiplier = _fleaconfig.PriceMultiplier;
            }

            var newPrice = originalPrice * multiplier;
            
            if (newPrice < _fleaconfig.MinimumPrice)
                newPrice = _fleaconfig.MinimumPrice;
            if (newPrice > _fleaconfig.MaximumPrice)
                newPrice = _fleaconfig.MaximumPrice;

            if (Math.Abs(newPrice - originalPrice) > 0.01)
            {
                prices[itemId] = newPrice;
            }
        }
    }
    
    private void ApplyDynamicFeeAdjustment()
    {
        // Only adjust fees if price multiplier > 1.0
        if (_fleaconfig.PriceMultiplier <= 1.0)
            return;
    
        // Calculate the highest effective multiplier (considering overrides)
        double effectiveMultiplier = _fleaconfig.PriceMultiplier;
    
        // Check range based pricing for higher multipliers
        if (_fleaconfig.UseRangeBasedPricing && _fleaconfig.PriceRanges.Any())
        {
            var maxRangeMultiplier = _fleaconfig.PriceRanges.Max(r => r.Multiplier);
            if (maxRangeMultiplier > effectiveMultiplier)
                effectiveMultiplier = maxRangeMultiplier;
        }
    
        // Check specific item overrides for higher multipliers
        if (_fleaconfig.SpecificItemOverrides.Any())
        {
            var maxOverrideMultiplier = _fleaconfig.SpecificItemOverrides.Values.Max();
            if (maxOverrideMultiplier > effectiveMultiplier)
                effectiveMultiplier = maxOverrideMultiplier;
        }
    
        // Use cubed divider to counter exponential fee growth
        double feeDivider = Math.Pow(effectiveMultiplier, 3);
    
        // Modify globals RagFair taxes
        ModifyGlobalsRagfairTaxes(feeDivider);
    }

    private void ModifyGlobalsRagfairTaxes(double feeDivider)
    {
        try
        {
            var globals = databaseService.GetGlobals();
            var globalsType = globals.GetType();
            
            // Get Configuration property
            var configProperty = globalsType.GetProperty("Configuration");
            if (configProperty == null)
                return;
            
            var config = configProperty.GetValue(globals);
            var configType = config.GetType();
            
            // Get RagFair from Configuration
            var ragfairProperty = configType.GetProperty("RagFair");
            if (ragfairProperty == null)
                return;
            
            var ragfair = ragfairProperty.GetValue(config);
            ModifyRagfairTaxes(ragfair, feeDivider);
        }
        catch (Exception ex)
        {
            // Silently fail
        }
    }
    private void ModifyRagfairTaxes(dynamic ragfair, double feeDivider)
    {
        try
        {
            var ragfairType = ragfair.GetType();
            
            // I'm just gonna use all three cause I'm too lazy to figure out which is the fee controller and I don't think it should break anything.
            var communityTaxProperty = ragfairType.GetProperty("CommunityTax");
            var communityItemTaxProperty = ragfairType.GetProperty("CommunityItemTax");
            var communityRequirementTaxProperty = ragfairType.GetProperty("CommunityRequirementTax");
            
            if (communityTaxProperty != null)
            {
                var currentValue = Convert.ToDouble(communityTaxProperty.GetValue(ragfair));
                var newValue = currentValue / feeDivider;
                communityTaxProperty.SetValue(ragfair, Convert.ChangeType(newValue, communityTaxProperty.PropertyType));
            }
            
            if (communityItemTaxProperty != null)
            {
                var currentValue = Convert.ToDouble(communityItemTaxProperty.GetValue(ragfair));
                var newValue = currentValue / feeDivider;
                communityItemTaxProperty.SetValue(ragfair, Convert.ChangeType(newValue, communityItemTaxProperty.PropertyType));
            }
            
            if (communityRequirementTaxProperty != null)
            {
                var currentValue = Convert.ToDouble(communityRequirementTaxProperty.GetValue(ragfair));
                var newValue = currentValue / feeDivider;
                communityRequirementTaxProperty.SetValue(ragfair, Convert.ChangeType(newValue, communityRequirementTaxProperty.PropertyType));
            }
        }
        catch (Exception ex)
        {
            // Silently fail
        }
    }

    private void RemoveFleaRestrictions()
    {
        try
        {
            var globals = databaseService.GetGlobals();
            var globalsType = globals.GetType();
        
            var configProperty = globalsType.GetProperty("Configuration");
            if (configProperty == null)
                return;
        
            var config = configProperty.GetValue(globals);
            var configType = config.GetType();
        
            var ragfairProperty = configType.GetProperty("RagFair");
            if (ragfairProperty == null)
                return;
        
            var ragfair = ragfairProperty.GetValue(config);
            var ragfairType = ragfair.GetType();
        
            var restrictionsProperty = ragfairType.GetProperty("ItemRestrictions");
            if (restrictionsProperty == null)
                return;
            
            var elementType = restrictionsProperty.PropertyType.GetGenericArguments()[0];
            // Create empty list and set it
            var listType = typeof(List<>).MakeGenericType(elementType);
            var emptyList = Activator.CreateInstance(listType);

            restrictionsProperty.SetValue(ragfair, emptyList);
        }
        catch (Exception ex)
        {
            // Silently fail
        }
    }

    private void AddItemsToFleaBlacklist()
    {
        try
        {
            var ragfairConfig = configServer.GetConfig<RagfairConfig>();
            var ragfairType = ragfairConfig.GetType();
            
            var dynamicProp = ragfairType.GetProperty("Dynamic");
            if (dynamicProp == null)
                return;
        
            var dynamicObj = dynamicProp.GetValue(ragfairConfig);  
            var dynamicType = dynamicObj.GetType();                
            
            var blacklistProp = dynamicType.GetProperty("Blacklist");  
            if (blacklistProp == null)
                return;
        
            var blacklistObj = blacklistProp.GetValue(dynamicObj);  
            var blacklistType = blacklistObj.GetType();             
            
            var customProp = blacklistType.GetProperty("Custom");   
            if (customProp == null)
                return;
        
            var customList = customProp.GetValue(blacklistObj);
            
            var addMethod = customList.GetType().GetMethod("Add");
            if (addMethod == null)
                return;
            foreach (var itemId in _fleaconfig.FleaBlacklistAddition)
            {
                var mongoId = new MongoId(itemId);
                addMethod.Invoke(customList, new object[] { mongoId });
            }    
        }
        catch (Exception ex)
        {
            // Silently Fail
        }
    }

    private void AddCategoriesToFleaBlacklist()
    {
        try
        {
            // Im lazy this is the same damn thing as the previous shit. So its stolen and reused.
            var ragfairConfig = configServer.GetConfig<RagfairConfig>();
            var ragfairType = ragfairConfig.GetType();
            
            var dynamicProp = ragfairType.GetProperty("Dynamic");
            if (dynamicProp == null)
                return;
        
            var dynamicObj = dynamicProp.GetValue(ragfairConfig);  
            var dynamicType = dynamicObj.GetType();                
            
            var blacklistProp = dynamicType.GetProperty("Blacklist");  
            if (blacklistProp == null)
                return;
        
            var blacklistObj = blacklistProp.GetValue(dynamicObj);  
            var blacklistType = blacklistObj.GetType();             
            
            var customProp = blacklistType.GetProperty("CustomItemCategoryList");   
            if (customProp == null)
                return;
        
            var customList = customProp.GetValue(blacklistObj);
            
            var addMethod = customList.GetType().GetMethod("Add");
            if (addMethod == null)
                return;
            foreach (var itemId in _fleaconfig.FleaCategoryBlacklistAddition)
            {
                var mongoId = new MongoId(itemId);
                addMethod.Invoke(customList, new object[] { mongoId });
            }
            var enableProp = blacklistType.GetProperty("EnableCustomItemCategoryList");
            if (enableProp != null)
            {
                enableProp.SetValue(blacklistObj, true);
            }
        }
        catch (Exception ex)
        {
            // Silently Fail
        }
    }
    
    private double GetMultiplierForPriceRange(double price)
    {
        if (_fleaconfig?.PriceRanges == null)
            return 1.0;

        var range = _fleaconfig.PriceRanges
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
    
    [JsonPropertyName("removeFleaListingLimits")]
    public bool RemoveFleaListingLimits { get; set; } = false;

    [JsonPropertyName("fleaBlacklistAddition")]
    public List<string> FleaBlacklistAddition { get; set; } = new();
    
    [JsonPropertyName("fleaCategoryBlacklistAddition")]
    public List<string> FleaCategoryBlacklistAddition { get; set; } = new();
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