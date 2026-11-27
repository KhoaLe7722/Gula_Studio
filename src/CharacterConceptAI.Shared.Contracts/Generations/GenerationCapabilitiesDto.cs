namespace CharacterConceptAI.Shared.Contracts.Generations;

public record GenerationCapabilitiesDto(string Provider, bool IsSimulated, bool Img2ImgAvailable,
    string Img2ImgStatus, bool UpscaleAvailable = false, bool FullQualityAvailable = false);
