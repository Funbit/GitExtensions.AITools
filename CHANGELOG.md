# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- Target Git Extensions 7.2.1 and .NET 10; AI Tools 6.x remains the version for Git Extensions 6.x.
- Pin development downloads to Git Extensions 7.2.1 in a separate versioned folder.
- Update Plugin Manager compatibility to the GitExtensions.Extensibility 7.x API.

## [6.0.1] 2026-03-20

### Fixed

- Fixed discovery by the GitExtensions Plugin Manager

## [6.0.0] 2026-03-20

### Added

- Initial release: AI commit message generation for Git Extensions 6.x
- Providers: Anthropic (Claude), OpenAI, GitHub Copilot, Claude Code, OpenCode
- Auto-fill mode (watches `.git/index` for stage/unstage)
- Manual mode using the commit template dropdown integration
- Custom instructions support
- French translation
