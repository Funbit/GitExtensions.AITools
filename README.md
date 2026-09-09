<p align="center">
  <img src="assets/ai-tools-logo.svg" alt="AI Tools Logo" width="128" height="128" />
</p>

<h1 align="center">Git Extensions AI Tools</h1>

<p align="center">
  AI-powered commit message generation for <a href="https://github.com/gitextensions/gitextensions">Git Extensions</a>
</p>

<p align="center">
  <a href="https://www.nuget.org/packages/GitExtensions.AITools"><img src="https://img.shields.io/nuget/v/GitExtensions.AITools" alt="NuGet Version" /></a>
  <a href="https://www.nuget.org/packages/GitExtensions.AITools"><img src="https://img.shields.io/nuget/dt/GitExtensions.AITools" alt="NuGet Downloads" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/JBTremblay/gitextensions.aitools" alt="License" /></a>
</p>

## Features

- **AI-generated commit messages** — generates conventional commit messages from staged diffs
- **Auto-fill mode** — automatically writes the commit message as you stage/unstage files
- **Commit template** — also available as a selectable template in the commit dialog dropdown
- **Multiple LLM providers:**
  - Anthropic (Claude)
  - OpenAI
  - GitHub Copilot
  - Claude Code
  - OpenCode

## Installation

Version 7.x targets **Git Extensions 7.2.1** and requires the **.NET 10 Desktop Runtime (x64), version 10.0.9 or later 10.x**. Use AI Tools 6.x with Git Extensions 6.x.

Install via [GitExtensions.PluginManager](https://github.com/gitextensions/gitextensions.pluginmanager):

1. Open Git Extensions → **Tools** → **Plugin Manager**
2. Search for **AI Tools**
3. Install and restart Git Extensions

## Configuration

Open **Plugins → AI Tools** in Git Extensions to configure:

| Setting | Description | Default |
|---------|-------------|---------|
| Enabled | Enable/disable the plugin | `true` |
| Auto-fill on stage/unstage | Automatically fill the commit message box | `true` |
| Provider | LLM provider to use | GitHub Copilot |
| API Key | API key (optional for GitHub Copilot / Claude Code / OpenCode) | — |
| Model override | Use a specific model instead of the provider default | — |
| Commit types | Comma-separated list of allowed conventional commit types | `feat, fix, refactor, ...` |
| Custom instructions | Appended to the built-in prompt | — |

## How It Works

- **With auto-fill enabled (default):** The commit message is generated automatically when you stage or unstage files and updates as you go.
- **With auto-fill disabled:** Select the **"AI: Generate commit message"** template from the commit message dropdown to trigger generation.

## Building from Source

Requires Windows and the .NET 10 SDK.

```
dotnet build -c Debug
```

The build automatically downloads Git Extensions **7.2.1** to `gitextensions.shared/v7.2.1`. After building, the plugin DLL and translations are copied to `gitextensions.shared/v7.2.1/UserPlugins/GitExtensions.AITools` for testing. Launch `gitextensions.shared/v7.2.1/GitExtensions.exe` to try the plugin.

To build against an existing Git Extensions 7.2.1 installation, override the reference path (the build also copies the plugin there):

```powershell
dotnet build -c Debug -p:GitExtensionsPath="C:\Tools\GitExtensions"
```

The build rejects binaries from a different Git Extensions major version. The versioned download folder keeps older development installations separate.

To pack as a NuGet package:

```
dotnet pack -c Release
```

## Contributing

Contributions are welcome! To get started:

1. Fork the repository
2. Create a branch from `develop`
3. Make your changes and push to your fork
4. Open a pull request targeting `develop`

Please make sure the CI build passes before requesting a review.

## License

[MIT](LICENSE)

---

<p align="center">
  Made by <a href="https://github.com/JBTremblay">JBTremblay</a> · <a href="https://github.com/sponsors/JBTremblay">Sponsor</a>
</p>
