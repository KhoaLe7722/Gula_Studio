using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using CharacterConceptAI.Shared.Contracts.Auth;
using CharacterConceptAI.Shared.Contracts.Characters;
using CharacterConceptAI.Shared.Contracts.Projects;

namespace CharacterConceptAI.Tests;

public class CharacterAdvanced09CTests : IClassFixture<TestApplicationFactory>
{
    private readonly TestApplicationFactory _factory;

    public CharacterAdvanced09CTests(TestApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<(HttpClient Client, string UserId, string Email)> CreateAndLoginUserAsync(string displayName)
    {
        var adminClient = _factory.CreateClient();
        var adminLoginResp = await adminClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(
            Email: "admin@conceptai.local",
            Password: "Admin@Secured2026"
        ));
        Assert.Equal(HttpStatusCode.OK, adminLoginResp.StatusCode);
        var adminAuth = await adminLoginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminAuth!["accessToken"].ToString());

        var email = $"user_{Guid.NewGuid():N}@conceptai.local";
        var password = "User@Secured2026";
        var createResp = await adminClient.PostAsJsonAsync("/api/v1/admin/accounts", new CreateAccountRequest(
            Email: email,
            DisplayName: displayName,
            Password: password,
            Roles: ["Designer"]
        ));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var account = await createResp.Content.ReadFromJsonAsync<AccountDto>();

        var userClient = _factory.CreateClient();
        var userLoginResp = await userClient.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, userLoginResp.StatusCode);
        var userAuth = await userLoginResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userAuth!["accessToken"].ToString());

        return (userClient, account!.Id, email);
    }

    [Fact]
    public async Task UNIT004_UnknownOrInvalidDynamicAttribute_Returns422_NoWrite()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Attribute Tester");

        // 1. Create project
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Attribute Project", "Anime", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        // 2. Create typed attribute definitions (Number, Boolean, SingleSelect)
        var numDefResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("power_level", "Mức Năng Lượng", "Number"));
        Assert.Equal(HttpStatusCode.Created, numDefResp.StatusCode);

        var boolDefResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("has_wings", "Có Cánh", "Boolean"));
        Assert.Equal(HttpStatusCode.Created, boolDefResp.StatusCode);

        var selectDefResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("element", "Nguyên Tố", "SingleSelect", AllowedValuesJson: "[\"Fire\", \"Ice\", \"Lightning\"]"));
        Assert.Equal(HttpStatusCode.Created, selectDefResp.StatusCode);

        // 3. Create a character
        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters",
            new CreateCharacterRequest("Test Hero", "Protagonist", "Mô tả"));
        Assert.Equal(HttpStatusCode.Created, charResp.StatusCode);
        var hero = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // Case A: Unknown attribute code -> 422
        var invalidCodeResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{hero!.Id}/attributes",
            new SetCharacterAttributesRequest(new Dictionary<string, string?> { ["unknown_attr_xyz"] = "123" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidCodeResp.StatusCode);

        // Case B: Invalid Number format -> 422
        var invalidNumResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{hero.Id}/attributes",
            new SetCharacterAttributesRequest(new Dictionary<string, string?> { ["power_level"] = "not-a-number" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidNumResp.StatusCode);

        // Case C: Invalid Boolean format -> 422
        var invalidBoolResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{hero.Id}/attributes",
            new SetCharacterAttributesRequest(new Dictionary<string, string?> { ["has_wings"] = "maybe" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidBoolResp.StatusCode);

        // Case D: Invalid Option for SingleSelect -> 422
        var invalidSelectResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{hero.Id}/attributes",
            new SetCharacterAttributesRequest(new Dictionary<string, string?> { ["element"] = "Poison" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidSelectResp.StatusCode);

        // Case E: Valid values -> 200 OK
        var validResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{hero.Id}/attributes",
            new SetCharacterAttributesRequest(new Dictionary<string, string?>
            {
                ["power_level"] = "9000",
                ["has_wings"] = "true",
                ["element"] = "Fire"
            }));
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
        var updatedAttrs = await validResp.Content.ReadFromJsonAsync<List<CharacterAttributeValueDto>>();
        Assert.Equal(3, updatedAttrs!.Count);
    }

    [Fact]
    public async Task UNIT006_CharacterRevision_AndStructuredDiff_Accurate()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Diff Tester");

        // 1. Create project & attribute definition
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Revision Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("weapon", "Vũ Khí", "Text"));

        // 2. Create character (generates Revision 1)
        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters",
            new CreateCharacterRequest("Aria", "Mage", "Initial mage description",
                InitialAttributes: new Dictionary<string, string?> { ["weapon"] = "Wooden Staff" }));
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // 3. Update character basic info (generates Revision 2)
        var updateResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character!.Id}",
            new UpdateCharacterRequest("Aria The Archmage", "Archmage", "Promoted to Archmage", character.RowVersion!));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updatedChar = await updateResp.Content.ReadFromJsonAsync<CharacterDto>();

        // 4. Update attribute (generates Revision 3)
        var attrResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/attributes",
            new SetCharacterAttributesRequest(new Dictionary<string, string?> { ["weapon"] = "Staff of Eternity" }));
        Assert.Equal(HttpStatusCode.OK, attrResp.StatusCode);

        // 5. Query revision history
        var revsResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/revisions");
        Assert.Equal(HttpStatusCode.OK, revsResp.StatusCode);
        var revisions = await revsResp.Content.ReadFromJsonAsync<List<CharacterRevisionDto>>();
        Assert.Equal(3, revisions!.Count);

        var rev1 = revisions.First(r => r.RevisionNumber == 1);
        var rev3 = revisions.First(r => r.RevisionNumber == 3);

        // 6. Compute diff between Revision 1 and Revision 3
        var diffResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/revisions/diff?from={rev1.Id}&to={rev3.Id}");
        Assert.Equal(HttpStatusCode.OK, diffResp.StatusCode);
        var diff = await diffResp.Content.ReadFromJsonAsync<CharacterRevisionDiffDto>();

        Assert.NotNull(diff);
        Assert.Equal(1, diff.FromRevisionNumber);
        Assert.Equal(3, diff.ToRevisionNumber);

        // Check accurate changes: name, role, description, and weapon attribute
        Assert.Contains(diff.Changes, c => c.FieldPath == "name" && c.OldValue == "Aria" && c.NewValue == "Aria The Archmage" && c.ChangeType == "Modified");
        Assert.Contains(diff.Changes, c => c.FieldPath == "role" && c.OldValue == "Mage" && c.NewValue == "Archmage" && c.ChangeType == "Modified");
        Assert.Contains(diff.Changes, c => c.FieldPath == "attributes.weapon" && c.OldValue == "Wooden Staff" && c.NewValue == "Staff of Eternity" && c.ChangeType == "Modified");
    }

    [Fact]
    public async Task UNIT007_Variants_DefaultSwitch_AndEffectiveAttributesOverride()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Variant Tester");

        // 1. Create project & attribute
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Variant Realm", "Novel", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("hair_color", "Màu Tóc", "Text"));
        await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("armor_type", "Loại Giáp", "Text"));

        // 2. Create character with base attributes
        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters",
            new CreateCharacterRequest("Valerie", "Knight", "Imperial Knight",
                InitialAttributes: new Dictionary<string, string?>
                {
                    ["hair_color"] = "Blonde",
                    ["armor_type"] = "Plate Armor"
                }));
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();
        var defaultVariant = character!.Variants.First(v => v.IsDefault);

        // 3. Create second variant with override on hair_color, but NOT armor_type
        var varResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/variants",
            new CreateVariantRequest("Shadow Valkyrie", "Dark form of Valerie", IsDefault: false,
                AttributeOverrides: new Dictionary<string, string?>
                {
                    ["hair_color"] = "Midnight Violet"
                }));
        Assert.Equal(HttpStatusCode.Created, varResp.StatusCode);
        var shadowVariant = await varResp.Content.ReadFromJsonAsync<CharacterVariantDto>();

        // Verify effective attributes: hair_color is overridden, armor_type inherited from base!
        Assert.NotNull(shadowVariant!.EffectiveAttributes);
        var hairAttr = shadowVariant.EffectiveAttributes.First(a => a.Code == "hair_color");
        var armorAttr = shadowVariant.EffectiveAttributes.First(a => a.Code == "armor_type");

        Assert.Equal("Midnight Violet", hairAttr.ValueJson);
        Assert.True(hairAttr.IsOverridden);
        Assert.Equal("Plate Armor", armorAttr.ValueJson);
        Assert.False(armorAttr.IsOverridden);

        // 4. Set "Shadow Valkyrie" as default variant
        var setDefaultResp = await client.PostAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/variants/{shadowVariant.Id}/set-default", null);
        Assert.Equal(HttpStatusCode.OK, setDefaultResp.StatusCode);

        // 5. Verify exactly one active default variant
        var getVariantsResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/variants");
        var variants = await getVariantsResp.Content.ReadFromJsonAsync<List<CharacterVariantDto>>();

        var activeDefaults = variants!.Where(v => v.IsDefault).ToList();
        Assert.Single(activeDefaults);
        Assert.Equal(shadowVariant.Id, activeDefaults[0].Id);

        var oldDefault = variants!.First(v => v.Id == defaultVariant.Id);
        Assert.False(oldDefault.IsDefault);
    }

    [Fact]
    public async Task UNIT008_CharacterRelationship_SameCharacter_AndCrossProject_RejectedWith422()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Relationship Tester");

        // 1. Create two projects
        var proj1Resp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Project Alpha", "Game", null));
        var proj1 = await proj1Resp.Content.ReadFromJsonAsync<ProjectDto>();

        var proj2Resp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Project Beta", "Game", null));
        var proj2 = await proj2Resp.Content.ReadFromJsonAsync<ProjectDto>();

        // 2. Create characters in Project 1 and Project 2
        var char1Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj1!.Id}/characters",
            new CreateCharacterRequest("Hero Alpha", "Protagonist", null));
        var heroAlpha = await char1Resp.Content.ReadFromJsonAsync<CharacterDto>();

        var char2Resp = await client.PostAsJsonAsync($"/api/v1/projects/{proj1.Id}/characters",
            new CreateCharacterRequest("Companion Alpha", "Support", null));
        var companionAlpha = await char2Resp.Content.ReadFromJsonAsync<CharacterDto>();

        var charBetaResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj2!.Id}/characters",
            new CreateCharacterRequest("Hero Beta", "Protagonist", null));
        var heroBeta = await charBetaResp.Content.ReadFromJsonAsync<CharacterDto>();

        // Violation 1: Relationship from same character to itself (UNIT-008)
        var selfRelResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj1.Id}/relationships",
            new CreateCharacterRelationshipRequest(heroAlpha!.Id, heroAlpha.Id, "Ally"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, selfRelResp.StatusCode);

        // Violation 2: Relationship across different projects -> 422
        var crossRelResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj1.Id}/relationships",
            new CreateCharacterRelationshipRequest(heroAlpha.Id, heroBeta!.Id, "Rival"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossRelResp.StatusCode);

        // Valid: Relationship between two characters in Project 1 -> 201 Created
        var validRelResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj1.Id}/relationships",
            new CreateCharacterRelationshipRequest(heroAlpha.Id, companionAlpha!.Id, "Ally", "Bạn đồng hành từ thủa thiếu thời"));
        Assert.Equal(HttpStatusCode.Created, validRelResp.StatusCode);
        var rel = await validRelResp.Content.ReadFromJsonAsync<CharacterRelationshipDto>();
        Assert.Equal("Hero Alpha", rel!.SourceCharacterName);
        Assert.Equal("Companion Alpha", rel.TargetCharacterName);
        Assert.Equal("Ally", rel.Type);

        // Violation 3: Duplicate identical relationship -> 409 Conflict
        var dupRelResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj1.Id}/relationships",
            new CreateCharacterRelationshipRequest(heroAlpha.Id, companionAlpha.Id, "Ally"));
        Assert.Equal(HttpStatusCode.Conflict, dupRelResp.StatusCode);
    }

    [Fact]
    public async Task UNIT005_CharacterTemplate_CreatePreviewApply_PriorRevisionsUntouched()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Template Tester");

        // 1. Create project & attribute definitions
        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Archetype Universe", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var hairDefResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("hair_style", "Kiểu Tóc", "Text"));
        var hairDef = await hairDefResp.Content.ReadFromJsonAsync<AttributeDefinitionDto>();

        var classDefResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/attribute-definitions",
            new CreateAttributeDefinitionRequest("class_archetype", "Hệ Phái", "Text"));
        var classDef = await classDefResp.Content.ReadFromJsonAsync<AttributeDefinitionDto>();

        // 2. Create a Template: "Warrior Archetype"
        var tplResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/character-templates",
            new CreateTemplateRequest("Warrior Archetype", "Mẫu hiệp sĩ cổ điển",
                new List<CreateTemplateAttributeItemRequest>
                {
                    new(hairDef!.Id, "Short Spiky Hair"),
                    new(classDef!.Id, "Berserker")
                }));
        Assert.Equal(HttpStatusCode.Created, tplResp.StatusCode);
        var template = await tplResp.Content.ReadFromJsonAsync<CharacterTemplateDto>();

        // 3. Create Character with initial attributes (Revision 1)
        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters",
            new CreateCharacterRequest("Ragnar", "Warrior", "Novice adventurer",
                InitialAttributes: new Dictionary<string, string?>
                {
                    ["hair_style"] = "Long Braided Hair"
                }));
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // 4. Preview template application (without overwrite)
        var previewResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character!.Id}/apply-template/preview",
            new ApplyTemplateRequest(template!.Id, OverwriteExisting: false));
        Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);
        var preview = await previewResp.Content.ReadFromJsonAsync<ApplyTemplatePreviewDto>();

        Assert.Equal(2, preview!.PreviewItems.Count);
        var hairPreview = preview.PreviewItems.First(p => p.Code == "hair_style");
        var classPreview = preview.PreviewItems.First(p => p.Code == "class_archetype");

        // Because OverwriteExisting=false, hair_style keeps "Long Braided Hair"
        Assert.False(hairPreview.WillChange);
        Assert.Equal("Long Braided Hair", hairPreview.NewValue);
        // class_archetype was not set, so it will change to "Berserker"
        Assert.True(classPreview.WillChange);
        Assert.Equal("Berserker", classPreview.NewValue);

        // 5. Commit template application (generates Revision 2)
        var applyResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/apply-template",
            new ApplyTemplateRequest(template.Id, OverwriteExisting: false));
        Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);

        // 6. Verify Revision 1 snapshot remains intact (UNIT-005)
        var revsResp = await client.GetAsync($"/api/v1/projects/{proj.Id}/characters/{character.Id}/revisions");
        var revs = await revsResp.Content.ReadFromJsonAsync<List<CharacterRevisionDto>>();
        Assert.Equal(2, revs!.Count);

        var rev1 = revs.First(r => r.RevisionNumber == 1);
        Assert.Contains("Long Braided Hair", rev1.SnapshotJson);
        Assert.DoesNotContain("Berserker", rev1.SnapshotJson); // Prior snapshot has NOT mutated!
    }

    [Fact]
    public async Task API009_StaleRowVersion_CharacterUpdate_Returns409()
    {
        var (client, _, _) = await CreateAndLoginUserAsync("Concurrency Tester");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new CreateProjectRequest("Concurrency Project", "Game", null));
        var proj = await projResp.Content.ReadFromJsonAsync<ProjectDto>();

        var charResp = await client.PostAsJsonAsync($"/api/v1/projects/{proj!.Id}/characters",
            new CreateCharacterRequest("Concurrent Hero", "Protagonist", null));
        var character = await charResp.Content.ReadFromJsonAsync<CharacterDto>();

        // Fake stale version (stale base64 bytes)
        var staleVersion = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });

        var conflictResp = await client.PutAsJsonAsync($"/api/v1/projects/{proj.Id}/characters/{character!.Id}",
            new UpdateCharacterRequest("Updated Hero", "Protagonist", null, staleVersion));

        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);
    }
}
