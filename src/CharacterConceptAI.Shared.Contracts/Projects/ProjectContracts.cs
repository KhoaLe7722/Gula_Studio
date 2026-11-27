namespace CharacterConceptAI.Shared.Contracts.Projects;

public record ProjectDto(
    Guid Id,
    string Name,
    string ProjectType,
    string? Description,
    string Status,
    string CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? RowVersion
);

public record CreateProjectRequest(
    string Name,
    string ProjectType,
    string? Description
);

public record UpdateProjectRequest(
    string Name,
    string ProjectType,
    string? Description,
    string Version
);

public record ProjectMemberDto(
    Guid ProjectId,
    string UserId,
    string DisplayName,
    string Email,
    string Role,
    DateTimeOffset JoinedAt
);

public record AddProjectMemberRequest(
    string Email,
    string Role = "Designer"
);

public record UpdateProjectMemberRoleRequest(
    string Role
);

public record TransferOwnershipRequest(
    string NewOwnerUserId
);
