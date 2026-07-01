# Changelog

All notable changes to this project will be documented in this file.

This project uses a SemVer-like versioning scheme while it is in the 0.x development phase.

Release notes are generated from this file. Keep changelog entries in English.

## Unreleased

## 0.2.1

### Changed

* Restructured the repository so the actual skill now lives under `skills/taiwu-mod-dev/` instead of the repository root.
* Updated the project layout to better match the expected structure used by `npx skills add`.

### Fixed

* Fixed an installation issue where `npx skills add gruiyuan/taiwu-mod-dev-skill` could install only `SKILL.md` while omitting required supporting directories such as `references/` and `scripts/`.
* Ensured the skill's supporting references and scripts are preserved during installation.

### Notes

* This release does not change the skill's behavior or Taiwu mod-development capabilities.
* Users who installed an earlier broken layout should reinstall the skill after upgrading.

## 0.2.0

### Added

* The skill can now ground itself in the game's own descriptions of how things work: it ships a builder that turns the in-game encyclopedia ("百晓册") into a queryable markdown knowledge base, so the agent understands game mechanics before writing patches rather than guessing from code alone.
* The skill can now look up the real numbers behind any game entity: it ships a builder that extracts all config tables (features, weapons, combat skills, etc.) from the game assembly, giving the agent authoritative current values to tune against in value-editing mods.

## 0.1.0

### Added

* Initial public release of `taiwu-mod-dev-skill`.
* Added the main Agent Skill entry file for Taiwu mod development.
* Added references for environment setup, decompilation, backend mod development, frontend mod development, Harmony patching, Config.lua, RPC, build/deploy workflow, logging/debugging, Steam Workshop publishing, and game update maintenance.
* Added installation guidance for project-level Agent Skills and supported AI coding agents.

### Notes

* This is the first public baseline version.
* The project is primarily documentation and workflow guidance for building independent C# mods for 《太吾绘卷：天幕心帷》.
* No binary release assets are provided; GitHub automatically provides source archives for the release tag.
