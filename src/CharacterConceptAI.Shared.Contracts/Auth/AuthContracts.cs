namespace CharacterConceptAI.Shared.Contracts.Auth;

public record LoginRequest(string Email, string Password);

public record RefreshTokenRequest(string RefreshToken);

public record BearerTokenResponse(
    string TokenType,
    string AccessToken,
    long ExpiresIn,
    string RefreshToken
);

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    long ExpiresIn,
    string TokenType,
    UserInfoResponse? User = null
);

public record UserInfoResponse(
    string Id,
    string Email,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> GlobalRoles
);

public record AccountDto(
    string Id,
    string Email,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles
);

public record CreateAccountRequest(
    string Email,
    string DisplayName,
    string Password,
    IReadOnlyList<string>? Roles = null
);

public record UpdateAccountStatusRequest(bool IsActive);

public record ResetPasswordRequest(string NewPassword);
