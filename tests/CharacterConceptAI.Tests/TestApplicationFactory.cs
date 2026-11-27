using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CharacterConceptAI.Tests;

// Tests explicitly opt into an isolated, simulated host. Never use a developer's
// configured SQL database, media folder or live GPU from an integration test.
public class TestApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var testId = Guid.NewGuid().ToString("N");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Testing:DatabaseName"] = "CharacterConceptAI_Test_" + testId,
            ["Media:RootPath"] = Path.Combine(Path.GetTempPath(), "CharacterConceptAI_Tests", testId),
            ["Admin:BootstrapEnabled"] = "true",
            ["Admin:Email"] = "admin@conceptai.local",
            ["Admin:Password"] = "Admin@Secured2026",
            ["ComfyUI:Provider"] = "Fake",
            ["Database:ApplyMigrationsOnStartup"] = "false",
            ["Database:SeedDemoData"] = "false"
        }));
    }
}
