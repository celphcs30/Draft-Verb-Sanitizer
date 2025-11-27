# Draft Verb Sanitizer

A RimWorld 1.6 mod that automatically cleans up orphaned verbs when pawns are drafted to prevent auto-attack errors and NullReferenceExceptions.

## Overview

Draft Verb Sanitizer addresses a common issue where destroyed equipment, removed hediffs, or deactivated abilities leave behind invalid verb references in pawn verb trackers. These orphaned verbs can cause `EffectiveRange` NullReferenceExceptions during auto-attack calculations, leading to game errors and combat system failures.

## How It Works

The mod uses Harmony patches to automatically sanitize verb trackers when pawns are drafted or undrafted:

1. **Automatic Sanitization**: Triggers whenever a pawn's drafted state changes, removing verbs that reference:
   - Destroyed or unequipped equipment
   - Removed hediffs
   - Deactivated abilities
   - Invalid verb trackers

2. **Safety Guard**: Provides a lightweight prefix patch on `VerbProperties.AdjustedRange` that returns safe fallback values instead of throwing NullReferenceExceptions if a bad verb slips through.

3. **MVCF Integration**: Automatically refreshes MVCF verb managers when present to maintain compatibility with mods that extend the verb system.

## Features

- **Zero Configuration**: Works automatically with no settings or UI
- **Fail-Safe Design**: All operations wrapped in try/catch blocks
- **Performance Optimized**: Minimal overhead, only runs on draft toggle
- **Compatible**: Works with MVCF and other verb-extending mods

## Requirements

- RimWorld 1.6
- Harmony (brrainz.harmony) - automatically listed as dependency

## Building

The project uses the standard RimWorld mod structure. Build in Release configuration to output to `Assemblies/DraftVerbSanitizer.dll`.

## License

CC0-1.0 - Public Domain

