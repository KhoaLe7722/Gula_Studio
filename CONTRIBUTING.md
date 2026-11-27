# Contributing to Gula Studio

Thank you for your interest in contributing to **Gula Studio**! Whether it's reporting a bug, proposing a new ComfyUI workflow node, improving WPF UI polish, or optimizing VRAM usage on 6 GB GPUs, every contribution is welcome.

## Development Setup

1. **Prerequisites**
   - Windows 10 / 11 (x64)
   - [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
   - SQL Server 2022/2025 Express (for live API mode; unit/integration tests run in-memory without SQL Server)
   - NVIDIA GPU with CUDA drivers (optional for UI/API development)

2. **Build & Test**
   ```powershell
   dotnet restore CharacterConceptAI.slnx
   dotnet build CharacterConceptAI.slnx -c Release
   dotnet test CharacterConceptAI.slnx -c Release
   ```

3. **WPF UI Visual Smoke Harness**
   ```powershell
   dotnet build tests/CharacterConceptAI.Desktop.Smoke/CharacterConceptAI.Desktop.Smoke.csproj -c Release
   ./tests/CharacterConceptAI.Desktop.Smoke/bin/Release/net10.0-windows/CharacterConceptAI.Desktop.Smoke.exe ./artifacts/ui-smoke
   ```

## Guidelines

- **Zero hardcoded developer paths:** Always use `GulaWorkspaceLocator`, `GULA_STUDIO_HOME`, or relative path discovery.
- **No large binaries or secrets in Git:** Never commit `.safetensors`, `.onnx`, `.gguf`, `.pth`, generated `MediaStorage/`, or connection credentials.
- **Keep Dark & Light themes synced:** Use `DynamicResource` theme brushes rather than hardcoded hex colors in XAML.
- **Verify tests pass:** Ensure `dotnet test` (79 tests) and the WPF smoke harness report `0` errors and `0` binding warnings before opening a PR.

## Pull Request Flow

1. Fork the repository and create a feature branch (`git checkout -b feat/your-feature`).
2. Commit your changes with clear conventional messages (`feat:`, `fix:`, `perf:`, `docs:`).
3. Open a Pull Request describing what changed and how you verified it.
