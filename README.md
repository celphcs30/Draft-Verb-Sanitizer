# Draft Verb Sanitizer

A RimWorld 1.6 mod that automatically cleans up orphaned verbs when pawns are drafted to prevent auto-attack errors and NullReferenceExceptions.

## Overview

Draft Verb Sanitizer addresses a common issue where destroyed equipment, removed hediffs, or deactivated abilities leave behind invalid verb references in pawn verb trackers. These orphaned verbs can cause `EffectiveRange` NullReferenceExceptions during auto-attack calculations, leading to game errors and combat system failures.

## How It Works

The mod uses Harmony patches to automatically sanitize verb trackers and prevent errors at multiple points:

1. **Automatic Sanitization**: Triggers whenever a pawn's drafted state changes, removing verbs that reference:
   - Destroyed or unequipped equipment
   - Removed hediffs
   - Deactivated abilities
   - Invalid verb trackers

2. **AdjustedRange Safety Guard**: Provides a lightweight prefix patch on `VerbProperties.AdjustedRange` (with Priority.First) that returns safe fallback values instead of throwing NullReferenceExceptions if a bad verb slips through. Runs before other patches to catch issues early.

3. **HostilityResponse Guard**: Prevents NREs in the hostility response system by sanitizing verbs and validating attack capabilities before the original logic runs.

4. **Melee Verb Error Suppression**: Suppresses "has no available melee attack" ErrorOnce spam by detecting when pawns have no melee verbs and returning null gracefully. Uses robust method discovery to handle RimWorld updates.

5. **Melee Gizmo Safety**: Prevents creating melee attack gizmos when no melee verbs are available, avoiding unnecessary UI work.

6. **MVCF Integration**: Automatically refreshes MVCF verb managers when present to maintain compatibility with mods that extend the verb system.

## Safety

**Fail-Safe Design**: Every operation is wrapped in try/catch blocks. If any check encounters an unexpected error, it logs a warning and continues. The mod will never crash your game.

**Isolated Operations**: Each patch operates independently. A failure in one patch does not affect others.

**Read-Only Where Possible**: The mod only removes invalid entries; it never modifies valid game data or core game logic.

**Reflection Safety**: All reflection calls use cached `FieldInfo`/`PropertyInfo`/`MethodInfo` objects and include null checks. Missing fields or properties are handled gracefully.

## Performance

- **Execution Time**: Sanitization typically completes in <1ms per pawn
- **Frequency**: Runs only when pawns are drafted/undrafted (user action, not every tick)
- **Overhead**: Minimal - uses cached reflection and efficient iteration patterns
- **Memory**: No persistent memory allocation; all cleanup is transient
- **Optimizations**:
  - Cached reflection lookups (ability property, MVCF methods, verb list methods)
  - HashSet-based O(1) membership checks instead of O(n) list searches
  - In-place removal eliminates unnecessary allocations
  - Priority.First on guards ensures earliest execution

## Features

- **Zero Configuration**: Works automatically with no settings or UI
- **Comprehensive Coverage**: Guards multiple code paths where verb errors can occur
- **Error Suppression**: Prevents spam from "has no available melee attack" errors
- **Compatible**: Works with MVCF, VEF, and other verb-extending mods

## Requirements

- RimWorld 1.6
- Harmony (brrainz.harmony) - automatically listed as dependency

## Building

The project uses the standard RimWorld mod structure. Build in Release configuration to output to `Assemblies/DraftVerbSanitizer.dll`.

## License

CC0-1.0 - Public Domain
