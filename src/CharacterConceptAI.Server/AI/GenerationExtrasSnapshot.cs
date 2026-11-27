using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Generations;

namespace CharacterConceptAI.Server.AI;

internal sealed record DepthPoseExtra(string PoseId, double Strength);
internal sealed record FaceIdentityExtra(
    Guid ReferenceConceptVersionId,
    double Strength,
    double RestoreStrength,
    IReadOnlyList<Guid>? AdditionalReferenceConceptVersionIds = null);
internal sealed record GenerationExtras(
    DepthPoseExtra? DepthPose,
    FaceIdentityExtra? FaceIdentity,
    FaceIdentityExtra? SecondaryFaceIdentity = null,
    IReadOnlyList<LoraSelectionDto>? Loras = null);

internal static class GenerationExtrasSnapshot
{
    internal const string DepthPoseProperty = "depthPose";
    internal const string FaceIdentityProperty = "faceIdentity";
    internal const string SecondaryFaceIdentityProperty = "secondaryFaceIdentity";
    internal const string LorasProperty = "loras";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void Attach(
        JsonObject snapshotRoot,
        DepthPoseExtra? depthPose,
        FaceIdentityExtra? faceIdentity,
        FaceIdentityExtra? secondaryFaceIdentity = null,
        IReadOnlyList<LoraSelectionDto>? loras = null)
    {
        if (depthPose != null)
            snapshotRoot[DepthPoseProperty] = JsonSerializer.SerializeToNode(depthPose, Options);
        if (faceIdentity != null)
            snapshotRoot[FaceIdentityProperty] = JsonSerializer.SerializeToNode(faceIdentity, Options);
        if (secondaryFaceIdentity != null)
            snapshotRoot[SecondaryFaceIdentityProperty] = JsonSerializer.SerializeToNode(secondaryFaceIdentity, Options);
        if (loras is { Count: > 0 })
            snapshotRoot[LorasProperty] = JsonSerializer.SerializeToNode(loras, Options);
    }

    private static FaceIdentityExtra? ParseFaceExtra(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var parsed = el.Deserialize<FaceIdentityExtra>(Options);
        if (parsed is null || parsed.ReferenceConceptVersionId == Guid.Empty) return null;
        var extras = parsed.AdditionalReferenceConceptVersionIds?
            .Where(id => id != Guid.Empty && id != parsed.ReferenceConceptVersionId)
            .Distinct()
            .Take(4)
            .ToList();
        return parsed with
        {
            Strength = Math.Clamp(parsed.Strength, 0.0, 1.0),
            RestoreStrength = Math.Clamp(parsed.RestoreStrength, 0.0, 1.0),
            AdditionalReferenceConceptVersionIds = extras is { Count: > 0 } ? extras : null
        };
    }

    public static GenerationExtras Read(GenerationJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ProfileSnapshotJson))
            return new GenerationExtras(null, null);
        try
        {
            using var doc = JsonDocument.Parse(job.ProfileSnapshotJson);
            DepthPoseExtra? depthPose = null;
            FaceIdentityExtra? faceIdentity = null;
            FaceIdentityExtra? secondaryFaceIdentity = null;
            List<LoraSelectionDto>? loras = null;

            if (doc.RootElement.TryGetProperty(DepthPoseProperty, out var dp) && dp.ValueKind == JsonValueKind.Object)
            {
                var parsed = dp.Deserialize<DepthPoseExtra>(Options);
                if (parsed is { PoseId.Length: > 0 })
                    depthPose = parsed with { Strength = Math.Clamp(parsed.Strength, 0.25, 1.25) };
            }

            if (doc.RootElement.TryGetProperty(FaceIdentityProperty, out var fi))
                faceIdentity = ParseFaceExtra(fi);

            if (doc.RootElement.TryGetProperty(SecondaryFaceIdentityProperty, out var sfi))
                secondaryFaceIdentity = ParseFaceExtra(sfi);

            if (doc.RootElement.TryGetProperty(LorasProperty, out var lr) && lr.ValueKind == JsonValueKind.Array)
            {
                var parsedLoras = lr.Deserialize<List<LoraSelectionDto>>(Options);
                if (parsedLoras is { Count: > 0 })
                {
                    loras = parsedLoras
                        .Where(l => !string.IsNullOrWhiteSpace(l.FileName))
                        .Take(3)
                        .Select(l => new LoraSelectionDto(l.FileName.Trim(), Math.Clamp(l.Strength, 0.05, 1.50)))
                        .ToList();
                }
            }

            return new GenerationExtras(depthPose, faceIdentity, secondaryFaceIdentity, loras is { Count: > 0 } ? loras : null);
        }
        catch
        {
            return new GenerationExtras(null, null);
        }
    }
}
