using System.Text.RegularExpressions;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Characters;

namespace CharacterConceptAI.Server.AI;

public record PromptBuildResult(
    string RawPrompt,
    string StructuredPrompt,
    string HybridPrompt,
    IReadOnlyList<string> Warnings,
    string TemplateVersion,
    IReadOnlyDictionary<string, string?> EffectiveAttributes
);

public interface IPromptBuilderService
{
    PromptBuildResult BuildPrompt(
        Character character,
        Guid? variantId = null,
        string? userAddendum = null);
}

public class PromptBuilderService : IPromptBuilderService
{
    public const string CurrentVersion = "1.1";

    private const string RenderStyles = @"photoreal(?:istic|ism)?|realistic photography|(?:photo|photograph) of|studio portrait|anime|manga|cartoon|cel[- ]?shaded|(?:2d|3d) animation|illustration|ảnh chụp|ảnh người thật|ảnh chân thực|hoạt hình|minh họa";
    private const string Negation = @"(?:not|no|without|không(?: phải)?)\s+(?:(?:an?|any)\s+)?";
    private static readonly Regex StyleCue = new($@"\b(?:{RenderStyles})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ExcludedStyleCue = new($@"\b{Negation}(?:{RenderStyles})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex StyleOnlyParenthesis = new(
        $@"\s*\(\s*{Negation}(?:{RenderStyles})(?:\s*,\s*{Negation}(?:{RenderStyles}))*\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public PromptBuildResult BuildPrompt(
        Character character,
        Guid? variantId = null,
        string? userAddendum = null)
    {
        // 1. Resolve variant (target or default)
        CharacterVariant? targetVariant = null;
        if (variantId.HasValue)
        {
            targetVariant = character.Variants.FirstOrDefault(v => v.Id == variantId.Value);
        }
        targetVariant ??= character.Variants.FirstOrDefault(v => v.IsDefault)
                          ?? character.Variants.FirstOrDefault();

        // 2. Resolve effective attributes: Base merged with Variant overrides
        var baseDict = character.AttributeValues
            .Where(av => av.Definition != null)
            .ToDictionary(av => av.DefinitionId, av => av);

        var overrideDict = targetVariant != null
            ? targetVariant.AttributeValues
                .Where(vav => vav.Definition != null)
                .ToDictionary(vav => vav.DefinitionId, vav => vav)
            : new Dictionary<Guid, VariantAttributeValue>();

        var allDefIds = baseDict.Keys.Union(overrideDict.Keys);
        var effectiveList = new List<(CharacterAttributeDefinition Definition, string? ValueJson, bool IsOverridden)>();

        foreach (var defId in allDefIds)
        {
            var isOverridden = overrideDict.TryGetValue(defId, out var ov);
            var baseAv = baseDict.GetValueOrDefault(defId);
            var def = ov?.Definition ?? baseAv?.Definition;
            if (def == null) continue;

            var val = isOverridden ? ov!.ValueJson : baseAv?.ValueJson;
            effectiveList.Add((def, val, isOverridden));
        }

        // Sort deterministically: Identity-critical first, then Code ascending
        effectiveList = effectiveList
            .OrderByDescending(x => x.Definition.IsIdentityCritical)
            .ThenBy(x => x.Definition.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var effectiveDict = effectiveList
            .ToDictionary(x => x.Definition.Code, x => x.ValueJson, StringComparer.OrdinalIgnoreCase);

        // 3. Build Mode 1: Raw Prompt
        var rawPrompt = !string.IsNullOrWhiteSpace(userAddendum)
            ? userAddendum.Trim()
            : $"Concept art of {character.Name}, {character.Role}";

        var profileStyles = effectiveList
            .Where(attribute => IsRenderStyle(attribute.Definition) && !string.IsNullOrWhiteSpace(attribute.ValueJson))
            .Select(attribute => attribute.ValueJson!.Trim().Trim('"'))
            .Where(style => !string.IsNullOrWhiteSpace(style))
            .ToArray();
        var addendum = userAddendum?.Trim() ?? string.Empty;
        var positiveStyle = StyleCue.IsMatch(ExcludedStyleCue.Replace(addendum, string.Empty));
        var specifiesStyle = positiveStyle || ExcludedStyleCue.IsMatch(addendum);

        var tokenParts = new List<string>
        {
            $"Character: {character.Name}",
            character.Role
        };

        if (targetVariant != null && !targetVariant.IsDefault && !string.IsNullOrWhiteSpace(targetVariant.Name))
        {
            tokenParts.Add($"variant {targetVariant.Name}");
        }

        foreach (var (def, val, _) in effectiveList)
        {
            if (string.IsNullOrWhiteSpace(val) || IsRenderStyle(def)) continue;
            var cleanVal = val.Trim();

            // Format natural English tokens based on common attribute codes
            var codeLower = def.Code.ToLowerInvariant();
            if (codeLower.Contains("hair_color") || codeLower == "hair")
            {
                tokenParts.Add($"{cleanVal} hair");
            }
            else if (codeLower.Contains("hair_style"))
            {
                tokenParts.Add(cleanVal);
            }
            else if (codeLower.Contains("eye_color") || codeLower == "eyes")
            {
                tokenParts.Add($"{cleanVal} eyes");
            }
            else if (codeLower.Contains("armor") || codeLower.Contains("clothing") || codeLower.Contains("outfit"))
            {
                tokenParts.Add($"wearing {cleanVal}");
            }
            else if (codeLower.Contains("weapon"))
            {
                tokenParts.Add($"holding {cleanVal}");
            }
            else if (codeLower.Contains("expression"))
            {
                tokenParts.Add($"{cleanVal} expression");
            }
            else if (codeLower.Contains("pose") || codeLower.Contains("action"))
            {
                tokenParts.Add($"pose: {cleanVal}");
            }
            else if (codeLower.Contains("lighting"))
            {
                tokenParts.Add($"{cleanVal} lighting");
            }
            else if (codeLower.Contains("background") || codeLower.Contains("setting") || codeLower.Contains("scene"))
            {
                tokenParts.Add($"set in {cleanVal}");
            }
            else if (codeLower.Contains("camera") || codeLower.Contains("framing") || codeLower.Contains("shot"))
            {
                tokenParts.Add($"{cleanVal} shot");
            }
            else if (codeLower.Contains("text_overlay") || codeLower.Contains("sign_text") || codeLower.Contains("title_text"))
            {
                // Z-Image Paper Sec. 3.1 & p.47-48: explicit text to render must be wrapped in English double quotes.
                var unquoted = cleanVal.Trim('"');
                tokenParts.Add($"with clear text reading \"{unquoted}\"");
            }
            else
            {
                tokenParts.Add($"{def.DisplayName}: {cleanVal}");
            }
        }

        var details = string.Join(", ", tokenParts);
        var structuredPrompt = profileStyles.Length > 0
            ? $"{string.Join(", ", profileStyles)}, {details}"
            : $"2D anime concept art, {details}, clean lines, solid background";

        // In Hybrid, this generation's style takes priority over the profile's medium.
        // Style-only negations in parentheses are omitted when a positive style is supplied,
        // so a request like "photo (not anime)" does not feed anime into positive conditioning.
        var hybridPrompt = addendum.Length == 0 ? structuredPrompt
            : specifiesStyle ? $"{(positiveStyle ? StyleOnlyParenthesis.Replace(addendum, string.Empty) : addendum)}, {details}"
            : $"{structuredPrompt}, {addendum}";
        // 6. Anti-Contradiction Analysis
        var warnings = new List<string>();
        if (specifiesStyle)
            warnings.Add("Hybrid ưu tiên phong cách trong mô tả hiện tại và giữ các thuộc tính nhân vật.");
        if (!string.IsNullOrWhiteSpace(userAddendum))
        {
            CheckContradictions(userAddendum, effectiveList, warnings);
        }

        return new PromptBuildResult(
            RawPrompt: rawPrompt,
            StructuredPrompt: structuredPrompt,
            HybridPrompt: hybridPrompt,
            Warnings: warnings,
            TemplateVersion: CurrentVersion,
            EffectiveAttributes: effectiveDict
        );
    }

    private static bool IsRenderStyle(CharacterAttributeDefinition definition)
    {
        var code = definition.Code.Replace('-', '_').ToLowerInvariant();
        return code is "style" or "art_style" or "visual_style" or "image_style" or "render_style" or "rendering_style"
            || definition.DisplayName.Trim().Equals("Phong cách hình ảnh", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckContradictions(
        string userAddendum,
        List<(CharacterAttributeDefinition Definition, string? ValueJson, bool IsOverridden)> attributes,
        List<string> warnings)
    {
        var lowerAddendum = userAddendum.ToLowerInvariant();

        foreach (var (def, val, _) in attributes)
        {
            if (string.IsNullOrWhiteSpace(val)) continue;
            var valLower = val.ToLowerInvariant();
            var codeLower = def.Code.ToLowerInvariant();

            // Hair color contradiction
            if (codeLower.Contains("hair") && (codeLower.Contains("color") || valLower.Contains("hair") || valLower.Contains("tóc")))
            {
                var colors = new[] { "blonde", "black", "white", "silver", "blue", "pink", "red", "green", "purple", "brown", "gold" };
                foreach (var color in colors)
                {
                    if (lowerAddendum.Contains($"{color} hair") && !valLower.Contains(color))
                    {
                        warnings.Add($"Phát hiện mâu thuẫn: Lời nhắc bổ sung chứa '{color} hair' nhưng thuộc tính '{def.DisplayName}' của nhân vật đang là '{val}'.");
                    }
                }
            }

            // Hair length contradiction
            if (codeLower.Contains("hair"))
            {
                if (valLower.Contains("short") && lowerAddendum.Contains("long hair"))
                {
                    warnings.Add($"Phát hiện mâu thuẫn: Lời nhắc chứa 'long hair' nhưng nhân vật có tóc ngắn ('{val}').");
                }
                else if (valLower.Contains("long") && lowerAddendum.Contains("short hair"))
                {
                    warnings.Add($"Phát hiện mâu thuẫn: Lời nhắc chứa 'short hair' nhưng nhân vật có tóc dài ('{val}').");
                }
            }

            // Eye color contradiction
            if (codeLower.Contains("eye"))
            {
                var eyeColors = new[] { "blue", "red", "green", "brown", "purple", "amber", "golden", "black" };
                foreach (var ec in eyeColors)
                {
                    if (lowerAddendum.Contains($"{ec} eyes") && !valLower.Contains(ec))
                    {
                        warnings.Add($"Phát hiện mâu thuẫn: Lời nhắc bổ sung chứa '{ec} eyes' nhưng thuộc tính mắt của nhân vật đang là '{val}'.");
                    }
                }
            }
        }
    }
}
