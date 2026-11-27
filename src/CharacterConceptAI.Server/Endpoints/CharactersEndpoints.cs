using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using CharacterConceptAI.Server.Authorization;
using CharacterConceptAI.Server.Data;
using CharacterConceptAI.Server.Data.Entities;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Common;

namespace CharacterConceptAI.Server.Endpoints;

public static class CharactersEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static RouteGroupBuilder MapCharactersEndpoints(this RouteGroupBuilder group)
    {
        var charsGroup = group.RequireAuthorization();

        // =========================================================================
        // 1. ATTRIBUTE DEFINITIONS
        // =========================================================================

        // List attribute definitions (project-scoped or global)
        charsGroup.MapGet("/{projectId:guid}/attribute-definitions", async (
            Guid projectId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var defs = await db.CharacterAttributeDefinitions
                .AsNoTracking()
                .Where(d => d.ProjectId == null || d.ProjectId == projectId)
                .OrderBy(d => d.DisplayName)
                .Select(d => new AttributeDefinitionDto(
                    d.Id,
                    d.ProjectId,
                    d.Code,
                    d.DisplayName,
                    d.DataType,
                    d.AllowedValuesJson,
                    d.IsRequired,
                    d.IsIdentityCritical
                ))
                .ToListAsync();

            return Results.Ok(defs);
        });

        // Create attribute definition (Owner or Designer)
        charsGroup.MapPost("/{projectId:guid}/attribute-definitions", async (
            Guid projectId,
            CreateAttributeDefinitionRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.DisplayName))
            {
                return Results.Problem("Mã thuộc tính (Code) và tên hiển thị không được để trống.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var validDataTypes = new[] { "Text", "Number", "Boolean", "SingleSelect", "MultiSelect" };
            if (!validDataTypes.Contains(req.DataType, StringComparer.OrdinalIgnoreCase))
            {
                return Results.Problem($"Kiểu dữ liệu '{req.DataType}' không hợp lệ. Cho phép: Text, Number, Boolean, SingleSelect, MultiSelect.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var normalizedCode = req.Code.Trim().ToLowerInvariant();

            var existing = await db.CharacterAttributeDefinitions
                .AnyAsync(d => (d.ProjectId == projectId || d.ProjectId == null) && d.Code == normalizedCode);
            if (existing)
            {
                return Results.Problem($"Mã thuộc tính '{normalizedCode}' đã tồn tại trong dự án.", statusCode: StatusCodes.Status409Conflict);
            }

            var def = new CharacterAttributeDefinition
            {
                ProjectId = projectId,
                Code = normalizedCode,
                DisplayName = req.DisplayName.Trim(),
                DataType = req.DataType.Trim(),
                AllowedValuesJson = req.AllowedValuesJson,
                IsRequired = req.IsRequired,
                IsIdentityCritical = req.IsIdentityCritical
            };

            db.CharacterAttributeDefinitions.Add(def);
            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{projectId}/attribute-definitions/{def.Id}", new AttributeDefinitionDto(
                def.Id,
                def.ProjectId,
                def.Code,
                def.DisplayName,
                def.DataType,
                def.AllowedValuesJson,
                def.IsRequired,
                def.IsIdentityCritical
            ));
        });

        // =========================================================================
        // 2. CHARACTERS CRUD & FILTERING
        // =========================================================================

        // List characters with filter/search
        charsGroup.MapGet("/{projectId:guid}/characters/paged", async (
            Guid projectId,
            int? page,
            int? pageSize,
            string? query,
            string? status,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();
            var access = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true, ct: ct);
            if (!access.IsAuthorized)
                return access.Status == ProjectAccessStatus.NotFound ? Results.NotFound() : Results.Problem(access.ErrorMessage, statusCode: 403);

            var pageNumber = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            if ((long)(pageNumber - 1) * size > int.MaxValue)
                return Results.BadRequest(new { error = "Trang yêu cầu vượt giới hạn hỗ trợ." });
            var q = db.Characters.AsNoTracking().Where(c => c.ProjectId == projectId);
            if (!string.IsNullOrWhiteSpace(query))
            {
                var term = query.Trim();
                q = q.Where(c => c.Name.Contains(term) || (c.Description != null && c.Description.Contains(term)));
            }
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status.Trim());
            var total = await q.CountAsync(ct);
            var characters = await q.OrderBy(c => c.Name).ThenBy(c => c.Id)
                .Skip((pageNumber - 1) * size).Take(size)
                .AsSplitQuery()
                .Include(c => c.Variants).ThenInclude(v => v.AttributeValues).ThenInclude(v => v.Definition)
                .Include(c => c.AttributeValues).ThenInclude(v => v.Definition)
                .ToListAsync(ct);
            return Results.Ok(new PagedResult<CharacterDto>
            {
                Items = characters.Select(MapToCharacterDto).ToList(), Page = pageNumber, PageSize = size, TotalCount = total
            });
        });

        charsGroup.MapGet("/{projectId:guid}/characters", async (
            Guid projectId,
            string? query,
            string? role,
            string? status,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var q = db.Characters
                .AsNoTrackingWithIdentityResolution()
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(query))
            {
                var term = query.Trim();
                q = q.Where(c => c.Name.Contains(term) || (c.Description != null && c.Description.Contains(term)));
            }

            if (!string.IsNullOrWhiteSpace(role))
            {
                q = q.Where(c => c.Role == role.Trim());
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                q = q.Where(c => c.Status == status.Trim());
            }

            var characters = await q.OrderByDescending(c => c.Name).ToListAsync();

            var dtos = characters.Select(c => MapToCharacterDto(c)).ToList();
            return Results.Ok(dtos);
        });

        // Create character
        charsGroup.MapPost("/{projectId:guid}/characters", async (
            Guid projectId,
            CreateCharacterRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.Problem("Tên nhân vật không được để trống.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // Validate initial attributes if provided
            var availableDefs = await db.CharacterAttributeDefinitions
                .Where(d => d.ProjectId == null || d.ProjectId == projectId)
                .ToDictionaryAsync(d => d.Code, d => d, StringComparer.OrdinalIgnoreCase);

            var attributeValuesToInsert = new List<CharacterAttributeValue>();
            if (req.InitialAttributes != null && req.InitialAttributes.Count > 0)
            {
                foreach (var (code, val) in req.InitialAttributes)
                {
                    if (!availableDefs.TryGetValue(code, out var def))
                    {
                        return Results.Problem($"Thuộc tính '{code}' không tồn tại trong hệ thống.", statusCode: StatusCodes.Status422UnprocessableEntity);
                    }

                    var valErr = ValidateAttributeValue(def, val);
                    if (valErr != null)
                    {
                        return Results.Problem(valErr, statusCode: StatusCodes.Status422UnprocessableEntity);
                    }

                    attributeValuesToInsert.Add(new CharacterAttributeValue
                    {
                        DefinitionId = def.Id,
                        Definition = def,
                        ValueJson = val,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
            }

            var character = new Character
            {
                ProjectId = projectId,
                Name = req.Name.Trim(),
                Role = string.IsNullOrWhiteSpace(req.Role) ? "Protagonist" : req.Role.Trim(),
                Description = req.Description?.Trim(),
                Status = "Active"
            };

            foreach (var av in attributeValuesToInsert)
            {
                av.CharacterId = character.Id;
                character.AttributeValues.Add(av);
            }

            var defaultVariant = new CharacterVariant
            {
                CharacterId = character.Id,
                Name = "Default",
                Description = "Biến thể mặc định ban đầu",
                IsDefault = true,
                Status = "Active"
            };
            character.Variants.Add(defaultVariant);

            // Create initial revision #1
            var initialSnapshot = CreateCanonicalSnapshot(character, db);
            var rev1 = new CharacterRevision
            {
                CharacterId = character.Id,
                VariantId = defaultVariant.Id,
                RevisionNumber = 1,
                SnapshotJson = initialSnapshot,
                ActorUserId = userId,
                ChangeSummary = "Khởi tạo nhân vật ban đầu"
            };
            character.Revisions.Add(rev1);

            db.Characters.Add(character);
            await db.SaveChangesAsync();

            // Reload for complete mapping
            await db.Entry(character).Collection(c => c.AttributeValues).Query().Include(av => av.Definition).LoadAsync();

            return Results.Created($"/api/v1/projects/{projectId}/characters/{character.Id}", MapToCharacterDto(character));
        });

        // Get single character detail
        charsGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var character = await db.Characters
                .AsNoTrackingWithIdentityResolution()
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            return Results.Ok(MapToCharacterDto(character));
        });

        // Update character core info (Name, Role, Description) with RowVersion check & Revision creation
        charsGroup.MapPut("/{projectId:guid}/characters/{characterId:guid}", async (
            Guid projectId,
            Guid characterId,
            UpdateCharacterRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            // Optimistic concurrency check
            if (string.IsNullOrWhiteSpace(req.Version))
                return Results.BadRequest(new { error = "Cần phiên bản dữ liệu hiện tại để lưu thay đổi." });
            if (character.RowVersion != null && !string.IsNullOrWhiteSpace(req.Version))
            {
                byte[] clientBytes;
                try { clientBytes = Convert.FromBase64String(req.Version); }
                catch (FormatException) { return Results.BadRequest(new { error = "Mã RowVersion không hợp lệ." }); }
                if (!character.RowVersion.SequenceEqual(clientBytes))
                {
                    return Results.Problem("Dữ liệu nhân vật đã bị thay đổi bởi phiên làm việc khác.", statusCode: StatusCodes.Status409Conflict);
                }
                db.Entry(character).OriginalValues[nameof(Character.RowVersion)] = clientBytes;
            }

            character.Name = (req.Name ?? string.Empty).Trim();
            character.Role = (req.Role ?? string.Empty).Trim();
            character.Description = req.Description?.Trim();

            // Append revision without loading full historical Revisions table (eliminates Cartesian explosion)
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);
            var snapshot = CreateCanonicalSnapshot(character, db);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                RevisionNumber = nextRev,
                SnapshotJson = snapshot,
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = $"Cập nhật thông tin cơ bản: {character.Name} ({character.Role})"
            });

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem("Xung đột đồng thời khi cập nhật nhân vật (DbUpdateConcurrencyException).", statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(MapToCharacterDto(character));
        });

        // Archive character
        charsGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/archive", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            character.Status = "Archived";
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = "Lưu trữ nhân vật (Archived)"
            });

            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Nhân vật '{character.Name}' đã được chuyển sang trạng thái lưu trữ." });
        });

        // Restore character
        charsGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/restore", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            character.Status = "Active";
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = "Khôi phục nhân vật (Active)"
            });

            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Nhân vật '{character.Name}' đã được khôi phục thành công." });
        });

        // =========================================================================
        // 3. BASE DYNAMIC ATTRIBUTES MANAGEMENT & VALIDATION (UNIT-004)
        // =========================================================================

        // Get base attributes
        charsGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}/attributes", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var character = await db.Characters
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var dtos = character.AttributeValues.Select(av => new CharacterAttributeValueDto(
                av.DefinitionId,
                av.Definition.Code,
                av.Definition.DisplayName,
                av.Definition.DataType,
                av.ValueJson,
                av.Definition.IsIdentityCritical,
                false
            )).ToList();

            return Results.Ok(dtos);
        });

        // Update/Set base attributes with validation (UNIT-004)
        charsGroup.MapPut("/{projectId:guid}/characters/{characterId:guid}/attributes", async (
            Guid projectId,
            Guid characterId,
            SetCharacterAttributesRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var availableDefs = await db.CharacterAttributeDefinitions
                .Where(d => d.ProjectId == null || d.ProjectId == projectId)
                .ToDictionaryAsync(d => d.Code, d => d, StringComparer.OrdinalIgnoreCase);

            // Validation pass first (UNIT-004)
            foreach (var (code, val) in req.Attributes)
            {
                if (!availableDefs.TryGetValue(code, out var def))
                {
                    return Results.Problem($"Thuộc tính '{code}' không tồn tại hoặc không được hỗ trợ trong dự án.", statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                var err = ValidateAttributeValue(def, val);
                if (err != null)
                {
                    return Results.Problem(err, statusCode: StatusCodes.Status422UnprocessableEntity);
                }
            }

            // Apply updates
            foreach (var (code, val) in req.Attributes)
            {
                var def = availableDefs[code];
                var existing = character.AttributeValues.FirstOrDefault(av => av.DefinitionId == def.Id);
                if (existing != null)
                {
                    existing.ValueJson = val;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    var newAv = new CharacterAttributeValue
                    {
                        CharacterId = character.Id,
                        DefinitionId = def.Id,
                        Definition = def,
                        ValueJson = val,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    character.AttributeValues.Add(newAv);
                    db.CharacterAttributeValues.Add(newAv);
                }
            }

            // Append revision
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = $"Cập nhật thuộc tính gốc ({req.Attributes.Count} thuộc tính)"
            });

            await db.SaveChangesAsync();

            // Return updated attribute list
            await db.Entry(character).Collection(c => c.AttributeValues).Query().Include(av => av.Definition).LoadAsync();
            var dtos = character.AttributeValues.Select(av => new CharacterAttributeValueDto(
                av.DefinitionId,
                av.Definition.Code,
                av.Definition.DisplayName,
                av.Definition.DataType,
                av.ValueJson,
                av.Definition.IsIdentityCritical,
                false
            )).ToList();

            return Results.Ok(dtos);
        });

        // =========================================================================
        // 4. VARIANTS & OVERRIDES (UNIT-007)
        // =========================================================================

        // List variants
        charsGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}/variants", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var character = await db.Characters
                .AsNoTrackingWithIdentityResolution()
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var dtos = character.Variants.Select(v => MapToVariantDto(v, character)).ToList();
            return Results.Ok(dtos);
        });

        // Create variant
        charsGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/variants", async (
            Guid projectId,
            Guid characterId,
            CreateVariantRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.Problem("Tên biến thể không được để trống.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var availableDefs = await db.CharacterAttributeDefinitions
                .Where(d => d.ProjectId == null || d.ProjectId == projectId)
                .ToDictionaryAsync(d => d.Code, d => d, StringComparer.OrdinalIgnoreCase);

            var overrideValues = new List<VariantAttributeValue>();
            if (req.AttributeOverrides != null && req.AttributeOverrides.Count > 0)
            {
                foreach (var (code, val) in req.AttributeOverrides)
                {
                    if (!availableDefs.TryGetValue(code, out var def))
                    {
                        return Results.Problem($"Thuộc tính ghi đè '{code}' không tồn tại.", statusCode: StatusCodes.Status422UnprocessableEntity);
                    }

                    var err = ValidateAttributeValue(def, val);
                    if (err != null) return Results.Problem(err, statusCode: StatusCodes.Status422UnprocessableEntity);

                    overrideValues.Add(new VariantAttributeValue
                    {
                        DefinitionId = def.Id,
                        Definition = def,
                        ValueJson = val
                    });
                }
            }

            // Single default invariant (UNIT-007)
            if (req.IsDefault)
            {
                foreach (var v in character.Variants)
                {
                    v.IsDefault = false;
                }
            }

            var newVariant = new CharacterVariant
            {
                CharacterId = character.Id,
                Name = req.Name.Trim(),
                Description = req.Description?.Trim(),
                IsDefault = req.IsDefault,
                Status = "Active"
            };

            foreach (var ov in overrideValues)
            {
                ov.VariantId = newVariant.Id;
                newVariant.AttributeValues.Add(ov);
            }

            character.Variants.Add(newVariant);
            db.CharacterVariants.Add(newVariant);

            // Append revision
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                VariantId = newVariant.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = $"Thêm biến thể mới '{newVariant.Name}' (Default={newVariant.IsDefault})"
            });

            await db.SaveChangesAsync();

            await db.Entry(newVariant).Collection(v => v.AttributeValues).Query().Include(vav => vav.Definition).LoadAsync();
            return Results.Created($"/api/v1/projects/{projectId}/characters/{characterId}/variants/{newVariant.Id}", MapToVariantDto(newVariant, character));
        });

        // Set default variant (UNIT-007: exactly one active default)
        charsGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/variants/{variantId:guid}/set-default", async (
            Guid projectId,
            Guid characterId,
            Guid variantId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var targetVariant = character.Variants.FirstOrDefault(v => v.Id == variantId);
            if (targetVariant is null) return Results.NotFound();

            // Set target as only default
            foreach (var v in character.Variants)
            {
                v.IsDefault = (v.Id == variantId);
            }

            // Append revision
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                VariantId = targetVariant.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = $"Chuyển biến thể mặc định sang '{targetVariant.Name}'"
            });

            await db.SaveChangesAsync();

            return Results.Ok(MapToVariantDto(targetVariant, character));
        });

        // Set variant attribute overrides
        charsGroup.MapPut("/{projectId:guid}/characters/{characterId:guid}/variants/{variantId:guid}/attributes", async (
            Guid projectId,
            Guid characterId,
            Guid variantId,
            SetVariantAttributesRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var targetVariant = character.Variants.FirstOrDefault(v => v.Id == variantId);
            if (targetVariant is null) return Results.NotFound();

            var availableDefs = await db.CharacterAttributeDefinitions
                .Where(d => d.ProjectId == null || d.ProjectId == projectId)
                .ToDictionaryAsync(d => d.Code, d => d, StringComparer.OrdinalIgnoreCase);

            // Validation pass first (UNIT-004)
            foreach (var (code, val) in req.AttributeOverrides)
            {
                if (!availableDefs.TryGetValue(code, out var def))
                {
                    return Results.Problem($"Thuộc tính '{code}' không tồn tại.", statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                var err = ValidateAttributeValue(def, val);
                if (err != null) return Results.Problem(err, statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // Apply overrides
            foreach (var (code, val) in req.AttributeOverrides)
            {
                var def = availableDefs[code];
                var existing = targetVariant.AttributeValues.FirstOrDefault(vav => vav.DefinitionId == def.Id);
                if (existing != null)
                {
                    existing.ValueJson = val;
                }
                else
                {
                    var newVav = new VariantAttributeValue
                    {
                        VariantId = targetVariant.Id,
                        DefinitionId = def.Id,
                        Definition = def,
                        ValueJson = val
                    };
                    targetVariant.AttributeValues.Add(newVav);
                    db.VariantAttributeValues.Add(newVav);
                }
            }

            // Append revision
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                VariantId = targetVariant.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = $"Cập nhật thuộc tính ghi đè cho biến thể '{targetVariant.Name}'"
            });

            await db.SaveChangesAsync();

            await db.Entry(targetVariant).Collection(v => v.AttributeValues).Query().Include(vav => vav.Definition).LoadAsync();
            return Results.Ok(MapToVariantDto(targetVariant, character));
        });

        // =========================================================================
        // 5. CHARACTER REVISIONS & STRUCTURED DIFF (UNIT-006)
        // =========================================================================

        // List revisions
        charsGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}/revisions", async (
            Guid projectId,
            Guid characterId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var revisions = await db.CharacterRevisions
                .AsNoTracking()
                .Where(r => r.CharacterId == characterId && r.Character.ProjectId == projectId)
                .OrderByDescending(r => r.RevisionNumber)
                .Select(r => new CharacterRevisionDto(
                    r.Id,
                    r.CharacterId,
                    r.VariantId,
                    r.RevisionNumber,
                    r.SnapshotJson,
                    r.PreviousRevisionId,
                    r.ActorUserId,
                    r.CreatedAt,
                    r.ChangeSummary
                ))
                .ToListAsync();

            return Results.Ok(revisions);
        });

        // Get single revision snapshot
        charsGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}/revisions/{revisionId:guid}", async (
            Guid projectId,
            Guid characterId,
            Guid revisionId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var revision = await db.CharacterRevisions
                .AsNoTracking()
                .Where(r => r.Id == revisionId && r.CharacterId == characterId && r.Character.ProjectId == projectId)
                .Select(r => new CharacterRevisionDto(
                    r.Id,
                    r.CharacterId,
                    r.VariantId,
                    r.RevisionNumber,
                    r.SnapshotJson,
                    r.PreviousRevisionId,
                    r.ActorUserId,
                    r.CreatedAt,
                    r.ChangeSummary
                ))
                .FirstOrDefaultAsync();

            if (revision is null) return Results.NotFound();

            return Results.Ok(revision);
        });

        // Structured Diff between two revisions (UNIT-006)
        charsGroup.MapGet("/{projectId:guid}/characters/{characterId:guid}/revisions/diff", async (
            Guid projectId,
            Guid characterId,
            Guid from,
            Guid to,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var fromRev = await db.CharacterRevisions
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == from && r.CharacterId == characterId && r.Character.ProjectId == projectId);
            var toRev = await db.CharacterRevisions
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == to && r.CharacterId == characterId && r.Character.ProjectId == projectId);

            if (fromRev is null || toRev is null) return Results.NotFound();

            var diffs = ComputeSnapshotDiff(fromRev.SnapshotJson, toRev.SnapshotJson);

            return Results.Ok(new CharacterRevisionDiffDto(
                fromRev.Id,
                toRev.Id,
                fromRev.RevisionNumber,
                toRev.RevisionNumber,
                diffs
            ));
        });

        // =========================================================================
        // 6. CHARACTER TEMPLATES (UNIT-005)
        // =========================================================================

        // List templates
        charsGroup.MapGet("/{projectId:guid}/character-templates", async (
            Guid projectId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var templates = await db.CharacterTemplates
                .AsNoTracking()
                .Where(t => (t.ProjectId == null || t.ProjectId == projectId) && !t.IsArchived)
                .Include(t => t.TemplateAttributes)
                    .ThenInclude(ta => ta.Definition)
                .OrderBy(t => t.Name)
                .Select(t => new CharacterTemplateDto(
                    t.Id,
                    t.ProjectId,
                    t.Name,
                    t.Description,
                    t.CreatedAt,
                    t.IsArchived,
                    t.TemplateAttributes.Select(ta => new TemplateAttributeItemDto(
                        ta.DefinitionId,
                        ta.Definition.Code,
                        ta.Definition.DisplayName,
                        ta.Definition.DataType,
                        ta.DefaultValueJson
                    )).ToList()
                ))
                .ToListAsync();

            return Results.Ok(templates);
        });

        // Create template
        charsGroup.MapPost("/{projectId:guid}/character-templates", async (
            Guid projectId,
            CreateTemplateRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Owner", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.Problem("Tên mẫu nhân vật không được để trống.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var template = new CharacterTemplate
            {
                ProjectId = projectId,
                Name = req.Name.Trim(),
                Description = req.Description?.Trim(),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsArchived = false
            };

            foreach (var attr in req.Attributes)
            {
                template.TemplateAttributes.Add(new TemplateAttributeDefinition
                {
                    TemplateId = template.Id,
                    DefinitionId = attr.DefinitionId,
                    DefaultValueJson = attr.DefaultValueJson
                });
            }

            db.CharacterTemplates.Add(template);
            await db.SaveChangesAsync();

            await db.Entry(template).Collection(t => t.TemplateAttributes).Query().Include(ta => ta.Definition).LoadAsync();

            return Results.Created($"/api/v1/projects/{projectId}/character-templates/{template.Id}", new CharacterTemplateDto(
                template.Id,
                template.ProjectId,
                template.Name,
                template.Description,
                template.CreatedAt,
                template.IsArchived,
                template.TemplateAttributes.Select(ta => new TemplateAttributeItemDto(
                    ta.DefinitionId,
                    ta.Definition.Code,
                    ta.Definition.DisplayName,
                    ta.Definition.DataType,
                    ta.DefaultValueJson
                )).ToList()
            ));
        });

        // Preview applying template
        charsGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/apply-template/preview", async (
            Guid projectId,
            Guid characterId,
            ApplyTemplateRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var character = await db.Characters
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var template = await db.CharacterTemplates
                .Where(t => t.Id == req.TemplateId && (t.ProjectId == null || t.ProjectId == projectId))
                .Include(t => t.TemplateAttributes)
                    .ThenInclude(ta => ta.Definition)
                .FirstOrDefaultAsync();

            if (template is null) return Results.NotFound();

            var previews = new List<TemplateApplyPreviewItemDto>();
            foreach (var ta in template.TemplateAttributes)
            {
                var cur = character.AttributeValues.FirstOrDefault(av => av.DefinitionId == ta.DefinitionId);
                var curVal = cur?.ValueJson;
                var newVal = req.OverwriteExisting ? ta.DefaultValueJson : (curVal ?? ta.DefaultValueJson);
                var willChange = curVal != newVal;

                previews.Add(new TemplateApplyPreviewItemDto(
                    ta.Definition.Code,
                    ta.Definition.DisplayName,
                    curVal,
                    newVal,
                    willChange
                ));
            }

            return Results.Ok(new ApplyTemplatePreviewDto(
                character.Id,
                template.Id,
                template.Name,
                previews
            ));
        });

        // Commit applying template (UNIT-005: creates new revision, previous snapshots untouched)
        charsGroup.MapPost("/{projectId:guid}/characters/{characterId:guid}/apply-template", async (
            Guid projectId,
            Guid characterId,
            ApplyTemplateRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var character = await db.Characters
                .AsSplitQuery()
                .Where(c => c.ProjectId == projectId && c.Id == characterId)
                .Include(c => c.AttributeValues)
                    .ThenInclude(av => av.Definition)
                .Include(c => c.Variants)
                    .ThenInclude(v => v.AttributeValues)
                        .ThenInclude(vav => vav.Definition)
                .FirstOrDefaultAsync();

            if (character is null) return Results.NotFound();

            var template = await db.CharacterTemplates
                .Where(t => t.Id == req.TemplateId && (t.ProjectId == null || t.ProjectId == projectId))
                .Include(t => t.TemplateAttributes)
                    .ThenInclude(ta => ta.Definition)
                .FirstOrDefaultAsync();

            if (template is null) return Results.NotFound();

            foreach (var ta in template.TemplateAttributes)
            {
                var existing = character.AttributeValues.FirstOrDefault(av => av.DefinitionId == ta.DefinitionId);
                if (existing != null)
                {
                    if (req.OverwriteExisting)
                    {
                        existing.ValueJson = ta.DefaultValueJson;
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                    }
                }
                else
                {
                    var newAv = new CharacterAttributeValue
                    {
                        CharacterId = character.Id,
                        DefinitionId = ta.DefinitionId,
                        Definition = ta.Definition,
                        ValueJson = ta.DefaultValueJson,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    character.AttributeValues.Add(newAv);
                    db.CharacterAttributeValues.Add(newAv);
                }
            }

            // Append revision
            var (nextRev, prevRevId) = await GetNextRevisionMetaAsync(db, character.Id);

            db.CharacterRevisions.Add(new CharacterRevision
            {
                CharacterId = character.Id,
                RevisionNumber = nextRev,
                SnapshotJson = CreateCanonicalSnapshot(character, db),
                PreviousRevisionId = prevRevId,
                ActorUserId = userId,
                ChangeSummary = $"Áp dụng mẫu nhân vật '{template.Name}' (Overwrite={req.OverwriteExisting})"
            });

            await db.SaveChangesAsync();

            return Results.Ok(MapToCharacterDto(character));
        });

        // =========================================================================
        // 7. CHARACTER RELATIONSHIPS (UNIT-008)
        // =========================================================================

        // List relationships in project
        charsGroup.MapGet("/{projectId:guid}/relationships", async (
            Guid projectId,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Viewer", allowArchived: true);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status == ProjectAccessStatus.NotFound
                    ? Results.NotFound()
                    : Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden);
            }

            var rels = await db.CharacterRelationships
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .Include(r => r.SourceCharacter)
                .Include(r => r.TargetCharacter)
                .OrderBy(r => r.Type)
                .Select(r => new CharacterRelationshipDto(
                    r.Id,
                    r.ProjectId,
                    r.SourceCharacterId,
                    r.SourceCharacter.Name,
                    r.TargetCharacterId,
                    r.TargetCharacter.Name,
                    r.Type,
                    r.Notes,
                    r.RowVersion != null ? Convert.ToBase64String(r.RowVersion) : null
                ))
                .ToListAsync();

            return Results.Ok(rels);
        });

        // Create relationship (UNIT-008: same character or cross-project rejected)
        charsGroup.MapPost("/{projectId:guid}/relationships", async (
            Guid projectId,
            CreateCharacterRelationshipRequest req,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            // Invariant: Source != Target (UNIT-008)
            if (req.SourceCharacterId == req.TargetCharacterId)
            {
                return Results.Problem("Không thể tạo mối quan hệ giữa một nhân vật với chính nó (Source == Target).", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            // Invariant: Both characters must belong to the exact same project
            var source = await db.Characters.FirstOrDefaultAsync(c => c.Id == req.SourceCharacterId && c.ProjectId == projectId);
            var target = await db.Characters.FirstOrDefaultAsync(c => c.Id == req.TargetCharacterId && c.ProjectId == projectId);

            if (source is null || target is null)
            {
                return Results.Problem("Cả hai nhân vật trong mối quan hệ phải thuộc cùng một dự án.", statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var relType = string.IsNullOrWhiteSpace(req.Type) ? "Ally" : req.Type.Trim();

            // Duplicate check
            var duplicate = await db.CharacterRelationships
                .AnyAsync(r => r.ProjectId == projectId && r.SourceCharacterId == req.SourceCharacterId && r.TargetCharacterId == req.TargetCharacterId && r.Type == relType);
            if (duplicate)
            {
                return Results.Problem($"Mối quan hệ loại '{relType}' giữa hai nhân vật này đã tồn tại.", statusCode: StatusCodes.Status409Conflict);
            }

            var rel = new CharacterRelationship
            {
                ProjectId = projectId,
                SourceCharacterId = req.SourceCharacterId,
                TargetCharacterId = req.TargetCharacterId,
                Type = relType,
                Notes = req.Notes?.Trim()
            };

            db.CharacterRelationships.Add(rel);
            await db.SaveChangesAsync();

            return Results.Created($"/api/v1/projects/{projectId}/relationships/{rel.Id}", new CharacterRelationshipDto(
                rel.Id,
                rel.ProjectId,
                rel.SourceCharacterId,
                source.Name,
                rel.TargetCharacterId,
                target.Name,
                rel.Type,
                rel.Notes,
                rel.RowVersion != null ? Convert.ToBase64String(rel.RowVersion) : null
            ));
        });

        // Delete relationship
        charsGroup.MapDelete("/{projectId:guid}/relationships/{id:guid}", async (
            Guid projectId,
            Guid id,
            ClaimsPrincipal principal,
            IProjectAuthorizationService auth,
            AppDbContext db) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null) return Results.Unauthorized();

            var authResult = await auth.AuthorizeAsync(userId, projectId, "Designer", allowArchived: false);
            if (!authResult.IsAuthorized)
            {
                return authResult.Status switch
                {
                    ProjectAccessStatus.NotFound => Results.NotFound(),
                    ProjectAccessStatus.Archived => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status422UnprocessableEntity),
                    _ => Results.Problem(authResult.ErrorMessage, statusCode: StatusCodes.Status403Forbidden)
                };
            }

            var rel = await db.CharacterRelationships.FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == projectId);
            if (rel is null) return Results.NotFound();

            db.CharacterRelationships.Remove(rel);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });

        return group;
    }

    // =========================================================================
    // HELPER METHODS: DTO MAPPERS & VALIDATORS
    // =========================================================================

    private static async Task<(int NextRev, Guid? PrevRevId)> GetNextRevisionMetaAsync(AppDbContext db, Guid characterId, CancellationToken ct = default)
    {
        var latest = await db.CharacterRevisions.AsNoTracking()
            .Where(r => r.CharacterId == characterId)
            .OrderByDescending(r => r.RevisionNumber)
            .Select(r => new { r.Id, r.RevisionNumber })
            .FirstOrDefaultAsync(ct);
        return latest is null ? (1, null) : (latest.RevisionNumber + 1, latest.Id);
    }

    private static CharacterDto MapToCharacterDto(Character c)
    {
        var attrDtos = c.AttributeValues.Select(av => new CharacterAttributeValueDto(
            av.DefinitionId,
            av.Definition?.Code ?? string.Empty,
            av.Definition?.DisplayName ?? string.Empty,
            av.Definition?.DataType ?? "Text",
            av.ValueJson,
            av.Definition?.IsIdentityCritical ?? false,
            false
        )).ToList();

        var variantDtos = c.Variants.Select(v => MapToVariantDto(v, c)).ToList();

        return new CharacterDto(
            c.Id,
            c.ProjectId,
            c.Name,
            c.Role,
            c.Description,
            c.Status,
            attrDtos,
            variantDtos,
            c.RowVersion != null ? Convert.ToBase64String(c.RowVersion) : null
        );
    }

    private static CharacterVariantDto MapToVariantDto(CharacterVariant v, Character character)
    {
        // Compute effective attributes: Base character attributes merged with variant overrides
        var baseDict = character.AttributeValues.ToDictionary(av => av.DefinitionId, av => av);
        var overrideDict = v.AttributeValues.ToDictionary(vav => vav.DefinitionId, vav => vav);

        var allDefIds = baseDict.Keys.Union(overrideDict.Keys);
        var effectiveList = new List<CharacterAttributeValueDto>();

        foreach (var defId in allDefIds)
        {
            var isOverridden = overrideDict.TryGetValue(defId, out var ov);
            var baseAv = baseDict.GetValueOrDefault(defId);

            var def = ov?.Definition ?? baseAv?.Definition;
            if (def == null) continue;

            var effectiveVal = isOverridden ? ov!.ValueJson : baseAv?.ValueJson;

            effectiveList.Add(new CharacterAttributeValueDto(
                def.Id,
                def.Code,
                def.DisplayName,
                def.DataType,
                effectiveVal,
                def.IsIdentityCritical,
                isOverridden
            ));
        }

        return new CharacterVariantDto(
            v.Id,
            v.CharacterId,
            v.Name,
            v.Description,
            v.IsDefault,
            v.Status,
            v.RowVersion != null ? Convert.ToBase64String(v.RowVersion) : null,
            effectiveList
        );
    }

    private static string? ValidateAttributeValue(CharacterAttributeDefinition def, string? val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            if (def.IsRequired)
            {
                return $"Thuộc tính bắt buộc '{def.Code}' ({def.DisplayName}) không được để trống.";
            }
            return null; // Null/empty allowed if not required
        }

        var trimmed = val.Trim();

        switch (def.DataType.ToLowerInvariant())
        {
            case "number":
                if (!double.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    return $"Giá trị '{val}' của thuộc tính '{def.Code}' không phải là định dạng số (Number) hợp lệ.";
                }
                break;

            case "boolean":
                if (!string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return $"Giá trị '{val}' của thuộc tính '{def.Code}' không phải là kiểu logic (Boolean). Cho phép: 'true' hoặc 'false'.";
                }
                break;

            case "singleselect":
                if (!string.IsNullOrWhiteSpace(def.AllowedValuesJson))
                {
                    try
                    {
                        var allowed = JsonSerializer.Deserialize<List<string>>(def.AllowedValuesJson, JsonOpts);
                        if (allowed != null && allowed.Count > 0 && !allowed.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                        {
                            return $"Giá trị '{val}' không nằm trong danh sách cho phép của thuộc tính '{def.Code}' [{string.Join(", ", allowed)}].";
                        }
                    }
                    catch
                    {
                        // Ignore JSON parsing if allowedValues is plain text
                    }
                }
                break;

            case "multiselect":
                if (!string.IsNullOrWhiteSpace(def.AllowedValuesJson))
                {
                    try
                    {
                        var allowed = JsonSerializer.Deserialize<List<string>>(def.AllowedValuesJson, JsonOpts);
                        if (allowed != null && allowed.Count > 0)
                        {
                            var items = trimmed.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var item in items)
                            {
                                if (!allowed.Contains(item, StringComparer.OrdinalIgnoreCase))
                                {
                                    return $"Giá trị '{item}' không nằm trong danh sách cho phép của thuộc tính '{def.Code}' [{string.Join(", ", allowed)}].";
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore parse error
                    }
                }
                break;

            case "text":
            default:
                // Text is always valid
                break;
        }

        return null;
    }

    private static string CreateCanonicalSnapshot(Character character, AppDbContext? db = null)
    {
        Dictionary<Guid, string>? resolvedCodes = null;
        var missingDefIds = character.AttributeValues
            .Where(av => av.Definition == null)
            .Select(av => av.DefinitionId)
            .Concat(character.Variants.SelectMany(v => v.AttributeValues).Where(vav => vav.Definition == null).Select(vav => vav.DefinitionId))
            .Distinct()
            .ToList();

        if (missingDefIds.Count > 0 && db != null)
        {
            resolvedCodes = db.CharacterAttributeDefinitions
                .Where(d => missingDefIds.Contains(d.Id))
                .ToDictionary(d => d.Id, d => d.Code);
        }

        string? GetCode(Guid defId, CharacterAttributeDefinition? def)
        {
            if (def != null && !string.IsNullOrEmpty(def.Code)) return def.Code;
            if (resolvedCodes != null && resolvedCodes.TryGetValue(defId, out var code)) return code;
            return null;
        }

        var baseAttrs = character.AttributeValues
            .Select(av => new { Code = GetCode(av.DefinitionId, av.Definition), av.ValueJson })
            .Where(x => !string.IsNullOrEmpty(x.Code))
            .OrderBy(x => x.Code)
            .ToDictionary(x => x.Code!, x => x.ValueJson);

        var variants = character.Variants
            .OrderBy(v => v.Name)
            .Select(v => new
            {
                v.Id,
                v.Name,
                v.Description,
                v.IsDefault,
                v.Status,
                Overrides = v.AttributeValues
                    .Select(vav => new { Code = GetCode(vav.DefinitionId, vav.Definition), vav.ValueJson })
                    .Where(x => !string.IsNullOrEmpty(x.Code))
                    .OrderBy(x => x.Code)
                    .ToDictionary(x => x.Code!, x => x.ValueJson)
            }).ToList();

        var snapshotObj = new
        {
            CharacterId = character.Id,
            character.ProjectId,
            character.Name,
            character.Role,
            character.Description,
            character.Status,
            BaseAttributes = baseAttrs,
            Variants = variants,
            SnapshotAt = DateTimeOffset.UtcNow
        };

        return JsonSerializer.Serialize(snapshotObj, JsonOpts);
    }

    private static IReadOnlyList<RevisionFieldDiff> ComputeSnapshotDiff(string jsonFrom, string jsonTo)
    {
        var diffs = new List<RevisionFieldDiff>();

        using var docFrom = JsonDocument.Parse(jsonFrom);
        using var docTo = JsonDocument.Parse(jsonTo);

        var rootFrom = docFrom.RootElement;
        var rootTo = docTo.RootElement;

        // Compare simple fields: name, role, description, status
        CompareProperty(rootFrom, rootTo, "name", diffs);
        CompareProperty(rootFrom, rootTo, "role", diffs);
        CompareProperty(rootFrom, rootTo, "description", diffs);
        CompareProperty(rootFrom, rootTo, "status", diffs);

        // Compare BaseAttributes
        var baseFrom = rootFrom.TryGetProperty("baseAttributes", out var bf) ? bf : default;
        var baseTo = rootTo.TryGetProperty("baseAttributes", out var bt) ? bt : default;

        var allKeys = new HashSet<string>();
        if (baseFrom.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in baseFrom.EnumerateObject()) allKeys.Add(prop.Name);
        }
        if (baseTo.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in baseTo.EnumerateObject()) allKeys.Add(prop.Name);
        }

        foreach (var key in allKeys)
        {
            var oldVal = baseFrom.ValueKind == JsonValueKind.Object && baseFrom.TryGetProperty(key, out var ov) ? ov.GetString() : null;
            var newVal = baseTo.ValueKind == JsonValueKind.Object && baseTo.TryGetProperty(key, out var nv) ? nv.GetString() : null;

            if (oldVal is null && newVal is not null)
            {
                diffs.Add(new RevisionFieldDiff($"attributes.{key}", null, newVal, "Added"));
            }
            else if (oldVal is not null && newVal is null)
            {
                diffs.Add(new RevisionFieldDiff($"attributes.{key}", oldVal, null, "Removed"));
            }
            else if (oldVal != newVal)
            {
                diffs.Add(new RevisionFieldDiff($"attributes.{key}", oldVal, newVal, "Modified"));
            }
        }

        return diffs;
    }

    private static void CompareProperty(JsonElement from, JsonElement to, string propName, List<RevisionFieldDiff> diffs)
    {
        var oldVal = from.TryGetProperty(propName, out var op) ? op.GetString() : null;
        var newVal = to.TryGetProperty(propName, out var np) ? np.GetString() : null;

        if (oldVal != newVal)
        {
            diffs.Add(new RevisionFieldDiff(propName, oldVal, newVal, "Modified"));
        }
    }
}
