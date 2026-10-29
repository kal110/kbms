using KnowledgeBase.Core.Domain.Access;

namespace KnowledgeBase.Core.Application.Access;

/// <summary>Who a grant is for, with a readable name (frame 13's people picker and grant rows).</summary>
public sealed record PrincipalDto(PrincipalType Type, Guid Id, string Key, string Name, string Detail);

public sealed record GrantDto(
    Guid Id,
    PrincipalDto Principal,
    AccessLevel Level,
    bool IsSystem,
    DateTimeOffset? ExpiresAt,
    bool Expired,
    string? GrantedBy,
    DateTimeOffset GrantedAt);

public sealed record AccessSourceDto(string Kind, Guid? Id, string Name);

/// <summary>An effective level after inheritance, and where it comes from.</summary>
public sealed record EffectiveAccessDto(PrincipalDto Principal, AccessLevel Level, AccessSourceDto Source, bool Inherited);

public sealed record ResourceAccessDto(
    ResourceType ResourceType,
    Guid ResourceId,
    string Name,
    bool IsSystem,
    bool InheritPermissions,
    AccessLevel MyLevel,
    bool CanManage,
    IReadOnlyList<GrantDto> Grants,
    IReadOnlyList<EffectiveAccessDto> Effective);

public sealed record GrantRequest(PrincipalType PrincipalType, Guid PrincipalId, AccessLevel Level, DateTimeOffset? ExpiresAt);

public sealed record InheritanceRequest(bool Inherit);
