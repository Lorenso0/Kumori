using System.Globalization;
using Kumori.FarmFinder;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using osu.Framework.Bindables;
using osu.Game.Configuration;
using osu.Game.Extensions;
using osu.Game.Online.API;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;

namespace Kumori.App.FarmFinder;

/// <summary>
/// Resolves API mods through the exact osu!standard ruleset pinned by Kumori.
/// Any unknown setting, conversion failure, or ineligible configured instance
/// rejects the whole score. User-playable Classic is the sole explicit
/// unranked exception and remains absent from the selectable ranked catalog.
/// </summary>
public sealed class OsuRankedModCatalog : IRankedModCatalog
{
    private readonly OsuRuleset ruleset = new();
    private readonly IReadOnlyList<RankedModDescriptor> descriptors;

    public OsuRankedModCatalog()
    {
        descriptors =
        [
            new RankedModDescriptor("NM", "No Mod"),
            .. ruleset.CreateAllMods()
                .Where(mod => mod.UserPlayable && mod.Ranked)
                .GroupBy(mod => mod.Acronym, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(mod => mod.Acronym, StringComparer.OrdinalIgnoreCase)
                .Select(mod => new RankedModDescriptor(mod.Acronym.ToUpperInvariant(), mod.Name)),
        ];
    }

    public IReadOnlyList<RankedModDescriptor> GetRankedMods() => descriptors;

    public RankedModEvaluation Evaluate(FarmMod source)
    {
        var acronym = source.NormalizedAcronym;
        // NM is a filter-only pseudo-option. An actual API score represents
        // No Mod with an empty array, never with an "NM" mod object.
        if (acronym.Length == 0 || acronym == "NM")
            return Reject(acronym);

        Mod? prototype = ruleset.CreateModFromAcronym(acronym);
        if (prototype is null || prototype is UnknownMod)
            return Reject(acronym);

        JObject settings;
        try
        {
            settings = string.IsNullOrWhiteSpace(source.SettingsJson)
                ? new JObject()
                : JObject.Parse(source.SettingsJson);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return Reject(acronym);
        }

        var settingProperties = prototype.GetSettingsSourceProperties()
            .ToDictionary(
                pair => pair.Item2.Name.ToSnakeCase(),
                pair => pair.Item2,
                StringComparer.OrdinalIgnoreCase);
        if (settings.Properties().Any(property => !settingProperties.ContainsKey(property.Name)))
            return Reject(acronym);

        var convertedSettings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var property in settings.Properties())
            {
                var sourceProperty = settingProperties[property.Name];
                var bindable = (IBindable)sourceProperty.GetValue(prototype)!;
                var current = bindable.GetUnderlyingSettingValue();
                var targetType = current?.GetType() ?? findBindableValueType(sourceProperty.PropertyType);
                var converted = property.Value.ToObject(targetType)
                                ?? throw new JsonSerializationException(
                                    $"Setting '{property.Name}' cannot be null.");
                convertedSettings[property.Name] = converted;
            }
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or
                                                 JsonSerializationException or
                                                 FormatException or
                                                 InvalidCastException or
                                                 OverflowException)
        {
            return Reject(acronym);
        }

        Mod configured;
        try
        {
            configured = new APIMod
            {
                Acronym = acronym,
                Settings = convertedSettings,
            }.ToMod(ruleset);
        }
        catch
        {
            return Reject(acronym);
        }

        var isClassicException =
            configured.Acronym.Equals("CL", StringComparison.OrdinalIgnoreCase);
        if (configured is UnknownMod ||
            !configured.UserPlayable ||
            (!configured.Ranked && !isClassicException))
            return Reject(acronym);

        foreach (var property in settings.Properties())
        {
            var sourceProperty = settingProperties[property.Name];
            var actual = ((IBindable)sourceProperty.GetValue(configured)!).GetUnderlyingSettingValue();
            if (!settingValuesEqual(actual, convertedSettings[property.Name]))
                return Reject(acronym);
        }

        var canonical = FarmFinderValidation.CanonicalJson(
            settings.ToString(Formatting.None));
        return new RankedModEvaluation(true, configured.Acronym.ToUpperInvariant(), canonical);
    }

    private static RankedModEvaluation Reject(string acronym) =>
        new(false, acronym, "{}");

    private static Type findBindableValueType(Type bindableType)
    {
        for (var current = bindableType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType)
                return current.GetGenericArguments()[0];
        }
        return typeof(object);
    }

    private static bool settingValuesEqual(object? actual, object? expected)
    {
        if (actual is null || expected is null)
            return actual is null && expected is null;
        if (actual is IConvertible && expected is IConvertible &&
            isNumeric(actual.GetType()) && isNumeric(expected.GetType()))
        {
            return Math.Abs(
                Convert.ToDouble(actual, CultureInfo.InvariantCulture) -
                Convert.ToDouble(expected, CultureInfo.InvariantCulture)) < 0.000001;
        }
        return Equals(actual, expected);
    }

    private static bool isNumeric(Type type) =>
        Type.GetTypeCode(Nullable.GetUnderlyingType(type) ?? type) is
            TypeCode.Byte or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or
            TypeCode.UInt64 or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or
            TypeCode.Decimal or TypeCode.Double or TypeCode.Single;
}
